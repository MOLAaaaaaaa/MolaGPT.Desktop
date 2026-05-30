using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MolaGPT.Desktop.Services;

namespace MolaGPT.Desktop.Views;

public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/MOLAaaaaaaa/MolaGPT.Desktop";
    private readonly UpdateCheckService _updateCheck;
    private readonly AutoUpdateService _autoUpdate;

    public AboutWindow(UpdateCheckService updateCheck, AutoUpdateService autoUpdate)
    {
        InitializeComponent();
        _updateCheck = updateCheck;
        _autoUpdate = autoUpdate;
        VersionText.Text = $"版本 {UpdateCheckService.CurrentDisplayVersion}";
        DependencyList.ItemsSource = Dependencies;
        LicenseText.Text = LicenseNotice;
    }

    private static readonly DependencyNotice[] Dependencies =
    {
        new("Markdig.Wpf", "WPF 下的 Markdown 渲染", "MIT"),
        new("Markdig", "CommonMark Markdown 解析引擎", "BSD-2-Clause"),
        new("WpfMath", "LaTeX 数学公式渲染（含字体）", "MIT / OFL-1.1"),
        new("ColorCode.Core", "代码语法高亮", "MIT"),
        new("CommunityToolkit.Mvvm", "MVVM 框架（ObservableObject / RelayCommand）", "MIT"),
        new("Dapper", "轻量级 ORM", "Apache-2.0"),
        new("Microsoft.Data.Sqlite", "SQLite 数据库驱动", "MIT"),
        new("Microsoft.Extensions.Hosting", "通用主机 / 依赖注入", "MIT"),
        new("Microsoft.Extensions.Http", "HttpClient 工厂", "MIT"),
        new("Microsoft.Extensions.Logging", "日志抽象与实现", "MIT"),
        new("Microsoft.Toolkit.Uwp.Notifications", "Windows 桌面通知", "MIT"),
        new("Microsoft.Win32.SystemEvents", "系统主题变更监听", "MIT"),
        new("System.Security.Cryptography.ProtectedData", "DPAPI 凭据加密", "MIT"),
        new("System.Text.Json", "JSON 序列化", "MIT")
    };

    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateText.Text = "检查中...";
        try
        {
            var latest = await _updateCheck.CheckLatestAsync();
            if (latest is null)
            {
                ShowUpdateDialog(
                    UpdateCheckService.CurrentDisplayVersion,
                    "无法连接到更新服务器，请稍后再试。",
                    null,
                    null,
                    title: "检查更新失败",
                    versionText: $"当前版本 v{UpdateCheckService.CurrentDisplayVersion}");
                return;
            }

            var current = ParseVersion(UpdateCheckService.CurrentDisplayVersion);
            var remote = ParseVersion(latest.LatestVersion);
            if (current is not null && remote is not null && remote.CompareTo(current) <= 0)
            {
                var notes = string.IsNullOrWhiteSpace(latest.Notes)
                    ? "当前已是最新版本。未能获取到该版本的 Release Note。"
                    : latest.Notes;
                ShowUpdateDialog(
                    latest.LatestVersion,
                    notes,
                    null,
                    null,
                    title: "已是最新版本",
                    versionText: $"Release {latest.LatestVersion}");
                return;
            }

            ShowUpdateDialog(
                latest.LatestVersion,
                latest.Notes,
                latest.DownloadUrl,
                latest.ActionText,
                latest.InstallerSha256);
        }
        finally
        {
            CheckUpdateText.Text = "检查更新";
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ShowUpdateDialog(
        string version,
        string? notes,
        string? url,
        string? actionText,
        string? installerSha256 = null,
        string title = "发现新版本",
        string? versionText = null)
    {
        var dlg = new UpdateDialog(version, notes, url, actionText, title, versionText, installerSha256, _autoUpdate)
        {
            Owner = this
        };
        dlg.ShowDialog();
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];
        var dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed[..dash];
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    private void OpenGitHubClick(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // The default browser may be unavailable or blocked.
        }
    }

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed record DependencyNotice(string Name, string Description, string License);

    private const string LicenseNotice = """
MolaGPT Desktop 使用了以下开源组件，并依据其许可证要求保留相应的版权与许可声明。

================================================================
MIT License
================================================================
适用组件：
  - Markdig.Wpf            © Kryptos-FR (Nicolas Musset)
  - WpfMath (代码部分)      © Alex Regueiro 2010；WPF-Math / XAML-Math Contributors
  - ColorCode.Core         © .NET Foundation and Contributors
  - CommunityToolkit.Mvvm  © .NET Foundation and Contributors
  - Microsoft.Data.Sqlite  © .NET Foundation and Contributors
  - Microsoft.Extensions.* © .NET Foundation and Contributors
  - Microsoft.Toolkit.Uwp.Notifications  © .NET Foundation and Contributors
  - Microsoft.Win32.SystemEvents          © .NET Foundation and Contributors
  - System.Security.Cryptography.ProtectedData  © .NET Foundation and Contributors
  - System.Text.Json       © .NET Foundation and Contributors

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
适用组件：
  - Markdig  © 2018-2019 Alexandre Mutel (xoofx)

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
适用组件：
  - Dapper  © 2019 Stack Exchange, Inc.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use
these files except in compliance with the License. You may obtain a copy of the
License at: http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.

================================================================
SIL Open Font License 1.1
================================================================
适用组件：
  - WpfMath 内置字体 jlm_msam10.ttf

This font is licensed under the SIL Open Font License, Version 1.1.
该字体依据 SIL 开放字体许可证 1.1 发布，允许自由使用、嵌入、修改与再分发，
但不得单独出售字体本身，且衍生字体不得使用保留字体名称。
完整条款见：https://openfontlicense.org
""";
}
