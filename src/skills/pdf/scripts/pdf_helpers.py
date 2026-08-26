"""pdf_helpers.py — 可复用的 PDF 生成/处理（MolaGPT 内置技能）

生成用 fpdf2（CJKReport 已注册中文字体），读取/合并/拆分用 pypdf。

用法：
    import sys; sys.path.append(r"<技能目录>/scripts")
    from pdf_helpers import CJKReport, extract_text, merge, split
    r = CJKReport()
    r.h1("项目报告"); r.body("正文段落，自动换行。")
    r.table(["名称","数量","金额"], [["苹果","3","￥9"]])
    r.save("output.pdf")
"""

import os
from fpdf import FPDF

# Windows 常见中文字体候选（按优先级）
_CJK_FONTS = [
    r"C:\Windows\Fonts\msyh.ttc",   # 微软雅黑
    r"C:\Windows\Fonts\msyh.ttf",
    r"C:\Windows\Fonts\simhei.ttf", # 黑体
    r"C:\Windows\Fonts\simsun.ttc", # 宋体
]


def _find_cjk_font():
    for p in _CJK_FONTS:
        if os.path.exists(p):
            return p
    return None


class CJKReport(FPDF):
    """带中文字体的 A4 报告。无中文字体时回退 Helvetica（仅 ASCII）。"""

    def __init__(self, format="A4"):
        super().__init__(format=format)
        self.set_auto_page_break(auto=True, margin=15)
        font_path = _find_cjk_font()
        self.cjk = bool(font_path)
        if self.cjk:
            self.add_font("cjk", "", font_path)
            self.add_font("cjk", "B", font_path)  # 同一文件当粗体，避免缺字
            self.base = "cjk"
        else:
            self.base = "Helvetica"
        self.add_page()
        self.set_font(self.base, size=12)

    def h1(self, text):
        self.set_font(self.base, "B", 20)
        self.multi_cell(0, 12, text, new_x="LMARGIN", new_y="NEXT")
        self.ln(2)
        self.set_font(self.base, size=12)

    def h2(self, text):
        self.set_font(self.base, "B", 15)
        self.multi_cell(0, 10, text, new_x="LMARGIN", new_y="NEXT")
        self.ln(1)
        self.set_font(self.base, size=12)

    def body(self, text):
        self.set_font(self.base, size=12)
        self.multi_cell(0, 8, text, new_x="LMARGIN", new_y="NEXT")
        self.ln(1)

    def table(self, header, rows, col_widths=None):
        n = len(header)
        if col_widths is None:
            usable = self.w - self.l_margin - self.r_margin
            col_widths = [usable / n] * n
        self.set_font(self.base, "B", 11)
        for h, w in zip(header, col_widths):
            self.cell(w, 8, str(h), border=1)
        self.ln()
        self.set_font(self.base, size=11)
        for row in rows:
            for v, w in zip(row, col_widths):
                self.cell(w, 8, str(v), border=1)
            self.ln()
        self.ln(2)

    # 插图直接用 FPDF 自带方法：report.image("chart.png", w=180)

    def save(self, path="output.pdf"):
        self.output(path)
        return path


# ---- 读取 / 处理（pypdf）----

def extract_text(path):
    from pypdf import PdfReader
    reader = PdfReader(path)
    pages = []
    for page in reader.pages:
        pages.append(page.extract_text() or "")
    return pages  # 每页一段文字；扫描件可能为空（需 OCR，本环境未装）


def merge(input_paths, output_path):
    from pypdf import PdfReader, PdfWriter
    writer = PdfWriter()
    for f in input_paths:
        for page in PdfReader(f).pages:
            writer.add_page(page)
    with open(output_path, "wb") as out:
        writer.write(out)
    return output_path


def split(input_path, out_dir="."):
    from pypdf import PdfReader, PdfWriter
    reader = PdfReader(input_path)
    outs = []
    for i, page in enumerate(reader.pages, 1):
        w = PdfWriter(); w.add_page(page)
        p = os.path.join(out_dir, f"page_{i}.pdf")
        with open(p, "wb") as out:
            w.write(out)
        outs.append(p)
    return outs


def extract_range(input_path, start, end, output_path):
    """提取 1 基页码 [start, end]（含）。"""
    from pypdf import PdfReader, PdfWriter
    reader = PdfReader(input_path)
    w = PdfWriter()
    for page in reader.pages[start - 1:end]:
        w.add_page(page)
    with open(output_path, "wb") as out:
        w.write(out)
    return output_path
