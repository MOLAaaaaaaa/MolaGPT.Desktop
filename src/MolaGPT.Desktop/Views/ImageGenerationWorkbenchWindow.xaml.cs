using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class ImageGenerationWorkbenchWindow : UserControl
{
    private readonly SettingsViewModel _settings;
    private readonly ImageGenerationTool _imageGeneration;
    private readonly AttachmentStore _attachmentStore;
    private readonly ConversationRepository _conversationRepo;
    private readonly MessageRepository _messageRepo;
    private readonly Func<string, string?, string>? _createConversation;
    private readonly Action? _conversationsChanged;
    private readonly Action<string, bool>? _onGeneratingChanged;
    private readonly NotificationService? _notificationService;
    private readonly Action? _closeRequested;
    private Button? _headerModelButton;
    private Popup? _headerModelPopup;
    private ItemsControl? _headerModelItems;
    private TextBlock? _headerModelLabel;
    private readonly ObservableCollection<ImageGenerationWorkbenchResult> _results = new();
    private readonly ObservableCollection<ImageGenerationWorkbenchResult> _gallery = new();
    private CancellationTokenSource? _cts;
    private string? _conversationId;
    private bool _loading;
    private bool _closedWhileGenerating;
    private bool _closeToastShown;
    // Edit-capable models: when true, a send edits the latest image (conversational
    // turn); when false, it generates a fresh image (plain card). Ignored for
    // generate-only models. Session-only, defaults to "reference latest".
    private bool _editReferenceEnabled = true;

    private bool CurrentModelSupportsEdit => _settings.SelectedImageGenerationModel?.SupportsEdit == true;

    public ImageGenerationWorkbenchWindow(
        SettingsViewModel settings,
        ImageGenerationTool imageGeneration,
        AttachmentStore attachmentStore,
        ConversationRepository conversationRepo,
        MessageRepository messageRepo,
        string? conversationId,
        Func<string, string?, string>? createConversation = null,
        Action? conversationsChanged = null,
        NotificationService? notificationService = null,
        Action<string, bool>? onGeneratingChanged = null,
        Action? closeRequested = null)
    {
        InitializeComponent();
        _settings = settings;
        _imageGeneration = imageGeneration;
        _attachmentStore = attachmentStore;
        _conversationRepo = conversationRepo;
        _messageRepo = messageRepo;
        _conversationId = conversationId;
        _createConversation = createConversation;
        _conversationsChanged = conversationsChanged;
        _notificationService = notificationService;
        _onGeneratingChanged = onGeneratingChanged;
        _closeRequested = closeRequested;
        ResultsItems.ItemsSource = _results;
        GalleryItems.ItemsSource = _gallery;
        _results.CollectionChanged += (_, _) => UpdateEmptyState();
        _gallery.CollectionChanged += (_, _) => UpdateEmptyState();
        InitializeUi();
    }

    private void InitializeUi()
    {
        _loading = true;
        try
        {
            var configuredSize = string.IsNullOrWhiteSpace(_settings.ImageGenerationSize)
                ? "1024x1024"
                : _settings.ImageGenerationSize;
            StyleTextBox.Text = _settings.ImageGenerationStyle ?? string.Empty;

            foreach (var item in SizePresetCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), configuredSize, StringComparison.OrdinalIgnoreCase))
                {
                    SizePresetCombo.SelectedItem = item;
                    break;
                }
            }
            SizePresetCombo.SelectedItem ??= SizePresetCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        LoadStoredImages();
        ShowCurrentPane();
        UpdateStatus();
        UpdateOptionChips();
        UpdateGenerateButton();
        UpdateEmptyState();
    }

    public void AttachHeaderModelSelector(Button button, Popup popup, ItemsControl items, TextBlock label)
    {
        DetachHeaderModelSelector();

        _headerModelButton = button;
        _headerModelPopup = popup;
        _headerModelItems = items;
        _headerModelLabel = label;
        _headerModelButton.Click += HeaderModelButton_Click;

        RebuildHeaderModelSelector();
        if ((_settings.SelectedImageGenerationModel ?? _settings.ImageGenerationProviderModels.FirstOrDefault()) is { } option)
            SelectImageGenerationModel(option);
    }

    public void DetachHeaderModelSelector()
    {
        if (_headerModelButton is not null)
            _headerModelButton.Click -= HeaderModelButton_Click;
        if (_headerModelPopup is not null)
            _headerModelPopup.IsOpen = false;
        if (_headerModelItems is not null)
            _headerModelItems.Items.Clear();

        _headerModelButton = null;
        _headerModelPopup = null;
        _headerModelItems = null;
        _headerModelLabel = null;
    }

    private void HeaderModelButton_Click(object sender, RoutedEventArgs e)
    {
        RebuildHeaderModelSelector();
        if (_headerModelPopup is not null)
            _headerModelPopup.IsOpen = !_headerModelPopup.IsOpen;
    }

    private void RebuildHeaderModelSelector()
    {
        if (_headerModelItems is null) return;

        _headerModelItems.Items.Clear();
        foreach (var option in _settings.ImageGenerationProviderModels)
        {
            var button = new Button
            {
                Style = (Style)FindResource("HintChip"),
                Margin = new Thickness(4, 2, 4, 2),
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = BuildHeaderModelSelectorContent(option),
                Tag = option
            };
            button.Click += (_, _) =>
            {
                if (button.Tag is ImageGenerationProviderModelOption selected)
                    SelectImageGenerationModel(selected);
                if (_headerModelPopup is not null)
                    _headerModelPopup.IsOpen = false;
            };
            _headerModelItems.Items.Add(button);
        }

        if (_headerModelItems.Items.Count == 0)
        {
            _headerModelItems.Items.Add(new TextBlock
            {
                Text = "当前没有可用的图像模型",
                Margin = new Thickness(12),
                Foreground = ResolveBrush("Brush.Text.Muted"),
                FontSize = 13
            });
        }
    }

    private static UIElement BuildHeaderModelSelectorContent(ImageGenerationProviderModelOption option)
    {
        return new TextBlock
        {
            Text = option.Label,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void SelectImageGenerationModel(ImageGenerationProviderModelOption option)
    {
        if (_loading)
            return;

        _settings.ImageGenerationEnabled = true;
        _settings.ImageGenerationProviderId = option.ProviderId;
        _settings.ImageGenerationModelId = option.ModelId;
        if (_headerModelLabel is not null)
            _headerModelLabel.Text = option.Label;
        ApplyWorkbenchMode();
        UpdateStatus();
        UpdateGenerateButton();
    }

    private void SizePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SizePresetCombo.SelectedItem is not ComboBoxItem item)
            return;

        var tag = item.Tag?.ToString();
        if (!string.IsNullOrWhiteSpace(tag))
            _settings.ImageGenerationSize = tag;
        UpdateStatus();
        UpdateOptionChips();
    }

    private void OptionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _settings.ImageGenerationStyle = string.IsNullOrWhiteSpace(StyleTextBox.Text)
            ? null
            : StyleTextBox.Text.Trim();
        UpdateStatus();
        UpdateOptionChips();
        UpdateGenerateButton();
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateGenerateButton();

    private async void GenerateClick(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "请先输入图像描述。";
            PromptBox.Focus();
            return;
        }

        if (!EnsureConversationForPrompt(prompt))
        {
            StatusText.Text = "无法新建图像任务，请稍后重试。";
            return;
        }

        var editSource = (CurrentModelSupportsEdit && _editReferenceEnabled) ? LatestEditableResult() : null;
        var isEdit = editSource is not null;
        var taskTitle = CurrentTaskTitle();
        if (IsDefaultTaskTitle(taskTitle))
            taskTitle = BuildTaskTitle(prompt);
        var pending = CreatePendingResult(prompt, taskTitle, isEdit, editSource);

        _closedWhileGenerating = false;
        _closeToastShown = false;
        _cts = new CancellationTokenSource();
        SetGenerating(true);
        _results.Add(pending);
        ShowCurrentPane();
        ScrollResultsToEnd();
        try
        {
            StatusText.Text = isEdit
                ? "正在编辑图片，完成后会通知你。"
                : "正在生成图片，完成后会通知你。";
            var options = _settings.BuildImageGenerationOptions() with
            {
                Size = SelectedSize(),
                Style = string.IsNullOrWhiteSpace(StyleTextBox.Text) ? null : StyleTextBox.Text.Trim(),
                AsTool = false
            };
            var images = isEdit
                ? await _imageGeneration.EditAsync(options, prompt, editSource!.Bytes, editSource.MimeType, _cts.Token)
                : await _imageGeneration.GenerateAsync(options, prompt, _cts.Token);
            if (images.Count == 0)
            {
                StatusText.Text = "未返回图片，请调整描述后重试。";
                ReplacePendingWithError(pending, prompt, taskTitle, isEdit, editSource, "未返回图片，请调整描述后重试。");
                return;
            }

            var added = 0;
            var insertIndex = IndexOfPendingOrStart(pending);
            if (insertIndex >= 0)
                _results.RemoveAt(insertIndex);
            else
                insertIndex = 0;

            foreach (var image in images)
            {
                var fileName = $"generated-{DateTime.Now:yyyyMMdd-HHmmss}-{_results.Count + 1}{ExtensionForMime(image.MimeType)}";
                var localName = _attachmentStore.Save(image.Bytes, image.MimeType, fileName);
                var result = new ImageGenerationWorkbenchResult(
                    fileName,
                    image.MimeType,
                    image.Bytes,
                    localName,
                    image.RevisedPrompt,
                    CreateThumbnail(image.Bytes),
                    prompt,
                    taskTitle,
                    DateTimeOffset.Now,
                    isEdit,
                    SourceThumbnail: editSource?.Thumbnail,
                    SourceTitle: editSource?.TaskTitle);

                _results.Insert(Math.Min(insertIndex + added, _results.Count), result);
                _gallery.Insert(0, result);
                PersistGeneratedImage(prompt, result);
                added++;
            }

            ScrollResultsToEnd();
            StatusText.Text = isEdit
                ? $"编辑完成，共 {added} 张图片。"
                : $"生成完成，共 {added} 张图片。";
            _conversationsChanged?.Invoke();
            _notificationService?.ShowImageGenerationCompleted(_conversationId!, taskTitle, added, force: _closedWhileGenerating || !IsVisible);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消本次生成。";
            ReplacePendingWithError(pending, prompt, taskTitle, isEdit, editSource, "已取消本次生成。");
            if (_notificationService is not null && !string.IsNullOrWhiteSpace(_conversationId) && (_closedWhileGenerating || !IsVisible))
            {
                _notificationService.ShowImageGenerationFailed(
                    _conversationId,
                    CurrentTaskTitle(),
                    "已取消本次生成。",
                    force: true);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成失败：" + ex.Message;
            ReplacePendingWithError(pending, prompt, taskTitle, isEdit, editSource, ex.Message);
            if (_notificationService is not null && !string.IsNullOrWhiteSpace(_conversationId))
            {
                _notificationService.ShowImageGenerationFailed(
                    _conversationId,
                    taskTitle,
                    ex.Message,
                    force: _closedWhileGenerating || !IsVisible);
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetGenerating(false);
        }
    }

    private bool EnsureConversationForPrompt(string prompt)
    {
        if (!string.IsNullOrWhiteSpace(_conversationId))
            return true;
        if (_createConversation is null)
            return false;

        _conversationId = _createConversation(BuildTaskTitle(prompt), _settings.ImageGenerationModelId);
        _conversationsChanged?.Invoke();
        return !string.IsNullOrWhiteSpace(_conversationId);
    }

    private ImageGenerationWorkbenchResult? LatestEditableResult() =>
        _results.LastOrDefault(item => item.HasImage);

    private ImageGenerationWorkbenchResult CreatePendingResult(
        string prompt,
        string taskTitle,
        bool isEdit,
        ImageGenerationWorkbenchResult? editSource) =>
        new(
            string.Empty,
            "image/png",
            Array.Empty<byte>(),
            null,
            null,
            null,
            prompt,
            taskTitle,
            DateTimeOffset.Now,
            isEdit,
            IsPending: true,
            SourceThumbnail: editSource?.Thumbnail,
            SourceTitle: editSource?.TaskTitle);

    private ImageGenerationWorkbenchResult CreateErrorResult(
        string prompt,
        string taskTitle,
        bool isEdit,
        ImageGenerationWorkbenchResult? editSource,
        string message) =>
        new(
            string.Empty,
            "image/png",
            Array.Empty<byte>(),
            null,
            null,
            null,
            prompt,
            taskTitle,
            DateTimeOffset.Now,
            isEdit,
            IsError: true,
            ErrorMessage: message,
            SourceThumbnail: editSource?.Thumbnail,
            SourceTitle: editSource?.TaskTitle);

    private int IndexOfPendingOrStart(ImageGenerationWorkbenchResult pending)
    {
        for (var i = 0; i < _results.Count; i++)
        {
            if (ReferenceEquals(_results[i], pending))
                return i;
        }

        return -1;
    }

    private void ReplacePendingWithError(
        ImageGenerationWorkbenchResult pending,
        string prompt,
        string taskTitle,
        bool isEdit,
        ImageGenerationWorkbenchResult? editSource,
        string message)
    {
        var error = CreateErrorResult(prompt, taskTitle, isEdit, editSource, message);
        var index = IndexOfPendingOrStart(pending);
        if (index >= 0)
            _results[index] = error;
        else
            _results.Add(error);
    }

    private void PreviewResultClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImageGenerationWorkbenchResult result)
            return;

        ImagePreviewWindow.Show(Window.GetWindow(this), result.Bytes, result.FileName);
    }

    private void SaveResultClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImageGenerationWorkbenchResult result)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "保存图片",
            FileName = result.FileName,
            Filter = FilterForMime(result.MimeType)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, result.Bytes);
            StatusText.Text = "已保存到 " + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private void ClearResultsClick(object sender, RoutedEventArgs e)
    {
        _results.Clear();
        StatusText.Text = "已清空当前结果，作品仍保留在画廊中。";
    }

    private void NewTaskClick(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        _conversationId = null;
        _results.Clear();
        PromptBox.Clear();
        ShowCurrentPane();
        StatusText.Text = "已新建图像任务。";
        UpdateGenerateButton();
    }

    private void CurrentTaskTabClick(object sender, RoutedEventArgs e) => ShowCurrentPane();

    private void GalleryTabClick(object sender, RoutedEventArgs e) => ShowGalleryPane();

    private void RatioChipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag?.ToString() is not { Length: > 0 } size)
            return;

        foreach (var item in SizePresetCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), size, StringComparison.OrdinalIgnoreCase))
            {
                SizePresetCombo.SelectedItem = item;
                break;
            }
        }

        UpdateOptionChips();
    }

    private void StyleChipClick(object sender, RoutedEventArgs e)
    {
        var style = (sender as Button)?.Tag?.ToString() ?? string.Empty;
        StyleTextBox.Text = style;
        UpdateOptionChips();
    }

    private void LoadStoredImages()
    {
        _results.Clear();
        _gallery.Clear();

        if (!string.IsNullOrWhiteSpace(_conversationId))
        {
            var currentTitle = CurrentTaskTitle();
            var current = _messageRepo.List(_conversationId)
                .SelectMany(row => ParseStoredImages(
                    row.Meta,
                    row.Content,
                    row.CreatedAt,
                    currentTitle))
                .OrderBy(item => item.CreatedAt);

            foreach (var item in current)
                _results.Add(item);
        }

        var gallery = _messageRepo
            .ListImageWorkbenchMessages(ConversationListViewModel.ImageWorkbenchProviderId)
            .SelectMany(row => ParseStoredImages(
                row.Meta,
                row.Content,
                row.CreatedAt,
                row.ConversationTitle))
            .OrderByDescending(item => item.CreatedAt);

        foreach (var item in gallery)
            _gallery.Add(item);

        if (_results.LastOrDefault() is { Prompt.Length: > 0 } latest)
            PromptBox.Text = latest.Prompt;

        ScrollResultsToEnd();
    }

    private IEnumerable<ImageGenerationWorkbenchResult> ParseStoredImages(
        string? meta,
        string content,
        long createdAt,
        string taskTitle)
    {
        if (string.IsNullOrWhiteSpace(meta))
            yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(meta);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("image_workbench", out var marker)
                || marker.ValueKind != JsonValueKind.True)
            {
                yield break;
            }

            var prompt = ReadString(root, "prompt") ?? content;
            var revisedPrompt = ReadString(root, "revised_prompt") ?? content;
            if (!root.TryGetProperty("attachments", out var attachments)
                || attachments.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in attachments.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var localName = ReadString(item, "localName");
                var bytes = _attachmentStore.Load(localName);
                if (bytes is not { Length: > 0 }) continue;

                var mime = ReadString(item, "mime") ?? "image/png";
                var fileName = ReadString(item, "filename") ?? localName ?? $"generated-{createdAt}{ExtensionForMime(mime)}";
                yield return new ImageGenerationWorkbenchResult(
                    fileName,
                    mime,
                    bytes,
                    localName,
                    revisedPrompt,
                    CreateThumbnail(bytes),
                    prompt,
                    taskTitle,
                    DateTimeOffset.FromUnixTimeMilliseconds(createdAt).ToLocalTime(),
                    ReadBool(root, "image_edit"));
            }
        }
    }

    private void PersistGeneratedImage(string prompt, ImageGenerationWorkbenchResult result)
    {
        if (string.IsNullOrWhiteSpace(_conversationId) || string.IsNullOrWhiteSpace(result.LocalName))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var meta = new JsonObject
        {
            ["image_workbench"] = true,
            ["prompt"] = prompt,
            ["revised_prompt"] = result.RevisedPrompt,
            ["image_edit"] = result.IsEditMode,
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["filename"] = result.FileName,
                ["label"] = "图片",
                ["localName"] = result.LocalName,
                ["mime"] = result.MimeType
            })
        };

        _messageRepo.Insert(new MessageRow(
            Guid.NewGuid().ToString("N"),
            _conversationId,
            "assistant",
            string.IsNullOrWhiteSpace(result.RevisedPrompt) ? "图像生成完成" : result.RevisedPrompt,
            meta.ToJsonString(),
            now));

        if (_conversationRepo.Get(_conversationId) is not { } row)
            return;

        var title = IsDefaultTaskTitle(row.Title) ? BuildTaskTitle(prompt) : row.Title;
        _conversationRepo.Upsert(row with
        {
            Title = title,
            ModelId = _settings.ImageGenerationModelId,
            UpdatedAt = now
        });
    }

    private string CurrentTaskTitle()
    {
        if (string.IsNullOrWhiteSpace(_conversationId))
            return "图像工作台";
        return _conversationRepo.Get(_conversationId)?.Title ?? "图像工作台";
    }

    private static string BuildTaskTitle(string prompt)
    {
        var compact = string.Join(" ", prompt.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length == 0) return "图像工作台";
        return compact.Length <= 18 ? compact : compact[..18] + "...";
    }

    private static bool IsDefaultTaskTitle(string? title) =>
        string.IsNullOrWhiteSpace(title)
        || string.Equals(title, "图像工作台", StringComparison.Ordinal)
        || string.Equals(title, "新对话", StringComparison.Ordinal);

    private void SetGenerating(bool generating)
    {
        // Mirror the generating state onto the sidebar item so a closed
        // workbench still shows the spinner on its conversation row, exactly
        // like a streaming text chat. No-op until a conversation exists.
        if (!string.IsNullOrWhiteSpace(_conversationId))
            _onGeneratingChanged?.Invoke(_conversationId, generating);

        StatusDot.Fill = ResolveBrush(generating ? "Brush.Warning" : "Brush.Success");
        GenerateButton.ToolTip = generating ? "停止" : "生成";
        GenerateButton.Style = (Style)FindResource(generating ? "StopButton" : "SendButton");
        GenerateButtonIcon.Text = generating ? "\uE71A" : "\uE724";
        GenerateButton.IsEnabled = generating || _settings.IsImageGenerationConfigured;
        if (!generating)
            UpdateGenerateButton();
        UpdateEmptyState();
    }

    private void UpdateStatus()
    {
        ApplyWorkbenchMode();
        var provider = _settings.GetImageGenerationProvider();
        if (provider is null)
        {
            ConfigSummaryText.Text = "暂无可用的图像服务，请在设置的「模型服务」中添加图像服务。";
            StatusText.Text = "图像服务尚未配置。";
            return;
        }

        ConfigSummaryText.Text =
            $"服务：{provider.Name}\n模型：{_settings.ImageGenerationModelId}\n尺寸：{ReadableSize(SelectedSize())}";
        StatusText.Text = _settings.IsImageGenerationConfigured
            ? CurrentModelSupportsEdit
                ? "已就绪。当前模型支持在上一张图的基础上继续编辑。"
                : "已就绪。当前模型仅支持生成新图。"
            : "请在设置中补全图像服务的地址、密钥和模型。";
    }

    private void ApplyWorkbenchMode()
    {
        if (ModeBadgeText is null) return;

        var supportsEdit = CurrentModelSupportsEdit;
        if (EditModeTogglePanel is not null)
            EditModeTogglePanel.Visibility = supportsEdit ? Visibility.Visible : Visibility.Collapsed;

        // Only edit-capable models with "reference latest" selected run as edits;
        // generate-only models, and the "fresh generate" choice, stay plain.
        var referencing = supportsEdit && _editReferenceEnabled;
        ModeBadgeText.Text = referencing ? "图像编辑" : "图像生成";

        if (EditRefOnButton is not null && EditRefOffButton is not null)
        {
            SetSegmentButtonActive(EditRefOnButton, referencing);
            SetSegmentButtonActive(EditRefOffButton, supportsEdit && !_editReferenceEnabled);
        }
    }

    private void EditRefModeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag?.ToString() is not { Length: > 0 } tag) return;
        _editReferenceEnabled = string.Equals(tag, "reference", StringComparison.Ordinal);
        ApplyWorkbenchMode();
    }

    private void ScrollResultsToEnd() =>
        Dispatcher.BeginInvoke(new Action(() => ResultsScroll?.ScrollToEnd()), DispatcherPriority.Loaded);

    private void UpdateGenerateButton()
    {
        GenerateButton.IsEnabled =
            (_cts is not null || _settings.IsImageGenerationConfigured)
            && !string.IsNullOrWhiteSpace(PromptBox.Text);
    }

    private void UpdateEmptyState()
    {
        EmptyResultsText.Visibility = _results.Count == 0 && _cts is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyGalleryText.Visibility = _gallery.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CurrentCountText.Text = _results.Count.ToString();
        GalleryCountText.Text = _gallery.Count.ToString();
    }

    private void ShowCurrentPane()
    {
        CurrentPane.Visibility = Visibility.Visible;
        GalleryPane.Visibility = Visibility.Collapsed;
        SetSegmentButtonActive(CurrentTaskTabButton, true);
        SetSegmentButtonActive(GalleryTabButton, false);
    }

    private void ShowGalleryPane()
    {
        CurrentPane.Visibility = Visibility.Collapsed;
        GalleryPane.Visibility = Visibility.Visible;
        SetSegmentButtonActive(CurrentTaskTabButton, false);
        SetSegmentButtonActive(GalleryTabButton, true);
    }

    private void UpdateOptionChips()
    {
        var size = SelectedSize();
        foreach (var button in RatioChipPanel.Children.OfType<Button>())
            SetChipActive(button, string.Equals(button.Tag?.ToString(), size, StringComparison.OrdinalIgnoreCase));

        var style = StyleTextBox.Text.Trim();
        foreach (var button in StyleChipPanel.Children.OfType<Button>())
            SetChipActive(button, string.Equals(button.Tag?.ToString() ?? string.Empty, style, StringComparison.OrdinalIgnoreCase));
    }

    private void SetSegmentButtonActive(Button button, bool active)
    {
        button.Background = active ? ResolveBrush("Brush.Bg.Elevated") : Brushes.Transparent;
        button.Foreground = active ? ResolveBrush("Brush.Primary") : ResolveBrush("Brush.Text.Secondary");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void SetChipActive(Button button, bool active)
    {
        button.Background = active ? ResolveBrush("Brush.Primary.Soft") : ResolveBrush("Brush.Bg.Elevated");
        button.BorderBrush = active ? ResolveBrush("Brush.Primary") : ResolveBrush("Brush.Border");
        button.Foreground = active ? ResolveBrush("Brush.Primary") : ResolveBrush("Brush.Text.Secondary");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private Brush ResolveBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Transparent;

    private static string ReadableSize(string? size) =>
        string.IsNullOrWhiteSpace(size) ? "1024×1024" : size.Trim().Replace("x", "×", StringComparison.OrdinalIgnoreCase);

    private string SelectedSize() =>
        (SizePresetCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } size
            ? size
            : "1024x1024";

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static BitmapImage? CreateThumbnail(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 640;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    internal static Brush BuildImageSkeletonBrush()
    {
        var group = new DrawingGroup();
        void Add(Color color, Point center, double radiusX, double radiusY)
        {
            var brush = new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = radiusX,
                RadiusY = radiusY,
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            brush.GradientStops.Add(new GradientStop(color, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        }

        Add(Color.FromArgb(190, 220, 20, 60), new Point(0.12, 0.25), 0.55, 0.45);
        Add(Color.FromArgb(180, 255, 200, 0), new Point(0.88, 0.12), 0.42, 0.55);
        Add(Color.FromArgb(166, 0, 200, 130), new Point(0.75, 0.88), 0.55, 0.42);
        Add(Color.FromArgb(174, 0, 140, 255), new Point(0.08, 0.75), 0.45, 0.52);
        Add(Color.FromArgb(128, 180, 0, 255), new Point(0.55, 0.45), 0.36, 0.36);

        return new DrawingBrush(group)
        {
            Stretch = Stretch.Fill
        };
    }

    private static string ExtensionForMime(string? mimeType) =>
        mimeType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png"
        };

    private static string FilterForMime(string? mimeType) =>
        mimeType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => "JPEG 图片 (*.jpg)|*.jpg|所有文件 (*.*)|*.*",
            "image/webp" => "WebP 图片 (*.webp)|*.webp|所有文件 (*.*)|*.*",
            _ => "PNG 图片 (*.png)|*.png|所有文件 (*.*)|*.*"
        };

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        NotifyClosedWhileGenerating();
        _closeRequested?.Invoke();
    }

    private void OpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MolaGPT.ViewModels.MainViewModel mainVm)
            mainVm.OpenSettingsCommand.Execute(null);
    }

    public void NotifyClosedWhileGenerating()
    {
        if (_cts is not null && !_closeToastShown)
        {
            _closedWhileGenerating = true;
            _notificationService?.ShowImageGenerationStarted(_conversationId ?? string.Empty, CurrentTaskTitle());
            _closeToastShown = true;
        }
    }
}

public sealed record ImageGenerationWorkbenchResult(
    string FileName,
    string MimeType,
    byte[] Bytes,
    string? LocalName,
    string? RevisedPrompt,
    BitmapImage? Thumbnail,
    string Prompt,
    string TaskTitle,
    DateTimeOffset CreatedAt,
    bool IsEditMode = false,
    bool IsPending = false,
    bool IsError = false,
    string? ErrorMessage = null,
    BitmapImage? SourceThumbnail = null,
    string? SourceTitle = null)
{
    public bool HasImage => !IsPending && !IsError && Bytes.Length > 0 && Thumbnail is not null;

    public Visibility PromptBubbleVisibility => IsEditMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AvatarVisibility => IsEditMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TaskPromptVisibility => IsEditMode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SourceStripVisibility => IsEditMode && SourceThumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => HasImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => IsError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FooterVisibility => IsPending ? Visibility.Collapsed : Visibility.Visible;
    public string PromptHeader => IsEditMode ? "修改指令" : "生成提示词";
    public string ModeLabel => IsPending
        ? IsEditMode ? "编辑中" : "生成中"
        : IsError ? "生成失败" : IsEditMode ? "图像编辑" : "图像生成";

    public string CreatedAtText => CreatedAt.ToString("MM-dd HH:mm");

    public string ImageMeta => IsPending ? "处理中" : IsError ? "失败" : MimeType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpeg",
        "image/webp" => "webp",
        _ => "png"
    };

    public Brush? SkeletonBrush => IsPending ? ImageGenerationWorkbenchWindow.BuildImageSkeletonBrush() : null;

    public string PendingStatusText => IsEditMode ? "正在编辑图片" : "正在生成图片";

    public string ErrorDisplay => string.IsNullOrWhiteSpace(ErrorMessage) ? "本次任务未完成。" : ErrorMessage;

    public string RevisedPromptDisplay =>
        IsError
            ? ErrorDisplay
            :
        string.IsNullOrWhiteSpace(RevisedPrompt)
            ? IsEditMode ? "图像编辑完成" : "图像生成完成"
            : (IsEditMode ? "编辑提示词：" : "修订提示词：") + RevisedPrompt;
}
