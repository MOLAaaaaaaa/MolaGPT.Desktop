# PPTX 成稿配方

用 `scripts/pptx_helpers.py` 的 `Deck` 组合出完整演示稿。先把 scripts 目录加入 sys.path：

```python
import sys
sys.path.append(r"<本技能目录>/scripts")
from pptx_helpers import Deck
```

## 配方 1：数据/业绩汇报（浅色三明治）

```python
d = Deck(palette="ocean")
d.title_slide("2024 Q4 业绩汇报", "营收、用户与展望")
d.stat_callouts("季度关键数字", [("¥38M", "营收"), ("+38%", "同比"), ("1.2M", "活跃用户"), ("4.6", "满意度")])
d.bullets("做对了什么", ["新定价提升转化", "渠道 A 投入产出最高", "客服响应时长 -45%"])
# 先用 data-analysis 技能/ matplotlib 出图存 trend.png，再插入
d.image("营收趋势", "trend.png", caption="环比连续 3 个月上行，12 月创新高")
d.two_column("风险 vs 计划", ["获客成本上升", "旺季产能紧张"], ["锁定长期渠道价", "提前扩容 + 预案"],
             left_head="风险", right_head="应对")
d.section("谢谢", number=None)
d.save("q4_report.pptx")
```

## 配方 2：产品路演（深色高级感）

```python
d = Deck(palette="midnight")
d.title_slide("Mola — 本地 AI 工作台", "让模型真正动手做事")
d.section("问题", number="01")
d.bullets("现状的痛", ["AI 只会聊，不会做", "数据要手工搬运", "结果难以复用"])
d.section("方案", number="02")
d.stat_callouts("我们带来什么", [("63", "内置 Python 库"), ("6", "开箱技能"), ("100%", "本地执行")])
d.two_column("对比", ["纯对话助手", "需手工导出"], ["直接产出 PPT/Excel/PDF", "一句话搞定"],
             left_head="别人", right_head="Mola")
d.section("立即开始", number="03")
d.save("pitch.pptx")
```

## 配方 3：基于品牌模板

把公司模板 `brand.pptx`（已含母版、Logo、配色）放到工作目录，新页继承它：

```python
d = Deck(template="brand.pptx")     # 继承母版/字体
d.bullets("本月要点", ["...", "..."])
d.save("monthly.pptx")
```

> 说明：python-pptx 没有独立"模板"概念——加载任意 .pptx 即以它为起点。要做品牌模板，最省力是在 PowerPoint 里设好母版/Logo/字体，删掉示例页，存成 .pptx 当模板用。

## 配图建议

`Deck.image()` 需要现成 PNG。优先用 matplotlib 出图（见 data-analysis 技能），统一 `dpi=150`、深浅与本 deck 配色协调，存成 PNG 再插入。一张图胜过一页文字。

## 收尾自检

```python
from pptx import Presentation
prs = Presentation("output.pptx")
for i, s in enumerate(prs.slides, 1):
    print(f"--- {i} ---")
    for sh in s.shapes:
        if sh.has_text_frame and sh.text_frame.text.strip():
            print(sh.text_frame.text)
```
核对：页序、错别字、是否有纯文字页可补图、占位符是否残留。
