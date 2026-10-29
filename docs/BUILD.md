# 构建与发布

> 面向贡献者与自行打包的开发者。普通用户请直接从 [Releases](https://github.com/wwwwwwwwang/bd2-mod-manager/releases/latest) 下载成品。

## 一、环境要求

| 依赖 | 版本 / 说明 |
|---|---|
| .NET SDK | 8.0（GUI / CLI / Core / 测试） |
| Python | 3.10+，仅用标准库（loader 打包、引用拆分、图标生成） |
| Git | 任意较新版本 |
| 操作系统 | 编译可在 Windows 或 Linux 完成；GUI 产物是 win-x64，最终验收在 Windows |

没有 .NET 时可以先装到用户目录：

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/.dotnet
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
```

Windows 也可以直接用 Visual Studio 2022 / Rider 打开项目；仓库没有 `.sln`，直接打开对应 `.csproj` 即可。

## 二、首次构建（手动）

顺序不能反：loader 包 → 引用程序集 → 插件 → 嵌入资源 → 发布 GUI / CLI。

```bash
# 1. 组装 loader 分发包（下载官方 release、校验 SHA256、生成 manifest）
python3 scripts/package_loader.py --out dist/bd2-loader.zip

# 2. 从 loader 包里拆出编译插件要用的 BepInEx / Harmony 引用
python3 scripts/prepare_refs.py --zip dist/bd2-loader.zip

# 3. 编译解锁插件，并跑离线验证
dotnet build src/BD2ModManager.SkinUnlock/BD2ModManager.SkinUnlock.csproj -c Release
dotnet run --project tests/BD2ModManager.SkinUnlock.Tests -c Release

# 4. 把 loader 包与插件嵌进 GUI（顺序不能反）
cp dist/bd2-loader.zip src/BD2ModManager.Gui/Assets/bd2-loader.zip
cp src/BD2ModManager.SkinUnlock/bin/Release/netstandard2.0/BD2SkinUnlock.dll \
   src/BD2ModManager.Gui/Assets/BD2SkinUnlock.dll

# 5. 发布 GUI（自包含单文件，约 45 MB）
dotnet publish src/BD2ModManager.Gui/BD2ModManager.Gui.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -o out/gui

# 6. 发布 CLI
dotnet publish src/BD2ModManager.Cli/BD2ModManager.Cli.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o out/cli
```

补充说明：

- 插件只引用「引用程序集」，编译**不需要游戏本体**。
- `UnityEngine.Modules 2021.3.33` 从 NuGet 取，`ExcludeAssets="runtime"`，只用于编译，不会进产物。
- BepInEx 与 HarmonyX 必须与运行时同版本，所以从我们自己组的 loader 包里拆，不用 NuGet。
- 若跳过第 4 步，GUI 仍能编译运行，但不会内嵌 loader 包与解锁插件；用户需要手动指定 `bd2-loader.zip`。

跑一遍 Core 测试（需要 `dist/bd2-loader.zip`）：

```bash
dotnet run --project tests/BD2ModManager.Core.Tests -c Release
```

## 三、构建产物

| 产物 | 内容 | 说明 |
|---|---|---|
| `out/gui/BD2ModManager.exe` | 图形界面，自包含单文件 | loader 包与 `BD2SkinUnlock.dll` 已内嵌；约 45 MB |
| `out/cli/bd2mm.exe` | 命令行版，自包含单文件 | 无 GUI 依赖 |
| `dist/bd2-loader.zip` | loader 分发包 | 从官方 release 现下现组，可单独取用 |
| `src/.../BD2SkinUnlock.dll` | 自研解锁插件 | 已内嵌进 GUI；单文件版给手动安装或旧版工具用 |

`dist/`、`refs/`、`src/BD2ModManager.Gui/Assets/bd2-loader.zip`、`BD2SkinUnlock.dll` 都被 `.gitignore` 排除。自包含 exe 体积大且无法增量 diff，二进制只走 GitHub Release。

## 四、loader 包是怎么组装的

`scripts/package_loader.py` **不把 BepInEx 二进制存进仓库**，而是每次从官方 release 下载、按固定 SHA256 校验、再组装。这样做有三个实际好处：

1. **可审计**：上游哈希写死在脚本里，任何篡改都会让构建失败，而不是悄悄进入发布物。
2. **仓库干净**：五十多个二进制文件不进 git，体积与 diff 噪声都省掉。
3. **确定性构建**：zip 内时间戳、排序、权限位固定，同样的输入必得同样的 SHA256（已实测两次构建完全一致）。

### 固定上游

| 组件 | 版本 | 上游发行包 SHA256 |
|---|---|---|
| BepInEx | 5.4.23.6 | `6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755` |
| BepInEx.ConfigurationManager | 19.1 | `dc10b1e7435dd3e0d32658b3f0f4230ddd71750164963b1095deca3bc3a98275` |

脚本还会校验：

- `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`changelog.txt` 来自官方包；
- `BepInEx/core/*` 与 `plugins/ConfigurationManager/*` 完整；
- 许可证全文与 `THIRD-PARTY-NOTICES.md` 随包分发。

### loader 包内容

```
winhttp.dll                          UnityDoorstop 4.6.0（官方原件）
doorstop_config.ini                  官方原件（4.x 语法：target_assembly）
.doorstop_version                    官方原件
changelog.txt                        官方原件
VERSION.txt                          版本标记
BepInEx/core/*                       官方 18 个文件
BepInEx/plugins/ConfigurationManager 官方 19.1，自带 LICENSE
BepInEx/Translation/*                CM 的多语言翻译
LICENSE-BepInEx-LGPL-2.1.txt         LGPL-2.1 全文
THIRD-PARTY-NOTICES.md               逐组件许可与源码链接
manifest.json                        每个文件 path + size + sha256（当前 55 个）
```

`manifest.json` 是安装器的唯一依据：`files[]` 记录每个文件的路径、大小、SHA256；`loader` 记录版本与 Doorstop；`target_game` 记录主程序名、数据目录与 Unity 版本。

## 五、CI / 发布流水线

### `.github/workflows/ci.yml`

触发：`push` 到 `main`、任意 `pull_request`、手动 `workflow_dispatch`。

步骤：

1. checkout + 安装 .NET 8；
2. 从 `Directory.Build.props` 读 `<Version>`；
3. `package_loader.py` 组装 loader 包；
4. `prepare_refs.py` 拆引用；
5. 编译解锁插件并跑 `SkinUnlock.Tests`；
6. 跑 `Core.Tests`（会读 `dist/bd2-loader.zip` 走完整安装 / 卸载）；
7. 把 loader 包与插件拷进 GUI 的 `Assets/`；
8. `dotnet restore` / `build`；
9. `dotnet publish` 出 GUI 与 CLI 单文件；
10. 上传 `bd2mm-<版本>-win-x64-dev` 调试产物（保留 7 天）。

### `.github/workflows/auto-tag.yml`

触发：`main` 分支的 CI 成功完成后。

流程：

1. 读取 `Directory.Build.props` 的 `<Version>`；
2. 如果 `v<Version>` 标签不存在，创建并推送该标签；
3. 通过 `gh workflow run release.yml --ref <tag> -f tag=<tag>` 显式触发发布。

正常发版只需要改 `<Version>` 并合并到 `main`，CI 通过后自动打标签、触发 Release，不需要手动执行 `git tag`。

> 注意：用 `GITHUB_TOKEN` 推送的标签不会触发 `on: push: tags` 的工作流，所以 auto-tag 必须显式 dispatch `release.yml`。

### `.github/workflows/release.yml`

触发：推送 `v*` 标签、由 `auto-tag.yml` 触发，或手动指定标签。

手动补发时可以执行：

```bash
git tag v0.5.0   # 示例：替换为实际版本
git push origin v0.5.0
```

流水线会：

- 校验标签以 `v` 开头；
- 读取 `Directory.Build.props` 的版本号；若标签版本与源码版本不一致，输出 warning（不阻断）；
- 重复 CI 的构建步骤；
- 整理发布资产并生成 `SHA256SUMS.txt`；
- 用 `gh release create` 创建 Release；已存在则 `gh release upload --clobber` 覆盖资产。

### 发布资产

| 资产 | 内容 |
|---|---|
| `BD2ModManager-<版本>-win-x64.exe` | 图形界面，自包含单文件，loader 包已内嵌 |
| `bd2mm-<版本>-win-x64.exe` | 命令行版，自包含单文件 |
| `bd2-loader-<版本>.zip` | 从官方 release 现下现组的 loader 包 |
| `BD2SkinUnlock.dll` | 自研解锁插件单文件 |
| `SHA256SUMS.txt` | 上述文件的校验和 |

## 六、常见问题

**`缺少 BepInEx 引用程序集`**

先跑 `package_loader.py`，再跑 `prepare_refs.py`；或构建时指定：

```bash
dotnet build ... -p:BepInExRefDir=<loader 包解开的 BepInEx/core 目录>
```

**插件编译报 `CS0012`（缺 `UnityEngine.dll` 聚合 facade）**

`UnityEngine.Modules` 的 NuGet 引用要保留；不要手工 `HintPath` 到 `lib/netstandard2.0/`，`PackageReference` 才能正确处理 TFM 与前向。

**发布出来的 exe 没有内嵌 loader**

检查 `src/BD2ModManager.Gui/Assets/bd2-loader.zip` 与 `BD2SkinUnlock.dll` 是否在 `dotnet publish` 之前拷贝到位。csproj 只在文件存在时嵌入，不存在会静默跳过。
