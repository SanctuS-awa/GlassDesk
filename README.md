<div align="center">

<img src="docs/images/logo.png" alt="GlassDesk 标志" width="80">

# GlassDesk

**时间，音乐。还有一点陪伴。**

Windows 11 桌面液态玻璃小组件 · 0.2.2

[下载](https://github.com/SanctuS-awa/GlassDesk/releases) · [展示页](https://sanctus-awa.github.io/GlassDesk/) · [反馈](https://github.com/SanctuS-awa/GlassDesk/issues)

<picture>
  <source media="(prefers-reduced-motion: reduce)" srcset="docs/images/liquid-morph.png">
  <img src="docs/images/liquid-morph.gif" alt="组件从时间胶囊展开为首页" width="720">
</picture>

<sub>动图与展示页仅作界面演示，不连接本机或控制播放器。</sub>

</div>

## 特性

- **液态玻璃**：可选插件，默认关闭；GPU 实时折射桌面，边缘随形态增厚，支持色散与叠层模糊。
- **胶囊到首页**：时间、网易云音乐 SMTC 与可用的播放控制。
- **首页**：左侧天气和含农历的日历，右侧音乐，中间留白；顶部 Wi-Fi 与电池（百分比在电池内，接电显示插头），右侧音量条，底部 Dock 提供桌面文件夹预览及网易云音乐、Edge 快捷方式。
- **实时天气**：Open-Meteo 免 Key；自动定位优先使用 Windows 定位，失败时按 IP 粗略定位，也可搜索或按省、市、区县选择。
- **陪伴球**：眼睛跟随鼠标、会眨眼；原生、猫娘、恶魔三种形态，可换颜色。
- **设置**：全中文，自动保存。

## 更多画面

| 拖动观察玻璃折射 | 胶囊小展开 |
| --- | --- |
| <img src="docs/images/liquid-drag.gif" alt="拖动首页观察液态玻璃折射" width="420"> | <img src="docs/images/liquid-peek.gif" alt="悬停胶囊查看小展开" width="420"> |

## 安装

从 [Releases](https://github.com/SanctuS-awa/GlassDesk/releases) 下载 `GlassDesk-0.2.2-win-x64-setup.exe` 并运行。
需要 Windows 11 22H2 或更新版本，x64。
安装包未代码签名，请从本仓库 Releases 下载。

## 从源码构建

双击 `一键编译.bat` 构建，或 `一键打包安装包.bat` 打包；详见[构建说明](docs/build.md)。

[使用说明](docs/user-guide.md) · [隐私](docs/privacy.md) · [构建](docs/build.md) · [插件开发](docs/plugin-development.md) · [更新记录](CHANGELOG.md) · [MIT 许可](LICENSE) · [第三方声明](THIRD-PARTY-NOTICES.md)

<sub>天气数据 © Open-Meteo（CC BY 4.0）</sub>
