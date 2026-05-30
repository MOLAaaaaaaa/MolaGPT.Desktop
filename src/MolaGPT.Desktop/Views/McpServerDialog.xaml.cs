using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class McpServerDialog : Window
{
    private string? _id;
    private Func<McpHttpClient>? _clientFactory;

    public McpServerEntry? Entry { get; private set; }

    public McpServerDialog()
    {
        InitializeComponent();
    }

    /// <summary>Opens the dialog. <paramref name="clientFactory"/> enables the
    /// "测试连接" button; pass null to hide it.</summary>
    public void ShowEdit(McpServerEntry? entry, Window owner, Func<McpHttpClient>? clientFactory = null)
    {
        Owner = owner;
        _clientFactory = clientFactory;
        if (clientFactory is null)
            TestButton.Visibility = Visibility.Collapsed;

        if (entry is not null)
        {
            DialogTitle.Text = "编辑 MCP 服务器";
            _id = entry.Id;
            NameBox.Text = entry.Name;
            UrlBox.Text = entry.Url;
            HeaderBox.Text = string.IsNullOrWhiteSpace(entry.HeaderName) ? "Authorization" : entry.HeaderName;
            TokenBox.Password = entry.Token ?? string.Empty;
            EnabledBox.IsChecked = entry.Enabled;
        }

        ShowDialog();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private bool TryBuildEntry(out McpServerEntry entry)
    {
        entry = default!;
        var url = UrlBox.Text.Trim();
        if (!IsValidUrl(url, out var error))
        {
            SetStatus(error, isError: true);
            return false;
        }

        entry = new McpServerEntry(
            string.IsNullOrWhiteSpace(_id) ? Guid.NewGuid().ToString("N") : _id!,
            string.IsNullOrWhiteSpace(NameBox.Text) ? "MCP Server" : NameBox.Text.Trim(),
            url,
            "http",
            string.IsNullOrWhiteSpace(HeaderBox.Text) ? "Authorization" : HeaderBox.Text.Trim(),
            string.IsNullOrWhiteSpace(TokenBox.Password) ? null : TokenBox.Password.Trim(),
            EnabledBox.IsChecked == true);
        return true;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildEntry(out var entry)) return;
        Entry = entry;
        DialogResult = true;
    }

    private async void TestClick(object sender, RoutedEventArgs e)
    {
        if (_clientFactory is null) return;
        if (!TryBuildEntry(out var entry)) return;

        TestButton.IsEnabled = false;
        SetStatus("正在连接…", isError: false);
        try
        {
            var options = new McpServerOptions(
                entry.Id, entry.Name, entry.Url, entry.Transport,
                entry.HeaderName, entry.Token, entry.Enabled);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var client = _clientFactory();
            var session = await client.InitializeAsync(options, cts.Token).ConfigureAwait(true);
            var tools = await client.ListToolsAsync(session, cts.Token).ConfigureAwait(true);
            SetStatus($"连接成功，发现 {tools.Count} 个工具", isError: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("连接超时", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"连接失败：{ex.Message}", isError: true);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetStatus(string text, bool isError)
    {
        TestStatusText.Text = text;
        TestStatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            isError ? "Brush.Error" : "Brush.Text.Muted");
    }

    private static bool IsValidUrl(string url, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "请填写服务器地址";
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "地址必须以 http:// 或 https:// 开头";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "远程地址必须使用 https://（仅本地 localhost 可用 http://）";
            return false;
        }
        return true;
    }
}
