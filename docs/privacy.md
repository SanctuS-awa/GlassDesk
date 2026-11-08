# 隐私与本地处理

适用于 GlassDesk 0.2.2 正式版的内置功能。第三方插件在主进程运行，可以执行自己的代码；下面的边界不能替第三方插件作保证。

## 音乐与音频可视化

应用通过 Windows 系统媒体会话读取准确匹配的网易云来源，展示曲名、歌手、封面与播放状态，并提交可用的播放控制。不提供网易云账号登录，不保存歌曲历史。

音频波浪条默认开启。程序可靠识别网易云进程后，以 Windows 进程树 loopback 处理该来源的输出，用于本地频谱分析；不使用麦克风，不保存音频文件。无法可靠绑定来源时停止采集，不改用其他应用或全系统音频。暂停、隐藏或波浪条不可见时停止音频采集。

相关实现：[音频来源与生命周期](../src/GlassDesk.Platform/ProcessAudioVisualizationSource.cs)、[进程树 loopback](../native/GlassDesk.Native/ProcessAudio.cpp)、[显示条件](../src/GlassDesk.App/GlassDeskCoordinator.cs)。

## 显示器背景与液态玻璃

液态玻璃是可选插件，默认关闭。启用后，桌面背景采样和折射在本机 GPU 图形管线中处理；应用不会上传桌面画面。

## 天气与定位

启用天气后，应用会将手动选择城市对应的近似经纬度，或自动定位得到的近似经纬度，发送给 Open-Meteo（`api.open-meteo.com`）获取天气。自动定位优先使用 Windows 定位服务；需在 Windows 设置中允许桌面应用访问位置。定位服务不可用时，应用会向 `myip.ipip.net` 与 `ipwho.is` 查询基于 IP 的大致位置。相关服务会收到网络连接所需的来源 IP；HTTP 请求还带有应用标识 `GlassDesk/0.2.2`。应用不会发送账号、歌曲历史或其他设备数据。

天气缓存保存在 `%LocalAppData%\GlassDesk\weather-cache.json`。可在“设置 → 天气”中关闭天气。

## 偏好与诊断

设置、插件与插件配置默认保存在 `%LocalAppData%\GlassDesk`，卸载默认保留。没有在线插件市场或自动上传诊断服务。

诊断在内存中最多保留 128 条系统、能力、错误类型与错误码等记录。点击“导出诊断”才会写出诊断 JSON；不会写入歌曲历史、账号或任意异常消息正文。系统版本等环境信息仍可能具有识别性，公开发送前请自行检查。

相关实现：[Diagnostics](../src/GlassDesk.Platform/Diagnostics.cs)。内存记录与手动导出是两个步骤，不能将“手动导出”理解为应用运行期间完全不记录错误。
