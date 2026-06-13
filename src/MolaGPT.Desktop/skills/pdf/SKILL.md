---
name: pdf
description: 凡是涉及 PDF 文件的任务都使用本技能——生成 PDF（报告/发票/证书/说明），从现有 PDF 提取文字，合并/拆分/旋转/裁剪页面，给 PDF 加水印或元数据。只要用户提到「PDF」「导出 PDF」「合并 PDF」「拆分 PDF」「提取 PDF 文字」就触发本技能。用本地 fpdf2（生成）+ pypdf（读取/处理）实现，无需联网。
---

# PDF 技能（fpdf2 + pypdf）

产物存到**当前工作目录**，用相对文件名（如 `output.pdf`）。

## 推荐：用内置脚本

`scripts/pdf_helpers.py`：`CJKReport` 自动注册中文字体生成 PDF；`extract_text/merge/split/extract_range` 处理现有 PDF：

```python
import sys
sys.path.append(r"<本技能目录>/scripts")
from pdf_helpers import CJKReport, extract_text, merge, split

r = CJKReport()
r.h1("项目报告")
r.body("正文段落，自动换行。")
r.table(["名称", "数量", "金额"], [["苹果", "3", "￥9"]])
r.save("output.pdf")

# 读取： pages = extract_text("input.pdf")   # 每页一段文字
# 合并： merge(["a.pdf", "b.pdf"], "merged.pdf")
# 拆分： split("input.pdf")                   # 每页一个文件
```

需要更细控制（自定义排版、表单、旋转）时用下面的原生 API。

## 生成 PDF（fpdf2）

```python
from fpdf import FPDF

pdf = FPDF(format="A4")
pdf.set_auto_page_break(auto=True, margin=15)
pdf.add_page()

# 中文：fpdf2 内置字体不含中文，需注册系统中文字体（Windows 自带微软雅黑）
import os
font_path = r"C:\Windows\Fonts\msyh.ttc"
has_cjk = os.path.exists(font_path)
if has_cjk:
    pdf.add_font("yahei", "", font_path)
    pdf.set_font("yahei", size=20)
else:
    pdf.set_font("Helvetica", size=20)   # 仅 ASCII

pdf.cell(0, 12, "项目报告" if has_cjk else "Project Report", new_x="LMARGIN", new_y="NEXT")
pdf.set_font("yahei" if has_cjk else "Helvetica", size=12)
pdf.multi_cell(0, 8, "这是正文段落，multi_cell 会自动换行。" if has_cjk else "Body text here.")

# 表格（简易）
pdf.ln(4)
col_w = [60, 40, 40]
for h, w in zip(["名称", "数量", "金额"], col_w):
    pdf.cell(w, 8, h, border=1)
pdf.ln()
for name, qty, amount in [("苹果", "3", "9.0")]:
    for v, w in zip([name, qty, amount], col_w):
        pdf.cell(w, 8, v, border=1)
    pdf.ln()

pdf.output("output.pdf")
print("已生成 output.pdf")
```

要点：
- **中文必须注册 TTF/TTC 字体**（`add_font` + `set_font`），否则中文会乱码或报错；Windows 用 `C:\Windows\Fonts\msyh.ttc`（微软雅黑）或 `simhei.ttf`。
- `cell(w, h, text, border=, new_x=, new_y=)` 单行；`multi_cell(w, h, text)` 自动换行。
- 插图：`pdf.image("chart.png", x=10, y=None, w=180)`。
- 用 matplotlib 出图存 PNG 再 `image()` 插入。

## 读取/提取文字（pypdf）

```python
from pypdf import PdfReader
reader = PdfReader("input.pdf")
print("页数：", len(reader.pages))
print("元数据：", reader.metadata)
for i, page in enumerate(reader.pages, 1):
    text = page.extract_text() or ""
    print(f"--- 第 {i} 页 ---")
    print(text)
```

> 提取文字依赖 PDF 内含文本层；扫描件（纯图片）提不出文字，需 OCR（本环境未装 OCR，遇到时如实告知用户）。

## 合并 / 拆分 / 旋转（pypdf）

```python
from pypdf import PdfReader, PdfWriter

# 合并
writer = PdfWriter()
for f in ["a.pdf", "b.pdf"]:
    for page in PdfReader(f).pages:
        writer.add_page(page)
with open("merged.pdf", "wb") as out:
    writer.write(out)

# 拆分：每页一个文件
reader = PdfReader("input.pdf")
for i, page in enumerate(reader.pages, 1):
    w = PdfWriter(); w.add_page(page)
    with open(f"page_{i}.pdf", "wb") as out:
        w.write(out)

# 提取页码范围 2-4
w = PdfWriter()
for page in reader.pages[1:4]:
    w.add_page(page)
with open("sub.pdf", "wb") as out:
    w.write(out)

# 旋转某页 90°
page = reader.pages[0]; page.rotate(90)
```

## 自检
- 生成后用 pypdf 读回，确认页数、能提取到预期文字。
- 中文文档：确认已注册中文字体且没有 `Helvetica` 残留导致的乱码。
