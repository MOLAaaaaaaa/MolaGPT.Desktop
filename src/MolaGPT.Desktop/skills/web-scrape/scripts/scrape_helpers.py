"""scrape_helpers.py — 网页抓取便捷封装（MolaGPT 内置技能）

需联网：若 Python 工具未开网络，调用会被权限策略拦截，请提示用户在
「设置 → 工具与能力 → Python」勾选「允许网络」。

用法：
    import sys; sys.path.append(r"<技能目录>/scripts")
    from scrape_helpers import fetch, to_markdown, extract_tables, extract_links
    soup, html = fetch("https://example.com")
    md = to_markdown(soup)
    tables = extract_tables(html)   # DataFrame 列表
"""

DEFAULT_UA = "Mozilla/5.0 (compatible; MolaGPT/1.0; +https://molagpt.local)"


def fetch(url, timeout=20, headers=None):
    """GET 并返回 (BeautifulSoup, 原始html)。自动纠正中文编码、去噪。"""
    import requests
    from bs4 import BeautifulSoup
    h = {"User-Agent": DEFAULT_UA}
    if headers:
        h.update(headers)
    resp = requests.get(url, headers=h, timeout=timeout)
    resp.raise_for_status()
    resp.encoding = resp.apparent_encoding or resp.encoding
    soup = BeautifulSoup(resp.text, "lxml")
    return soup, resp.text


def _main_node(soup):
    for tag in soup(["script", "style", "nav", "footer", "header", "aside", "noscript"]):
        tag.decompose()
    return soup.find("article") or soup.find("main") or soup.body or soup


def to_markdown(soup, strip_links=False):
    """把主要正文转 Markdown。"""
    from markdownify import markdownify as md
    node = _main_node(soup)
    return md(str(node), heading_style="ATX", strip=["a"] if strip_links else None)


def extract_links(soup, base_url=""):
    from urllib.parse import urljoin
    out = []
    for a in soup.select("a[href]"):
        text = a.get_text(strip=True)
        if text:
            out.append((text, urljoin(base_url, a["href"])))
    return out


def extract_tables(html):
    """返回页面中所有表格的 DataFrame 列表（无表格则空列表）。"""
    import pandas as pd
    try:
        return pd.read_html(html)
    except ValueError:
        return []


def title_of(soup):
    return (soup.title.string or "").strip() if soup.title else ""
