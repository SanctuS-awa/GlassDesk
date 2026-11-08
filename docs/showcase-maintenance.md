# 液态玻璃展示页维护

## 文件位置

| 文件 | 用途 |
| --- | --- |
| [index.html](index.html) | 单文件展示页，内联 CSS、程序化壁纸、WebGL2 面板和 L2/L3 叠层着色器 |
| [showcase/record.js](showcase/record.js) | 用 Puppeteer-core 逐帧调用 renderAt，并以 ffmpeg 输出 GIF 与首帧 PNG |
| [images/liquid-morph.gif](images/liquid-morph.gif) | 胶囊、小展开与首页连续变形演示 |
| [images/liquid-drag.gif](images/liquid-drag.gif) | 大面板移动及叠层模糊演示 |
| [images/liquid-peek.gif](images/liquid-peek.gif) | 小展开、眼神轨迹、眨眼与播放按钮演示 |
| [images/liquid-*.png](images/) | 对应 GIF 的静态首帧，供减少动态效果的访客查看 |

网页只使用自绘背景、系统字体和内联代码，不请求网络资源。演示曲目、天气、日期和系统状态均为虚构数据，不连接本机或播放器。

## 本地预览

直接用浏览器打开 index.html 即可。需要测试捕获画面时，在 URL 后加场景参数：

- <code>index.html?capture=morph</code>：只显示 720 × 450 的变形画面。
- <code>index.html?capture=drag</code>：只显示 720 × 450 的拖动画面。
- <code>index.html?capture=peek</code>：只显示 560 × 320 的小展开画面。

浏览器支持 WebGL2 时由着色器绘制玻璃；不支持时会显示 CSS backdrop-filter 磨砂降级，并提示当前使用降级材质。页面中的三个按钮用于选择胶囊、小展开和大展开；悬停组件会进入小展开，点击组件会进入大展开，拖动主体可观察背景折射。

## 动画场景

| 场景 | 时长 | 画面 |
| --- | ---: | --- |
| morph | 6 秒 | 胶囊停留约 1 秒，之后依次展开到小展开和首页，再收回胶囊；尺寸和圆角用弹簧曲线连续插值 |
| drag | 4 秒 | 首页在背景上左右移动，突出边缘折射和卡片叠层 |
| peek | 3 秒 | 小展开中的陪伴球沿固定轨迹看向鼠标并眨一次眼，播放按钮在场景中按下 |

系统启用 prefers-reduced-motion 时，网页停止背景漂移，并让形态转换直接切换。

## 确定性渲染接口

页面加载完成后提供：

<pre><code>window.GlassDeskShowcase.renderAt(t, scene)
window.GlassDeskShowcase.status()</code></pre>

t 是秒数；scene 使用 morph、drag 或 peek。给定同一时间、场景和浏览器像素比例时，背景运动、形状状态、眼神、眨眼与按钮状态由 t 决定。录制场景不读取当前时间或随机数。渲染器通过 window.devicePixelRatio 计算物理像素尺寸。

浏览器控制台示例：

<pre><code>window.GlassDeskShowcase.renderAt(1.25, "morph")</code></pre>

## 重新生成 GIF

仓库工作目录不包含 node_modules。准备 Node.js、Puppeteer-core 和 ffmpeg 后，在仓库根目录运行：

<pre><code>node docs/showcase/record.js</code></pre>

当前录制环境默认从 <code>/workspace/gd0213/gifkit/node_modules/puppeteer-core</code> 加载 Puppeteer-core。脚本也会尝试常规模块解析；可用 <code>PUPPETEER_CORE_PATH</code> 指定其他安装位置。浏览器默认使用 <code>/usr/bin/google-chrome</code>，可用 <code>GLASSDESK_CHROME</code> 或 <code>PUPPETEER_EXECUTABLE_PATH</code> 指向 Chrome/Edge。<code>FFMPEG_PATH</code> 可指定 ffmpeg 可执行文件。

在 Windows PowerShell 中设置本机浏览器路径后运行：

<pre><code>$env:GLASSDESK_CHROME = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
node docs/showcase/record.js</code></pre>

脚本输出以下文件：morph 为 720 × 450、15 fps、6 秒；drag 为 720 × 450、15 fps、4 秒；peek 为 560 × 320、15 fps、3 秒。每个场景同时输出同尺寸首帧 PNG。录制帧放在系统临时目录，完成后删除；GIF 编码会按需减少调色板颜色数，尽量将单个文件压到 4 MB 以内。

