"""viz_helpers.py — 数据可视化便捷封装（MolaGPT 内置技能）

复用本地 Python 工具已配好的 matplotlib headless + 中文字体。统一主题与导出。

用法：
    import sys; sys.path.append(r"<技能目录>/scripts")
    import pandas as pd
    from viz_helpers import set_theme, bar, line, hist, heatmap
    set_theme()
    df = pd.read_csv("data.csv")
    bar(df.groupby("city")["sales"].sum().sort_values(ascending=False).head(10),
        "各城市销量 Top10", "bar.png", ylabel="销量")
"""

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

# 与 pptx 技能一致的一组好看颜色
PALETTE = ["#1C7293", "#B85042", "#2C5F2D", "#6D2E46", "#E8A33D", "#065A82"]


def set_theme():
    try:
        import seaborn as sns
        sns.set_theme(style="whitegrid")
    except Exception:
        plt.style.use("seaborn-v0_8-whitegrid")
    plt.rcParams["axes.unicode_minus"] = False
    # 中文字体由本地 Python 工具的 runner 预置；这里再兜底一次
    plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "DejaVu Sans"]


def _save(fig, path, dpi=150):
    fig.tight_layout()
    fig.savefig(path, dpi=dpi, bbox_inches="tight")
    plt.close(fig)
    return path


def bar(series, title, path, xlabel="", ylabel="", color=None, figsize=(8, 4.5)):
    fig, ax = plt.subplots(figsize=figsize)
    series.plot(kind="bar", color=color or PALETTE[0], ax=ax)
    ax.set_title(title); ax.set_xlabel(xlabel); ax.set_ylabel(ylabel)
    plt.setp(ax.get_xticklabels(), rotation=30, ha="right")
    return _save(fig, path)


def line(series_or_df, title, path, xlabel="", ylabel="", figsize=(9, 4.2)):
    fig, ax = plt.subplots(figsize=figsize)
    series_or_df.plot(ax=ax, marker="o", color=PALETTE)
    ax.set_title(title); ax.set_xlabel(xlabel); ax.set_ylabel(ylabel)
    return _save(fig, path)


def hist(series, title, path, bins=30, xlabel="", figsize=(8, 4.5)):
    fig, ax = plt.subplots(figsize=figsize)
    ax.hist(series.dropna(), bins=bins, color=PALETTE[0], edgecolor="white")
    ax.set_title(title); ax.set_xlabel(xlabel); ax.set_ylabel("频数")
    return _save(fig, path)


def scatter(df, x, y, title, path, hue=None, figsize=(7, 5)):
    fig, ax = plt.subplots(figsize=figsize)
    if hue:
        for i, (key, grp) in enumerate(df.groupby(hue)):
            ax.scatter(grp[x], grp[y], s=18, label=str(key), color=PALETTE[i % len(PALETTE)])
        ax.legend(title=hue)
    else:
        ax.scatter(df[x], df[y], s=18, color=PALETTE[0])
    ax.set_title(title); ax.set_xlabel(x); ax.set_ylabel(y)
    return _save(fig, path)


def heatmap(corr_df, title, path, figsize=(6, 5)):
    fig, ax = plt.subplots(figsize=figsize)
    try:
        import seaborn as sns
        sns.heatmap(corr_df, annot=True, fmt=".2f", cmap="coolwarm", ax=ax)
    except Exception:
        im = ax.imshow(corr_df, cmap="coolwarm")
        ax.set_xticks(range(len(corr_df.columns))); ax.set_xticklabels(corr_df.columns, rotation=45, ha="right")
        ax.set_yticks(range(len(corr_df.index))); ax.set_yticklabels(corr_df.index)
        fig.colorbar(im, ax=ax)
    ax.set_title(title)
    return _save(fig, path)
