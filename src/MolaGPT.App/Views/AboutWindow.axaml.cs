using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App.Views;

public sealed record AboutDependency(string Name, string Description, string License);

public partial class AboutWindow : MolaWindow
{
    private const string GitHubUrl = "https://github.com/MOLAaaaaaaa/MolaGPT.Desktop";
    private readonly UpdateCheckService _updateCheck;
    private string? _updateUrl;

    public AboutWindow(UpdateCheckService updateCheck)
    {
        _updateCheck = updateCheck;
        InitializeComponent();

        PART_Version.Text = $"版本 {UpdateCheckService.CurrentDisplayVersion}";
        PART_DependencyList.ItemsSource = Dependencies;
        PART_LicenseText.Text = LicenseNotice;

        PART_Header.PointerPressed += OnHeaderPointerPressed;
        PART_Close.Click += (_, _) => Close();
        PART_CheckUpdate.Click += CheckUpdate;
        PART_GitHub.Click += (_, _) => OpenUrl(GitHubUrl);
        PART_UpdateAction.Click += (_, _) => OpenUrl(_updateUrl);
    }

    private async void CheckUpdate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PART_CheckUpdate.IsEnabled = false;
        PART_CheckUpdateText.Text = "检查中...";
        PART_UpdateStatus.IsVisible = false;
        PART_UpdateAction.IsVisible = false;

        try
        {
            var latest = await _updateCheck.CheckLatestAsync();
            if (latest is null)
            {
                ShowUpdateStatus("暂时无法连接更新服务器。");
                return;
            }

            var currentVersion = ParseVersion(UpdateCheckService.CurrentDisplayVersion);
            var latestVersion = ParseVersion(latest.LatestVersion);
            if (currentVersion is not null && latestVersion is not null
                && latestVersion.CompareTo(currentVersion) <= 0)
            {
                ShowUpdateStatus("当前已是最新版本。");
                return;
            }

            _updateUrl = latest.DownloadUrl;
            ShowUpdateStatus($"发现新版本 {latest.LatestVersion}");
            if (!string.IsNullOrWhiteSpace(_updateUrl))
            {
                PART_UpdateAction.Content = latest.ActionText;
                PART_UpdateAction.IsVisible = true;
            }
        }
        finally
        {
            PART_CheckUpdate.IsEnabled = true;
            PART_CheckUpdateText.Text = "检查更新";
        }
    }

    private void ShowUpdateStatus(string text)
    {
        PART_UpdateStatus.Text = text;
        PART_UpdateStatus.IsVisible = true;
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ShowUpdateStatus("无法打开默认浏览器。");
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimStart('v', 'V');
        var separator = trimmed.IndexOfAny(['+', '-']);
        if (separator >= 0) trimmed = trimmed[..separator];
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    private static readonly AboutDependency[] Dependencies =
    [
        new("Avalonia UI", "跨平台桌面界面框架", "MIT"),
        new("Markdig", "CommonMark Markdown 解析引擎", "BSD-2-Clause"),
        new("AvaloniaMath", "LaTeX 数学公式渲染（含字体）", "MIT / OFL-1.1"),
        new("TextMateSharp", "代码语法分析与高亮", "MIT"),
        new("SkiaSharp", "图像解码与处理", "MIT"),
        new("CommunityToolkit.Mvvm", "MVVM 框架", "MIT"),
        new("Dapper", "轻量级 ORM", "Apache-2.0"),
        new("PdfPig", "PDF 附件文字提取", "Apache-2.0"),
        new("Microsoft.Data.Sqlite / SQLitePCLRaw", "SQLite 数据库驱动与原生库", "MIT / Apache-2.0 / Public Domain"),
        new("Microsoft.Extensions", "依赖注入、HTTP 与日志基础设施", "MIT"),
        new("Geist / Font Awesome Free", "界面字体与图标", "OFL-1.1 / CC-BY-4.0")
    ];

    private const string LicenseNotice = """
MolaGPT Desktop 使用了以下开源组件，并依据其许可证要求保留相应的版权与许可声明。

================================================================
MIT License
================================================================
适用组件：Avalonia UI、AvaloniaMath（代码部分）、TextMateSharp、SkiaSharp、
CommunityToolkit.Mvvm、Microsoft.Data.Sqlite、Microsoft.Extensions.*。

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

================================================================
BSD 2-Clause License
================================================================
适用组件：Markdig。

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

================================================================
Apache License 2.0
================================================================
适用组件：Dapper、SQLitePCLRaw、PdfPig。

Licensed under the Apache License, Version 2.0 (the "License"); you may not use
these files except in compliance with the License. You may obtain a copy of the
License at: https://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.

================================================================
SQLite Public Domain Dedication
================================================================
SQLite is in the public domain. 详情见：https://sqlite.org/copyright.html

================================================================
SIL Open Font License 1.1 / Creative Commons Attribution 4.0
================================================================
AvaloniaMath 内置字体与 Geist 依据 SIL OFL 1.1 发布；Font Awesome Free
图标字形依据 CC BY 4.0 发布。完整条款见：
https://openfontlicense.org
https://creativecommons.org/licenses/by/4.0/
""";
}
