#!/usr/bin/env python3
"""从官方 release 组装 bd2-loader 分发包。

设计原则：
1. 只使用官方原件 —— 下载后逐字节校验 SHA256，校验失败立即终止
2. 确定性输出 —— 固定时间戳 + 固定排序，相同输入必得相同 zip 哈希
3. 不含任何闭源插件 —— 无再分发授权的第三方组件一律不打包

用法：
    python3 scripts/package_loader.py --out dist/bd2-loader-0.1.0.zip
    python3 scripts/package_loader.py --cache-dir .cache/loader --out /tmp/test.zip

CI 中由 .github/workflows/release.yml 调用。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

# ---------------------------------------------------------------- 上游固定

BEPINEX = {
    "name": "BepInEx",
    "version": "5.4.23.6",
    "url": (
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/"
        "BepInEx_win_x64_5.4.23.6.zip"
    ),
    "sha256": "6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755",
}

CONFIG_MANAGER = {
    "name": "BepInEx.ConfigurationManager",
    "version": "19.1",
    "url": (
        "https://github.com/BepInEx/BepInEx.ConfigurationManager/releases/download/v19.1/"
        "BepInEx.ConfigurationManager_BepInEx5_v19.1.zip"
    ),
    "sha256": "dc10b1e7435dd3e0d32658b3f0f4230ddd71750164963b1095deca3bc3a98275",
}

# 目标游戏的识别特征，写入 manifest 供安装器核对
TARGET_GAME = {
    "exe": "BrownDust II.exe",
    "data_dir": "BrownDust II_Data",
    "unity": "2022.3.62f2",
}

# BepInEx 发行包根目录下需要带到游戏根目录的文件
ROOT_FILES = ("winhttp.dll", "doorstop_config.ini", "changelog.txt", ".doorstop_version")

# 固定 zip 内时间戳，保证可复现
FIXED_DT = (2026, 10, 29, 0, 0, 0)

ASSETS = Path(__file__).resolve().parent / "loader-assets"


# ---------------------------------------------------------------- 工具函数


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fetch(spec: dict, cache_dir: Path) -> Path:
    """下载并校验上游发行包；命中缓存且哈希一致时跳过下载。"""
    cache_dir.mkdir(parents=True, exist_ok=True)
    dest = cache_dir / Path(spec["url"]).name

    if dest.exists():
        actual = sha256_file(dest)
        if actual == spec["sha256"]:
            print(f"  缓存命中  {dest.name}")
            return dest
        print(f"  缓存哈希不符，重新下载  {dest.name}")
        dest.unlink()

    print(f"  下载  {spec['url']}")
    last_error: Exception | None = None
    for attempt in range(1, 4):
        try:
            with urllib.request.urlopen(spec["url"], timeout=120) as resp:
                with open(dest, "wb") as f:
                    shutil.copyfileobj(resp, f)
            break
        except Exception as exc:  # 网络抖动重试
            last_error = exc
            print(f"    第 {attempt} 次失败：{exc}")
            if attempt == 3:
                raise SystemExit(f"下载失败：{spec['url']}") from last_error

    actual = sha256_file(dest)
    if actual != spec["sha256"]:
        raise SystemExit(
            "SHA256 校验失败，拒绝使用该文件\n"
            f"  文件  {dest.name}\n"
            f"  期望  {spec['sha256']}\n"
            f"  实际  {actual}"
        )
    print(f"  校验通过  {dest.name}")
    return dest


def unzip_to(zip_path: Path, out_dir: Path) -> Path:
    out_dir.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path) as zf:
        zf.extractall(out_dir)
    return out_dir


def sorted_files(root: Path):
    """按相对路径排序，保证跨平台稳定。"""
    return sorted(
        (p for p in root.rglob("*") if p.is_file()),
        key=lambda p: p.relative_to(root).as_posix(),
    )


# ---------------------------------------------------------------- 组装


def assemble(work: Path, bep_zip: Path, cm_zip: Path) -> Path:
    root = work / "bd2-loader"
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(parents=True)

    print("[2/4] 解压上游发行包")
    bep = unzip_to(bep_zip, work / "_bepinex")
    cm = unzip_to(cm_zip, work / "_configmanager")

    print("[3/4] 组装目录结构")
    for name in ROOT_FILES:
        src = bep / name
        if not src.exists():
            raise SystemExit(f"上游包缺少所需文件：{name}")
        shutil.copy2(src, root / name)

    if not (bep / "BepInEx").is_dir():
        raise SystemExit("上游包缺少 BepInEx 目录")
    shutil.copytree(bep / "BepInEx", root / "BepInEx")

    cm_plugins = cm / "BepInEx" / "plugins" / "ConfigurationManager"
    if not cm_plugins.is_dir():
        raise SystemExit("ConfigurationManager 包缺少 plugins/ConfigurationManager")
    plugins_dir = root / "BepInEx" / "plugins"
    plugins_dir.mkdir(parents=True, exist_ok=True)
    shutil.copytree(cm_plugins, plugins_dir / "ConfigurationManager")

    cm_translation = cm / "BepInEx" / "Translation"
    if cm_translation.is_dir():
        shutil.copytree(cm_translation, root / "BepInEx" / "Translation")

    # 许可证与第三方声明（合规必需）
    for asset in ("LICENSE-BepInEx-LGPL-2.1.txt", "THIRD-PARTY-NOTICES.md"):
        src = ASSETS / asset
        if not src.exists():
            raise SystemExit(f"缺少随包资产：{src}")
        shutil.copy2(src, root / asset)

    doorstop = (root / ".doorstop_version").read_text(encoding="utf-8").strip()
    (root / "VERSION.txt").write_text(
        f"{BEPINEX['version']}\nDoorstop {doorstop}\n", encoding="utf-8"
    )

    return root


def write_manifest(root: Path, doorstop: str) -> dict:
    files = []
    for p in sorted_files(root):
        rel = p.relative_to(root).as_posix()
        if rel == "manifest.json":
            continue
        files.append(
            {"path": rel, "size": p.stat().st_size, "sha256": sha256_file(p)}
        )
    files.sort(key=lambda item: item["path"])

    dirs = sorted(
        {
            Path(item["path"]).parent.as_posix()
            for item in files
            if Path(item["path"]).parent.as_posix() != "."
        }
    )

    manifest = {
        "schema": 2,
        "package": "bd2-loader",
        "loader": {
            "name": BEPINEX["name"],
            "version": BEPINEX["version"],
            "doorstop": doorstop,
            "arch": "x64",
        },
        "target_game": TARGET_GAME,
        "files": files,
        "dirs": dirs,
        "entrypoints": {
            "hijack": "winhttp.dll",
            "preloader": "BepInEx/core/BepInEx.Preloader.dll",
            "doorstop_config": "doorstop_config.ini",
            "doorstop_version": ".doorstop_version",
        },
        "notes": "纯官方来源，逐字节校验；不含任何闭源插件。",
    }
    (root / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    return manifest


def write_zip(root: Path, out_zip: Path) -> None:
    out_zip.parent.mkdir(parents=True, exist_ok=True)
    if out_zip.exists():
        out_zip.unlink()

    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for p in sorted_files(root):
            arcname = f"{root.name}/{p.relative_to(root).as_posix()}"
            info = zipfile.ZipInfo(arcname, date_time=FIXED_DT)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o644 & 0xFFFF) << 16  # 固定权限位
            zf.writestr(info, p.read_bytes())


# ---------------------------------------------------------------- 入口


def main() -> int:
    parser = argparse.ArgumentParser(
        description="组装 bd2-loader 分发包（纯官方来源，确定性构建）"
    )
    parser.add_argument("--out", required=True, help="输出 zip 路径")
    parser.add_argument(
        "--cache-dir",
        default=".cache/loader",
        help="上游发行包缓存目录（默认 .cache/loader）",
    )
    parser.add_argument(
        "--keep-work",
        action="store_true",
        help="保留中间目录，便于排查",
    )
    args = parser.parse_args()

    cache_dir = Path(args.cache_dir).resolve()
    out_zip = Path(args.out).resolve()

    print("[1/4] 获取上游发行包")
    bep_zip = fetch(BEPINEX, cache_dir)
    cm_zip = fetch(CONFIG_MANAGER, cache_dir)

    work = Path(tempfile.mkdtemp(prefix="bd2-loader-build-"))
    try:
        root = assemble(work, bep_zip, cm_zip)
        doorstop = (root / ".doorstop_version").read_text(encoding="utf-8").strip()
        manifest = write_manifest(root, doorstop)

        print("[4/4] 打包")
        write_zip(root, out_zip)

        digest = sha256_file(out_zip)
        print()
        print(f"输出    {out_zip}")
        print(f"大小    {out_zip.stat().st_size:,} 字节")
        print(f"文件数  {len(manifest['files'])} 个（另有 manifest.json）")
        print(f"SHA256  {digest}")
    finally:
        if args.keep_work:
            print(f"中间目录保留于 {work}")
        else:
            shutil.rmtree(work, ignore_errors=True)

    return 0


if __name__ == "__main__":
    sys.exit(main())
