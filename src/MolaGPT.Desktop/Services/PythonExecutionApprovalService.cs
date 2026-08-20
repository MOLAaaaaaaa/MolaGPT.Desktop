using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
        var outsideWorkspace = request.Capabilities.HasFlag(ToolCapability.OutsideWorkspace);
        var needsPrompt = request.AlwaysAsk
                          || request.Capabilities.HasFlag(ToolCapability.Destructive)
                          || (mode == ToolPermissionMode.Approval
                              && (request.Capabilities.HasFlag(ToolCapability.Write) || outsideWorkspace));
        if (!needsPrompt)
            return ToolApprovalDecision.Approved;

        // A remembered folder or drive covers this read without asking again.
        if (outsideWorkspace
            && request.ResolvedPath is { Length: > 0 } resolved
            && _grants.IsPathGranted(resolved))
            return ToolApprovalDecision.Approved;

        // A previous "don't ask again" for this exact tool. Checked after the
        // capability gate so a grant can never widen what the mode already allows —
        // and never for a path outside the workspace, because that grant was
        // answered about the tool, not about the user's disk. Letting it apply
        // would turn one click on "始终允许 read_file" into standing permission to
        // read every file on the machine, which is not what the dialog offered.
        if (!request.AlwaysAsk && !outsideWorkspace && _grants.IsGranted(request.ToolName))
            return ToolApprovalDecision.Approved;

        var app = Application.Current;
        if (app?.Dispatcher is null)
            return ToolApprovalDecision.Denied;

        ct.ThrowIfCancellationRequested();
        var outcome = await app.Dispatcher.InvokeAsync(() => ShowToolApprovalDialog(request, mode))
            .Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (outcome.Approved)
        {
            if (outcome.PathPrefix is { Length: > 0 } prefix)
                _grants.GrantPath(prefix, outcome.Scope);
            else if (!outsideWorkspace)
                _grants.Grant(request.ToolName, outcome.Scope);
        }

        return outcome.Approved ? ToolApprovalDecision.Approved : ToolApprovalDecision.Denied;
    }

    /// <param name="PathPrefix">Set when the user chose to remember a folder or a
    /// drive rather than the tool itself.</param>
    private sealed record ToolApprovalOutcome(
        bool Approved,
        ToolGrantScope Scope = ToolGrantScope.Once,
        string? PathPrefix = null);

    private static ToolApprovalOutcome ShowToolApprovalDialog(ToolApprovalRequest request, ToolPermissionMode mode)
    {
        var outsideWorkspace = request.Capabilities.HasFlag(ToolCapability.OutsideWorkspace);
        var owner = FindOwnerWindow();
        var dialog = new Window
        {
            Title = outsideWorkspace ? "读取工作目录以外的文件" : "工具调用审批",
            Width = 680,
            Height = 500,
            MinWidth = 560,
            MinHeight = 400,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.CanResize,
            FontFamily = TryFindFont("Font.Cjk") ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13
        };
        ApplyDialogChrome(dialog);

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

        var details = new StackPanel();
        if (outsideWorkspace)
            details.Children.Add(BuildOutsideWorkspaceBanner(request));
        details.Children.Add(new TextBlock
        {
            Text = $"涉及权限：{FormatCapabilities(request.Capabilities)} · 审批模式：{FormatMode(mode)}"
                   + (request.AlwaysAsk ? " · 该操作每次均需确认" : string.Empty),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Background = TryFindBrush("Brush.Bg.Tertiary") ?? new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFF)),
            Foreground = TryFindBrush("Brush.Text.Primary") ?? Brushes.Black
        });
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var argsBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12
        };
        ApplyCodeBoxStyle(argsBox);
        Grid.SetRow(argsBox, 2);
        root.Children.Add(argsBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        // Whatever the user picks lands here; the default stands for every way of
        // leaving the dialog that is not a button (Esc, the title-bar X), all of
        // which must read as a refusal.
        var outcome = new ToolApprovalOutcome(false);
        void Finish(ToolApprovalOutcome value)
        {
            outcome = value;
            dialog.DialogResult = value.Approved;
            dialog.Close();
        }

        var deny = new Button { Content = "拒绝", Width = 96, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        ApplySecondaryButtonStyle(deny);
        deny.Click += (_, _) => Finish(new ToolApprovalOutcome(false));
        buttons.Children.Add(deny);

        if (outsideWorkspace)
        {
            // What is being answered here is "may this app read that place", not
            // "is read_file trustworthy". So the only thing worth remembering is a
            // location, and the widest one on offer is a drive the user names —
            // never "this tool, everywhere", which is what the generic branch
            // below would record.
            var grantOptions = BuildPathGrantOptions(request.ResolvedPath);
            var remember = new Button
            {
                Content = "允许并记住 ▾",
                Width = 132,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                // Nothing to offer when the path would not resolve; "仅允许本次"
                // is still available, so the call is not stuck.
                IsEnabled = grantOptions.Count > 0
            };
            ApplySecondaryButtonStyle(remember);
            remember.Click += (_, _) =>
            {
                var menu = CreateThemedMenu();
                menu.PlacementTarget = remember;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                foreach (var (header, scope, prefix) in grantOptions)
                {
                    var entry = new MenuItem { Header = header };
                    entry.Click += (_, _) => Finish(new ToolApprovalOutcome(true, scope, prefix));
                    menu.Items.Add(entry);
                }
                menu.IsOpen = true;
            };
            buttons.Children.Add(remember);
        }
        else
        {
            // Three tiers rather than two. "始终允许" used to be the only way to stop
            // being asked, which made one hurried click a permanent grant; the session
            // tier covers the common case ("I'm doing this task now") without leaving
            // anything behind after the app closes.
            var sessionAllow = new Button { Content = "本次会话内允许", Width = 120, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            var alwaysAllow = new Button { Content = "始终允许", Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            ApplySecondaryButtonStyle(sessionAllow);
            ApplySecondaryButtonStyle(alwaysAllow);
            sessionAllow.Click += (_, _) => Finish(new ToolApprovalOutcome(true, ToolGrantScope.Session));
            alwaysAllow.Click += (_, _) => Finish(new ToolApprovalOutcome(true, ToolGrantScope.Always));
            buttons.Children.Add(sessionAllow);
            buttons.Children.Add(alwaysAllow);
        }

        var allow = new Button { Content = "仅允许本次", Width = 110, Height = 34, IsDefault = true };
        ApplyPrimaryButtonStyle(allow);
        allow.Click += (_, _) => Finish(new ToolApprovalOutcome(true, ToolGrantScope.Once));
        buttons.Children.Add(allow);

        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        dialog.Content = root;
        return ShowApprovalModal(dialog, owner) ? outcome : new ToolApprovalOutcome(false);
    }

    /// <summary>
    /// The folder and the drive holding <paramref name="resolvedPath"/>, each
    /// offered for this session or for good. Named in full in the menu, because
    /// "记住" is worthless as a choice if the user cannot see what it covers.
    /// </summary>
    private static IReadOnlyList<(string Header, ToolGrantScope Scope, string Prefix)> BuildPathGrantOptions(string? resolvedPath)
    {
        var options = new List<(string, ToolGrantScope, string)>();
        if (string.IsNullOrWhiteSpace(resolvedPath)) return options;

        var folder = WorkspaceScope.FolderPrefix(resolvedPath);
        var drive = WorkspaceScope.DrivePrefix(resolvedPath);

        if (folder is not null)
        {
            options.Add(($"此文件夹（{folder}）· 仅本次会话", ToolGrantScope.Session, folder));
            options.Add(($"此文件夹（{folder}）· 永久", ToolGrantScope.Always, folder));
        }

        // Skipped when the folder already is the drive root, and for UNC paths,
        // where there is no drive to grant.
        if (drive is not null && !string.Equals(drive, folder, StringComparison.OrdinalIgnoreCase))
        {
            options.Add(($"整个磁盘（{drive}）· 仅本次会话", ToolGrantScope.Session, drive));
            options.Add(($"整个磁盘（{drive}）· 永久", ToolGrantScope.Always, drive));
        }

        return options;
    }

    private static Border BuildOutsideWorkspaceBanner(ToolApprovalRequest request)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "此次调用将读取本次对话工作目录以外的位置：",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindBrush("Brush.Danger.Foreground")
                         ?? new SolidColorBrush(Color.FromRgb(0x8A, 0x1C, 0x1C))
        });
        stack.Children.Add(new TextBlock
        {
            // The resolved path, not the argument the model wrote: "notes.txt" and
            // "..\..\.ssh\id_rsa" are the same shape in a tool call.
            Text = request.ResolvedPath ?? "（未能解析出具体路径）",
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Foreground = TryFindBrush("Brush.Danger.Foreground")
                         ?? new SolidColorBrush(Color.FromRgb(0x8A, 0x1C, 0x1C))
        });

        return new Border
        {
            Child = stack,
            Background = TryFindBrush("Brush.Danger.Surface")
                         ?? new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC)),
            BorderBrush = TryFindBrush("Brush.Danger.Border")
                          ?? new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0xB0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };
    }

    private static string FormatCapabilities(ToolCapability capabilities)
    {
        var labels = new List<string>();
        if (capabilities.HasFlag(ToolCapability.Read)) labels.Add("读取");
        if (capabilities.HasFlag(ToolCapability.Write)) labels.Add("写入");
        if (capabilities.HasFlag(ToolCapability.External)) labels.Add("外部服务");
        if (capabilities.HasFlag(ToolCapability.Destructive)) labels.Add("破坏性");
        if (capabilities.HasFlag(ToolCapability.OutsideWorkspace)) labels.Add("工作目录以外");
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
            FontFamily = TryFindFont("Font.Cjk") ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13
        };
        ApplyDialogChrome(dialog);

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
            Foreground = TryFindBrush("Brush.Text.Primary") ?? Brushes.Black,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        // The two things worth interrupting someone for go above the fold, in their
        // own colour. Everything else stays in the detail box below, where a flat
        // list is fine — the failure this avoids is a reader skimming past "writes
        // to your Desktop" because it looked like "imported os".
        var headline = new StackPanel();
        foreach (var banner in BuildAlertBanners(request))
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
        ApplyCodeBoxStyle(riskBox);
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
            FontSize = 12
        };
        ApplyCodeBoxStyle(codeBox);
        Grid.SetRow(codeBox, 3);
        root.Children.Add(codeBox);

        // "Remember this decision" section: turn what triggered this prompt —
        // non-whitelisted imports, folders outside the working directory — into
        // allow rules. The scope is chosen via a dropdown next to the "允许并记住"
        // button (default: this session).
        var importCandidates = GetImportCandidates(request);
        var folderCandidates = GetFolderCandidates(request);
        var importChecks = new List<CheckBox>();
        var folderChecks = new List<CheckBox>();
        var hasCandidates = importCandidates.Count > 0 || folderCandidates.Count > 0;

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
            // Folders, never drives. A read-only tool granted a whole volume can
            // at worst read it; Python granted one can rewrite it, so the widest
            // thing on offer here is the folder the code actually named.
            foreach (var folder in folderCandidates)
            {
                var cb = new CheckBox
                {
                    Content = $"允许读写 {folder}",
                    Margin = new Thickness(0, 0, 16, 6),
                    Tag = folder
                };
                folderChecks.Add(cb);
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
        ApplySecondaryButtonStyle(denyButton);
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
        ApplySecondaryButtonStyle(rememberButton);
        var allowButton = new Button
        {
            Content = "仅允许本次",
            Width = 110,
            Height = 34,
            IsDefault = true
        };
        ApplyPrimaryButtonStyle(allowButton);

        void CommitRemember(bool toSession)
        {
            foreach (var cb in importChecks.Where(c => c.IsChecked == true))
                ApplyImportRule((string)cb.Tag, toSession);
            foreach (var cb in folderChecks.Where(c => c.IsChecked == true))
                _grants.GrantPath(
                    (string)cb.Tag,
                    toSession ? ToolGrantScope.Session : ToolGrantScope.Always,
                    allowWriting: true);
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
            var menu = CreateThemedMenu();
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
        return ShowApprovalModal(dialog, owner)
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

    private void ApplyImportRule(string module, bool sessionScope)
    {
        if (sessionScope)
            _sessionAllowList.AllowImport(module);
        else
            _settings.PythonToolAllowedImports = AppendCsv(_settings.PythonToolAllowedImports, module);
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
    /// The findings that go above everything else, in two tones so the louder one
    /// keeps its meaning.
    ///
    /// Red is reserved for deletion — the operation nothing later can undo.
    /// Reaching outside the working directory is a real thing to know about, but
    /// it is usually "read that spreadsheet"; giving it the same red as
    /// <c>rmtree</c> would train the red out of both.
    /// </summary>
    private static IReadOnlyList<Border> BuildAlertBanners(PythonExecutionApprovalRequest request)
    {
        var banners = new List<Border>();

        if (request.Risk.Flags.Any(f => string.Equals(f.Code, "destructive_file", StringComparison.Ordinal)))
        {
            banners.Add(Banner(
                "此次执行涉及删除、移动或覆盖文件，无法撤销",
                "Brush.Danger.Surface", Color.FromRgb(0xFD, 0xEC, 0xEC),
                "Brush.Danger.Border", Color.FromRgb(0xE8, 0xB0, 0xB0),
                "Brush.Danger.Foreground", Color.FromRgb(0x8A, 0x1C, 0x1C)));
        }

        var outside = OutsidePaths(request);
        if (outside.Count > 0)
        {
            var lines = new List<string>
            {
                // Not "读取". Static analysis cannot tell a read from a write here,
                // so the prompt states the capability rather than guessing the
                // intent — see the comment in PythonExecutionRiskAnalyzer.
                "此次执行将访问工作目录以外的位置（Python 对这些位置可读、可写、可删）："
            };
            lines.AddRange(outside.Take(4).Select(p => "　" + p));
            if (outside.Count > 4) lines.Add($"　等 {outside.Count - 4} 项");

            banners.Add(Banner(
                string.Join("\n", lines),
                "Brush.Warning.Surface", Color.FromRgb(0xFF, 0xF6, 0xE5),
                "Brush.Warning.Border", Color.FromRgb(0xE8, 0xCE, 0x9A),
                "Brush.Warning.Foreground", Color.FromRgb(0x7A, 0x51, 0x0C)));
        }

        return banners;

        Border Banner(
            string text,
            string surfaceKey, Color surfaceFallback,
            string borderKey, Color borderFallback,
            string foregroundKey, Color foregroundFallback) => new()
        {
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryFindBrush(foregroundKey) ?? new SolidColorBrush(foregroundFallback),
                FontWeight = FontWeights.SemiBold,
                LineHeight = 20
            },
            Background = TryFindBrush(surfaceKey) ?? new SolidColorBrush(surfaceFallback),
            BorderBrush = TryFindBrush(borderKey) ?? new SolidColorBrush(borderFallback),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };
    }

    /// <summary>The literal paths outside the working directory this run named.</summary>
    private static IReadOnlyList<string> OutsidePaths(PythonExecutionApprovalRequest request) =>
        request.Risk.Flags
            .Where(f => string.Equals(f.Code, "outside_workspace", StringComparison.Ordinal))
            .Select(f => f.Subject)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Folders offered as "remember this". One per outside path, deduplicated —
    /// the folder holding a named file, or the folder itself when the code named a
    /// directory.
    ///
    /// Only fully-rooted literals qualify. The analyzer also picks up <c>~/…</c>
    /// and <c>%VAR%\…</c>, which are not paths yet; resolving them here would
    /// produce a grant for a folder that is not the one the code will open.
    /// </summary>
    private static IReadOnlyList<string> GetFolderCandidates(PythonExecutionApprovalRequest request) =>
        OutsidePaths(request)
            .Where(Path.IsPathRooted)
            .Select(WorkspaceScope.FolderPrefix)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

    private static string BuildRiskText(PythonExecutionApprovalRequest request)
    {
        var builder = new StringBuilder();

        if (request.Risk.Imports.Count > 0)
            builder.Append("涉及模块：").AppendLine(string.Join("、", request.Risk.Imports));

        var codes = request.Risk.Flags.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);

        var rest = request.Risk.Flags
            // Shown in the banners above.
            .Where(f => f.Code is not ("destructive_file" or "outside_workspace"))
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
        "outside_workspace" => $"涉及工作目录以外的路径 {flag.Subject}",
        _ => flag.Message,
    };

    /// <summary>
    /// The window an approval dialog should hang off.
    ///
    /// Every candidate is filtered for being visible and not minimized, which the
    /// earlier version did not do for <see cref="Application.MainWindow"/>. That
    /// mattered: this app hides its main window to the tray, and WPF hands
    /// activation back to the owner when a modal dialog closes — an owner sitting
    /// in the tray has nothing to hand it to, so the foreground fell through to
    /// whatever other application was next in the Z order.
    /// </summary>
    private static Window? FindOwnerWindow()
    {
        var app = Application.Current;
        if (app is null) return null;

        return Usable(app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive))
               ?? Usable(app.MainWindow)
               ?? app.Windows.OfType<Window>().FirstOrDefault(w => Usable(w) is not null);
    }

    /// <summary>A window that can actually receive the foreground: shown, and not
    /// sitting minimized or hidden in the tray.</summary>
    private static Window? Usable(Window? window) =>
        window is { IsVisible: true } && window.WindowState != WindowState.Minimized
            ? window
            : null;

    /// <summary>
    /// Shows an approval dialog so it actually reaches the user, and hands the
    /// foreground back to the app afterwards.
    ///
    /// Both ends need help, because these dialogs open in the middle of a turn
    /// rather than in response to a click:
    ///
    /// Opening — a turn can run for a minute, and people switch away while it
    /// does. MolaGPT is then not the foreground application, so Windows'
    /// foreground lock lets the dialog appear behind whatever the user is looking
    /// at, flashing in the taskbar instead of coming forward.
    ///
    /// Closing — this is the one that shows up as "另一个窗口突然跳到最前面". While
    /// a modal dialog is up, WPF disables the owner window (Win32 <c>WS_DISABLED</c>)
    /// and only re-enables it <em>after</em> the dialog's HWND has been destroyed.
    /// Windows, destroying the foreground window, looks for the next window to
    /// activate and skips every disabled one — ours included — so it lands on
    /// whichever other application is next in the Z order. Our window then ends up
    /// behind a window it was in front of a moment earlier.
    ///
    /// Fixing that up after <c>ShowDialog</c> returns does not work, and that was
    /// the first attempt here: by then the other application already holds the
    /// foreground, its activation is still in flight as posted messages, and
    /// whatever we do synchronously gets overwritten a frame later. The gap has to
    /// be closed rather than patched, so the owner is re-enabled and re-activated
    /// from <see cref="Window.Closing"/> — while the dialog still exists. By the
    /// time it is destroyed there is an enabled window of ours holding the
    /// foreground, and Windows has no reason to go looking. WPF re-enables the
    /// owner again on its own afterwards, which is harmless.
    /// </summary>
    private static bool ShowApprovalModal(Window dialog, Window? owner)
    {
        dialog.Loaded += (_, _) => Raise(dialog);
        dialog.Closing += (_, _) => HandBackForeground(owner);

        try
        {
            return dialog.ShowDialog() == true;
        }
        finally
        {
            // Belt and braces for the paths where Closing could not do its job —
            // the owner having gone to the tray mid-turn, say. A no-op when the
            // foreground is already where it should be.
            Raise(Usable(owner) ?? Usable(Application.Current?.MainWindow));
        }
    }

    /// <summary>
    /// Puts the foreground back on our window while the dialog is still alive.
    /// Goes through Win32 rather than <see cref="Window.Activate"/> because the
    /// owner is still disabled at this point, and activating a disabled window
    /// does not stick — it has to be re-enabled first.
    /// </summary>
    private static void HandBackForeground(Window? owner)
    {
        var target = Usable(owner) ?? Usable(Application.Current?.MainWindow);
        if (target is null) return;

        try
        {
            var handle = new WindowInteropHelper(target).Handle;
            if (handle == IntPtr.Zero) return;

            EnableWindow(handle, true);
            SetForegroundWindow(handle);
        }
        catch
        {
            // Racing a window that is closing is not worth failing an approval over.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void Raise(Window? window)
    {
        if (window is null) return;
        try
        {
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        }
        catch
        {
            // Racing a window that is closing is not worth failing an approval over.
        }
    }

    private static void ApplyDialogChrome(Window dialog)
    {
        dialog.Background = TryFindBrush("Brush.Bg.Primary") ?? dialog.Background;
        dialog.Foreground = TryFindBrush("Brush.Text.Primary") ?? dialog.Foreground;
    }

    private static void ApplyCodeBoxStyle(TextBox box)
    {
        if (TryFindStyle("ThemedCodeBox") is Style style)
            box.Style = style;
        else
        {
            box.Background = TryFindBrush("Brush.Bg.Secondary") ?? box.Background;
            box.Foreground = TryFindBrush("Brush.Text.Primary") ?? box.Foreground;
            box.BorderBrush = TryFindBrush("Brush.Border") ?? box.BorderBrush;
            box.Padding = new Thickness(10);
        }
    }

    private static void ApplySecondaryButtonStyle(Button button)
    {
        var width = button.Width;
        var height = button.Height;
        var margin = button.Margin;
        if (TryFindStyle("OutlineButton") is Style style)
            button.Style = style;
        button.Width = width;
        button.Height = height;
        button.Margin = margin;
        button.Padding = new Thickness(0);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.MinWidth = 0;
    }

    private static void ApplyPrimaryButtonStyle(Button button)
    {
        var width = button.Width;
        var height = button.Height;
        var margin = button.Margin;
        if (TryFindStyle("PillPrimaryButton") is Style style)
            button.Style = style;
        button.Width = width;
        button.Height = height;
        button.Margin = margin;
        button.Padding = new Thickness(0);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.MinWidth = 0;
    }

    private static ContextMenu CreateThemedMenu()
    {
        var menu = new ContextMenu
        {
            Background = TryFindBrush("Brush.Bg.Elevated") ?? Brushes.White,
            Foreground = TryFindBrush("Brush.Text.Primary") ?? Brushes.Black,
            BorderBrush = TryFindBrush("Brush.Border") ?? Brushes.Gray
        };
        var itemForeground = TryFindBrush("Brush.Text.Primary") ?? Brushes.Black;
        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, itemForeground));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        menu.ItemContainerStyle = itemStyle;
        return menu;
    }

    private static Style? TryFindStyle(string key)
    {
        try { return Application.Current?.TryFindResource(key) as Style; }
        catch { return null; }
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
