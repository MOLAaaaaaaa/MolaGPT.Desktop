---
name: pptx
description: 凡是涉及 PowerPoint 演示文稿（.pptx）的任务都使用本技能——创建幻灯片/演示稿/汇报/路演 PPT，读取或提取现有 .pptx 的文字，修改、合并、拆分演示文稿。只要用户提到「PPT」「幻灯片」「演示」「slides」「deck」或给出 .pptx 文件名，就触发本技能。用本地 python-pptx 实现，无需联网。
---

# PPTX 技能（python-pptx）

用本地 `python-pptx` 创建/读取 PowerPoint。产物存到**当前工作目录**（相对文件名如 `output.pptx`），执行后按 display_instructions 处理。

## 快速参考

| 任务 | 怎么做 |
|------|--------|
| 创建有设计感的演示稿 | 用 `scripts/pptx_helpers.py` 的 `Deck`（首选，省去手写坐标/配色） |
| 想了解配色/排版/避坑 | 读 [references/design.md](references/design.md) |
| 想看成稿配方（报告/路演/对比） | 读 [references/recipes.md](references/recipes.md) |
| 读取/提取现有 .pptx | 见下方「读取内容」 |
| 用品牌模板 | 把 .pptx 放进技能目录，`Deck(template="brand.pptx")` 继承其母版/字体/Logo |

## 推荐：用内置构建模块

`scripts/pptx_helpers.py` 提供 `Deck`，内置 6 套专业配色、中文字体处理、标题页/小节页/要点/两栏/大数字标注/图片/表格等版式。先把脚本路径加入 sys.path 再 import：

```python
import sys
sys.path.append(r"<本技能目录>/scripts")   # 用 SKILL.md 所在目录下的 scripts
from pptx_helpers import Deck

d = Deck(palette="ocean")                  # midnight/forest/coral/ocean/charcoal/berry
d.title_slide("产品发布", "2024 Q4 新特性")
d.section("一、背景", number="01")
d.bullets("我们要解决的问题", ["问题A", "问题B", "问题C"])
d.two_column("方案对比", ["旧：慢", "旧：贵"], ["新：快 3x", "新：省 40%"],
             left_head="现状", right_head="改进后")
d.stat_callouts("关键数字", [("3x", "性能提升"), ("40%", "成本下降"), ("1.2M", "受益用户")])
# d.image("效果图", "chart.png", caption="季度趋势显著上升")
# d.table("价目", ["套餐","价格"], [["基础","￥9"],["专业","￥29"]])
d.save("output.pptx")
print("已生成 output.pptx")
```

需要更细控制时，再读 design.md 直接用 python-pptx 原生 API。**务必先读 design.md 的"避免"清单**——尤其：不要做纯文字页、不要标题下加装饰横线、不要默认蓝色。

## 读取内容

```python
from pptx import Presentation
prs = Presentation("input.pptx")
for i, slide in enumerate(prs.slides, 1):
    print(f"--- Slide {i} ---")
    for shape in slide.shapes:
        if shape.has_text_frame and shape.text_frame.text.strip():
            print(shape.text_frame.text)
    if slide.has_notes_slide:
        note = slide.notes_slide.notes_text_frame.text
        if note.strip():
            print("[备注]", note)
```

## 质量自检（必做）

生成后用上面的读取代码打印全部文字核对：缺内容、错别字、顺序、残留占位符（搜 `xxxx`/`lorem`/「在此输入」）。本环境无 LibreOffice，无法渲染成图做视觉检查，所以坐标要保守、留足边距——优先用 `Deck` 的版式函数（已调好间距）。
