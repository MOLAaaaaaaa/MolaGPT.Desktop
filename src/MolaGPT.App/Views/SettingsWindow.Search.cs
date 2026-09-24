using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace MolaGPT.App.Views;

/// <summary>
/// 搜索设置. The index is read off the pages themselves — row titles, field
/// labels, section headings, check boxes, folds, buttons — so a row added to a
/// page is findable without anyone remembering to register it. What the pages
/// cannot say for themselves (the words people type for 模型服务 are 「API
/// Key」「密钥」, not the page's name) sits in <see cref="PageKeywords"/>.
/// </summary>
public partial class SettingsWindow
{
    private const int MaxSearchResults = 30;

    /// <summary>How long a result's row stays marked after the jump.</summary>
    private static readonly TimeSpan SearchHitDuration = TimeSpan.FromSeconds(1.6);

    private static readonly Dictionary<string, string> PageKeywords = new()
    {
        ["PAGE_Account"] = "账号 账户 登录 退出 用量 额度 点数 次数 同步 云同步 Tracks 个性化 记忆",
        ["PAGE_Appearance"] = "主题 深色 浅色 暗色 夜间 暗黑 外观 字体 字号 文字大小 缩放 动画 红点",
        ["PAGE_Chat"] = "Enter 回车 发送 换行 快捷键 输入 思考 推理 折叠 标题 画布 可视化 图表 HTML SVG Mermaid CSV",
        ["PAGE_System"] = "通知 提醒 托盘 最小化 关闭 后台 系统",
        ["PAGE_Personas"] = "角色 人设 系统提示词 提示词 prompt 角色卡 世界书 SillyTavern RisuAI 提示词编排 预设 preset 氛围 默认模型 温度 temperature",
        ["PAGE_Memory"] = "记忆 本地记忆 个人资料 称呼 职业 所在地 语言 历史对话 整理",
        ["PAGE_PostProcessing"] = "文本替换 替换 正则 regex 后处理",
        ["PAGE_Providers"] = "模型服务 API Key 密钥 key 接口 地址 BaseURL OpenAI Anthropic Claude Gemini DeepSeek OpenRouter Kimi BYOK 自定义模型 价格",
        ["PAGE_Search"] = "联网 搜索 网页 Tavily Exa DuckDuckGo",
        ["PAGE_ImageGeneration"] = "图像生成 画图 绘图 生图 文生图 DALL·E gpt-image",
        ["PAGE_Vision"] = "视觉 识图 看图 图片 多模态",
        ["PAGE_Sandbox"] = "Python 代码 执行 沙箱 解释器 pip 包 文件 读取 Read Glob Grep",
        ["PAGE_Browser"] = "浏览器 Chrome Edge Kimi 网站 自动化 操作网页",
        ["PAGE_Mcp"] = "MCP 工具 插件 服务器 Model Context Protocol",
        ["PAGE_Skills"] = "技能 skill SKILL.md 导入",
        ["PAGE_Approval"] = "权限 审批 授权 确认 始终允许 安全",
        ["PAGE_Agent"] = "远程 手机 移动端 Bridge 桥接 Agent Claude Code Codex",
    };

    private readonly List<SearchEntry> _searchIndex = [];

    /// <summary>Must run before the window gets its DataContext: bound text is
    /// still empty then, so only what the XAML spells out gets indexed — a
    /// status line's current wording is not a setting's name.</summary>
    private void BuildSearchIndex()
    {
        // Editors that are opened from inside a page rather than reached by
        // switching to it; a hit in one could only land on the list above it.
        var skipped = new Control[]
        {
            PART_ProviderEditor, PART_McpEditor, PART_MemoryDetail,
            PART_PersonaEditorSurface, PART_ResponseRuleEditor
        };

        string? group = null;
        foreach (var nav in PART_Nav.Items.OfType<ListBoxItem>())
        {
            if (nav.Classes.Contains("navgroup"))
            {
                group = nav.Content as string;
                continue;
            }

            if (nav.Tag is not string name || this.FindControl<StackPanel>(name) is not { } page) continue;

            var pageTitle = NavLabel(nav);
            var keywords = PageKeywords.GetValueOrDefault(name, string.Empty);
            _searchIndex.Add(new SearchEntry(pageTitle, pageTitle, nav, page, page, string.Empty,
                string.Empty, keywords, _searchIndex.Count, IsPage: true)
            {
                Group = group ?? "账号"
            });

            var section = string.Empty;
            var seen = new HashSet<string> { pageTitle };
            foreach (var node in page.GetLogicalDescendants().OfType<Control>())
            {
                if (skipped.Any(root => root == node || root.IsLogicalAncestorOf(node))) continue;

                if (node is TextBlock { Classes: var classes } heading && classes.Contains("section")
                    && Label(heading.Text) is { } sectionText && heading.Name is null)
                {
                    section = sectionText;
                    Add(sectionText, heading);
                    continue;
                }

                switch (node)
                {
                    case TextBlock text when text.Name is null
                                             && (text.Classes.Contains("rowtitle") || text.Classes.Contains("label")):
                        Add(Label(text.Text), text);
                        break;
                    case CheckBox or RadioButton when node is ContentControl { Content: string content }:
                        Add(Label(content), node);
                        break;
                    case Expander { Header: string header }:
                        Add(Label(header), node);
                        break;
                    case Button { Content: string caption } when node is not ToggleButton:
                        Add(Label(caption), node);
                        break;
                }
            }

            void Add(string? title, Control element)
            {
                if (title is null || !seen.Add(title)) return;
                var target = RowOf(element, page);
                _searchIndex.Add(new SearchEntry(title, pageTitle, nav, page, target,
                    section == title ? string.Empty : section, HintsIn(target), keywords,
                    _searchIndex.Count, IsPage: false)
                {
                    Element = element
                });
            }
        }
    }

    private void InitializeSearch()
    {
        BuildSearchIndex();
        PART_SettingsSearch.TextChanged += (_, _) => RunSearch();
        PART_SettingsSearch.KeyDown += OnSearchBoxKeyDown;
        PART_ClearSettingsSearch.Click += (_, _) =>
        {
            PART_SettingsSearch.Text = string.Empty;
            PART_SettingsSearch.Focus();
        };
        PART_SearchResults.KeyDown += OnSearchResultsKeyDown;
        PART_SearchResults.Tapped += (_, _) =>
        {
            if (PART_SearchResults.SelectedItem is SettingsSearchResult result) OpenSearchResult(result);
        };
    }

    private void FocusSearch()
    {
        PART_SettingsSearch.Focus();
        PART_SettingsSearch.SelectAll();
    }

    private void RunSearch()
    {
        var query = PART_SettingsSearch.Text?.Trim() ?? string.Empty;
        var searching = query.Length > 0;
        PART_ClearSettingsSearch.IsVisible = searching;
        PART_Nav.IsVisible = !searching;
        PART_SearchPanel.IsVisible = searching;
        if (!searching)
        {
            PART_SearchResults.ItemsSource = null;
            return;
        }

        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = _searchIndex
            .Select(entry => (Entry: entry, Score: Score(entry, query, tokens)))
            .Where(hit => hit.Score >= 0)
            .OrderBy(hit => hit.Score)
            .ThenBy(hit => hit.Entry.Order)
            .Take(MaxSearchResults)
            .Select(hit => new SettingsSearchResult(hit.Entry))
            .ToList();

        PART_SearchResults.ItemsSource = results;
        PART_SearchResults.SelectedIndex = results.Count > 0 ? 0 : -1;
        PART_SearchEmpty.Text = $"没有找到与「{query}」相关的设置。";
        PART_SearchEmpty.IsVisible = results.Count == 0;
    }

    /// <summary>Lower is better; -1 for no match. Every word has to land
    /// somewhere, ranked by where: the setting's own name, then its section,
    /// then its description. The page's name and synonyms count for a row only
    /// next to a word that matched the row itself — otherwise「远程」would list
    /// every button on 远程控制. A page whose synonyms hold the whole phrase
    /// (「api key」→ 模型服务) ranks with an exact name.</summary>
    private static int Score(SearchEntry entry, string query, string[] tokens)
    {
        var title = entry.Title.ToLowerInvariant();
        var total = entry.IsPage ? 0 : 1;
        var own = false;
        foreach (var token in tokens)
        {
            int best;
            if (title.StartsWith(token, StringComparison.Ordinal)) (best, own) = (0, true);
            else if (title.Contains(token, StringComparison.Ordinal)) (best, own) = (2, true);
            else if (entry.IsPage)
            {
                if (!entry.Keywords.Contains(token, StringComparison.OrdinalIgnoreCase)) return -1;
                (best, own) = (7, true);
            }
            else if (entry.Section.Contains(token, StringComparison.OrdinalIgnoreCase)) (best, own) = (4, true);
            else if (entry.Hints.Contains(token, StringComparison.OrdinalIgnoreCase)) (best, own) = (6, true);
            else if (entry.PageTitle.Contains(token, StringComparison.OrdinalIgnoreCase)) best = 5;
            else if (entry.Keywords.Contains(token, StringComparison.OrdinalIgnoreCase)) best = 8;
            else return -1;
            total += best;
        }

        if (!own) return -1;
        return entry.IsPage && entry.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase)
            ? Math.Min(total, 1)
            : total;
    }

    /// <summary>Where a hit can actually land. A row the page is hiding — the
    /// tray's close behaviour with the tray off, usage while signed out — is
    /// still listed, since that is exactly what someone looks for when they
    /// cannot find it; the jump lands on the card that holds the switch which
    /// reveals it, or on the page when nothing closer is showing.</summary>
    private static (Control? Target, Control? Name) Reachable(SearchEntry entry)
    {
        ILogical? hidden = null;
        for (ILogical? node = entry.Element ?? entry.Target; node is not null && node != entry.Page; node = node.LogicalParent)
        {
            if (node is Visual { IsVisible: false }) hidden = node;
        }

        if (hidden is null) return (entry.Target, entry.Element);
        var shown = hidden.LogicalParent as Control;
        if (shown is null || shown == entry.Page) return (null, null);
        var card = shown.GetSelfAndLogicalAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("settingscard"));
        return (card ?? shown, null);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when !string.IsNullOrEmpty(PART_SettingsSearch.Text):
                PART_SettingsSearch.Text = string.Empty;
                e.Handled = true;
                break;
            case Key.Down when PART_SearchResults.ItemCount > 0:
                PART_SearchResults.SelectedIndex = Math.Min(1, PART_SearchResults.ItemCount - 1);
                PART_SearchResults.ContainerFromIndex(PART_SearchResults.SelectedIndex)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when PART_SearchResults.SelectedItem is SettingsSearchResult result:
                OpenSearchResult(result);
                e.Handled = true;
                break;
        }
    }

    private void OnSearchResultsKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when PART_SearchResults.SelectedItem is SettingsSearchResult result:
                OpenSearchResult(result);
                e.Handled = true;
                break;
            case Key.Up when PART_SearchResults.SelectedIndex <= 0:
            case Key.Escape:
                FocusSearch();
                e.Handled = true;
                break;
        }
    }

    private void OpenSearchResult(SettingsSearchResult result)
    {
        var entry = result.Entry;
        PART_SettingsSearch.Text = string.Empty;

        if (PART_Nav.SelectedItem == entry.Nav) ShowSelectedPage();
        else PART_Nav.SelectedItem = entry.Nav;
        entry.Nav.Focus();
        if (entry.IsPage || Reachable(entry) is not ({ } target, var name)) return;

        var opened = false;
        foreach (var expander in target.GetSelfAndLogicalAncestors().OfType<Expander>())
        {
            if (expander.IsExpanded) continue;
            expander.IsExpanded = true;
            opened = true;
        }

        _ = RevealSearchHitAsync(target, name, opened);
    }

    /// <summary>Scrolls the hit to near the top of the page — BringIntoView would
    /// leave it on the bottom edge — and marks it for a moment. A fold that was
    /// just opened grows over ~200ms (RevealPresenter), so the page can be too
    /// short to scroll that far until it has finished.</summary>
    private async Task RevealSearchHitAsync(Control target, Control? name, bool waitForFold)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        ScrollToHit(target);
        Mark(target, name, true);

        if (waitForFold)
        {
            await Task.Delay(260);
            ScrollToHit(target);
        }

        DispatcherTimer.RunOnce(() => Mark(target, name, false), SearchHitDuration);
    }

    private void ScrollToHit(Control target)
    {
        PART_ContentScroll.UpdateLayout();
        if (target.TranslatePoint(default, PART_Pages) is not { } at) return;
        PART_ContentScroll.Offset = new Vector(0, Math.Max(0, at.Y + PART_Pages.Margin.Top - 28));
    }

    /// <summary>The card the row sits in takes the accent outline so the eye
    /// finds it; the row's own name turns the accent colour so the row is
    /// unambiguous inside a card that holds several. A class, not an
    /// animation, and removed by a timer — see CLAUDE.md on idle frames.</summary>
    private static void Mark(Control target, Control? name, bool on)
    {
        var card = target.GetSelfAndLogicalAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("settingscard"));
        foreach (var control in new[] { card, name })
        {
            if (control is null) continue;
            if (on) control.Classes.Add("searchhit");
            else control.Classes.Remove("searchhit");
        }
    }

    /// <summary>The unit a hit scrolls to: its settings row or field block when it
    /// has one, the element itself otherwise.</summary>
    private static Control RowOf(Control element, Control page)
    {
        for (ILogical? node = element; node is not null && node != page; node = node.LogicalParent)
        {
            if (node is Grid grid && grid.Classes.Contains("settingrow")) return grid;
            if (node is StackPanel panel && panel.Classes.Contains("fieldrow")) return panel;
        }

        return element;
    }

    /// <summary>The descriptions and choices under a row. They make「换行」find
    /// 「按 Enter 直接发送消息」and「深色」find「主题」, but only as a weak match.</summary>
    private static string HintsIn(Control target) =>
        target is TextBlock ? string.Empty
            : string.Join(' ', target.GetLogicalDescendants().Select(node => node switch
            {
                TextBlock text when text.Classes.Contains("hint") && text.Name is null => text.Text,
                ComboBoxItem { Content: string choice } => choice,
                _ => null
            }).OfType<string>());

    private static string NavLabel(ListBoxItem item) =>
        item.Content as string ?? AutomationProperties.GetName(item) ?? item.Tag as string ?? string.Empty;

    /// <summary>The text a person would type for this control, or null when it
    /// is a symbol, a placeholder, or still unbound.</summary>
    private static string? Label(string? text)
    {
        var label = text?.Trim().TrimStart('+').Trim().TrimEnd('.', '…').Trim();
        return string.IsNullOrEmpty(label) || !label.Any(char.IsLetter) ? null : label;
    }

    internal sealed record SearchEntry(
        string Title,
        string PageTitle,
        ListBoxItem Nav,
        StackPanel Page,
        Control Target,
        string Section,
        string Hints,
        string Keywords,
        int Order,
        bool IsPage)
    {
        /// <summary>The control whose text matched; <see cref="Target"/> is the row around it.</summary>
        public Control? Element { get; init; }

        /// <summary>The rail group a page sits under, shown as a page result's path.</summary>
        public string Group { get; init; } = string.Empty;
    }
}

/// <summary>One line in the search results: the setting's name, and where it lives.</summary>
public sealed class SettingsSearchResult
{
    internal SettingsSearchResult(SettingsWindow.SearchEntry entry)
    {
        Entry = entry;
        Title = entry.Title;
        Path = entry.IsPage
            ? entry.Group
            : entry.Section.Length > 0 ? $"{entry.PageTitle} › {entry.Section}" : entry.PageTitle;
    }

    internal SettingsWindow.SearchEntry Entry { get; }
    public string Title { get; }
    public string Path { get; }
}
