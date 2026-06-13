# MolaGPT 内置技能（Built-in Skills）

本目录是 MolaGPT 随应用分发的内置 Agent Skills。每个技能是一个文件夹：
`SKILL.md`（YAML 头 `name`/`description` + 正文）+ 可选 `scripts/`、`references/`。
模型先看到 `name`/`description`（注入系统提示词），需要时用本地 Python 工具
（`execute_python_code`）读取 `SKILL.md` 全文与脚本并执行——即 Agent Skills 的
"渐进式披露"。

## 内置清单

| 技能 | 用途 | 主要依赖（已随内置 Python 环境安装） |
|------|------|----------------------------------|
| pptx | 创建/读取 PowerPoint | python-pptx |
| docx | 创建/读取 Word | python-docx |
| xlsx | 创建/读取 Excel（公式、图表） | openpyxl / xlsxwriter |
| pdf | 生成/提取/合并/拆分 PDF | fpdf2 / pypdf |
| data-analysis | 数据清洗、统计、可视化出图 | pandas / matplotlib / seaborn |
| web-scrape | 抓取网页、转 Markdown、提取表格（需联网） | requests / beautifulsoup4 / markdownify |

## 关于来源与开源协议

这些技能由 MolaGPT **原创编写（净室实现）**，在结构与流程上**借鉴**了
Anthropic / OpenAI / 社区公开的 Agent Skills 实践与开放标准
（[agentskills.io](https://agentskills.io)、[anthropics/skills](https://github.com/anthropics/skills)、
[openai/skills](https://github.com/openai/skills)），但**未照抄**其受限许可的文本或代码：

- Anthropic 官方 office 技能为专有许可（© Anthropic, PBC, All rights reserved），
  且其创建主路径依赖 Node(pptxgenjs/html2pptx) 与 LibreOffice——本环境均不具备，
  故我们用纯 Python 库重写了机制，仅在思路层面参考。
- 因此本目录内容可随 MolaGPT 自由分发。

## 自定义技能

用户可在「设置 → 技能」导入自己的技能（.zip 内含 `SKILL.md`，或文件夹），
导入后存放在 `%LocalAppData%/MolaGPT/skills/`，与内置技能并列出现在列表中。
编写规范见上述 Agent Skills 开放标准。
