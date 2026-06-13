---
name: docx
description: 凡是涉及 Word 文档（.docx）的任务都使用本技能——创建报告/简历/合同/说明文档，读取或提取现有 .docx 的文字，修改已有文档（追加段落、改文字、加表格图片）。只要用户提到「Word」「文档」「.docx」「报告」并希望产出可下载的 Word 文件，就触发本技能。用本地 python-docx 实现，无需联网。
---

# DOCX 技能（python-docx）

用本地 `python-docx` 创建和读取 Word 文档。产物存到**当前工作目录**，用相对文件名（如 `report.docx`）。

## 推荐：用内置构建脚本

`scripts/docx_helpers.py` 的 `Doc` 已处理中文字体、标题层级、列表、表格：

```python
import sys
sys.path.append(r"<本技能目录>/scripts")
from docx_helpers import Doc

d = Doc(cjk_font="微软雅黑")
d.title("项目报告")
d.heading("一、概述", 1)
d.para("正文段落。")
d.bullets(["要点一", "要点二"])
d.table(["名称", "数量", "金额"], [["苹果", "3", "￥9"]])
d.save("report.docx")
print("已生成 report.docx")
```

需要更细控制时，用下面的原生 API。

## 读取内容

```python
from docx import Document
doc = Document("input.docx")
for p in doc.paragraphs:
    if p.text.strip():
        print(p.text)
for t_i, table in enumerate(doc.tables):
    print(f"--- 表格 {t_i+1} ---")
    for row in table.rows:
        print(" | ".join(cell.text for cell in row.cells))
```

## 从零创建

```python
from docx import Document
from docx.shared import Pt, Inches, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH

doc = Document()

# 标题
h = doc.add_heading("项目报告", level=0)

# 一级标题 + 正文
doc.add_heading("一、概述", level=1)
p = doc.add_paragraph("这里是正文。")
run = p.add_run(" 这是加粗强调。"); run.bold = True

# 项目符号 / 编号列表
doc.add_paragraph("要点一", style="List Bullet")
doc.add_paragraph("步骤一", style="List Number")

# 表格
table = doc.add_table(rows=1, cols=3)
table.style = "Light Grid Accent 1"
hdr = table.rows[0].cells
hdr[0].text, hdr[1].text, hdr[2].text = "名称", "数量", "金额"
for name, qty, amount in [("苹果", "3", "￥9")]:
    cells = table.add_row().cells
    cells[0].text, cells[1].text, cells[2].text = name, qty, amount

# 插入图片（先用 matplotlib 出图存 PNG，再插入）
# doc.add_picture("chart.png", width=Inches(5.5))

doc.save("report.docx")
print("已生成 report.docx")
```

要点：
- 标题层级用 `add_heading(text, level=0..9)`，0 是文档主标题。
- 字体/字号：`run.font.size = Pt(12)`、`run.font.color.rgb = RGBColor(0x1E,0x27,0x61)`。
- 对齐：`paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER`。
- 中文字体：`run.font.name = "微软雅黑"`；如需确保东亚字体生效，设置 `run._element.rPr.rFonts.set(qn('w:eastAsia'), '微软雅黑')`（`from docx.oxml.ns import qn`）。
- 分页：`doc.add_page_break()`。
- 列表样式名（内置）：`List Bullet`、`List Number`；表格样式如 `Light Grid Accent 1`、`Table Grid`。

## 修改现有文档

```python
from docx import Document
doc = Document("input.docx")
doc.add_heading("追加章节", level=1)
doc.add_paragraph("追加的内容。")
doc.save("input-updated.docx")   # 另存，避免覆盖原件
```

## 质量自检

生成后用上面的读取代码把全文打印出来核对：标题层级、表格内容、错别字、占位文字。中文字体未设时可能在部分阅读器里显示不一致，重要文档显式设置中文字体。
