using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MolaGPT.App.Views;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

internal sealed class ToolApprovalService : IToolApprovalService, IPythonExecutionApprovalService
{
    private readonly IToolGrantStore _grants;
    private readonly IPythonSessionAllowList _sessionAllowList;
    private readonly SettingsViewModel _settings;

    public ToolApprovalService(
        IToolGrantStore grants,
        IPythonSessionAllowList sessionAllowList,
        SettingsViewModel settings)
    {
        _grants = grants;
        _sessionAllowList = sessionAllowList;
        _settings = settings;
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
        if (!needsPrompt) return ToolApprovalDecision.Approved;

        var forWriting = request.Capabilities.HasFlag(ToolCapability.Write)
                         || request.Capabilities.HasFlag(ToolCapability.Destructive);
        if (outsideWorkspace
            && request.ResolvedPath is { Length: > 0 } resolvedPath
            && _grants.IsPathGranted(resolvedPath, forWriting))
        {
            return ToolApprovalDecision.Approved;
        }

        // A previous "don't ask again" for this exact tool. Checked after the
        // capability gate so a grant can never widen what the mode already
        // allows — and never for a path outside the workspace, because that
        // grant was answered about the tool, not about the user's disk.
        if (!request.AlwaysAsk && !outsideWorkspace && _grants.IsGranted(request.ToolName))
            return ToolApprovalDecision.Approved;

        var outcome = await AskToolAsync(request, mode, ct).ConfigureAwait(true);
        if (!outcome.Approved) return ToolApprovalDecision.Denied;

        if (outcome.PathPrefix is { Length: > 0 } prefix)
            _grants.GrantPath(prefix, outcome.Scope, forWriting);
        else if (!outsideWorkspace)
            _grants.Grant(request.ToolName, outcome.Scope);

        return ToolApprovalDecision.Approved;
    }

    public async Task<PythonExecutionApprovalDecision> RequestApprovalAsync(
        PythonExecutionApprovalRequest request,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return PythonExecutionApprovalDecision.Denied;

        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = MainWindow();

                // No window means nobody can see the prompt. Deny rather than
                // run unattended code.
                if (owner is null || !owner.IsVisible) return PythonExecutionApprovalDecision.Denied;

                var approved = false;
                var closed = false;
                ToolApprovalWindow? dialog = null;

                // Assigned by the out parameter below; the remember handler only
                // reads it after the user has clicked, which is long after.
                IReadOnlyList<CheckBox> chips = [];

                void Finish(bool value)
                {
                    if (closed) return;
                    approved = value;
                    closed = true;
                    dialog?.Close();
                }

                dialog = BuildPythonDialog(
                    request,
                    onRemember: toSession =>
                    {
                        CommitRules(chips, toSession);
                        Finish(true);
                    },
                    out var approve,
                    out var deny,
                    out chips);

                approve.Click += (_, _) => Finish(true);
                deny.Click += (_, _) => Finish(false);

                await using var registration = ct.Register(() =>
                    Dispatcher.UIThread.Post(() => dialog.Close()));

                await dialog.ShowDialog(owner);
                closed = true;

                return approved && !ct.IsCancellationRequested
                    ? PythonExecutionApprovalDecision.Approved
                    : PythonExecutionApprovalDecision.Denied;
            });
        }
        catch
        {
            return PythonExecutionApprovalDecision.Denied;
        }
    }

    /// <summary>
    /// Turns the ticked chips into standing rules. Imports and folders go to
    /// different stores: an import is a language-level allowance and belongs to
    /// the Python tool's own list, a folder is a filesystem grant and belongs to
    /// the shared grant store the other tools read too.
    /// </summary>
    private void CommitRules(IReadOnlyList<CheckBox> rules, bool toSession)
    {
        foreach (var box in rules)
        {
            if (box.IsChecked != true || box.Tag is not ApprovalRule rule) continue;

            if (rule.IsImport)
                ApplyImportRule(rule.Subject, toSession);
            else
                _grants.GrantPath(
                    rule.Subject,
                    toSession ? ToolGrantScope.Session : ToolGrantScope.Always,
                    allowWriting: true);
        }
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
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!items.Any(i => string.Equals(i, addition.Trim(), StringComparison.OrdinalIgnoreCase)))
            items.Add(addition.Trim());
        return string.Join(",", items);
    }

    private static Window? MainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static string Describe(ToolCapability capabilities)
    {
        var names = new List<string>();
        if (capabilities.HasFlag(ToolCapability.Read)) names.Add("读取");
        if (capabilities.HasFlag(ToolCapability.Write)) names.Add("写入");
        if (capabilities.HasFlag(ToolCapability.External)) names.Add("外部服务");
        if (capabilities.HasFlag(ToolCapability.Destructive)) names.Add("破坏性");
        if (capabilities.HasFlag(ToolCapability.OutsideWorkspace)) names.Add("工作目录以外");
        return names.Count == 0 ? "未声明" : string.Join(" / ", names);
    }

    private static string FormatMode(ToolPermissionMode mode) =>
        mode == ToolPermissionMode.FullAccess ? "完全权限" : "审批权限";

    // ---- generic tool approval --------------------------------------------

    internal sealed record ToolApprovalOutcome(
        bool Approved,
        ToolGrantScope Scope = ToolGrantScope.Once,
        string? PathPrefix = null);

    private static async Task<ToolApprovalOutcome> AskToolAsync(
        ToolApprovalRequest request,
        ToolPermissionMode mode,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new ToolApprovalOutcome(false);

        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = MainWindow();
                if (owner is null || !owner.IsVisible) return new ToolApprovalOutcome(false);

                var outcome = new ToolApprovalOutcome(false);
                var dialog = BuildToolDialog(request, mode, value => outcome = value, out var finish);

                await using var registration = ct.Register(() =>
                    Dispatcher.UIThread.Post(() => dialog.Close()));
                await dialog.ShowDialog(owner);
                finish();
                return ct.IsCancellationRequested ? new ToolApprovalOutcome(false) : outcome;
            });
        }
        catch
        {
            return new ToolApprovalOutcome(false);
        }
    }

    internal static ToolApprovalWindow BuildToolDialog(
        ToolApprovalRequest request,
        ToolPermissionMode mode,
        Action<ToolApprovalOutcome> setOutcome,
        out Action finish)
    {
        var outsideWorkspace = request.Capabilities.HasFlag(ToolCapability.OutsideWorkspace);
        var dialog = new ToolApprovalWindow(
            outsideWorkspace ? "读取工作目录以外的文件" : "工具调用审批")
        {
            Width = 680,
            Height = 544,
            MinWidth = 584,
            MinHeight = 444
        };

        var root = new Grid
        {
            Margin = new Thickness(22, 20, 22, 22),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        var heading = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock
        {
            Text = request.DisplayName,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(request.Description) ? "模型请求调用该工具" : request.Description,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "secondary" }
        });
        root.Children.Add(heading);

        var details = new StackPanel();
        Grid.SetRow(details, 1);
        if (outsideWorkspace)
        {
            // Red here, unlike the Python dialog's amber: leaving the working
            // directory is the entire reason this prompt exists, so it is not
            // competing with a louder finding for attention.
            details.Children.Add(Banner("danger", new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock
                    {
                        Text = "将读取以下位置：",
                        TextWrapping = TextWrapping.Wrap
                    },
                    // The resolved path, not the argument the model wrote:
                    // "notes.txt" and "..\..\.ssh\id_rsa" are the same shape in
                    // a tool call.
                    new SelectableTextBlock
                    {
                        Text = request.ResolvedPath ?? "（未能解析出具体路径）",
                        FontFamily = MonoFamily(),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }));
        }

        details.Children.Add(new Border
        {
            Classes = { "factpill" },
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = $"涉及权限：{Describe(request.Capabilities)} · 审批模式：{FormatMode(mode)}"
                       + (request.AlwaysAsk ? " · 该操作每次均需确认" : string.Empty),
                TextWrapping = TextWrapping.Wrap
            }
        });
        root.Children.Add(details);

        var argsBox = new Border
        {
            Classes = { "codebox" },
            MinHeight = 140,
            Child = new ScrollViewer
            {
                Content = new SelectableTextBlock
                {
                    Text = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        Grid.SetRow(argsBox, 2);
        root.Children.Add(argsBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        var closed = false;
        void Finish(ToolApprovalOutcome value)
        {
            if (closed) return;
            setOutcome(value);
            closed = true;
            dialog.Close();
        }
        finish = () => closed = true;

        var deny = new Button { Content = "拒绝", Classes = { "outline" }, Padding = new Thickness(16, 7) };
        deny.Click += (_, _) => Finish(new ToolApprovalOutcome(false));
        buttons.Children.Add(deny);

        if (outsideWorkspace)
        {
            // What is being answered here is "may this app read that place", not
            // "is read_file trustworthy". So the only thing worth remembering is
            // a location — never "this tool, everywhere", which is what the
            // generic branch below would record.
            var remember = new Button
            {
                Content = "允许并记住 ▾",
                Classes = { "outline" },
                Padding = new Thickness(16, 7)
            };
            var menu = new MenuFlyout();
            foreach (var (label, scope, prefix) in BuildPathGrantOptions(request.ResolvedPath))
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => Finish(new ToolApprovalOutcome(true, scope, prefix));
                menu.Items.Add(item);
            }
            remember.Flyout = menu;
            // Nothing to offer when the path would not resolve; "仅允许本次" is
            // still available, so the call is not stuck.
            remember.IsEnabled = menu.Items.Count > 0;
            buttons.Children.Add(remember);
        }
        else
        {
            // Three tiers rather than two. "始终允许" used to be the only way to
            // stop being asked, which made one hurried click a permanent grant;
            // the session tier covers "I'm doing this task now" without leaving
            // anything behind after the app closes.
            var session = new Button
            {
                Content = "本次会话内允许",
                Classes = { "outline" },
                Padding = new Thickness(16, 7)
            };
            session.Click += (_, _) => Finish(new ToolApprovalOutcome(true, ToolGrantScope.Session));
            buttons.Children.Add(session);

            var always = new Button
            {
                Content = "始终允许",
                Classes = { "outline" },
                Padding = new Thickness(16, 7)
            };
            always.Click += (_, _) => Finish(new ToolApprovalOutcome(true, ToolGrantScope.Always));
            buttons.Children.Add(always);
        }

        var once = new Button { Content = "仅允许本次", Classes = { "approvalprimary" } };
        once.Click += (_, _) => Finish(new ToolApprovalOutcome(true));
        buttons.Children.Add(once);

        dialog.SetBody(root);
        return dialog;
    }

    /// <summary>
    /// The folder and the drive holding <paramref name="resolvedPath"/>, each
    /// offered for this session or for good. Named in full, because "记住" is
    /// worthless as a choice if the user cannot see what it covers.
    /// </summary>
    private static IReadOnlyList<(string Label, ToolGrantScope Scope, string Prefix)> BuildPathGrantOptions(
        string? resolvedPath)
    {
        var result = new List<(string, ToolGrantScope, string)>();
        if (string.IsNullOrWhiteSpace(resolvedPath)) return result;

        var folder = WorkspaceScope.FolderPrefix(resolvedPath);
        var drive = WorkspaceScope.DrivePrefix(resolvedPath);
        if (folder is not null)
        {
            result.Add(($"此文件夹（{folder}）· 仅本次会话", ToolGrantScope.Session, folder));
            result.Add(($"此文件夹（{folder}）· 永久", ToolGrantScope.Always, folder));
        }

        // Skipped when the folder already is the drive root, and for UNC paths,
        // where there is no drive to grant.
        if (drive is not null && !string.Equals(drive, folder, StringComparison.OrdinalIgnoreCase))
        {
            result.Add(($"整个磁盘（{drive}）· 仅本次会话", ToolGrantScope.Session, drive));
            result.Add(($"整个磁盘（{drive}）· 永久", ToolGrantScope.Always, drive));
        }
        return result;
    }

    // ---- Python execution approval ----------------------------------------

    /// <summary>A rule the user can tick to stop being asked about this again.</summary>
    internal sealed record ApprovalRule(bool IsImport, string Subject);

    /// <summary>
    /// The Python approval dialog.
    ///
    /// Everything the risk analyzer produced is shown, and in a deliberate
    /// order: the one or two findings worth interrupting someone for go on top
    /// in their own colour, the stated purpose next, the full finding list
    /// after that, and the code itself takes the remaining height. The failure
    /// this avoids is a reader skimming past "writes to your Desktop" because
    /// it was formatted the same as "imported os".
    /// </summary>
    /// <param name="onRemember">Invoked with true for session scope, false for
    /// permanent, once the user picks one from the remember button.</param>
    /// <param name="rules">The rule chips, for the caller to read on remember.</param>
    internal static ToolApprovalWindow BuildPythonDialog(
        PythonExecutionApprovalRequest request,
        Action<bool> onRemember,
        out Button approve,
        out Button deny,
        out IReadOnlyList<CheckBox> rules)
    {
        // Everything except the buttons scrolls, and nothing is given a share of
        // a fixed height.
        //
        // The star-sized code row this replaces was the obvious layout and the
        // wrong one: two banners plus a purpose card plus a findings list left
        // the code — the thing actually being approved — about ten pixels tall.
        // Sizing each section to its content and scrolling the overflow means a
        // busy prompt gets longer instead of illegible, and the decision row
        // stays pinned where it cannot be scrolled past.
        var root = new Grid
        {
            Margin = new Thickness(22, 20, 22, 22),
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        var body = new StackPanel();

        body.Children.Add(new TextBlock
        {
            Text = "模型请求在本机执行 Python 代码",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // Read first, so they open the scroll.
        foreach (var banner in BuildAlertBanners(request))
            body.Children.Add(banner);
        body.Children.Add(BuildPurposeCard(request));

        body.Children.Add(new Border
        {
            Classes = { "codebox" },
            MaxHeight = 150,
            Margin = new Thickness(0, 12, 0, 12),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new SelectableTextBlock
                {
                    Text = BuildRiskText(request),
                    // The findings are prose, not code: left in the dialog font
                    // so the code box below is the only monospaced thing here.
                    FontFamily = UiFamily(),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });

        body.Children.Add(new Border
        {
            Classes = { "codebox" },
            MinHeight = 200,
            MaxHeight = 420,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new SelectableTextBlock
                {
                    Text = request.Code,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.NoWrap
                }
            }
        });

        // "Remember this decision": turn what triggered the prompt —
        // non-whitelisted imports, folders outside the working directory — into
        // allow rules. The scope is chosen in the button's dropdown.
        var chips = new List<CheckBox>();
        foreach (var module in GetImportCandidates(request))
        {
            chips.Add(new CheckBox
            {
                Content = $"允许导入 {module}",
                Classes = { "rulechip" },
                Tag = new ApprovalRule(IsImport: true, module)
            });
        }

        // Folders, never drives. A read-only tool granted a whole volume can at
        // worst read it; Python granted one can rewrite it, so the widest thing
        // on offer here is the folder the code actually named.
        foreach (var folder in GetFolderCandidates(request))
        {
            chips.Add(new CheckBox
            {
                Content = $"允许读写 {folder}",
                Classes = { "rulechip" },
                Tag = new ApprovalRule(IsImport: false, folder)
            });
        }
        rules = chips;

        if (chips.Count > 0)
        {
            var remember = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            remember.Children.Add(new TextBlock { Text = "创建规则并记住：", Classes = { "rulelabel" } });

            var wrap = new WrapPanel();
            foreach (var chip in chips) wrap.Children.Add(chip);
            remember.Children.Add(wrap);
            body.Children.Add(remember);
        }

        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0)
        };
        root.Children.Add(scroll);

        approve = new Button { Content = "仅允许本次", Classes = { "approvalprimary" } };
        deny = new Button { Content = "拒绝", Classes = { "outline" }, Padding = new Thickness(16, 7) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(deny);

        if (chips.Count > 0)
        {
            // Name the folder when it is the only thing being granted. "允许并记住
            // 「桌面」" tells the user what they are agreeing to without making them
            // map a checkbox to a button; with several rules on offer there is no
            // single subject to name, so the generic label is the honest one.
            var soleFolder = chips.Count == 1
                    && chips[0].Tag is ApprovalRule { IsImport: false } only
                ? FriendlyFolderName(only.Subject)
                : null;

            var rememberButton = new Button
            {
                Content = soleFolder is null ? "允许并记住 ▾" : $"允许并记住「{soleFolder}」▾",
                Classes = { "outline" },
                Padding = new Thickness(16, 7)
            };
            var sessionItem = new MenuItem { Header = "本次会话内有效" };
            sessionItem.Click += (_, _) => onRemember(true);
            var permanentItem = new MenuItem { Header = "长期有效" };
            permanentItem.Click += (_, _) => onRemember(false);

            var menu = new MenuFlyout();
            menu.Items.Add(sessionItem);
            menu.Items.Add(permanentItem);
            rememberButton.Flyout = menu;
            buttons.Children.Add(rememberButton);
        }

        buttons.Children.Add(approve);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        var dialog = new ToolApprovalWindow("Python 执行审批")
        {
            Width = 704,
            Height = 624,
            MinWidth = 564,
            MinHeight = 464
        };
        dialog.SetBody(root);
        return dialog;
    }

    private static Border BuildPurposeCard(PythonExecutionApprovalRequest request)
    {
        var hasPurpose = !string.IsNullOrWhiteSpace(request.Description);
        var card = new Border { Classes = { "purposecard" } };
        if (!hasPurpose) card.Classes.Add("unstated");

        card.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "执行用途", Classes = { "cardlabel" } },
                new TextBlock
                {
                    Text = hasPurpose ? request.Description!.Trim() : "模型未说明用途，请先确认下方代码",
                    Classes = { "cardbody" },
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        return card;
    }

    /// <summary>
    /// The findings that go above everything else, in two tones so the louder
    /// one keeps its meaning. Red is reserved for deletion — the operation
    /// nothing later can undo.
    /// </summary>
    private static IReadOnlyList<Border> BuildAlertBanners(PythonExecutionApprovalRequest request)
    {
        var banners = new List<Border>();

        if (request.Risk.Flags.Any(f => string.Equals(f.Code, "destructive_file", StringComparison.Ordinal)))
        {
            banners.Add(Banner("danger", new TextBlock
            {
                Text = "包含删除、移动或覆盖文件的操作，不可撤销",
                TextWrapping = TextWrapping.Wrap
            }));
        }

        // The model said up front which folders it needs. That is a stronger
        // statement than anything inferred from the source, so it leads — and it
        // is phrased as the grant the user is about to make, not as a finding.
        var requested = RequestedPaths(request);
        if (requested.Count > 0)
        {
            var lines = new List<string> { "此次执行申请写入以下位置：" };
            lines.AddRange(requested.Take(4).Select(p => "　" + p));
            if (requested.Count > 4) lines.Add($"　等 {requested.Count - 4} 项");

            banners.Add(Banner("warning", new TextBlock
            {
                Text = string.Join("\n", lines),
                TextWrapping = TextWrapping.Wrap
            }));
        }

        // Paths the analyzer spotted in the source that the model did not declare.
        // Suppressed once a declaration covers them, so one folder never produces
        // two banners saying the same thing.
        var outside = OutsidePaths(request)
            .Where(p => !requested.Any(r => WorkspaceScope.Covers(r, p)))
            .ToArray();
        if (outside.Length > 0)
        {
            var lines = new List<string>
            {
                // "完全访问" rather than "读取": static analysis cannot tell a read
                // from a write here, and naming the weaker one would be the wrong
                // guess to make — see PythonExecutionRiskAnalyzer.
                "以下位置将被完全访问："
            };
            lines.AddRange(outside.Take(4).Select(p => "　" + p));
            if (outside.Length > 4) lines.Add($"　等 {outside.Length - 4} 项");

            banners.Add(Banner("warning", new TextBlock
            {
                Text = string.Join("\n", lines),
                TextWrapping = TextWrapping.Wrap
            }));
        }

        return banners;
    }

    private static Border Banner(string tone, Control child) =>
        new() { Classes = { "banner", tone }, Child = child };

    /// <summary>
    /// What to call a folder in a button. The user's own folders get the name
    /// they see in Explorer; anything else gets its leaf name, and the full path
    /// is on screen in the banner and the chip either way.
    /// </summary>
    private static string FriendlyFolderName(string path)
    {
        foreach (var (folder, label) in new[]
                 {
                     (Environment.SpecialFolder.DesktopDirectory, "桌面"),
                     (Environment.SpecialFolder.MyDocuments, "文档"),
                     (Environment.SpecialFolder.MyPictures, "图片"),
                     (Environment.SpecialFolder.MyMusic, "音乐"),
                     (Environment.SpecialFolder.MyVideos, "视频"),
                 })
        {
            string known;
            try { known = Environment.GetFolderPath(folder); }
            catch { continue; }

            if (!string.IsNullOrWhiteSpace(known)
                && string.Equals(
                    WorkspaceScope.Normalize(known),
                    WorkspaceScope.Normalize(path),
                    StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }

        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(leaf) ? path : leaf;
    }

    /// <summary>Folders the model declared it needs to write to, already resolved
    /// and already filtered to the ones not yet writable.</summary>
    private static IReadOnlyList<string> RequestedPaths(PythonExecutionApprovalRequest request) =>
        request.RequestedPaths ?? Array.Empty<string>();

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
    /// Imports offered as "remember this" — only the ones that are not already
    /// known-safe, since those are what can push a run above the auto-approve
    /// bar in the first place.
    /// </summary>
    private static IReadOnlyList<string> GetImportCandidates(PythonExecutionApprovalRequest request) =>
        request.Risk.Imports
            .Where(m => !PythonExecutionRiskAnalyzer.IsDefaultAllowedImport(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

    /// <summary>
    /// Folders offered as "remember this", deduplicated.
    ///
    /// Only fully-rooted literals qualify. The analyzer also picks up
    /// <c>~/…</c> and <c>%VAR%\…</c>, which are not paths yet; resolving them
    /// here would produce a grant for a folder that is not the one the code
    /// will open.
    /// </summary>
    private static IReadOnlyList<string> GetFolderCandidates(PythonExecutionApprovalRequest request) =>
        // Declared folders first: they are already resolved and already known to
        // be the thing being granted, so they are the offer most worth making.
        RequestedPaths(request)
            .Concat(OutsidePaths(request)
                .Where(Path.IsPathRooted)
                .Select(WorkspaceScope.FolderPrefix)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!))
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
            // call-site pattern both match — the module-specific line wins,
            // since it names what to allow.
            .Where(f => f.Code is not "process_execution" || !codes.Contains("restricted_import"))
            .Where(f => f.Code is not "network_call" || !codes.Contains("network_import"))
            .ToList();

        if (rest.Count == 0)
            builder.AppendLine("无其他敏感操作");
        else
            foreach (var flag in rest)
                builder.Append("· ").AppendLine(DescribeFlag(flag));

        return builder.ToString().Trim();
    }

    /// <summary>Plain-language version of a finding. Falls back to the
    /// analyzer's own wording for anything not worth a hand-written line.</summary>
    private static string DescribeFlag(PythonRiskFlag flag) => flag.Code switch
    {
        "package_install" => "安装或修改 Python 包",
        "process_execution" => "运行系统程序",
        "network_call" => "网络访问",
        "environment_access" => "读取环境变量或用户目录",
        "dynamic_execution" => "动态执行代码",
        "restricted_import" => $"{flag.Subject} 模块，可运行系统程序",
        "network_import" => $"网络模块 {flag.Subject}",
        "system_import" => $"系统模块 {flag.Subject}",
        "unknown_import" => $"非常用模块 {flag.Subject}",
        "denied_import" => $"已禁用的模块 {flag.Subject}",
        "denied_path" => $"已禁用的路径 {flag.Subject}",
        "outside_workspace" => $"工作目录以外的路径 {flag.Subject}",
        _ => flag.Message,
    };

    private static FontFamily MonoFamily() =>
        Resource<FontFamily>("Font.Mono") ?? new FontFamily("Consolas");

    private static FontFamily UiFamily() =>
        Resource<FontFamily>("Font.UI") ?? FontFamily.Default;

    /// <summary>
    /// Looks a token up against the variant that is actually on screen.
    ///
    /// The variant is not optional. Every colour in this app lives in a
    /// ThemeDictionaries entry keyed Light or Dark, and a lookup without one
    /// silently returns null for all of them — which is how this dialog came to
    /// render with no card, no code block and no banners at all.
    /// </summary>
    private static T? Resource<T>(string key) where T : class =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as T
            : null;
}
