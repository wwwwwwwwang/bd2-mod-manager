#!/usr/bin/env python3
"""从 loader 包里提取编译/测试插件所需的程序集。

为什么不用 NuGet 取 BepInEx：插件引用的 BepInEx.dll 与 0Harmony.dll
必须和用户实际运行的版本完全一致，否则元数据对不上会加载失败。
所以直接拆我们自己打包的 loader —— 它是我们从官方 release 组装的，来源可查。

产物目录已在 .gitignore 里，不进版本库。

用法：
    python3 scripts/package_loader.py --out dist/bd2-loader.zip
    python3 scripts/prepare_refs.py                     # 默认读 dist/bd2-loader.zip
    python3 scripts/prepare_refs.py --zip other.zip --out refs/bepinex
"""

from __future__ import annotations

import argparse
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 整个 core 目录都要：编译只用 BepInEx.dll / 0Harmony.dll，
# 但跑离线测试时 Harmony 还会去加载 MonoMod 那几个运行时依赖。
CORE_PREFIX = "BepInEx/core/"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--zip", default=str(ROOT / "dist" / "bd2-loader.zip"))
    ap.add_argument("--out", default=str(ROOT / "refs" / "bepinex"))
    args = ap.parse_args()

    zip_path = Path(args.zip)
    if not zip_path.exists():
        print(f"找不到 loader 包：{zip_path}", file=sys.stderr)
        print("先运行：python3 scripts/package_loader.py --out dist/bd2-loader.zip", file=sys.stderr)
        return 1

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    written = 0
    with zipfile.ZipFile(zip_path) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue

            name = info.filename.replace("\\", "/")
            cut = name.find(CORE_PREFIX)
            if cut < 0:
                continue

            rel = name[cut + len(CORE_PREFIX):]
            if not rel or "/" in rel:
                continue
            if not rel.endswith(".dll"):
                continue

            (out / rel).write_bytes(zf.read(info))
            written += 1

    if written == 0:
        print(f"包里没有 {CORE_PREFIX}*.dll，确认这是个 loader 包", file=sys.stderr)
        return 1

    if not (out / "BepInEx.dll").exists() or not (out / "0Harmony.dll").exists():
        print("缺少 BepInEx.dll 或 0Harmony.dll", file=sys.stderr)
        return 1

    print(f"已提取 {written} 个程序集 → {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
