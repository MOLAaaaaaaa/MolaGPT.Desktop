using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Personalization;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class PersonalizationWindow : MolaContentWindow
{
    private const string NotificationKey = "personalization";

    private readonly PersonalizationViewModel _vm;
    private readonly SettingsViewModel _settings;
    private readonly MolaGptAuthService? _auth;
    private readonly CloudSyncService? _cloudSync;
    private readonly NotificationCenter? _notifications;
    private readonly List<ToggleButton> _styleChips = [];
    private bool _loadingStyleUi;

    public PersonalizationWindow(
        PersonalizationViewModel vm,
        SettingsViewModel settings,
        MolaGptAuthService? auth = null,
        CloudSyncService? cloudSync = null,
        NotificationCenter? notifications = null)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _auth = auth;
        _cloudSync = cloudSync;
        _notifications = notifications;

        InitializeComponent();
        DataContext = _vm;

        PART_TracksToggle.DataContext = _settings;
        PART_TracksToggle.Bind(ToggleButton.IsCheckedProperty,
            new Binding(nameof(SettingsViewModel.TracksEnabled)) { Mode = BindingMode.TwoWay });

        PART_Candidates.ItemsSource = _vm.Candidates;
        PART_Groups.ItemsSource = _vm.Groups;
        PART_NewMemorySection.ItemsSource = MemorySections.Ordered.Select(MemorySections.Label).ToList();
        PART_NewMemorySection.SelectedIndex = Math.Max(0,
            MemorySections.Ordered.ToList().IndexOf(MemorySection.Context));
        BuildStyleChips();

        PART_Close.Click += (_, _) => Close();
        _vm.PropertyChanged += OnVmChanged;
        _vm.Candidates.CollectionChanged += (_, _) => RefreshListsUi();
        _vm.Groups.CollectionChanged += (_, _) => RefreshListsUi();
        // The budget bar is sized in pixels, so it has to be re-laid out rather
        // than merely re-bound when the window measures the track.
        PART_ProjectionTrack.SizeChanged += (_, _) => UpdateProjectionBar();
        Closed += (_, _) => _vm.PropertyChanged -= OnVmChanged;

        Opened += (_, _) => _ = LoadAsync();
        RefreshLoginState();
        RefreshListsUi();
        RefreshStyleUi();
    }

    private bool IsLoggedIn => _auth is null || !string.IsNullOrEmpty(_auth.CurrentJwt);

    private async Task LoadAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 MolaGPT 账号后再管理个性化记忆。", isError: true);
            return;
        }
        SetStatus("正在加载记忆…");
        var error = await _vm.LoadAsync().ConfigureAwait(true);
        if (error is not null)
        {
            SetStatus(error, isError: true);
            _notifications?.Error("个性化记忆加载失败", error, NotificationKey);
            return;
        }
        RefreshStyleUi(fromModel: true);
        SetStatus(SummaryText());
    }

    private string SummaryText() =>
        _vm.Candidates.Count > 0
            ? $"共 {_vm.Entries.Count} 条记忆 · {_vm.Candidates.Count} 条待确认"
            : $"共 {_vm.Entries.Count} 条记忆";

    /// <summary>The footer is the only always-visible surface, so it carries
    /// both the resting summary and the last thing that went wrong.</summary>
    private void SetStatus(string text, bool isError = false)
    {
        PART_Status.Text = text;
        PART_Status.Classes.Set("error", isError);
        PART_StatusDot.Classes.Set("error", isError);
    }

    // -- lists --

    private void RefreshListsUi()
    {
        PART_CandidatesSection.IsVisible = _vm.Candidates.Count > 0;
        PART_CandidatesTitle.Text = _vm.CandidatesTitle;
        PART_Projection.Text = _vm.ProjectionText;
        PART_Projection.IsVisible = !string.IsNullOrEmpty(_vm.ProjectionText);
        UpdateProjectionBar();
        // Placeholder or content, never both: entries land before IsLoading
        // clears (the style request is still in flight), and the loading card
        // used to sit above the rows it was supposedly waiting for.
        PART_Empty.IsVisible = _vm.Groups.Count == 0;
        PART_EmptyText.Text = _vm.IsLoading
            ? "正在加载记忆…"
            : "还没有形成记忆。多与 MolaGPT 对话，它会在夜间自动整理；也可以在上方「添加记忆」里直接写下想让 MolaGPT 记住的事。";
        PART_Refresh.IsEnabled = !_vm.IsRefreshing && IsLoggedIn;
    }

    /// <summary>
    /// Injection-budget bar. Sized in pixels off the measured track rather than
    /// bound, because the fill is a plain Border — a ProgressBar would drag in a
    /// themed template whose inner height this window does not control.
    /// </summary>
    private void UpdateProjectionBar()
    {
        PART_ProjectionTrack.IsVisible = _vm.HasProjectionBar;
        if (!_vm.HasProjectionBar) return;
        var track = PART_ProjectionTrack.Bounds.Width;
        PART_ProjectionFill.Width = Math.Clamp(track * _vm.Projection.Usage, 0, track);
        // Skipped entries mean the budget actually bit, not that it is merely close.
        PART_ProjectionFill.Classes.Set("tight", _vm.IsProjectionTight);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PersonalizationViewModel.IsLoading)
            or nameof(PersonalizationViewModel.Projection)
            or nameof(PersonalizationViewModel.ProjectionText)
            or nameof(PersonalizationViewModel.CandidatesTitle))
            RefreshListsUi();
        else if (args.PropertyName is nameof(PersonalizationViewModel.IsRefreshing))
            PART_Refresh.IsEnabled = !_vm.IsRefreshing && IsLoggedIn;
        else if (args.PropertyName is nameof(PersonalizationViewModel.Style)
                 or nameof(PersonalizationViewModel.StyleDirty)
                 or nameof(PersonalizationViewModel.IsSavingStyle))
            RefreshStyleUi();
        else if (args.PropertyName is nameof(PersonalizationViewModel.IsAddingEntry))
            PART_AddMemory.IsEnabled = !_vm.IsAddingEntry && IsLoggedIn;
    }

    private void RefreshLoginState()
    {
        var enabled = IsLoggedIn;
        PART_Refresh.IsEnabled = enabled;
        PART_AddMemory.IsEnabled = enabled;
        PART_SaveStyle.IsEnabled = enabled && _vm.StyleDirty && !_vm.IsSavingStyle;
        PART_ClearAll.IsEnabled = enabled;
    }

    // -- toggle --

    private async void OnTracksToggleClick(object? sender, RoutedEventArgs e)
    {
        var value = PART_TracksToggle.IsChecked == true;
        var error = await TracksToggleHelper.SyncAsync(value, _settings, _auth, _cloudSync).ConfigureAwait(true);
        if (error is null) return;
        SetStatus(error, isError: true);
        _notifications?.Error("个性化设置同步失败", error, NotificationKey);
    }

    // -- entries --

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        SetStatus("正在刷新…");
        var error = await _vm.RefreshAsync().ConfigureAwait(true);
        if (error is not null)
        {
            SetStatus(error, isError: true);
            _notifications?.Error("刷新失败", error, NotificationKey);
        }
        else SetStatus(SummaryText());
    }

    private async void OnRateAgree(object? sender, RoutedEventArgs e) =>
        await RateAsync(sender, MemoryRating.Agree).ConfigureAwait(true);

    private async void OnRateDoubt(object? sender, RoutedEventArgs e) =>
        await RateAsync(sender, MemoryRating.Doubt).ConfigureAwait(true);

    private async void OnRateReject(object? sender, RoutedEventArgs e) =>
        await RateAsync(sender, MemoryRating.Reject).ConfigureAwait(true);

    private async Task RateAsync(object? sender, MemoryRating rating)
    {
        if (sender is not Control { Tag: string id }) return;
        var current = _vm.Entries.FirstOrDefault(e => e.Id == id)?.UserRating;
        var message = await _vm.RateAsync(id, current == rating ? null : rating).ConfigureAwait(true);
        Notify(message);
    }

    private async void OnEditEntry(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        var row = _vm.Entries.FirstOrDefault(x => x.Id == id);
        if (row is null) return;
        var text = await EditMemoryAsync(row.Text).ConfigureAwait(true);
        if (text is null) return;
        Notify(await _vm.UpdateAsync(id, text).ConfigureAwait(true));
    }

    private async void OnDeleteEntry(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        if (!await Confirm.AskAsync(this, "删除此记忆",
                "确定删除这条记忆吗？删除后 MolaGPT 将不再基于它回答。", "删除").ConfigureAwait(true))
            return;
        Notify(await _vm.DeleteAsync(id).ConfigureAwait(true));
    }

    private async void OnAddMemoryClick(object? sender, RoutedEventArgs e)
    {
        var section = PART_NewMemorySection.SelectedIndex >= 0
            ? MemorySections.Ordered[PART_NewMemorySection.SelectedIndex]
            : MemorySection.Context;
        var notice = await _vm.AddAsync(PART_NewMemoryText.Text ?? string.Empty, section).ConfigureAwait(true);
        Notify(notice);
        if (notice.Ok) PART_NewMemoryText.Text = string.Empty;
    }

    private async void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        if (!await Confirm.AskAsync(this, "清除全部记忆",
                "将永久删除服务器上的全部长期记忆，此操作不可撤销。", "清除").ConfigureAwait(true))
            return;
        Notify(await _vm.ClearAllAsync().ConfigureAwait(true));
    }

    // -- candidates --

    private async void OnAcceptCandidate(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        Notify(await _vm.AcceptCandidateAsync(id).ConfigureAwait(true));
    }

    private async void OnDismissCandidate(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        Notify(await _vm.DismissCandidateAsync(id).ConfigureAwait(true));
    }

    // -- style --

    /// <summary>
    /// Chips rather than check boxes: six mutually compatible tone options are a
    /// palette to pick from, and the app already has one chip for that
    /// (<c>toolchip</c>, brand tint when checked) in the composer's toolbar.
    /// </summary>
    private void BuildStyleChips()
    {
        _styleChips.Clear();
        PART_StyleChecks.Children.Clear();
        foreach (var style in ConversationStyles.All)
        {
            var chip = new ToggleButton
            {
                Content = style.Label(),
                Tag = style,
                Classes = { "toolchip" }
            };
            chip.Click += OnStyleChipClick;
            _styleChips.Add(chip);
            PART_StyleChecks.Children.Add(chip);
        }
    }

    private void OnStyleChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: ConversationStyle style }) return;
        _vm.ToggleStyle(style);
    }

    private void RefreshStyleUi(bool fromModel = false)
    {
        _loadingStyleUi = true;
        try
        {
            foreach (var chip in _styleChips)
            {
                if (chip.Tag is ConversationStyle style)
                    chip.IsChecked = _vm.Style.HasStyle(style);
            }
            if (fromModel || PART_CustomInstruction.Text != _vm.Style.CustomInstruction)
                PART_CustomInstruction.Text = _vm.Style.CustomInstruction;
            PART_CustomCount.Text =
                $"{_vm.Style.CustomInstruction.Length}/{StylePreferences.CustomInstructionMax}";
            PART_StyleSummary.Text = StyleSummary();
            PART_SaveStyle.IsEnabled = IsLoggedIn && _vm.StyleDirty && !_vm.IsSavingStyle;
            PART_SaveStyle.Content = _vm.IsSavingStyle ? "保存中…" : "保存风格";
        }
        finally
        {
            _loadingStyleUi = false;
        }
    }

    /// <summary>What the collapsed style row says. A row you have to open to
    /// find out whether it is set is a row you open every time.</summary>
    private string StyleSummary()
    {
        var picked = ConversationStyles.All
            .Where(_vm.Style.HasStyle)
            .Select(s => s.Label())
            .ToList();
        if (!string.IsNullOrWhiteSpace(_vm.Style.CustomInstruction)) picked.Add("自定义指令");
        return picked.Count == 0
            ? "设置回复的语气与组织方式。"
            : string.Join(" · ", picked);
    }

    private void OnCustomInstructionChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingStyleUi) return;
        _vm.SetCustomInstruction(PART_CustomInstruction.Text ?? string.Empty);
    }

    private async void OnSaveStyleClick(object? sender, RoutedEventArgs e) =>
        Notify(await _vm.SaveStyleAsync().ConfigureAwait(true));

    // -- shared --

    private void Notify(PersonalizationNotice notice)
    {
        if (string.IsNullOrEmpty(notice.Text)) return;
        if (!notice.Ok)
        {
            SetStatus(notice.Text, isError: true);
            _notifications?.Error("操作失败", notice.Text, NotificationKey);
            return;
        }
        if (notice.Text is "已取消评分" or "已忽略 · 不会再次建议")
            _notifications?.Info(notice.Text, key: NotificationKey);
        else
            _notifications?.Success(notice.Text, key: NotificationKey);
        SetStatus(SummaryText());
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Inline edit dialog. Null = cancelled.</summary>
    private Task<string?> EditMemoryAsync(string initial)
    {
        var completion = new TaskCompletionSource<string?>();
        var text = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            MinLines = 3,
            MaxLength = PersonalizationViewModel.MemoryTextMax,
            PlaceholderText = "请输入更准确的描述…",
            Classes = { "field" }
        };
        var count = new TextBlock
        {
            Classes = { "muted" },
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        void UpdateCount() => count.Text = $"{text.Text?.Length ?? 0}/{PersonalizationViewModel.MemoryTextMax}";
        text.TextChanged += (_, _) => UpdateCount();
        UpdateCount();

        var save = new Button { Content = "保存修改", Classes = { "primary" } };
        var cancel = new Button { Content = "取消", Classes = { "outline" } };
        var dialog = new MolaDialogWindow("调整记忆");
        dialog.SetBody(new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "若认为 MolaGPT 的记忆不够准确，可在此修正记忆条目。",
                    Classes = { "secondary" },
                    TextWrapping = TextWrapping.Wrap
                },
                text,
                count,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, save }
                }
            }
        });
        save.Click += (_, _) => { completion.TrySetResult(text.Text ?? string.Empty); dialog.Close(); };
        cancel.Click += (_, _) => { completion.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => completion.TrySetResult(null);
        _ = dialog.ShowDialog(this);
        return completion.Task;
    }
}
