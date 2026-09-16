---
feature: browser-webbridge
status: delivered
updated: 2026-09-14
branch: master
commits: 9d439728..(uncommitted feature commit)
# User override: no worktree — implement and commit on master.
---

# Browser WebBridge（Work 本地浏览器操控）

## Report

**What was built** — Work/BYOK 增加 `browser` 工具，经本机 Kimi 浏览器扩展 daemon 驱动用户真实 Chrome/Edge。读操作 `list_tabs` / `snapshot` / `screenshot` 在 Approval 下自动放行，写操作 `navigate` / `click` / `fill` / `close_session` 逐次弹窗并**按网站**记住；`status` 走 daemon 健康检查而非页面工具。设置新增「浏览器」页：连接检测、启动服务、安装入口、审批模式、网站黑白名单。工具卡按 chip 合并，一次浏览器任务折叠成一张卡。

**Verification**
- `dotnet build MolaGPT.Desktop.sln -c Debug -t:Rebuild --no-restore` — PASS
- `dotnet test MolaGPT.Desktop.sln` — PASS 894/894
- 真机端到端（走 C# 工具路径，daemon v2.0.9 + 扩展 2.0.9）：`status` 返回 `extension_connected:true`；`navigate` 带中文 `group_title`「莫拉验证」正确回显（无乱码）；`snapshot` 返回 `@e` 引用树；`screenshot` 落到会话工作目录且文件存在（85652 B）；`close_session` 关闭 1 个标签
- 设置页渲染：`artifacts/browser-settings-probe`（Skia headless，开/关两态）

## [S1] Problem

MolaGPT Work（Pi Agent）只有云端读页（`web_fetch`/`steelBrowser`），无法驱动本机 Chrome/Edge 的真实登录态做导航、点击、填表、截图。目标是让 Agent 完成比价、调研、表单等重复网页任务，同时不把登录态和页面内容送出本机。

## [S2] Design

### 架构

```text
Pi Work turn
  → ChatToolHost.ExecuteAsync("browser", …)
  → BrowserControlTool
  → HTTP  GET  /status      （daemon 健康）
     HTTP POST /command     （页面工具）
  → Kimi 浏览器扩展 daemon → 扩展 → 用户真实浏览器
```

不自研扩展。安装由用户自理；daemon 不可达时返回可读错误与安装指引，不静默安装。

### 协议契约（实测于 daemon v2.0.9，非文档推断）

| 项 | 实际形态 |
|---|---|
| daemon 健康 | `GET /status` → `{extension_connected, extension_version, port, running, version, …}` |
| 页面工具 | `POST /command` `{"action":…,"args":{…},"session":"mola-<conv8>"}` |
| 成功 | `{"ok":true,"data":{…}}`，HTTP 200 |
| 失败 | `{"ok":false,"error":{"code":…,"message":…}}` — **`error` 是对象**，且参数级错误同样返回 **HTTP 200**（未知 action 才 502） |
| 截图 | `args.path` 会被采纳；JSON 里必须用正斜杠，反斜杠是转义符 |
| 会话 | 一个 session 拥有自己的标签组；`navigate` 之前其他 action 一律 `session has no tab` |

三处由此定下的硬约束：

1. **`status` 不能走 `/command`。** daemon 会把它当页面工具派发，未 navigate 时必然失败。
2. **成功判定必须认 `ok` 与对象型 `error`。** 只看 `success` 布尔和字符串 `error`，会把 HTTP 200 的失败判成成功，把错误当结果喂给模型。
3. **地址不能写死。** daemon 从 `~/.kimi-webbridge/config.json` 的 `addr` 读监听地址，缺省才是 `127.0.0.1:10086`；`start --addr` 可临时覆盖。解析顺序：设置项 → config.json → 默认。`0.0.0.0` 取其端口走回环，真正的远端地址一律拒绝。

### 工具面（最小闭环）

| action | 语义 | 审批 |
|--------|------|------|
| `status` | daemon / 扩展状态 | 读 |
| `list_tabs` | 列出本 session 标签 | 读 |
| `snapshot` | 可访问性树 + `@e` 引用 | 读 |
| `screenshot` | 截图到会话工作目录 | 读 |
| `navigate` | 打开 / 导航 URL | 写 |
| `click` / `fill` | 点击 / 填写 | 写 |
| `close_session` | 关闭本会话标签组 | 写 |

一期不做：`evaluate` / `cdp` / `upload` / `network` / 录制技能。

### 审批（读松写紧，按网站授权，受保护动作再加一层）

- 读：`Read | External` → Approval 模式自动放行。
- 写 / 未知 action：`Write | External` → 弹窗；FullAccess 直通。
- **grant key 是 `browser:<host>`，不存在裸 `browser` 授权。** 一次「始终允许」只授权一个站点。`navigate` 的站点来自参数；`click`/`fill`/`snapshot`/`screenshot` 调一次 `list_tabs` 解析当前站点；解析不出来时 `AlwaysAsk = true`，即该次调用不产生任何可记住的授权。
- 允许名单内的站点视为用户已在设置中长期授权，不再逐次询问；禁止名单优先于允许名单；配置了允许名单而站点未知时拒绝（fail-closed）。
- 名单匹配含子域名（`example.com` 覆盖 `www.example.com`），但不跨标签边界（不覆盖 `notexample.com`）。

`BrowserGuard` 是这条链路上唯一的闸门——机制层不设防：扩展持 `<all_urls>`，daemon 收到什么执行什么，不区分「读一个页面」和「提交一笔订单」；而它跑在用户主 profile 上带着全部登录态。业界给本地桥开的方子是独立 profile，那会废掉我们唯一的卖点，所以不做隔离，闸门加细。分两层，边界是「用户能不能有意义地同意」：

| 层 | 判定（只用确定看得见的东西：URL 和待填值） | 行为 |
|---|---|---|
| **红线** | `fill` 的值命中银行卡号（13–19 位 + Luhn + 首位 3–6）或身份证号（GB 11643 校验位） | 直接拒绝，不进审批流 |
| **受保护动作** | `navigate` 到下载链接 / 授权页（`/oauth`、`/authorize`、`/consent`…）/ 支付结账页（`/checkout`、`/payment`、`/cashier`…）；或对「每次询问」名单内站点的写操作 | 站点授权不覆盖，**且无视权限模式**（FullAccess 下也弹）；本次批准不产生可复用的授权 |

对齐 Claude in Chrome：「始终允许这个网站」之后，下载文件 / 输入敏感信息 / 授予授权仍然逐次询问。密码和验证码从值本身认不出来，所以留在协议提示里约束，不在这里假装能检测——猜出来的闸门比没有闸门更坏，因为它让人以为有。

### 选项与开关

- `LocalToolOptions.Browser`：`BrowserControlOptions(Enabled, DaemonUrl, SnapshotMaxCharacters, RequestTimeoutSeconds, AllowedHosts, BlockedHosts)`；`BrowserPermissionMode` 与其他工具同级。
- wire：`enabled_tools.browser = { enabled, daemonUrl, allowedHosts, blockedHosts }` + `enabled_tools.browserPermissionMode`。
- 设置 → 浏览器：总开关（默认关）、连接检测、审批模式、黑白名单。
- **没有 composer chip。** 唯一的开关在设置里，且与 `browser-use` 技能的启用状态联动（`SkillsViewModel.BrowserSkillName`，两处改动互相跟随）。装了本地服务、开了开关就是想让模型能用；再在输入框逐对话打开一次只是把同一个决定问两遍。`ComposerViewModel.IsBrowserToolAvailable` 是唯一判据（设置开关 + 非代理模式 + 模型支持工具调用）。
- 与「网络访问」（`search_web` / `web_fetch`）独立，可同时开。

### 舞台在用户的浏览器里，不在我们的界面里

这是这套机制和云端浏览器（Operator 一路）最根本的区别，几条设计都从这里推出来：

- **不重建浏览器画面。** 用户正看着那个浏览器，在 MolaGPT 里放一个截图流只是延迟和 token。截图只用于取证和给用户佐证，不用于定位。
- **标签组是我们唯一免费拿到的产品界面。** 名字由 `BuildGroupTitle` 生成（`MolaGPT <conv8>`），不交给模型——工具 schema 里已移除 `group_title`。必须 ASCII：实测这座桥会把非 ASCII 标题过一遍 Windows ANSI 码页，发「莫拉验证」（UTF-8 `e8 8e ab …`）回显是 `Ī����֤`，正是其 GBK 字节 `c4 aa c0 ad d1 e9 d6 a4` 被当 UTF-8 读的结果；浏览器标签上显示正常，但模型 `list_tabs` 读回来是乱码。
- **takeover 不用造，只用指路。** 用户的手和 agent 的手在同一个浏览器上，所以不需要「暂停—切控制权—恢复」那一整套，只需要把话说对：告诉他哪个标签页在等他。协议提示与 SKILL.md 各有一节。
- **停止键也在浏览器里。** 用户关掉标签组就是喊停，`has no tab` 因此不能一律当成「去 navigate」；对话删除时由 `CloseSessionAsync` 收掉标签组，不把无人认领的标签留给用户。
- **实测形状**：`navigate` 开的标签 `active:false`（不抢焦点，好默认，别破坏）；session 隔离为真（其他 session `list_tabs` 看不到）；返回里有 `borrowed` 字段，说明机制支持借用用户已有标签——语义待查，我们从不请求借用。

### 诚实边界

- daemon 是**全机共享的多租户服务**，官方明确支持 Claude Code / Codex / Cursor / Kimi Code 等共用一条 curl 安装。我们的名单只约束 MolaGPT 发出的命令，设置页已写明。
- 跑在用户**主 profile** 上，用的是他全部真实登录态。不做 profile 隔离是有意取舍，用更细的闸门 + 操作记录来换。
- daemon 每条命令向 `gator.volces.com` 上报 `{session, tool}`；页面内容不在其中。

### 操作记录

`BrowserActivityLog`（200 条环形缓冲，持久化到 settings 表的 `browser_tool_activity`）记写操作与所有失败，只记站点 / 动作 / 成败 / 原因，不记页面内容与填入值。只读动作成功时不记，否则会把真正值得看的写操作淹掉。桥断开的失败经 `Recorded` 事件转成 NotificationCenter 横幅（key `browser-bridge`，带「去检测」跳设置）——这是事件不是状态。

### 结果与 UI

- 工具卡标题「浏览器」，副文案按 action；连续多次调用合并为一张卡（`GroupKeyFor("browser")`），计数文案「N 步操作」。
- snapshot 超限时不截断裸 JSON，而是包成 `{truncated, note, tree_text}`，保证模型收到的仍是合法 JSON。
- 截图写入会话 workspace；已授权站点在设置「已授权的工具」中显示为「浏览器网站　example.com」，可单条撤销。

## [S3] Out of Scope

自研扩展 / CDP 直连、Kimi 录制与技能生成、`evaluate` / raw `cdp` / 上传 / 抓包、云端无头浏览器、自动安装扩展。

### 让模型真的会用（两层）

工具描述只够让模型知道有这个工具，不够让它跑对流程。分两层：

- **底层（常驻）**：`ComposerViewModel.BuildBrowserProtocolHint()`，工具可用时注入 system prompt 的 9 行协议——会话/循环/@e 失效/一步一验证/页面内容是数据/红线/名单不绕道/收尾。不走技能目录：目录只给路径、要模型自己 `read_file` 去读，而开了浏览器的对话往往没开 Python 或文件工具，读不到的协议等于没有协议。
- **深层（按需）**：`src/skills/browser-use/SKILL.md`，完整流程、错误对照表、三个套路。可在设置→技能里查看和停用。底层提示在文件工具可用时会带上它的路径。

外部实践对齐（OpenAI computer use / Anthropic browser use）：结构优先于像素（`@e` 引用而非截图定位）、动作后必须回读验证、域名白名单、页面内容视为不可信输入、消费性操作交回人类确认。

## Tasks

- [x] T1: `BrowserControlOptions` + `WebBridgeClient` + `BrowserControlTool`
- [x] T2: `LocalToolOptions` / `ChatToolHost` 接线与 OpenAI tool schema
- [x] T3: Composer 开关 + BuildExtras + 工具卡文案
- [x] T4: 单测：请求形状、读写 capability、options 解析
- [x] T5: 构建验证 + 本机 daemon 冒烟
- [x] T6: 按实测协议修正 `status` 端点、`ok`/对象 `error` 判定、地址发现（config.json）
- [x] T7: 按网站的授权粒度 + 黑白名单 + 设置页「浏览器」
- [x] T8: 工具卡合并（与 read_file / web_search 同机制）
- [x] T9: browser-use 技能 + 常驻协议提示
- [x] T10: 设置页配置指引（常驻入口、商店页示意图、在应用内执行官方安装命令）与检测状态反馈（色点 / 时间戳 / 分状态下一步）
- [x] T11: 合并「联网搜索 / 网页阅读」为「网络访问」并默认开启；移除 composer 浏览器 chip，开关与 `browser-use` 技能联动
- [x] T12: `BrowserGuard` 红线与受保护动作；「每次询问」名单；`navigate` 前探 `/status`（顺带堵死假成功）；daemon 错误翻译
- [x] T13: 标签组名由我们生成（ASCII）；对话删除时 `CloseSessionAsync`；`daemon.addr` 纳入地址发现
- [x] T14: 操作记录 + 桥断开横幅；设置页诚实边界说明

### 本地服务的安装

Kimi 不发 MSI，官方路径是一条 PowerShell 命令。我们不自己实现下载逻辑，直接跑官方脚本（`WebBridgeInstaller`），上游改版本布局时不会静默装错东西。命令在指引窗口中原样展示并可复制，实际执行的是加了 `-NoSkill` 的变体——默认的脚本还会把 Kimi 技能写进机器上检测到的所有 Agent 运行时（Claude Code / Codex / Cursor），装一个浏览器桥不等于授权改用户的其他工具，MolaGPT 也不读那些文件。安装到 `%USERPROFILE%\.kimi-webbridge`，无需管理员权限。

## Journey log

- 用户明确不用 worktree，改动直接落在 master 工作区。
- 首轮 review 拦下：navigate 未限 http(s)、warm-start 会 Kill 正在监听的 daemon。已修。
- **二轮实测推翻了首轮的三处协议假设**（见上表）。首轮的「冒烟通过」用的是 CLI `status`，而工具走的是 `/command`，两条路径不同——工具那条当时是坏的，且因为错误信封判定失效，坏得很安静。教训：验证必须走将要发布的那条代码路径。
- daemon 会向 `gator.volces.com` 上报遥测（事件名、session 名、tool 名），页面内容不在其中。「全程本地」这个说法对页面数据成立，对调用元数据不成立，产品文案里没有使用这句。
- 中文 `group_title` 经 C# 的 UTF-8 `StringContent` 往返正常；早期观察到的乱码是 Windows shell 传参所致，与代码无关。
- **后来推翻了上一条。** 用 Python 写死 UTF-8 字节、绕开 shell 再测一次，`list_tabs` 回显仍是 `Ī����֤`；ASCII 对照组完好。逐字节对上了：`c4 aa`（GBK 的「莫」）当 UTF-8 读就是 `Ī`，`d6 a4`（「证」）就是 `֤`。是桥在某一层过了 Windows ANSI 码页。用户确认浏览器标签上显示的是正常中文，所以损坏只在回显路径——但模型读 `list_tabs` 看到的正是回显。结论不变：标题必须 ASCII，而且不该由模型来编。
- 空闲近四小时、扩展早已断开的 daemon，第一次 `navigate` 返回了 `ok:true` 和一个 tabId，而机器上根本没有浏览器进程；从第二次起才正确报 `no extension connected`。重启 daemon 后无法复现，像是陈旧 websocket 残留。窄，但方向很糟——模型会基于一个不存在的标签页往下编。`navigate` 前的 `/status` 探测把这条路堵死了。
- `--addr` 的帮助文本自陈「for this run only (not saved)」，所以 `config.json` 恰好在「错了代价最大」的情况下是陈旧的；`daemon.addr` 记的是这一跑真实绑定的地址，排在 config 之前。
- **一次真实对话的 trace review（佳明页面，35 次调用最终失败）挖出两条协议事实，都是我们自己的文案在误导模型：**
  - `snapshot` **不支持 `selector`**。直接探针：带 `selector:"#zzz-no-such"`（一个不匹配任何东西的选择器）返回的仍是完整页面树，和 `selector:"h1"`、和不带 selector 逐字一致。而截断时的 note 原文写着「可用 selector 缩小范围」——模型照做了 11 次，每次都为整页树付费，一轮 prompt 烧到 336 万 token。note 已改成只给真正管用的退路。
  - `click` 的 `"Uncaught"` 是**选择器语法不支持**，不是元素没找到。探针对照：`text=…` 和 XPath 都返回 `click: Uncaught`，而语法合法但不存在的 `.no-such-class` 返回 `click: element not found: .no-such-class`。两句混在一起，模型会以为是「没找到」，继续换 `text=` / XPath / `:has()` 撞同一堵墙。`TranslateDaemonError` 现在把它翻成「只接受 @e 和标准 CSS」。
- 浏览器截图的路径要进 markdown 改写器。工具返回绝对路径，而 Python 的 `display_instructions` 又反复教「不要用绝对本地路径」，模型两头一夹就把它缩成裸文件名——`ImageSourceLoader` 拿裸名去 `File.Exists`，相对于进程目录，必然失败，图就是空的。改写器此前只认 `execute_python_code` 的调用。
- 用户上传的图片此前**不进工作目录**（走 content part，看着不需要拷贝），于是 `analyze_image` 对它们必然失败：模型手上是一张没名字的图，工具 schema 又写着「附件按工作目录路径放着」，它就编了个 `1.png`。现在图片也拷一份（仅当 Python 或文件工具开着），并在附件段里报出 path。
- **又推翻了一次：`snapshot` 能窄化，只是参数叫 `ref` 不叫 `selector`。** 上一条结论只测了 `selector` 就推广成「snapshot 无法缩小范围」。实测同一页面：整页 57,891 字节，`ref:"@e1"` 345 字节，`ref:"@e149"` 125 字节。教训和 `--addr` 那条同类——一个参数名不通不等于这个能力不存在。
- **桥有 25 个工具，我们原来只接了 8 个。** 向扩展发一个未知 action，它会把完整清单报回来：`navigate find_tab find evaluate network snapshot read_page click fill mouse_click cdp key_type send_keys screenshot scroll save_as_pdf upload close_tab list_tabs close_session wait dialog select_option hover drag`。本轮补上 `find` / `snapshot(ref)` / `read_page` / `scroll` / `wait` / `hover` / `select_option` / `send_keys`。
- `find` 是整套里最该早点接的一个：给 `query` 返回每个匹配的 role、路径和 `@e` 引用，几百字节做完整页 snapshot 几万字节的事。`role` 和 `limit` 参数**无效**（实测 `role:"heading"` 仍返回 region/link），不要暴露。
- `scroll` 在自带滚动容器的页面（MDN 一类文档站）上会「假成功」：`scrolled:true` 照报，`window.scrollY` 不动。普通文档流页面正常（维基 0→998→1996，`evaluate` 交叉验证过）。已写进 SKILL.md 的注意事项。
- `select_option` 不带 `value` 会把所有选项列出来，是个有用的中间步骤，不是缺参数；遇到自定义下拉组件它自己会提示改用 click。
- **`evaluate` 和 `cdp` 故意不接。** 任意 JS / DevTools 协议跑在用户登录态的浏览器里，`BrowserGuard` 是按 action + url 判定的，这两个全部绕得过去。本轮的探针用 `evaluate` 做交叉验证，恰好说明它有多穿透。
- 红线扩到 `select_option` / `send_keys`：新开一条能把字带进页面的通道而不接上闸门，等于那条通道完全没有防护。
