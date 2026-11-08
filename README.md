<div align="center">

<img src="docs/images/logo.png" alt="GlassDesk 标志" width="80">

# GlassDesk

**时间，音乐。还有一点陪伴。**

Windows 11 桌面上的液态玻璃小组件 · 0.2.1.3 beta

[下载安装包](https://github.com/SanctuS-awa/GlassDesk/releases) · [打开液态玻璃展示页](https://sanctus-awa.github.io/GlassDesk/) · [反馈问题](https://github.com/SanctuS-awa/GlassDesk/issues)

<picture>
  <source media="(prefers-reduced-motion: reduce)" srcset="docs/images/liquid-morph.png">
  <img src="docs/images/liquid-morph.gif" alt="液态玻璃组件从时间胶囊连续展开为首页，折射边缘随尺寸增厚" width="720">
</picture>

<sub>展示页中的曲目、天气与状态均为虚构演示数据；页面不会连接本机或控制播放器。</sub>

</div>

GlassDesk 把时间、音乐控制和会看向你的陪伴球放在桌面一角。液态玻璃是可选插件，启用后会通过 GPU 捕获桌面背景并实时折射；普通磨砂材质仍可单独使用。

## 主要功能

- **液态玻璃**：GPU 实时采样桌面背景，使用圆角透镜 SDF、厚边缘折射和 R/B 色散。胶囊、小展开与大展开的折射带随形状短边增加；卡片叠在面板上时继续模糊下层，深色主题使用较轻的材质色调。该插件默认关闭。
- **陪伴球**：眼睛跟随鼠标并会眨眼；可在设置中调整外观、颜色和跟随行为。
- **音乐控制**：读取网易云音乐提供的 Windows 系统媒体会话（SMTC），显示歌曲与封面，并按来源能力提供上一首、播放/暂停和下一首。网页演示不会连接或控制音乐。
- **首页**：天气卡、日历、纵向音乐卡、Wi‑Fi/电池状态栏、系统音量条与 Dock。天气尚未接入实时服务；Wi‑Fi、电池或主音量数据不可用时，相关位置会显示未知或禁用状态。

## 液态玻璃

![大展开在彩色线条与色块背景上移动，卡片呈现厚边缘折射和叠层模糊](docs/images/liquid-drag.gif)

原生材质使用 Windows Graphics Capture 在 GPU 上采样桌面纹理，再由 D3D11 着色器对圆角矩形边缘做透镜折射。折射带宽度由组件短边连续计算，因此从胶囊展开到首页时，玻璃边缘会逐渐变厚。色散让红、蓝采样在边缘分开，卡片层则对下层内容再次做高斯模糊。折射模型受 [Glasswork](https://github.com/kubai087/glasswork)（MIT）启发；项目第三方组件声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

下表按网页展示尺寸和 DPI 1 计算；原生渲染会按实际窗口尺寸和 DPI 换算。

| 形态 | 展示尺寸 | 折射带 B | 位移上限 D |
| --- | ---: | ---: | ---: |
| 胶囊 | 200 × 48 DIP | 约 14 DIP | 约 6 DIP |
| 小展开 | 320 × 120 DIP | 约 34 DIP | 约 13 DIP |
| 大展开首页 | 960 × 512 DIP | 约 56 DIP | 约 22 DIP |

## 小展开

![小展开中的陪伴球、虚构曲目和播放控制](docs/images/liquid-peek.gif)

悬停胶囊可查看陪伴球、时间、封面、曲名与媒体按钮；点击组件可展开首页，也可以拖动组件观察背景如何经过玻璃边缘。展示页中的播放按钮只改变图标，不播放声音。

## 安装与使用

1. 从 [Releases](https://github.com/SanctuS-awa/GlassDesk/releases) 下载 <strong>GlassDesk-0.2.1.3beta-win-x64-setup.exe</strong> 并运行安装程序。
2. 系统要求：Windows 11 22H2（Build 22621）或更新版本，x64。安装包自带运行时。
3. 安装后从开始菜单启动。安装包目前未代码签名，请确认从本仓库 Releases 获取。
4. 悬停组件可进入小展开，点击可展开首页，拖动空白区域可移动位置。托盘菜单可显示/隐藏组件、打开设置或退出应用。
5. 在设置 → 插件中启用液态玻璃。旁边的 “Liquid Glass” 按钮会打开独立的中文液态玻璃设置窗口。

网易云音乐需要开启 SMTC / 系统媒体控制并正在播放。音乐命令、封面和进度能力取决于系统媒体会话提供的内容。应用不提供账号登录、歌单同步或收藏功能。

## 从源码构建

在 Windows 开发环境双击根目录的 <strong>一键编译.bat</strong>；需要 Visual Studio 2022 的“使用 C++ 的桌面开发”工作负载和 .NET SDK。构建细节见 [构建说明](docs/build.md)。

打包安装包请双击 <strong>一键打包安装包.bat</strong>；需要安装 Inno Setup，详见构建说明。

## 常见问题

**液态玻璃默认开启吗？**  
不是。默认关闭；可在设置 → 插件中启用。捕获背景会增加图形与资源开销。关闭后使用普通系统磨砂材质。

**音乐没有连接？**  
确认网易云音乐正在播放，且已启用 SMTC / 系统媒体控制，再到设置 → 通用重新连接。可用控制取决于媒体来源能力。

**首页的天气、Wi‑Fi、电池或音量没有数据？**  
天气卡目前显示尚未接入状态。Wi‑Fi、电池和主音量能力尚未接入的系统会显示未知或禁用状态。

**设置保存在哪里？**  
默认保存在 <code>%LocalAppData%\GlassDesk</code>。卸载会保留设置目录。开机启动默认关闭，快捷键默认未绑定。

## 隐私与本地处理

音乐信息来自 Windows 系统媒体会话，在本机显示和发送可用控制。音频波浪条只对可靠识别的网易云进程树输出做本地分析，不使用麦克风，也不保存音频文件。启用液态玻璃后，桌面采样只在本机图形管线中处理；应用不会自动上传桌面画面或诊断信息。

诊断记录暂存于有界内存队列，只有手动导出时才写成文件。插件运行在主进程内，拥有主程序权限，不能视为安全沙箱。更多内容见 [隐私说明](docs/privacy.md)。

## 文档、贡献与许可

- [使用说明](docs/user-guide.md) · [已知问题](docs/known-issues.md) · [构建说明](docs/build.md)
- [架构概览](docs/architecture.md) · [应用 API](docs/app-api.md) · [原生 API](docs/native-api.md) · [平台 API](docs/platform-api.md)
- [插件开发](docs/plugin-development.md) · [液态玻璃展示页维护](docs/showcase-maintenance.md)
- 欢迎阅读 [参与贡献指南](CONTRIBUTING.md) 并提交问题或改进建议。
- 项目采用 [MIT 许可](LICENSE)。运行依赖与第三方组件的许可和声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与 [licenses](licenses/)。

