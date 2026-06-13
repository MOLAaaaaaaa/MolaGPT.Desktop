---
name: web-scrape
description: 凡是涉及抓取网页、从 URL 提取正文/表格/链接、把网页转成 Markdown 的任务都使用本技能。只要用户给出网址并要求「抓取」「提取网页内容」「把这个页面转成 Markdown」「读取这个链接的表格」就触发本技能。用本地 requests/httpx + beautifulsoup4 + lxml + markdownify 实现。注意：本技能需要联网，必须在 Python 工具设置中开启「允许网络」，否则会被拦截。
---

# 网页抓取技能（requests + BeautifulSoup + markdownify）

**前提：需联网。** 若 Python 工具未开启网络，代码会被权限策略拦截——此时提示用户去「设置 → 工具与能力 → Python」勾选「允许模型在 Python 代码中使用网络」。

产物（如转好的 Markdown / 提取的表格）可存到**当前工作目录**。

## 推荐：用内置脚本

`scripts/scrape_helpers.py` 封装了抓取、去噪、转 Markdown、提取表格/链接：

```python
import sys
sys.path.append(r"<本技能目录>/scripts")
from scrape_helpers import fetch, to_markdown, extract_tables, extract_links, title_of

soup, html = fetch("https://example.com/article")
print(title_of(soup))
md = to_markdown(soup)                 # 主要正文转 Markdown
open("page.md", "w", encoding="utf-8").write(md)
tables = extract_tables(html)          # DataFrame 列表
```

下面是不依赖脚本的原生写法。

## 抓取并转 Markdown

```python
import requests
from bs4 import BeautifulSoup
from markdownify import markdownify as md

url = "https://example.com/article"
headers = {"User-Agent": "Mozilla/5.0 (compatible; MolaGPT/1.0)"}
resp = requests.get(url, headers=headers, timeout=20)
resp.raise_for_status()
resp.encoding = resp.apparent_encoding   # 纠正中文编码

soup = BeautifulSoup(resp.text, "lxml")

# 去掉脚本/样式/导航等噪音
for tag in soup(["script", "style", "nav", "footer", "header", "aside"]):
    tag.decompose()

# 优先取主要正文容器
main = soup.find("article") or soup.find("main") or soup.body
markdown = md(str(main), heading_style="ATX", strip=["a"] if False else None)
print(markdown[:3000])

with open("page.md", "w", encoding="utf-8") as f:
    f.write(markdown)
print("已保存 page.md")
```

## 提取结构化数据

```python
# 标题
title = (soup.title.string or "").strip() if soup.title else ""

# 所有链接（文字 + 绝对地址）
from urllib.parse import urljoin
links = [(a.get_text(strip=True), urljoin(url, a["href"]))
         for a in soup.select("a[href]") if a.get_text(strip=True)]

# 表格直接交给 pandas
import pandas as pd
try:
    tables = pd.read_html(resp.text)   # 返回 DataFrame 列表
    print(f"页面有 {len(tables)} 个表格")
    if tables:
        print(tables[0].head())
        tables[0].to_csv("table1.csv", index=False, encoding="utf-8-sig")
except ValueError:
    print("页面无可解析表格")
```

## 要点与礼貌
- 带 `User-Agent`、设 `timeout`，对 `resp.raise_for_status()` 检查状态码。
- 中文页面用 `resp.apparent_encoding` 纠正编码，避免乱码。
- 选择器优先 `article`/`main`，再退到 `body`，能显著减少噪音。
- 抓多页时加适当 `time.sleep`，遵守目标站点的 robots 与频率限制；不要抓取需要登录或明确禁止抓取的内容。
- 动态渲染（纯 JS 加载）的页面用 requests 抓不到内容——本环境无浏览器内核，遇到时如实告知用户。
