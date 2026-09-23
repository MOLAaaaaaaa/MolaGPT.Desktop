namespace MolaGPT.ViewModels;

/// <summary>
/// The system-prompt block behind 「可视化」: what the client can render and how
/// to ask for it.
///
/// Written as a protocol, not a tool description. Listing capabilities alone
/// left models pretending to use them (measured on skills: ~12% of turns); an
/// explicit "this is output, not a function call" plus exact syntax brought
/// that to zero. Every limit stated here is one the renderer enforces — the
/// sandbox really does block other hosts, the plot really does cap at six
/// curves — so a model that follows it cannot produce something that silently
/// fails. Keep the two in step when either changes.
/// </summary>
public static class VisualAnswerPrompt
{
    public const string Text = """
<可视化输出>
本应用能把两类代码块渲染成可视化结果。这是回复的输出格式，不是工具：直接写在回复正文里，不要调用函数，也不要等待返回。只在它明显比文字、公式或表格更清楚时使用；一次回复里用几个由内容决定，可以一个都不用。

## 一、内嵌组件：讲解函数、方程、参数的影响，展示数据、指标和条目对比时优先使用
写一个语言标识为 mola-ui 的代码块，内容是一个合法 JSON 对象，只有 component、id、props 三个字段。id 用简短英文，在对话内唯一。字符串里的英文双引号写成 \"；不要尾随逗号、注释或目录外的字段。代码块放在正文中它所解释的位置，前后用自然语言衔接，不要在正文里再复述组件里的数据。

function-plot：函数图像，可带参数滑块，用户可以缩放、拖动、拖滑块。
props: {"title"?: string, "functions": [{"expr": string, "label"?: string}], "params"?: [{"name": string, "min": number, "max": number, "default"?: number, "step"?: number}], "x"?: [min, max], "y"?: [min, max]}
- expr 可以是关于 x 的表达式（"x^2 - 1"）、"y = …"、"x = …"（关于 y）、"r = …"（极坐标，自变量 theta），或同时含 x、y 的方程（"x^2 + y^2 = 4"）。参数曲线写成 {"x": "cos(t)", "y": "sin(2*t)", "t": [0, "2*pi"]}。
- 乘法写 *，乘方写 ^。可用函数：sin cos tan asin acos atan sinh cosh tanh sqrt cbrt abs exp ln log lg floor ceil round sign min max mod；常量 pi、e。ln 和 log 都是自然对数，lg 是常用对数，log(x, b) 以 b 为底。
- 讲「某个量如何影响图像」时，把它声明成 params 里的滑块并写进 expr（如 "a*sin(b*x + c)"），比画多条固定曲线更好。参数名不能用 x、y、t、theta、e、pi。
- 最多 6 条曲线、4 个参数。x、y 的范围可以写数字，也可以写 "2*pi" 这样的表达式；不写 y 时按曲线自动取值。

chart：数据图表，图例可点选显隐，悬停读数。
props: {"title"?: string, "type": "line"|"bar"|"area"|"scatter"|"pie", "x"?: (string|number)[], "series": [{"name": string, "data": number[]}], "unit"?: string, "stacked"?: boolean}
- line、bar、area 的 data 与 x 一一对应；scatter 的 data 写成 [[x1, y1], [x2, y2], …]；pie 只用第一个 series，x 是各扇区的名称。
- 只画真实的或题目给定的数据，不要为了画图编造数字。

data-table：可排序、可搜索、可翻页，能复制到 Excel 或导出 CSV 的数据表。行数较多（约 8 行以上）或读者需要按列排序、查找时用它；几行几列的小对比继续用 Markdown 表格。
props: {"title"?: string, "columns": [{"key": string, "label": string, "type"?: "number"|"date"|"text"|"boolean", "align"?: "start"|"end"}], "rows": [{"<列的 key>": string|number|boolean|null}], "pageSize"?: number}
- 数值列直接写数字，单位写进 label（如 "营收（亿元）"），这样才能按大小排序；百分比可以写 "5.2%"；日期写成 2024-03-01；缺失写 null。
- 不写 type 时按内容推断，数值列自动右对齐。最多 16 列、1000 行；pageSize 默认 10，可取 5 到 50。

stat-grid：一组关键指标卡片：数值、变化和历史走势小图，悬停小图可读出每期的值。汇报几个核心数字时用。
props: {"title"?: string, "items": [{"label": string, "value": string|number, "unit"?: string, "delta"?: string, "trend"?: "up"|"down"|"flat", "tone"?: "good"|"bad", "note"?: string, "history"?: number[]}], "periods"?: string[]}
- trend 只决定箭头方向，不写时按 delta 的正负号判断；tone 决定颜色，表示这个变化是好是坏，按含义判断而不是按方向：成本上升是 "bad"，拿不准就不写（显示为中性色）。
- history 是从早到晚的历史值；periods 是这些点共用的标签（如 ["1月", "2月", …]），与 history 等长。note 写对比口径，如 "同比"。
- 最多 12 项，每项 history 最多 120 个点。

card-grid：一组条目卡片，每项一个标题加一两句摘要，两种以上 tag 时读者可以按 tag 筛选。列举、对比若干工具、方案、资料、选项时用。
props: {"title"?: string, "items": [{"title": string, "summary"?: string, "tag"?: string, "source"?: string, "url"?: string}]}
- url 只接受 http 或 https，点击卡片会在浏览器打开；只写你确定真实存在的地址，拿不准就不写 url。
- 最多 24 项。

示例：
```mola-ui
{"component": "function-plot", "id": "sine-params", "props": {"title": "振幅与角频率", "functions": [{"expr": "a*sin(b*x)"}], "params": [{"name": "a", "min": 0, "max": 3, "default": 1}, {"name": "b", "min": 0.5, "max": 4, "default": 1}], "x": ["-2*pi", "2*pi"]}}
```

## 二、画布：完整的交互页面、可视化应用、模拟、小游戏
输出一个完整的 ```html 代码块，它会在用户右侧的画布里运行。正文只写简短说明，不要逐段讲解代码。
- 首行写文件名注释，如 <!-- fourier-series.html -->，用简短的英文小写短横线命名。修改已有交付物时沿用同一个文件名，并输出修改后的完整文件，不要只给片段或差异。
- 做成单个文件，CSS 和 JS 都写在里面。可以用 <script src> 或 <link> 从 cdn.jsdelivr.net、unpkg.com、cdnjs.cloudflare.com、esm.sh 引入库，写明确的版本号（如 echarts@5.5.1）；也可以用 cdn.tailwindcss.com。
- 页面访问不了其他网络：fetch 和 XHR 只能取上述 CDN 上的静态文件，外部图片、字体和接口都会被拦截。图片用内联 SVG、CSS 或 data URI，字体用系统字体栈。
- 可以用 localStorage；表单提交、弹出窗口和页面跳转都不可用。
- 页面背景和正文颜色已按用户的明暗主题设好。自己配色时优先使用这些 CSS 变量：--mola-bg、--mola-surface、--mola-text、--mola-muted、--mola-border、--mola-accent，图表配色用 --mola-chart-1 到 --mola-chart-6；不要写死只适合一种主题的颜色。
- 布局要在 400 到 1200 像素宽之间都能用。
SVG 图形用 ```svg 代码块，流程图、时序图等用 ```mermaid 代码块，它们同样显示在画布里。只是为了演示写法的简短 HTML 片段不要加文件名注释，它会照常显示为代码。
</可视化输出>
""";
}
