---
name: xlsx
description: 凡是涉及 Excel 表格（.xlsx）的任务都使用本技能——创建电子表格/数据报表/带公式与图表的工作簿，读取或提取现有 .xlsx 的数据，修改已有工作簿。只要用户提到「Excel」「表格」「.xlsx」「工作簿」「spreadsheet」并希望产出可下载的 Excel 文件，就触发本技能。用本地 openpyxl（读+改）/ xlsxwriter（高效写）/ pandas 实现，无需联网。
---

# XLSX 技能（openpyxl / xlsxwriter / pandas）

产物存到**当前工作目录**，用相对文件名（如 `data.xlsx`）。

## 推荐：用内置构建脚本

`scripts/xlsx_helpers.py` 封装了表头样式、数字格式、公式列、图表、自动列宽与读取：

```python
import sys
sys.path.append(r"<本技能目录>/scripts")
from xlsx_helpers import WorkbookBuilder, read_xlsx

wb = WorkbookBuilder("report.xlsx")
wb.sheet("销售")
wb.table(["月份", "销量", "单价"], [["1月", 120, 9.9], ["2月", 150, 9.9]], money_cols=[2])
wb.bar_chart(title="月度销量", cat_col=0, val_col=1, n_rows=2)
wb.close()

# 读取： data = read_xlsx("report.xlsx")  # {表名: 二维列表}
```

需要更细控制时，用下面的原生 API。

## 选库
- **读取/修改现有 .xlsx** → openpyxl（能保留已有内容）。
- **从零高效写、要图表/条件格式** → xlsxwriter（只能写、不能读）。
- **DataFrame 直接落表** → pandas（底层调 openpyxl/xlsxwriter）。

## 读取（openpyxl）

```python
import openpyxl
wb = openpyxl.load_workbook("input.xlsx", data_only=True)  # data_only 取公式计算值
for ws in wb.worksheets:
    print(f"=== 工作表 {ws.title}（{ws.max_row}x{ws.max_column}）===")
    for row in ws.iter_rows(values_only=True):
        print(row)
```

用 pandas 读：`import pandas as pd; df = pd.read_excel("input.xlsx", sheet_name=None)`（None 读全部工作表为 dict）。

## 创建（xlsxwriter：样式/公式/图表）

```python
import xlsxwriter
wb = xlsxwriter.Workbook("report.xlsx")
ws = wb.add_worksheet("销售")

bold = wb.add_format({"bold": True, "bg_color": "#1E2761", "font_color": "white", "border": 1})
money = wb.add_format({"num_format": "￥#,##0.00", "border": 1})
cell = wb.add_format({"border": 1})

headers = ["月份", "销量", "单价", "金额"]
for c, h in enumerate(headers):
    ws.write(0, c, h, bold)

data = [("1月", 120, 9.9), ("2月", 150, 9.9), ("3月", 90, 12.5)]
for r, (m, qty, price) in enumerate(data, start=1):
    ws.write(r, 0, m, cell)
    ws.write_number(r, 1, qty, cell)
    ws.write_number(r, 2, price, money)
    ws.write_formula(r, 3, f"=B{r+1}*C{r+1}", money)   # 公式
ws.write(len(data)+1, 0, "合计", bold)
ws.write_formula(len(data)+1, 3, f"=SUM(D2:D{len(data)+1})", money)

ws.set_column(0, 3, 14)   # 列宽

# 柱状图
chart = wb.add_chart({"type": "column"})
chart.add_series({
    "name": "销量",
    "categories": f"=销售!$A$2:$A${len(data)+1}",
    "values": f"=销售!$B$2:$B${len(data)+1}",
})
chart.set_title({"name": "月度销量"})
ws.insert_chart("F2", chart)

wb.close()
print("已生成 report.xlsx")
```

## 修改现有（openpyxl）

```python
import openpyxl
wb = openpyxl.load_workbook("input.xlsx")
ws = wb.active
ws["E1"] = "新增列"
ws.append(["新行", 1, 2, 3])
wb.save("input-updated.xlsx")
```

## pandas 落表

```python
import pandas as pd
with pd.ExcelWriter("out.xlsx", engine="xlsxwriter") as w:
    df1.to_excel(w, sheet_name="明细", index=False)
    df2.to_excel(w, sheet_name="汇总", index=False)
```

## 要点与自检
- xlsxwriter 行列是 0 基（`write(row, col, ...)`）；A1 表示法用 `ws.write("A1", ...)`。
- 公式用 Excel 函数名（`SUM`/`AVERAGE`），字符串里写 `=...`。
- 数字格式：`num_format="0.00%"`（百分比）、`"#,##0"`（千分位）。
- 生成后用 openpyxl `data_only=False` 读回检查公式是否写对、表头与列宽是否合理。
