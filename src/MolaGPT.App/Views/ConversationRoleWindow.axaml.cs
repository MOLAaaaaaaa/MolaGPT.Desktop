using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.App.Views;

public partial class ConversationRoleWindow : MolaContentWindow
{
    private ChatViewModel _chat = null!;
    private ConversationRoleContext _draft = new();
    private ConversationRoleContext _openedContext = new();
    private PersonaProfile _profile = new();
    private IReadOnlyList<MessageRow> _history = [];
    private readonly Dictionary<StoryMemory, TextBox> _editors = [];
    private OneShotCompletionClient? _client;
    private NotificationCenter? _notifications;
    private readonly CancellationTokenSource _lifetime = new();

    public ConversationRoleWindow()
    {
        InitializeComponent();
        PART_IdentityChoice.SelectionChanged += (_, _) =>
        {
            if (_loadingRoleChoices || PART_IdentityChoice.SelectedItem is not RoleIdentityChoice choice) return;
            _draft.UserPersonaId = choice.Id;
            _draft.UserName = null;
            _draft.UserDescription = null;
            UpdateIdentityBaseline();
            LoadIdentity(_identityName, _identityDescription, PART_Scenario.Text ?? "");
        };
        PART_LoreSource.SelectionChanged += (_, _) =>
        {
            if (_loadingRoleChoices) return;
            _draft.SharedLorebookIds = PART_LoreSource.SelectedIndex == 0 ? null
                : _draft.SharedLorebookIds ?? _profile.SharedLorebookIds.ToList();
            LoadSharedBooks();
        };
        PART_RefreshRequest.Click += (_, _) => LoadPromptDetails();
        PART_Cancel.Click += (_, _) => Close(false);
        PART_Save.Click += (_, _) => Save();
        PART_PromptSource.SelectionChanged += (_, _) => RefreshPromptEditor();
        PART_ConversationPrompt.TextChanged += (_, _) =>
        {
            if (PART_PromptSource.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(PART_ConversationPrompt.Text))
                PART_PromptSource.SelectedIndex = 1;
        };
        PART_UseDefaultPrompt.Click += (_, _) =>
        {
            PART_ConversationPrompt.Text = PART_DefaultPrompt.Text;
            PART_PromptSource.SelectedIndex = 2;
            PART_ConversationPrompt.Focus();
        };
        PART_Reset.Click += (_, _) =>
        {
            _draft.UserPersonaId = null;
            _draft.UserName = null;
            _draft.UserDescription = null;
            _draft.Scenario = null;
            LoadRoleChoices();
            if (PART_Greetings.ItemCount > 0) PART_Greetings.SelectedIndex = 0;
        };
        // Nothing overridden, nothing to restore. Leaving it live made it look
        // like the click had failed.
        PART_UserName.TextChanged += (_, _) => RefreshResetState();
        PART_UserDescription.TextChanged += (_, _) => RefreshResetState();
        PART_Scenario.TextChanged += (_, _) => RefreshResetState();
        PART_Greetings.SelectionChanged += (_, _) => RefreshResetState();
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (PART_ConversationTabs.SelectedItem == PART_AtmosphereTab) PART_UserName.Focus();
            else PART_ConversationPrompt.Focus();
        });
        PART_Generate.Click += async (_, _) => await GenerateAsync();
        PART_AddMemory.Click += (_, _) =>
        {
            var memory = new StoryMemory
            {
                SourceMessageIds = _history.LastOrDefault() is { } last ? [last.Id] : []
            };
            _draft.Memories.Add(memory);
            RefreshMemories();
            _editors[memory].Focus();
        };
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    public Task<bool> ShowForAsync(ChatViewModel chat, OneShotCompletionClient client, NotificationCenter notifications, Window owner)
    {
        _chat = chat;
        _client = client;
        _notifications = notifications;
        _draft = RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(chat.RoleContext));
        _openedContext = RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(chat.RoleContext));
        _profile = chat.ActivePersona?.Profile ?? new PersonaProfile();
        _history = chat.GetStoredHistory();
        PART_Avatar.Value = chat.ActivePersona?.Avatar;
        PART_RoleName.Text = chat.ActivePersona?.Name ?? "通用助手";
        PART_ConversationPrompt.Text = chat.ConversationSystemPrompt ?? "";
        PART_DefaultPrompt.Text = string.IsNullOrWhiteSpace(chat.ActivePersona?.SystemPrompt)
            ? chat.ActiveModelSystemPrompt ?? "" : chat.ActivePersona.SystemPrompt;
        PART_DefaultPromptExpander.IsVisible = !string.IsNullOrWhiteSpace(PART_DefaultPrompt.Text);
        PART_PromptSource.SelectedIndex = string.IsNullOrWhiteSpace(chat.ConversationSystemPrompt) ? 0
            : chat.SystemPromptMode == "append" ? 1 : 2;
        RefreshPromptEditor();
        PART_ModeLabel.Text = chat.InteractionModeLabel;
        RefreshModeUi();
        LoadRoleChoices();
        PART_AuthorNote.Text = _draft.AuthorNote;
        PART_AuthorNoteDepth.Value = _draft.AuthorNoteDepth;
        PART_AuthorNoteRole.SelectedIndex = _draft.AuthorNoteRole switch { "user" => 1, "assistant" => 2, _ => 0 };
        PART_AutoSummary.IsChecked = _draft.AutoSummarize;
        PART_AutoSummary.IsEnabled = chat.ActiveProvider is IOneShotTarget;
        PART_Summary.Text = _draft.Summary;
        var greetings = new[] { _profile.Greeting }.Concat(_profile.AlternateGreetings).ToArray();
        PART_GreetingPanel.IsVisible = greetings.Length > 1 && !_history.Any(row => row.Role == ChatMessage.RoleUser);
        PART_Greetings.ItemsSource = greetings.Select(text => text.Length > 75 ? text[..75] + "…" : text).ToArray();
        PART_Greetings.SelectedIndex = Math.Clamp(_draft.GreetingIndex, 0, greetings.Length - 1);

        var canGenerate = _history.Count > 0 && chat.ActiveProvider is IOneShotTarget;
        PART_Generate.IsEnabled = canGenerate;
        PART_GenerateHint.IsVisible = !canGenerate;
        if (_history.Count == 0)
            PART_GenerateHint.Text = "暂无可整理的消息";
        else if (chat.ActiveProvider is null)
            PART_GenerateHint.Text = "请先选择模型";
        else if (chat.ActiveProvider is not IOneShotTarget)
            PART_GenerateHint.Text = "当前模型不支持剧情整理";

        RefreshMemories();
        PART_CharacterBooks.ItemsSource = _profile.Lorebooks;
        PART_NoCharacterBooks.IsVisible = _profile.Lorebooks.Count == 0;
        LoadPromptDetails();
        chat.PropertyChanged += OnChatStateChanged;
        Closed += (_, _) => chat.PropertyChanged -= OnChatStateChanged;
        return ShowDialog<bool>(owner);
    }

    private void RefreshModeUi()
    {
        var atmosphere = _chat.IsAtmosphereMode;
        PART_AtmosphereTab.IsVisible = atmosphere;
        PART_StoryTab.IsVisible = atmosphere;
        PART_LoreTab.IsVisible = atmosphere;
        PART_ContextTab.IsVisible = atmosphere;
        PART_ReplacePrompt.Content = atmosphere ? "替换主提示词" : "替换提示词";
        PART_ConversationTabs.SelectedItem = atmosphere ? PART_AtmosphereTab : PART_PromptTab;
    }

    private bool _loadingRoleChoices;
    private string _identityName = "";
    private string _identityDescription = "";

    private void LoadRoleChoices()
    {
        _loadingRoleChoices = true;
        var choices = new List<RoleIdentityChoice> { new(null, "沿用角色"), new("", "自定义身份") };
        choices.AddRange((_chat.RoleLibrary?.Identities ?? []).Select(identity => new RoleIdentityChoice(identity.Id, identity.Name)));
        PART_IdentityChoice.ItemsSource = choices;
        PART_IdentityChoice.SelectedItem = choices.FirstOrDefault(choice => choice.Id == _draft.UserPersonaId);
        UpdateIdentityBaseline();
        LoadIdentity(_draft.UserName ?? _identityName, _draft.UserDescription ?? _identityDescription, _draft.Scenario ?? _profile.Scenario);
        PART_LoreSource.SelectedIndex = _draft.SharedLorebookIds is null ? 0 : 1;
        LoadSharedBooks();
        _loadingRoleChoices = false;
    }

    private void RefreshPromptEditor()
    {
        var custom = PART_PromptSource.SelectedIndex > 0;
        PART_ConversationPrompt.IsVisible = custom;
        PART_PromptVariables.IsVisible = custom;
        PART_ConversationPrompt.PlaceholderText = PART_PromptSource.SelectedIndex == 1 ? "填写补充要求" : "填写提示词";
        PART_DefaultPromptExpander.IsExpanded = !custom;
    }

    private void OnChatStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ChatViewModel.RoleEvaluation) or nameof(ChatViewModel.LastRolePromptTrace))) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) LoadPromptDetails(refreshMatches: e.PropertyName == nameof(ChatViewModel.RoleEvaluation));
        });
    }

    private void UpdateIdentityBaseline()
    {
        var identity = _chat.RoleLibrary?.ResolveIdentity(_draft.UserPersonaId ?? _profile.UserPersonaId);
        _identityName = identity?.Name ?? _profile.UserName;
        _identityDescription = identity?.Description ?? _profile.UserDescription;
    }

    private void LoadSharedBooks()
    {
        var selected = _draft.SharedLorebookIds ?? _profile.SharedLorebookIds;
        var books = (_chat.RoleLibrary?.Books ?? []).OrderBy(book => book.Name).ToArray();
        var custom = _draft.SharedLorebookIds is not null;
        var inherited = books.Where(book => selected.Contains(book.Id)).ToArray();
        PART_InheritedBooks.ItemsSource = inherited;
        PART_InheritedBooks.IsVisible = !custom;
        PART_ConversationBooks.Children.Clear();
        foreach (var book in books)
        {
            var toggle = new CheckBox { Content = $"{book.Name} · {book.Entries.Count} 条" + (book.Enabled ? "" : " · 已停用"),
                Tag = book.Id, IsChecked = selected.Contains(book.Id) };
            toggle.Click += (_, _) => _draft.SharedLorebookIds = SelectedBooks();
            PART_ConversationBooks.Children.Add(toggle);
        }
        PART_ConversationBooks.IsVisible = custom;
        PART_NoSharedBooks.IsVisible = custom ? books.Length == 0 : inherited.Length == 0;
        PART_NoSharedBooks.Text = custom ? "共享库为空" : "未关联共享世界书";
    }

    private List<string> SelectedBooks() => PART_ConversationBooks.Children.OfType<CheckBox>()
        .Where(box => box.IsChecked == true).Select(box => (string)box.Tag!).ToList();

    private async void OnManageIdentities(object? sender, RoutedEventArgs e)
    {
        if (_chat.RoleLibrary is not { } library) return;
        _draft.UserName = Override(PART_UserName.Text, _identityName);
        _draft.UserDescription = Override(PART_UserDescription.Text, _identityDescription);
        _draft.Scenario = Override(PART_Scenario.Text, _profile.Scenario);
        await new UserPersonaWindow
        {
            CheckIdentityDeletion = identity => _draft.UserPersonaId == identity.Id ? "当前对话仍在使用这个身份。" : null
        }.ShowForAsync(library, _notifications, this);
        LoadRoleChoices();
    }

    private async void OnManageSharedBooks(object? sender, RoutedEventArgs e)
    {
        if (_chat.RoleLibrary is not { } library) return;
        var window = new LorebookWindow(_notifications)
        {
            Title = "共享世界书",
            CheckBookDeletion = book => (_draft.SharedLorebookIds ?? _profile.SharedLorebookIds).Contains(book.Id)
                ? "当前对话仍在使用这本世界书。" : library.BookDeletionReason(book)
        };
        var books = await window.ShowForAsync(library.Books.ToList(), this, _profile.Compatibility);
        if (books is null) return;
        try { library.SaveBooks(books); }
        catch (Exception ex) { _notifications?.Error("共享世界书保存失败", ex.Message, "lorebook-library"); }
        LoadSharedBooks();
    }

    private void LoadPromptDetails(bool refreshMatches = true)
    {
        if (refreshMatches) LoadLoreHits(_chat.ActiveLoreEntries);
        PART_LoreDecisions.Children.Clear();
        foreach (var decision in _chat.RoleEvaluation?.Lore.Decisions ?? [])
        {
            var text = new TextBlock
            {
                Text = decision.BookName + " · " + decision.EntryName + "：" + decision.Reason,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            };
            text.Classes.Add("hint");
            PART_LoreDecisions.Children.Add(text);
        }
        PART_LoreDecisionPanel.IsVisible = PART_LoreDecisions.Children.Count > 0;
        PART_RequestMessages.Children.Clear();
        var evaluation = _chat.RoleEvaluation;
        PART_ContextBudget.Text = evaluation is null ? "" :
            $"世界书与剧情 · 约 {evaluation.Lore.EstimatedTokens + evaluation.StoryEstimatedTokens:N0} / {evaluation.ContextBudget:N0} tokens";
        PART_ContextBudget.IsVisible = evaluation is not null;
        var trace = _chat.LastRolePromptTrace;
        PART_RequestCaption.Text = trace is not null ? "最近一次请求 · " + trace.CreatedAt.ToLocalTime().ToString("HH:mm:ss")
            : evaluation is not null ? "角色上下文预览" : "尚无请求记录";
        var messages = trace?.Messages ?? (evaluation is null ? [] : new[] { new RolePromptMessage("system", evaluation.SystemPrompt) }
            .Concat(evaluation.Plan.Examples.SelectMany(block => block))
            .Concat(evaluation.Plan.Insertions.Select(item => new RolePromptMessage(item.Role,
                item.Source + " · " + (item.Depth == 0 ? "对话末尾" : $"距末尾 {item.Depth} 条消息") + "\n\n" + item.Text))).ToArray());
        var messageIndex = 0;
        foreach (var message in messages)
        {
            var label = message.Role switch { "user" => "用户", "assistant" => "角色", "tool" => "工具", _ => "系统" };
            var sources = evaluation?.Plan.Insertions.Where(item => message.Text.Contains(item.Text, StringComparison.Ordinal))
                .Select(item => item.Source).Distinct().ToArray() ?? [];
            if (sources.Length > 0) label += " · " + string.Join("、", sources);
            PART_RequestMessages.Children.Add(new Expander
            {
                Header = $"{++messageIndex}. {label}", Classes = { "settingssection" },
                Content = new SelectableTextBlock { Text = message.Text, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 10) },
                Margin = new Thickness(0, 0, 0, 8)
            });
        }
    }

    private void OnInsertPromptVariable(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string token }) return;
        var text = PART_ConversationPrompt.Text ?? "";
        var start = Math.Min(PART_ConversationPrompt.SelectionStart, PART_ConversationPrompt.SelectionEnd);
        var end = Math.Max(PART_ConversationPrompt.SelectionStart, PART_ConversationPrompt.SelectionEnd);
        PART_ConversationPrompt.Text = text[..start] + token + text[end..];
        PART_ConversationPrompt.CaretIndex = start + token.Length;
        PART_PromptVariables.Flyout?.Hide();
        PART_ConversationPrompt.Focus();
    }

    private void LoadLoreHits(IReadOnlyList<LorebookHit> hits)
    {
        PART_LoreHits.Children.Clear();
        PART_LoreCaption.Text = _chat.RoleEvaluation is null ? "匹配结果" : $"最近一次匹配 · {hits.Count} 条";
        PART_LoreEmpty.IsVisible = hits.Count == 0;
        PART_LoreEmpty.Text = _chat.RoleEvaluation is null ? "尚无匹配记录" : "本轮未选中条目";
        foreach (var hit in hits)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(new TextBlock
            {
                Text = $"{hit.BookName} · {hit.Entry.DisplayName}",
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            header.Children.Add(Muted($"≈{hit.EstimatedTokens} tokens", 1));

            // Boxed rather than bare rows: without a surface the entries read as
            // stray lines of text under the caption. The fold inside wears
            // settingssection so its chevron sits at the leading edge — a filled
            // box with a chevron on the right is this app's ComboBox, and one of
            // those directly above (共享世界书) is what these rows sat under.
            PART_LoreHits.Children.Add(new Border
            {
                Classes = { "settingscard" },
                Padding = new Thickness(14, 6, 14, 8),
                Margin = default,
                Child = new Expander
                {
                    Classes = { "settingssection" },
                    Header = header,
                    Content = new SelectableTextBlock
                    {
                        Text = ((hit.Position ?? hit.Entry.Placement) switch
                        {
                            LorePosition.BeforeCharacter => "角色资料前", LorePosition.AfterCharacter => "角色资料后",
                            LorePosition.BeforeNote => "对话补充前", LorePosition.AfterNote => "对话补充后",
                            LorePosition.BeforeExamples => "对话示例前", LorePosition.AfterExamples => "对话示例后",
                            LorePosition.Outlet => "指定位置：" + hit.Entry.OutletName,
                            _ => (hit.Depth ?? hit.Entry.Depth) == 0 ? "对话末尾" : $"距末尾 {hit.Depth ?? hit.Entry.Depth} 条消息"
                        }) + "\n\n" + hit.Content,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 4)
                    }
                }
            });
        }
    }

    private static TextBlock Muted(string text, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        block.Classes.Add("hint");
        Grid.SetColumn(block, column);
        return block;
    }

    private void LoadIdentity(string name, string description, string scenario)
    {
        PART_UserName.Text = name;
        PART_UserDescription.Text = description;
        PART_Scenario.Text = scenario;
        RefreshResetState();
    }

    private void RefreshResetState() => PART_Reset.IsEnabled =
        _draft.UserPersonaId is not null
        || Override(PART_UserName.Text, _identityName) is not null
        || Override(PART_UserDescription.Text, _identityDescription) is not null
        || Override(PART_Scenario.Text, _profile.Scenario) is not null
        || PART_Greetings.SelectedIndex > 0;

    private void RefreshMemories()
    {
        FlushMemoryEditors();
        _editors.Clear();
        PART_MemoryList.Children.Clear();
        PART_MemoryEmpty.IsVisible = _draft.Memories.Count == 0;
        foreach (var memory in _draft.Memories)
            PART_MemoryList.Children.Add(BuildMemoryCard(memory));
    }

    private Control BuildMemoryCard(StoryMemory memory)
    {
        var editor = new TextBox
        {
            Text = memory.Text,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 62,
            MaxHeight = 150
        };
        editor.Classes.Add("field");
        _editors[memory] = editor;

        var remove = new Button { Content = "删除", Margin = new Thickness(10, 0, 0, 0) };
        remove.Classes.Add("textlink");
        remove.Click += (_, _) =>
        {
            _draft.Memories.Remove(memory);
            RefreshMemories();
        };

        var pinned = new CheckBox { Content = "优先保留", IsChecked = memory.Pinned };
        pinned.Click += (_, _) => memory.Pinned = pinned.IsChecked == true;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(Muted($"事件 {_draft.Memories.IndexOf(memory) + 1}", 0));
        Grid.SetColumn(pinned, 1);
        header.Children.Add(pinned);
        Grid.SetColumn(remove, 2);
        header.Children.Add(remove);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(editor);
        var source = new ContentControl { Content = BuildMemorySource(memory) };
        body.Children.Add(source);
        editor.LostFocus += (_, _) =>
        {
            if (ApplyMemoryText(memory, editor.Text)) source.Content = BuildMemorySource(memory);
        };

        return new Border { Classes = { "settingscard" }, Padding = new Thickness(14, 10, 14, 12), Margin = default, Child = body };
    }

    private Expander? BuildMemorySource(StoryMemory memory)
    {
        if (memory.SourceMessageIds.Count == 0) return null;
        return new Expander
        {
            // 「来源消息」 promises a quote. A hand-written event has none, only
            // the point in the transcript it was written at, so it says so.
            Header = memory.SourceQuote.Length > 0 ? "来源消息" : "记录位置",
            Margin = new Thickness(0, 8, 0, 0),
            Content = new TextBlock
            {
                Text = string.Join("\n\n", _history
                    .Where(row => memory.SourceMessageIds.Contains(row.Id)).Select(row => row.Content)),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Classes = { "muted" }
            }
        };
    }

    /// <summary>
    /// Re-anchors a hand-edited event to the end of the transcript: the quote it
    /// was extracted from no longer backs the text the user just typed.
    /// </summary>
    private bool ApplyMemoryText(StoryMemory memory, string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed == memory.Text) return false;
        memory.Text = trimmed;
        memory.SourceQuote = "";
        memory.SourceMessageIds = _history.LastOrDefault() is { } last ? [last.Id] : [];
        return true;
    }

    /// <summary>Pull the cards' text down before anything reads the draft — Save
    /// and 整理剧情 can both be reached without the focused box losing focus.</summary>
    private void FlushMemoryEditors()
    {
        foreach (var (memory, editor) in _editors) ApplyMemoryText(memory, editor.Text);
    }

    private async Task GenerateAsync()
    {
        if (_client is null || _chat.ActiveProvider is not { } provider || _chat.ActiveModel is not { } model) return;
        FlushMemoryEditors();
        PART_StoryEditor.IsEnabled = false;
        PART_Save.IsEnabled = false;
        PART_Generate.Content = "整理中…";
        var ct = _lifetime.Token;
        try
        {
            var context = RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(_draft));
            context.Summary = PART_Summary.Text ?? "";
            var result = await StorySummaryService.GenerateAsync(_client, provider, model, _history, context, ct);
            ct.ThrowIfCancellationRequested();
            _draft.Summary = result.Summary;
            _draft.SummarySourceIds = result.SummarySourceIds;
            _draft.LastSummarizedMessageId = result.LastSummarizedMessageId;
            _draft.Memories = result.Memories;
            PART_Summary.Text = result.Summary;
            RefreshMemories();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _notifications?.Error("剧情整理失败", ex.Message, "story-summary"); }
        finally
        {
            PART_Generate.Content = "整理剧情";
            PART_StoryEditor.IsEnabled = true;
            PART_Save.IsEnabled = true;
        }
    }

    private void Save()
    {
        FlushMemoryEditors();
        _draft.UserName = Override(PART_UserName.Text, _identityName);
        _draft.UserDescription = Override(PART_UserDescription.Text, _identityDescription);
        _draft.Scenario = Override(PART_Scenario.Text, _profile.Scenario);
        _draft.GreetingIndex = PART_Greetings.SelectedIndex;
        _draft.AuthorNote = PART_AuthorNote.Text ?? "";
        _draft.AuthorNoteDepth = (int)(PART_AuthorNoteDepth.Value ?? 4);
        _draft.AuthorNoteRole = PART_AuthorNoteRole.SelectedIndex switch { 1 => "user", 2 => "assistant", _ => "system" };
        _draft.AutoSummarize = PART_AutoSummary.IsChecked == true;
        var summary = PART_Summary.Text ?? "";
        if (summary != _draft.Summary)
        {
            _draft.SummarySourceIds = string.IsNullOrWhiteSpace(summary) ? [] : _history.Select(row => row.Id).ToList();
            _draft.LastSummarizedMessageId = _draft.SummarySourceIds.LastOrDefault();
        }
        _draft.Summary = summary;
        _draft.Memories.RemoveAll(memory => string.IsNullOrWhiteSpace(memory.Text));
        try
        {
            var summaryChanged = _draft.Summary != _openedContext.Summary
                || !_draft.SummarySourceIds.SequenceEqual(_openedContext.SummarySourceIds)
                || _draft.LastSummarizedMessageId != _openedContext.LastSummarizedMessageId;
            var memoriesChanged = RoleJson.Serialize(_draft.Memories) != RoleJson.Serialize(_openedContext.Memories);
            var current = RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(_chat.RoleContext));
            if ((summaryChanged || memoriesChanged) && current.HistoryRevision != _openedContext.HistoryRevision)
                throw new InvalidOperationException("对话记录已更新，请重新打开设定后编辑剧情。");
            current.UserPersonaId = _draft.UserPersonaId;
            current.SharedLorebookIds = _draft.SharedLorebookIds;
            current.UserName = _draft.UserName;
            current.UserDescription = _draft.UserDescription;
            current.Scenario = _draft.Scenario;
            current.GreetingIndex = _draft.GreetingIndex;
            current.AuthorNote = _draft.AuthorNote;
            current.AuthorNoteDepth = _draft.AuthorNoteDepth;
            current.AuthorNoteRole = _draft.AuthorNoteRole;
            current.AutoSummarize = _draft.AutoSummarize;
            if (summaryChanged)
            {
                current.Summary = _draft.Summary;
                current.SummarySourceIds = _draft.SummarySourceIds;
                current.LastSummarizedMessageId = _draft.LastSummarizedMessageId;
            }
            if (memoriesChanged)
            {
                var deleted = _openedContext.Memories.Where(memory => !_draft.Memories.Any(item => item.Id == memory.Id))
                    .Select(memory => memory.Id).ToHashSet(StringComparer.Ordinal);
                current.Memories.RemoveAll(memory => deleted.Contains(memory.Id));
                foreach (var memory in _draft.Memories)
                {
                    var original = _openedContext.Memories.FirstOrDefault(item => item.Id == memory.Id);
                    if (original is not null && RoleJson.Serialize(memory) == RoleJson.Serialize(original)) continue;
                    var index = current.Memories.FindIndex(item => item.Id == memory.Id);
                    if (index < 0) current.Memories.Add(memory);
                    else current.Memories[index] = memory;
                }
            }
            _chat.SaveRoleContext(current);
            _chat.SaveConversationSystemPrompt(PART_PromptSource.SelectedIndex == 0 ? null : PART_ConversationPrompt.Text);
            _chat.SaveSystemPromptMode(PART_PromptSource.SelectedIndex == 1 ? "append" : "override");
            _chat.RefreshRoleGreeting();
            Close(true);
        }
        catch (Exception ex) { _notifications?.Error("对话设定保存失败", ex.Message, "role-context"); }
    }

    private static string? Override(string? value, string inherited) =>
        string.Equals(value ?? "", inherited, StringComparison.Ordinal) ? null : value ?? "";
}
