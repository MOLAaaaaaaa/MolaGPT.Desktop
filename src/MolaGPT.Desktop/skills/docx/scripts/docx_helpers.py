"""docx_helpers.py — 可复用的 python-docx 文档构建（MolaGPT 内置技能）

用法：
    import sys; sys.path.append(r"<技能目录>/scripts")
    from docx_helpers import Doc
    d = Doc(cjk_font="微软雅黑")
    d.title("项目报告")
    d.heading("一、概述", 1)
    d.para("正文段落。", )
    d.bullets(["要点一", "要点二"])
    d.table(["名称","数量","金额"], [["苹果","3","￥9"]])
    d.save("report.docx")
"""

from docx import Document
from docx.shared import Pt, Inches, RGBColor
from docx.oxml.ns import qn
from docx.enum.text import WD_ALIGN_PARAGRAPH


class Doc:
    def __init__(self, template=None, cjk_font="微软雅黑"):
        self.doc = Document(template) if template else Document()
        self.cjk = cjk_font

    def _cjk_run(self, run):
        run.font.name = self.cjk
        try:
            rPr = run._element.get_or_add_rPr()
            rFonts = rPr.get_or_add_rFonts()
            rFonts.set(qn("w:eastAsia"), self.cjk)
        except Exception:
            pass

    def title(self, text):
        h = self.doc.add_heading(text, level=0)
        for r in h.runs:
            self._cjk_run(r)
        return h

    def heading(self, text, level=1):
        h = self.doc.add_heading(text, level=level)
        for r in h.runs:
            self._cjk_run(r)
        return h

    def para(self, text, bold=False, size=None, align=None, color=None):
        p = self.doc.add_paragraph()
        if align == "center":
            p.alignment = WD_ALIGN_PARAGRAPH.CENTER
        elif align == "right":
            p.alignment = WD_ALIGN_PARAGRAPH.RIGHT
        r = p.add_run(text)
        r.bold = bold
        if size:
            r.font.size = Pt(size)
        if color:
            r.font.color.rgb = RGBColor.from_string(color)
        self._cjk_run(r)
        return p

    def bullets(self, items, numbered=False):
        style = "List Number" if numbered else "List Bullet"
        for it in items:
            p = self.doc.add_paragraph(style=style)
            r = p.add_run(str(it))
            self._cjk_run(r)

    def table(self, header, rows, style="Light Grid Accent 1"):
        t = self.doc.add_table(rows=1, cols=len(header))
        try:
            t.style = style
        except Exception:
            t.style = "Table Grid"
        for c, h in enumerate(header):
            cell = t.rows[0].cells[c]
            cell.text = ""
            r = cell.paragraphs[0].add_run(str(h)); r.bold = True
            self._cjk_run(r)
        for row in rows:
            cells = t.add_row().cells
            for c, v in enumerate(row):
                cells[c].text = ""
                r = cells[c].paragraphs[0].add_run(str(v))
                self._cjk_run(r)
        return t

    def image(self, path, width_inches=5.5):
        return self.doc.add_picture(path, width=Inches(width_inches))

    def page_break(self):
        self.doc.add_page_break()

    def save(self, path="report.docx"):
        self.doc.save(path)
        return path
