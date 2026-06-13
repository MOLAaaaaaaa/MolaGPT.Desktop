# PPTX 设计指南

把它当成"如何不做出无聊幻灯片"的清单。`scripts/pptx_helpers.py` 已经内置了这里的配色与排版；当你绕开它手写 python-pptx 时，遵循本指南。

## 开始前

- **选一套贴合主题的大胆配色**：配色要为「这个主题」设计；若换到别的演示里也毫无违和，说明还不够具体。
- **主次分明**：一个颜色主导（60-70% 视觉重量），1-2 个辅助色，一个锐利强调色。绝不让所有颜色等权重。
- **明暗对比（三明治结构）**：标题页/结论页用深色背景，内容页用浅色；或全程深色营造高级感。
- **确立视觉母题**：选一个独特元素反复使用——圆角图框、彩色圆圈里的图标、单侧色块——贯穿每页。

## 配色参考（helpers 内置同名 palette）

| 名称 | 主色 | 辅色 | 强调 |
|------|------|------|------|
| midnight 午夜行政 | `1E2761` | `CADCFC` | `4F86F7` |
| forest 森林苔藓 | `2C5F2D` | `97BC62` | `E8A33D` |
| coral 珊瑚活力 | `F96167` | `F9E795` | `2F3C7E` |
| ocean 海洋 | `065A82` | `1C7293` | `21C0A8` |
| charcoal 炭灰极简 | `36454F` | `B0BEC5` | `FF7043` |
| berry berry | `6D2E46` | `A26769` | `D4A373` |

## 字号

| 元素 | 字号 |
|------|------|
| 封面标题 | 44-48pt 粗体 |
| 幻灯片标题 | 30-36pt 粗体 |
| 小节标题 | 20-24pt 粗体 |
| 正文 | 16-18pt |
| 大数字标注 | 54-72pt |
| 注释 | 10-12pt 弱化 |

## 版式选项

- 两栏（左文右图 / 对比）
- 图标 + 文字行（图标放彩色圆圈，粗体小标题 + 描述）
- 2x2 / 2x3 网格卡片
- 半出血大图（左或右整侧）配文字
- 大数字标注（60-72pt 大数 + 小标签）
- 时间线 / 编号流程

## 间距

- 至少 0.5" 边距
- 内容块之间 0.3-0.5" 统一间隔
- 留呼吸感，别填满每一寸

## 避免（常见错误）

- 不要每页同一版式——交替用栏、卡片、标注。
- 正文不要居中——段落/列表左对齐，只有标题/封面居中。
- 标题字号要够大（30pt+）和正文（16-18pt）拉开层级。
- 不要默认蓝色——选贴主题的颜色。
- 不要只美化一页而其余留白。
- 不要做纯文字页——加图片、图标、图表、色块。
- **永远不要在标题下加装饰横线**——这是 AI 生成幻灯片的典型标志，用留白/背景色/强调色块代替。
- 文本框对齐时注意内边距，必要时设 `margin_left/top = 0`（`text_frame` 的 margin 属性）。
- 避免低对比——浅底浅字、深底深字都不行。

## 原生 API 速记（绕开 helpers 时）

```python
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR

slide = prs.slides.add_slide(prs.slide_layouts[6])         # 空白版式
slide.background.fill.solid()
slide.background.fill.fore_color.rgb = RGBColor.from_string("1E2761")
tb = slide.shapes.add_textbox(Inches(1), Inches(1), Inches(11), Inches(2))
tf = tb.text_frame; tf.word_wrap = True
p = tf.paragraphs[0]; r = p.add_run(); r.text = "标题"
r.font.size = Pt(40); r.font.bold = True; r.font.name = "Microsoft YaHei"
r.font.color.rgb = RGBColor.from_string("FFFFFF")
shp = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(1), Inches(3), Inches(0.6), Inches(0.12))
shp.fill.solid(); shp.fill.fore_color.rgb = RGBColor.from_string("21C0A8"); shp.line.fill.background()
slide.shapes.add_picture("img.png", Inches(7), Inches(2), width=Inches(5.5))
```
