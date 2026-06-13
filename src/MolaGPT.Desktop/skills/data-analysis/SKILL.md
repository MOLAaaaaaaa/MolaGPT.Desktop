---
name: data-analysis
description: 凡是涉及数据分析、统计计算、数据清洗或可视化出图的任务都使用本技能——读取 CSV/Excel/JSON 数据、用 pandas 做清洗与统计聚合、用 matplotlib/seaborn 画折线/柱状/散点/热力图等并导出 PNG。只要用户给出数据文件、要求「分析数据」「画图」「统计」「可视化」「图表」就触发本技能。用本地 pandas + numpy + matplotlib + seaborn 实现，无需联网。
---

# 数据分析与可视化技能（pandas + matplotlib/seaborn）

产物（图片/清洗后的数据）存到**当前工作目录**，图片存为 PNG，执行后按 display_instructions 用相对路径展示图片。

## 推荐：用内置出图脚本

`scripts/viz_helpers.py` 封装了统一主题与常用图（已配中文字体/headless）：

```python
import sys
sys.path.append(r"<本技能目录>/scripts")
import pandas as pd
from viz_helpers import set_theme, bar, line, hist, scatter, heatmap

set_theme()
df = pd.read_csv("data.csv")
bar(df.groupby("city")["sales"].sum().sort_values(ascending=False).head(10),
    "各城市销量 Top10", "bar.png", ylabel="销量")
heatmap(df.select_dtypes("number").corr(), "相关性", "corr.png")
```

下面是不依赖脚本的原生写法。

## 读数据

```python
import pandas as pd
df = pd.read_csv("data.csv")          # 或 read_excel / read_json
print(df.shape)
print(df.head())
print(df.dtypes)
print(df.describe(include="all"))
print("缺失值：\n", df.isna().sum())
```

## 清洗 / 统计

```python
df = df.drop_duplicates()
df["date"] = pd.to_datetime(df["date"], errors="coerce")
df["amount"] = pd.to_numeric(df["amount"], errors="coerce")
df = df.dropna(subset=["amount"])

# 分组聚合
g = df.groupby("category")["amount"].agg(["count", "sum", "mean"]).sort_values("sum", ascending=False)
print(g)
g.to_csv("summary.csv", encoding="utf-8-sig")   # utf-8-sig 让 Excel 正确识别中文
```

## 出图（matplotlib / seaborn）

环境已为本地 Python 工具配好 headless（Agg）后端与中文字体，**直接画即可**，中文不会乱码。务必 `savefig` 到当前目录、并 `plt.close()`。

```python
import matplotlib.pyplot as plt
import seaborn as sns

sns.set_theme(style="whitegrid")          # 美观默认主题

# 柱状图
fig, ax = plt.subplots(figsize=(8, 4.5), dpi=150)
g["sum"].head(10).plot(kind="bar", color="#1C7293", ax=ax)
ax.set_title("各类别金额 Top 10")
ax.set_xlabel(""); ax.set_ylabel("金额")
fig.tight_layout()
fig.savefig("bar_top10.png")
plt.close(fig)

# 折线（时间序列）
daily = df.groupby(df["date"].dt.date)["amount"].sum()
fig, ax = plt.subplots(figsize=(9, 4), dpi=150)
daily.plot(ax=ax, marker="o", color="#B85042")
ax.set_title("每日金额趋势")
fig.tight_layout(); fig.savefig("trend.png"); plt.close(fig)

# 相关性热力图
num = df.select_dtypes("number")
if num.shape[1] >= 2:
    fig, ax = plt.subplots(figsize=(6, 5), dpi=150)
    sns.heatmap(num.corr(), annot=True, cmap="coolwarm", ax=ax)
    fig.tight_layout(); fig.savefig("corr.png"); plt.close(fig)

print("已生成 bar_top10.png / trend.png / corr.png")
```

## 要点
- 出图统一 `dpi=150` + `figsize` 控制清晰度；画完 `tight_layout()` 防止标签被裁。
- 多张图分别 `savefig` 成不同 PNG，便于在回复里逐张展示。
- 导出给用户的 CSV 用 `encoding="utf-8-sig"`，避免中文在 Excel 里乱码。
- 大数据先 `df.sample()` 或聚合后再画，避免散点过密。
- 分析结论用文字 `print` 出来（关键统计量、趋势、异常），不要只丢图。
