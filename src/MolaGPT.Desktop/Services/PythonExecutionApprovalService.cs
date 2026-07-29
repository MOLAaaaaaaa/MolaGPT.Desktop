using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Services;

public sealed class PythonExecutionApprovalService : IPythonExecutionApprovalService, IToolApprovalService
{
    private readonly IPythonSessionAllowList _sessionAllowList;
    private readonly SettingsViewModel _settings;
    private readonly IToolGrantStore _grants;

    public PythonExecutionApprovalService(
        IPythonSessionAllowList sessionAllowList,
        SettingsViewModel settings,
        IToolGrantStore grants)
    {
        _sessionAllowList = sessionAllowList;
        _settings = settings;
        _grants = grants;
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

    public async Task<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request,
        ToolPermissionMode mode,
        CancellationToken ct)
    {
        var needsPrompt = request.AlwaysAsk
                          || request.Capabilities.HasFlag(ToolCapability.Destructive)
                          || (mode == ToolPermissionMode.Approval
                              && request.Capabilities.HasFlag(ToolCapability.Write));
        if (!needsPrompt)
            return ToolApprovalDecision.Approved;

        // A previous "don't ask again" for this exact tool. Checked after the
        // capability gate so a grant can never widen what the mode already allows.
        if (!request.AlwaysAsk && _grants.IsGranted(request.ToolName))
            return ToolApprovalDecision.Approved;

        var app = Application.Current;
        if (app?.Dispatcher is null)
            return ToolApprovalDecision.Denied;

        ct.ThrowIfCancellationRequested();
        var (approved, scope) = await app.Dispatcher.InvokeAsync(() => ShowToolApprovalDialog(request, mode))
            .Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (approved)
            _grants.Grant(request.ToolName, scope);
        return approved ? ToolApprovalDecision.Approved : ToolApprovalDecision.Denied;
    }

    private static (bool Approved, ToolGrantScope Scope) ShowToolApprovalDialog(ToolApprovalRequest request, ToolPermissionMode mode)
    {
        var owner = FindOwnerWindow();
        var dialog = new Window
        {
            Title = "工具调用审批",
            Width = 680,
            Height = 500,
            MinWidth = 560,
            MinHeight = 400,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.CanResize,
            Background = TryFindBrush("Brush.Bg.Primary") ?? Brushes.White,
            FontFamily = TryFindFont("Font.Cjk") ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13
        };

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock
        {
            Text = request.DisplayName,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindBrush("Brush.Text.Primary") ?? Brushes.Black
        });
        heading.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(request.Description)
                ? "模型请求调用该工具"
                : request.Description,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindBrush("Brush.Text.Secondary") ?? Brushes.Gray
        });
        root.Children.Add(heading);

        var capabilityText = new TextBlock
        {
            Text = $"涉及权限：{FormatCapabilities(request.Capabilities)} · 审批模式：{FormatMode(mode)}"
                   + (request.AlwaysAsk ? " · 该操作每次均需确认" : string.Empty),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Background = TryFindBrush("Brush.Primary.Blockquote") ?? new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFF)),
            Foreground = TryFindBrush("Brush.Text.Primary") ?? Brushes.Black
        };
        Grid.SetRow(capabilityText, 1);
        root.Children.Add(capabilityText);

        var argsBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Padding = new Thickness(10)
        };
        Grid.SetRow(argsBox, 2);
        root.Children.Add(argsBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var deny = new Button { Content = "拒绝", Width = 96, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        // Three tiers rather than two. "始终允许" used to be the only way to stop
        // being asked, which made one hurried click a permanent grant; the session
        // tier covers the common case ("I'm doing this task now") without leaving
        // anything behind after the app closes.
        var sessionAllow = new Button { Content = "本次会话内允许", Width = 120, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        var alwaysAllow = new Button { Content = "始终允许", Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        var allow = new Button { Content = "仅允许本次", Width = 110, Height = 34, IsDefault = true };
        deny.Click += (_, _) => { dialog.Tag = "deny"; dialog.DialogResult = false; dialog.Close(); };
        sessionAllow.Click += (_, _) => { dialog.Tag = "session"; dialog.DialogResult = true; dialog.Close(); };
        alwaysAllow.Click += (_, _) => { dialog.Tag = "always"; dialog.DialogResult = true; dialog.Close(); };
        allow.Click += (_, _) => { dialog.Tag = "once"; dialog.DialogResult = true; dialog.Close(); };
        buttons.Children.Add(deny);
        buttons.Children.Add(sessionAllow);
        buttons.Children.Add(alwaysAllow);
        buttons.Children.Add(allow);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.ShowDialog();

        // Closing the dialog any other way (Esc, the title-bar X) must read as a
        // refusal, and an unrecognised tag as the narrowest grant.
        if (dialog.DialogResult != true) return (false, ToolGrantScope.Once);
        return (true, (dialog.Tag as string) switch
        {
            "always" => ToolGrantScope.Always,
            "session" => ToolGrantScope.Session,
            _ => ToolGrantScope.Once,
        });
    }

    private static string FormatCapabilities(ToolCapability capabilities)
    {
        var labels = new List<string>();
        if (capabilities.HasFlag(ToolCapability.Read)) labels.Add("读取");
        if (capabilities.HasFlag(ToolCapability.Write)) labels.Add("写入");
        if (capabilities.HasFlag(ToolCapability.External)) labels.Add("外部服务");
        if (capabilities.HasFlag(ToolCapability.Destructive)) labels.Add("破坏性");
        return labels.Count == 0 ? "未声明" : string.Join(" / ", labels);
    }

    private static string FormatMode(ToolPermissionMode mode) =>
        mode == ToolPermissionMode.FullAccess ? "完全权限" : "审批权限";

    private PythonExecutionApprovalDecision ShowApprovalDialog(PythonExecutionApprovalRequest request)
    {
        var owner = FindOwnerWindow();
        var dialog = new Window
        {
            Title = "Python 执行审批",
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
            Text = "模型请求在本机执行 Python 代码",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        // The two things worth interrupting someone for go above the fold, in their
        // own colour. Everything else stays in the detail box below, where a flat
        // list is fine — the failure this avoids is a reader skimming past "writes
        // to your Desktop" because it looked like "imported os".
        var headline = new StackPanel();
        if (BuildAlertBanner(request) is { } banner)
            headline.Children.Add(banner);
        headline.Children.Add(BuildPurposeCard(request));
        Grid.SetRow(headline, 1);
        root.Children.Add(headline);

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
                Text = "创建规则并记住：",
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
                var cb = new CheckBox { Content = $"允许访问 {prefix}", Margin = new Thickness(0, 0, 16, 6), Tag = prefix };
                pathChecks.Add(cb);
                chips.Children.Add(cb);
            }
            remember.Children.Add(chips);

            // The session-vs-permanent choice lives in the button's dropdown, where
            // each option says which it is. Explaining it again here just gave
            // people a paragraph to skip past.

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
            Content = "仅允许本次",
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
            var sessionItem = new MenuItem { Header = "本次会话内有效" };
            sessionItem.Click += (_, _) => CommitRemember(toSession: true);
            var permanentItem = new MenuItem { Header = "长期有效" };
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
            Text = hasPurpose ? request.Description!.Trim() : "模型未说明用途，请先确认下方代码",
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

    /// <summary>
    /// The loud part: only fires for the two findings that mean "this reaches your
    /// real files", and names what it will touch.
    /// </summary>
    private static Border? BuildAlertBanner(PythonExecutionApprovalRequest request)
    {
        var destructive = request.Risk.Flags.Any(f => string.Equals(f.Code, "destructive_file", StringComparison.Ordinal));
        var outsidePaths = request.Risk.Flags
            .Where(f => f.Code is "outside_allowed_path" or "absolute_path")
            .Select(f => f.Subject)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!destructive && outsidePaths.Count == 0) return null;

        var lines = new List<string>();
        if (destructive) lines.Add("此次执行涉及删除、移动或覆盖文件");
        if (outsidePaths.Count > 0)
        {
            lines.Add("此次执行将访问当前会话工作目录以外的位置：");
            lines.AddRange(outsidePaths.Take(4).Select(p => "　" + p));
            if (outsidePaths.Count > 4) lines.Add($"　等 {outsidePaths.Count - 4} 项");
        }

        var text = new TextBlock
        {
            Text = string.Join("\n", lines),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x1C, 0x1C)),
            FontWeight = FontWeights.SemiBold,
            LineHeight = 20
        };

        return new Border
        {
            Child = text,
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0xB0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };
    }

    private static string BuildRiskText(PythonExecutionApprovalRequest request)
    {
        var builder = new StringBuilder();

        if (request.Risk.Imports.Count > 0)
            builder.Append("涉及模块：").AppendLine(string.Join("、", request.Risk.Imports));

        var codes = request.Risk.Flags.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);

        var rest = request.Risk.Flags
            // Shown in the banner above.
            .Where(f => f.Code is not ("destructive_file" or "outside_allowed_path" or "absolute_path"))
            // The analyzer finds the same fact twice when a module import and a
            // call-site pattern both match — the module-specific line wins, since
            // it names what to allow.
            .Where(f => f.Code is not "process_execution" || !codes.Contains("restricted_import"))
            .Where(f => f.Code is not "network_call" || !codes.Contains("network_import"))
            .ToList();

        if (rest.Count == 0)
            builder.AppendLine("未发现其他需要关注的操作");
        else
            foreach (var flag in rest)
                builder.Append("· ").AppendLine(Describe(flag));

        return builder.ToString().Trim();
    }

    /// <summary>Plain-language version of a finding. Falls back to the analyzer's
    /// own wording for anything not worth a hand-written line.</summary>
    private static string Describe(PythonRiskFlag flag) => flag.Code switch
    {
        "package_install" => "涉及安装或修改 Python 包",
        "process_execution" => "涉及运行系统程序等敏感操作",
        "network_call" => "涉及网络访问",
        "environment_access" => "涉及读取环境变量或用户目录",
        "dynamic_execution" => "涉及动态执行代码",
        "restricted_import" => $"涉及 {flag.Subject} 模块，可执行系统程序等敏感操作",
        "network_import" => $"涉及网络模块 {flag.Subject}",
        "system_import" => $"涉及系统模块 {flag.Subject}",
        "unknown_import" => $"涉及非常用模块 {flag.Subject}",
        "denied_import" => $"涉及已被规则禁用的模块 {flag.Subject}",
        "denied_path" => $"涉及已被规则禁用的路径 {flag.Subject}",
        _ => flag.Message,
    };

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
