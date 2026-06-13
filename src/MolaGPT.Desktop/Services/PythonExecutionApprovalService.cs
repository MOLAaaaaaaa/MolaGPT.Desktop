using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Services;

public sealed class PythonExecutionApprovalService : IPythonExecutionApprovalService
{
    private readonly IPythonSessionAllowList _sessionAllowList;
    private readonly SettingsViewModel _settings;

    public PythonExecutionApprovalService(
        IPythonSessionAllowList sessionAllowList,
        SettingsViewModel settings)
    {
        _sessionAllowList = sessionAllowList;
        _settings = settings;
    }

    public async Task<PythonExecutionApprovalDecision> RequestApprovalAsync(
        PythonExecutionApprovalRequest request,
        CancellationToken ct)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null)
            return PythonExecutionApprovalDecision.Denied;

        ct.ThrowIfCancellationRequested();
        var decision = await app.Dispatcher.InvokeAsync(() => ShowApprovalDialog(request)).Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return decision;
    }

    private PythonExecutionApprovalDecision ShowApprovalDialog(PythonExecutionApprovalRequest request)
    {
        var owner = FindOwnerWindow();
        var dialog = new Window
        {
            Title = "审批 Python 执行",
            Width = 760,
            Height = 620,
            MinWidth = 620,
            MinHeight = 480,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.CanResize,
            Background = Brushes.White,
            FontFamily = TryFindFont("Font.Cjk") ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13
        };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 title
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1 purpose
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2 risk
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3 code
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4 remember
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 5 buttons

        var title = new TextBlock
        {
            Text = "模型请求执行本地 Python",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        var purposeCard = BuildPurposeCard(request);
        Grid.SetRow(purposeCard, 1);
        root.Children.Add(purposeCard);

        var riskBox = new TextBox
        {
            Text = BuildRiskText(request),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 96,
            MaxHeight = 150,
            Margin = new Thickness(0, 12, 0, 12)
        };
        Grid.SetRow(riskBox, 2);
        root.Children.Add(riskBox);

        var codeBox = new TextBox
        {
            Text = request.Code,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Padding = new Thickness(10, 8, 10, 8)
        };
        Grid.SetRow(codeBox, 3);
        root.Children.Add(codeBox);

        // "Remember this decision" section: let the user turn the things that
        // triggered this prompt (non-whitelisted imports / referenced folders)
        // into allow rules. The scope is chosen via a dropdown next to the
        // "允许并记住" button (default: this session).
        var importCandidates = GetImportCandidates(request);
        var pathCandidates = GetPathPrefixCandidates(request);
        var importChecks = new List<CheckBox>();
        var pathChecks = new List<CheckBox>();
        var hasCandidates = importCandidates.Count > 0 || pathCandidates.Count > 0;

        if (hasCandidates)
        {
            var remember = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            remember.Children.Add(new TextBlock
            {
                Text = "勾选要记住的项，下次符合规则的代码将自动放行：",
                FontSize = 12,
                Foreground = TryFindBrush("Brush.Text.Secondary") ?? Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var chips = new WrapPanel();
            foreach (var module in importCandidates)
            {
                var cb = new CheckBox { Content = $"允许导入 {module}", Margin = new Thickness(0, 0, 16, 6), Tag = module };
                importChecks.Add(cb);
                chips.Children.Add(cb);
            }
            foreach (var prefix in pathCandidates)
            {
                var cb = new CheckBox { Content = $"允许路径 {prefix}", Margin = new Thickness(0, 0, 16, 6), Tag = prefix };
                pathChecks.Add(cb);
                chips.Children.Add(cb);
            }
            remember.Children.Add(chips);

            remember.Children.Add(new TextBlock
            {
                Text = "本次会话：仅本次运行有效，重启应用后失效。永久：写入设置页「高级规则」，长期生效、可在那里查看或删除。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryFindBrush("Brush.Text.Muted") ?? Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 0)
            });

            Grid.SetRow(remember, 4);
            root.Children.Add(remember);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var denyButton = new Button
        {
            Content = "拒绝",
            Width = 96,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        // The scope picker is built into the remember button: clicking it drops a
        // menu with "this session" (default) and "permanent" choices.
        var rememberButton = new Button
        {
            Content = "允许并记住 ▾",
            Width = 132,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = hasCandidates ? Visibility.Visible : Visibility.Collapsed
        };
        var allowButton = new Button
        {
            Content = "允许本次",
            Width = 110,
            Height = 34,
            IsDefault = true
        };

        void CommitRemember(bool toSession)
        {
            foreach (var cb in importChecks.Where(c => c.IsChecked == true))
                ApplyImportRule((string)cb.Tag, toSession);
            foreach (var cb in pathChecks.Where(c => c.IsChecked == true))
                ApplyPathRule((string)cb.Tag, toSession);
            dialog.DialogResult = true;
            dialog.Close();
        }

        denyButton.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };
        allowButton.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };
        rememberButton.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            var sessionItem = new MenuItem { Header = "仅本次会话" };
            sessionItem.Click += (_, _) => CommitRemember(toSession: true);
            var permanentItem = new MenuItem { Header = "之后的所有会话 " };
            permanentItem.Click += (_, _) => CommitRemember(toSession: false);
            menu.Items.Add(sessionItem);
            menu.Items.Add(permanentItem);
            menu.PlacementTarget = rememberButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        };
        buttons.Children.Add(denyButton);
        buttons.Children.Add(rememberButton);
        buttons.Children.Add(allowButton);
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);

        dialog.Content = root;
        return dialog.ShowDialog() == true
            ? PythonExecutionApprovalDecision.Approved
            : PythonExecutionApprovalDecision.Denied;
    }

    private static IReadOnlyList<string> GetImportCandidates(PythonExecutionApprovalRequest request)
    {
        // Offer only the imports that are not already known-safe; those are the
        // ones that can push a run above the auto-approve bar.
        return request.Risk.Imports
            .Where(m => !PythonExecutionRiskAnalyzer.IsDefaultAllowedImport(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyList<string> GetPathPrefixCandidates(PythonExecutionApprovalRequest request)
    {
        // Suggest the parent folder of each referenced literal path as the prefix
        // to allow, so a whole working directory can be trusted in one click.
        var prefixes = new List<string>();
        foreach (var path in request.Risk.LiteralPaths)
        {
            string? prefix;
            try { prefix = Path.GetDirectoryName(path.Trim()); }
            catch { prefix = null; }
            prefix = string.IsNullOrWhiteSpace(prefix) ? path.Trim() : prefix;
            if (!string.IsNullOrWhiteSpace(prefix))
                prefixes.Add(prefix!);
        }
        return prefixes.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
    }

    private void ApplyImportRule(string module, bool sessionScope)
    {
        if (sessionScope)
            _sessionAllowList.AllowImport(module);
        else
            _settings.PythonToolAllowedImports = AppendCsv(_settings.PythonToolAllowedImports, module);
    }

    private void ApplyPathRule(string prefix, bool sessionScope)
    {
        if (sessionScope)
            _sessionAllowList.AllowPathPrefix(prefix);
        else
            _settings.PythonToolAllowedPathPrefixes = AppendCsv(_settings.PythonToolAllowedPathPrefixes, prefix);
    }

    private static string AppendCsv(string? existing, string addition)
    {
        var items = (existing ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!items.Any(i => string.Equals(i, addition.Trim(), StringComparison.OrdinalIgnoreCase)))
            items.Add(addition.Trim());
        return string.Join(",", items);
    }

    private static Border BuildPurposeCard(PythonExecutionApprovalRequest request)
    {
        var hasPurpose = !string.IsNullOrWhiteSpace(request.Description);
        var card = new Border
        {
            Background = hasPurpose
                ? TryFindBrush("Brush.Primary.Blockquote") ?? new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFF))
                : TryFindBrush("Brush.Bg.Tertiary") ?? new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
            BorderBrush = TryFindBrush("Brush.Primary.Border") ?? new SolidColorBrush(Color.FromRgb(0xC7, 0xD7, 0xF5)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 12)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "执行用途",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindBrush("Brush.Text.Secondary") ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = hasPurpose ? request.Description!.Trim() : "模型未说明用途。请在批准前仔细阅读下方代码。",
            FontSize = 14,
            FontStyle = hasPurpose ? FontStyles.Normal : FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Foreground = hasPurpose
                ? TryFindBrush("Brush.Text.Primary") ?? Brushes.Black
                : TryFindBrush("Brush.Text.Muted") ?? Brushes.Gray
        });

        card.Child = stack;
        return card;
    }

    private static string BuildRiskText(PythonExecutionApprovalRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("权限模式：").AppendLine(request.Options.PermissionMode.ToString());
        builder.Append("风险等级：").AppendLine(request.Risk.Level.ToString());
        builder.AppendLine("这段代码将以当前 Windows 用户权限运行。");

        if (request.Risk.Imports.Count > 0)
            builder.Append("导入模块：").AppendLine(string.Join(", ", request.Risk.Imports));

        if (request.Risk.Flags.Count == 0)
        {
            builder.AppendLine("未发现明显高风险操作。");
        }
        else
        {
            builder.AppendLine("风险项：");
            foreach (var flag in request.Risk.Flags)
                builder.Append(" - ").Append(flag.Severity).Append("：").AppendLine(flag.Message);
        }

        return builder.ToString().Trim();
    }

    private static Window? FindOwnerWindow()
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
               ?? app.MainWindow
               ?? app.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
    }

    private static FontFamily? TryFindFont(string key)
    {
        try { return Application.Current?.TryFindResource(key) as FontFamily; }
        catch { return null; }
    }

    private static Brush? TryFindBrush(string key)
    {
        try { return Application.Current?.TryFindResource(key) as Brush; }
        catch { return null; }
    }
}
