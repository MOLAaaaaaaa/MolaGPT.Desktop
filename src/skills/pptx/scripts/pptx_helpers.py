"""
pptx_helpers.py — 可复用的 python-pptx 幻灯片构建模块（MolaGPT 内置技能）

设计目标：模型 import 本模块后，用少量调用就能产出"有设计感"的幻灯片，
而不必每次手写坐标与配色。集中管理配色、字体、版式。

用法（在当前工作目录执行）：
    import sys; sys.path.append(r"<技能目录>/scripts")
    from pptx_helpers import Deck
    d = Deck(palette="midnight")
    d.title_slide("2024 年度回顾", "增长、产品与展望")
    d.bullets("三大成果", ["营收 +38%", "用户破百万", "新市场落地"])
    d.stat_callouts("关键数字", [("+38%", "营收同比"), ("1.2M", "活跃用户"), ("4", "新市场")])
    d.save("output.pptx")

也可基于品牌模板（推荐做法）：把一个 .pptx 放进技能目录，传 template=路径，
新页会继承其母版/字体/Logo：
    d = Deck(template="brand.pptx")
"""

from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.enum.shapes import MSO_SHAPE
from pptx.oxml.ns import qn

# 6 套贴主题的专业配色（不带 #）。primary 主导，secondary 辅助，accent 强调。
PALETTES = {
    "midnight":   {"primary": "1E2761", "secondary": "CADCFC", "accent": "4F86F7", "bg_dark": "16203A", "bg_light": "F4F7FE", "text_dark": "1A1A2E", "text_light": "FFFFFF"},
    "forest":     {"primary": "2C5F2D", "secondary": "97BC62", "accent": "E8A33D", "bg_dark": "1E3D1F", "bg_light": "F5F7F0", "text_dark": "1B2E1B", "text_light": "FFFFFF"},
    "coral":      {"primary": "F96167", "secondary": "F9E795", "accent": "2F3C7E", "bg_dark": "2F3C7E", "bg_light": "FFF6F0", "text_dark": "2B2B2B", "text_light": "FFFFFF"},
    "ocean":      {"primary": "065A82", "secondary": "1C7293", "accent": "21C0A8", "bg_dark": "0A2A3B", "bg_light": "EFF6F9", "text_dark": "10242E", "text_light": "FFFFFF"},
    "charcoal":   {"primary": "36454F", "secondary": "B0BEC5", "accent": "FF7043", "bg_dark": "212121", "bg_light": "F5F5F5", "text_dark": "1C1C1C", "text_light": "FFFFFF"},
    "berry":      {"primary": "6D2E46", "secondary": "A26769", "accent": "D4A373", "bg_dark": "3E1B2A", "bg_light": "FBF4EF", "text_dark": "2A1620", "text_light": "FFFFFF"},
}

# 字体预设：header 有个性，body 干净。中文统一回退到雅黑/黑体。
FONT_HEADER = "Microsoft YaHei"
FONT_BODY = "Microsoft YaHei"

EMU_W, EMU_H = 13.333, 7.5  # 16:9 英寸


def _hex(c):
    return RGBColor.from_string(c)


def _set_run_font(run, name):
    """设置一个 run 的字体，并同时写入东亚字体(ea)，确保中文不回退到默认字体。"""
    run.font.name = name
    try:
        rPr = run.font._rPr  # a:rPr
        for tag in ("a:latin", "a:ea", "a:cs"):
            el = rPr.find(qn(tag))
            if el is None:
                el = rPr.makeelement(qn(tag), {})
                rPr.append(el)
            el.set("typeface", name)
    except Exception:
        pass  # 字体名已设到 latin，绝大多数情况下足够


class Deck:
    def __init__(self, palette="midnight", template=None):
        self.prs = Presentation(template) if template else Presentation()
        if not template:
            self.prs.slide_width = Inches(EMU_W)
            self.prs.slide_height = Inches(EMU_H)
        self.pal = PALETTES.get(palette, PALETTES["midnight"])

    # ---- 基础 ----
    def _blank(self):
        # 版式 6 通常是空白；越界则取最后一个。
        layouts = self.prs.slide_layouts
        idx = 6 if len(layouts) > 6 else len(layouts) - 1
        return self.prs.slides.add_slide(layouts[idx])

    def set_bg(self, slide, hex_color):
        slide.background.fill.solid()
        slide.background.fill.fore_color.rgb = _hex(hex_color)

    def _box(self, slide, left, top, width, height, anchor=MSO_ANCHOR.TOP):
        tb = slide.shapes.add_textbox(Inches(left), Inches(top), Inches(width), Inches(height))
        tf = tb.text_frame
        tf.word_wrap = True
        tf.vertical_anchor = anchor
        return tf

    def _line(self, tf, text, size, color, bold=False, align=PP_ALIGN.LEFT,
              font=FONT_BODY, first=False, space_after=6):
        p = tf.paragraphs[0] if first and not tf.paragraphs[0].runs else tf.add_paragraph()
        p.alignment = align
        p.space_after = Pt(space_after)
        run = p.add_run()
        run.text = text
        run.font.size = Pt(size)
        run.font.bold = bold
        run.font.color.rgb = _hex(color)
        _set_run_font(run, font)
        return p

    def _accent_chip(self, slide, left, top):
        """强调小色块（视觉母题），代替"标题下划线"这种 AI 味十足的做法。"""
        shp = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(left), Inches(top), Inches(0.55), Inches(0.12))
        shp.fill.solid(); shp.fill.fore_color.rgb = _hex(self.pal["accent"])
        shp.line.fill.background()
        return shp

    # ---- 幻灯片类型 ----
    def title_slide(self, title, subtitle=None):
        s = self._blank()
        self.set_bg(s, self.pal["bg_dark"])
        self._accent_chip(s, 1.0, 2.35)
        tf = self._box(s, 1.0, 2.6, 11.3, 2.4)
        self._line(tf, title, 46, self.pal["text_light"], bold=True, font=FONT_HEADER, first=True)
        if subtitle:
            self._line(tf, subtitle, 20, self.pal["secondary"], space_after=0)
        return s

    def section(self, text, number=None):
        s = self._blank()
        self.set_bg(s, self.pal["primary"])
        tf = self._box(s, 1.0, 2.8, 11.3, 2.0, anchor=MSO_ANCHOR.MIDDLE)
        if number:
            self._line(tf, str(number), 22, self.pal["accent"], bold=True, font=FONT_HEADER, first=True)
            self._line(tf, text, 40, self.pal["text_light"], bold=True, font=FONT_HEADER)
        else:
            self._line(tf, text, 40, self.pal["text_light"], bold=True, font=FONT_HEADER, first=True)
        return s

    def _content_header(self, slide, title):
        self.set_bg(slide, self.pal["bg_light"])
        self._accent_chip(slide, 0.9, 0.75)
        tf = self._box(slide, 0.9, 0.95, 11.5, 1.0)
        self._line(tf, title, 32, self.pal["primary"], bold=True, font=FONT_HEADER, first=True)

    def bullets(self, title, items):
        s = self._blank()
        self._content_header(s, title)
        tf = self._box(s, 0.95, 2.1, 11.4, 4.8)
        for i, it in enumerate(items):
            self._line(tf, "•  " + str(it), 18, self.pal["text_dark"], first=(i == 0), space_after=12)
        return s

    def two_column(self, title, left_items, right_items, left_head=None, right_head=None):
        s = self._blank()
        self._content_header(s, title)
        for items, head, x in ((left_items, left_head, 0.95), (right_items, right_head, 6.9)):
            tf = self._box(s, x, 2.1, 5.4, 4.8)
            first = True
            if head:
                self._line(tf, head, 20, self.pal["accent"], bold=True, font=FONT_HEADER, first=True, space_after=10)
                first = False
            for it in items:
                self._line(tf, "•  " + str(it), 17, self.pal["text_dark"], first=first, space_after=10)
                first = False
        return s

    def stat_callouts(self, title, stats):
        """stats: [(大数字, 标签), ...]，最多 4 个，横向大字标注。"""
        s = self._blank()
        self._content_header(s, title)
        stats = stats[:4]
        n = len(stats)
        gap = 0.4
        total = 11.4
        w = (total - gap * (n - 1)) / n
        for i, (big, label) in enumerate(stats):
            x = 0.95 + i * (w + gap)
            tf = self._box(s, x, 2.8, w, 2.2, anchor=MSO_ANCHOR.MIDDLE)
            self._line(tf, str(big), 54, self.pal["primary"], bold=True, font=FONT_HEADER, align=PP_ALIGN.CENTER, first=True, space_after=4)
            self._line(tf, str(label), 14, self.pal["text_dark"], align=PP_ALIGN.CENTER, space_after=0)
        return s

    def image(self, title, image_path, caption=None):
        s = self._blank()
        self._content_header(s, title)
        # 右半放图，左半留给说明，避免纯图无信息。
        s.shapes.add_picture(image_path, Inches(6.6), Inches(2.1), width=Inches(6.0))
        if caption:
            tf = self._box(s, 0.95, 2.1, 5.3, 4.6, anchor=MSO_ANCHOR.MIDDLE)
            self._line(tf, caption, 18, self.pal["text_dark"], first=True)
        return s

    def table(self, title, header, rows):
        s = self._blank()
        self._content_header(s, title)
        nrows, ncols = len(rows) + 1, len(header)
        gtbl = s.shapes.add_table(nrows, ncols, Inches(0.95), Inches(2.1), Inches(11.4), Inches(0.5 + 0.45 * nrows)).table
        for c, h in enumerate(header):
            cell = gtbl.cell(0, c)
            cell.text = str(h)
            para = cell.text_frame.paragraphs[0]
            para.runs[0].font.bold = True
            para.runs[0].font.size = Pt(14)
            para.runs[0].font.color.rgb = _hex(self.pal["text_light"])
            _set_run_font(para.runs[0], FONT_HEADER)
            cell.fill.solid(); cell.fill.fore_color.rgb = _hex(self.pal["primary"])
        for r, row in enumerate(rows, start=1):
            for c, val in enumerate(row):
                cell = gtbl.cell(r, c)
                cell.text = str(val)
                run = cell.text_frame.paragraphs[0].runs[0]
                run.font.size = Pt(12)
                run.font.color.rgb = _hex(self.pal["text_dark"])
                _set_run_font(run, FONT_BODY)
        return s

    def save(self, path="output.pptx"):
        self.prs.save(path)
        return path
