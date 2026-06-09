using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class ModelConfigDialog : Window
{
    private EditableModelEntry? _model;
    private ObservableCollection<BatchModelItem>? _batchItems;
    private ICollectionView? _batchView;
    private bool _isCustomProvider;
    private bool _isImageProvider;
    private bool _updatingSelectAll;

    public bool Confirmed { get; private set; }
    public List<ProviderModelEntry>? SelectedModels { get; private set; }

    public ModelConfigDialog()
    {
        InitializeComponent();
    }

    public void ShowSingleEdit(EditableModelEntry model, Window owner, bool isCustomProvider = false, bool isImageProvider = false)
    {
        _model = model;
        _isCustomProvider = isCustomProvider;
        _isImageProvider = isImageProvider;
        Owner = owner;
        DialogTitle.Text = "模型配置";
        SingleEditPanel.Visibility = Visibility.Visible;
        BatchPanel.Visibility = Visibility.Collapsed;
        ConfirmButton.Content = "确定";
        LoadModel(model);
        ShowDialog();
    }

    public List<ProviderModelEntry>? ShowBatchDetect(
        IReadOnlyList<ProviderModelEntry> detected,
        IReadOnlyList<string> existingIds,
        Window owner)
    {
        Owner = owner;
        DialogTitle.Text = $"检测到 {detected.Count} 个模型";
        SingleEditPanel.Visibility = Visibility.Collapsed;
        BatchPanel.Visibility = Visibility.Visible;
        ConfirmButton.Content = "添加选中模型";

        _batchItems = new ObservableCollection<BatchModelItem>(
            detected.Select(m =>
            {
                var exists = existingIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase);
                return new BatchModelItem
                {
                    Entry = m,
                    IsSelected = !exists,
                    IsEnabled = !exists,
                    StatusText = exists ? "(已存在)" : ""
                };
            }));
        foreach (var item in _batchItems)
            item.PropertyChanged += BatchModelItem_PropertyChanged;

        BatchModelSearchBox.Text = string.Empty;
        _batchView = CollectionViewSource.GetDefaultView(_batchItems);
        _batchView.Filter = BatchModelFilter;
        BatchModelList.ItemsSource = _batchView;
        UpdateBatchSelectAllState();

        ShowDialog();
        return SelectedModels;
    }

    private void LoadModel(EditableModelEntry m)
    {
        EditModelId.Text = m.Id;
        EditDisplayName.Text = m.DisplayName;
        EditContextWindow.Text = m.ContextWindow?.ToString() ?? "";
        ChkVision.IsChecked = m.Vision;
        ChkTools.IsChecked = m.Tools;
        ChkThinking.IsChecked = m.Thinking;
        ChkReasoningEffort.IsChecked = m.ReasoningEffort;
        ChkImageEdit.IsChecked = m.ImageEdit;
        ChkImageEdit.Visibility = _isImageProvider ? Visibility.Visible : Visibility.Collapsed;
        EditSystemPrompt.Text = m.SystemPrompt ?? "";

        if (_isCustomProvider && m.Thinking)
        {
            ThinkingConfigPanel.Visibility = Visibility.Visible;
            SetThinkingKind(m.ThinkingParamKind);
            EditDefaultEffort.Text = m.DefaultEffort ?? "";
            EditBudgetMin.Text = m.ThinkingBudgetMin?.ToString() ?? "";
            EditBudgetMax.Text = m.ThinkingBudgetMax?.ToString() ?? "";
            EditBudgetDefault.Text = m.ThinkingBudgetDefault?.ToString() ?? "";
        }
        else
        {
            ThinkingConfigPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveToModel()
    {
        if (_model is null) return;
        _model.Id = EditModelId.Text.Trim();
        _model.DisplayName = EditDisplayName.Text.Trim();
        _model.ContextWindow = int.TryParse(EditContextWindow.Text.Trim(), out var ctx) ? ctx : null;
        _model.Vision = ChkVision.IsChecked == true;
        _model.Tools = ChkTools.IsChecked == true;
        _model.Thinking = ChkThinking.IsChecked == true;
        _model.ReasoningEffort = ChkReasoningEffort.IsChecked == true;
        _model.ImageEdit = _isImageProvider && ChkImageEdit.IsChecked == true;
        _model.SystemPrompt = string.IsNullOrWhiteSpace(EditSystemPrompt.Text) ? null : EditSystemPrompt.Text.Trim();

        if (_isCustomProvider && _model.Thinking)
        {
            _model.ThinkingParamKind = (ThinkingKindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            _model.DefaultEffort = string.IsNullOrWhiteSpace(EditDefaultEffort.Text) ? null : EditDefaultEffort.Text.Trim();
            _model.ThinkingBudgetMin = int.TryParse(EditBudgetMin.Text.Trim(), out var min) ? min : null;
            _model.ThinkingBudgetMax = int.TryParse(EditBudgetMax.Text.Trim(), out var max) ? max : null;
            _model.ThinkingBudgetDefault = int.TryParse(EditBudgetDefault.Text.Trim(), out var def) ? def : null;
        }
        else if (!_model.Thinking)
        {
            _model.DefaultEffort = null;
            _model.ThinkingBudgetMin = null;
            _model.ThinkingBudgetMax = null;
            _model.ThinkingBudgetDefault = null;
        }
    }

    private void SetThinkingKind(string? kind)
    {
        if (string.IsNullOrEmpty(kind))
        {
            ThinkingKindCombo.SelectedIndex = 0;
            return;
        }
        foreach (var item in ThinkingKindCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), kind, StringComparison.OrdinalIgnoreCase))
            {
                ThinkingKindCombo.SelectedItem = item;
                return;
            }
        }
        ThinkingKindCombo.SelectedIndex = 0;
    }

    private void ChkThinking_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isCustomProvider)
        {
            ThinkingConfigPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ThinkingConfigPanel.Visibility = ChkThinking.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ThinkingKindCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        var tag = (ThinkingKindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var isBudgetKind = tag is "AnthropicBudget" or "GeminiBudget" or "QwenThinkingBudget";
        BudgetRangePanel.Visibility = isBudgetKind ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectAll)
            return;
        if (_batchItems is null) return;
        var check = ChkSelectAll.IsChecked == true;
        foreach (var item in CurrentBatchSelectionScope().Where(i => i.IsEnabled))
            item.IsSelected = check;
        UpdateBatchSelectAllState();
    }

    private void BatchModelSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _batchView?.Refresh();
        UpdateBatchSelectAllState();
    }

    private bool BatchModelFilter(object item)
    {
        if (item is not BatchModelItem model)
            return false;
        var query = BatchModelSearchBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return ContainsModelText(model.Entry.Id, query)
               || ContainsModelText(model.Entry.DisplayName, query)
               || ContainsModelText(model.CapabilitySummary, query);
    }

    private static bool ContainsModelText(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private IEnumerable<BatchModelItem> CurrentBatchSelectionScope()
    {
        if (_batchView is not null)
        {
            foreach (var item in _batchView)
            {
                if (item is BatchModelItem model)
                    yield return model;
            }
            yield break;
        }

        if (_batchItems is null)
            yield break;
        foreach (var item in _batchItems)
            yield return item;
    }

    private void BatchModelItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchModelItem.IsSelected))
            UpdateBatchSelectAllState();
    }

    private void UpdateBatchSelectAllState()
    {
        if (_batchItems is null)
            return;

        var matched = CurrentBatchSelectionScope().ToList();
        BatchEmptyText.Visibility = matched.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        bool? state = false;
        var visible = matched.Where(i => i.IsEnabled).ToList();
        if (visible.Count > 0)
        {
            var selected = visible.Count(i => i.IsSelected);
            state = selected == visible.Count
                ? true
                : selected == 0 ? false : null;
        }

        _updatingSelectAll = true;
        try
        {
            ChkSelectAll.IsChecked = state;
        }
        finally
        {
            _updatingSelectAll = false;
        }
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        if (SingleEditPanel.Visibility == Visibility.Visible)
        {
            SaveToModel();
        }
        else if (_batchItems is not null)
        {
            SelectedModels = _batchItems
                .Where(i => i.IsSelected && i.IsEnabled)
                .Select(i => i.Entry)
                .ToList();
        }
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

public sealed class BatchModelItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public ProviderModelEntry Entry { get; init; } = default!;
    public bool IsEnabled { get; init; } = true;
    public string StatusText { get; init; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public string CapabilitySummary
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Vision) parts.Add("视觉");
            if (Entry.Tools) parts.Add("工具");
            if (Entry.Thinking) parts.Add("思考");
            if (Entry.ReasoningEffort) parts.Add("强度");
            if (Entry.ImageEdit) parts.Add("图像编辑");
            return parts.Count > 0 ? string.Join(" · ", parts) : "基础对话";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
