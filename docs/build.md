# 构建与检查

当前应用版本由根目录 `Directory.Build.props` 决定，为 **0.2.2**（版本字段为 `0.2.2`）。最低声明为 Windows 11 Build 22621 x64；声明最低版本不等于已在干净机器验证。

## 工具链

- Windows x64 开发环境。
- .NET SDK **10.0.401**，见 `global.json`。还原使用已提交的锁文件。
- Visual Studio 的 MSVC x64 C++ 工具；脚本通过 `vswhere` 查找。
- Windows SDK **10.0.26100.0**；原生脚本校验所选 SDK。
- 运行一键打包脚本需要 Inno Setup 6 或 7；可用 `winget install JRSoftware.InnoSetup` 安装 Inno Setup 6。

主要依赖与原始许可保存在 [第三方声明](../THIRD-PARTY-NOTICES.md) 和 `licenses` 中。不要用项目 MIT 许可替换微软二进制包的许可。

## 自包含编译

从仓库根目录执行：

```powershell
.\scripts\build-native.ps1 -Configuration Release
dotnet restore .\src\GlassDesk.App\GlassDesk.App.csproj --locked-mode
dotnet publish .\src\GlassDesk.App\GlassDesk.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:GlassDeskUiPreview=false -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\artifacts\publish
```

原生步骤会生成宿主 DLL 与捕获子进程。WinUI 发布保留完整资源与运行依赖，不要仅复制一个 EXE。

## 普通检查

以下为控制台检查程序，使用 `dotnet run` 才会执行断言。`--no-stress` 关闭 Core / Plugin 的循环压力部分，Platform 忽略该参数。

```powershell
dotnet run --project tests/GlassDesk.Core.Tests -c Release -- --no-stress
dotnet run --project tests/GlassDesk.PlatformChecks -c Release -- --no-stress
dotnet run --project tests/GlassDesk.PluginChecks -c Release -- --no-stress
```

CI 执行原生编译、锁定还原、自包含发布和以上隔离检查；不安装应用、不操作桌面输入或真实音乐，也不自动发布安装包。首次运行结果以 [Actions](https://github.com/SanctuS-awa/GlassDesk/actions) 为准，不预先宣称成功。

工作流使用官方 [checkout](https://github.com/actions/checkout) 与 [setup-dotnet](https://github.com/actions/setup-dotnet)，按已核实提交固定版本；仓库权限为只读。参考 [GitHub 工作流语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)。

## 安装包

在 Windows 上双击仓库根目录 `一键打包安装包.bat`，也可以从 PowerShell 运行：

```powershell
.\一键打包安装包.bat
```

首次打包需要安装 Inno Setup 6 或 7：

```powershell
winget install JRSoftware.InnoSetup
```

脚本会在 `artifacts\GlassDesk\GlassDesk.exe` 不存在时先编译应用；传入 `/rebuild` 可强制重新编译。安装包及其 SHA-256 校验文件输出到 `artifacts\installer`：

- `GlassDesk-0.2.2-win-x64-setup.exe`
- `GlassDesk-0.2.2-win-x64-setup.exe.sha256`，内容格式为 `<hash>  <文件名>`。

`scripts/build-release.ps1` 保留为完整发布候选流程：它执行原生构建、锁定还原、自包含发布、检查项目构建和安装包打包，并可用 `-RunChecks` 在指定的独立 Windows 机器上运行完整检查。它默认查找仓库内的 Inno Setup 7.1.0 编译器，也可通过 `-CompilerPath` 指定 ISCC.exe。GitHub Actions 工作流单独执行 CI 构建与隔离检查，不调用此脚本，也不自动发布安装包。
