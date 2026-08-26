using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MolaGPT.App.Infrastructure;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class ImageGenerationWorkbenchView : UserControl
{
    private readonly SettingsViewModel _settings;
    private readonly ImageGenerationTool _imageGeneration;
    private readonly AttachmentStore _attachmentStore;
    private readonly ConversationRepository _conversationRepo;
    private readonly MessageRepository _messageRepo;
    private readonly Func<string, string?, string> _createConversation;
    private readonly Action<string, bool> _onGeneratingChanged;
    private readonly AppNotificationService? _notificationService;
    private readonly ObservableCollection<ImageWorkbenchResult> _results = new();
    private readonly ObservableCollection<ImageWorkbenchResult> _gallery = new();
    private CancellationTokenSource? _cts;
    private string? _conversationId;
    private bool _loading;
    private bool _referenceLatest = true;
    private bool _hiddenWhileGenerating;
    private bool _hiddenNotificationShown;

    public ImageGenerationWorkbenchView(
        SettingsViewModel settings,
        ImageGenerationTool imageGeneration,
        AttachmentStore attachmentStore,
        ConversationRepository conversationRepo,
        MessageRepository messageRepo,
        string? conversationId,
        Func<string, string?, string> createConversation,
        Action<string, bool> onGeneratingChanged,
        AppNotificationService? notificationService = null)
    {
        _settings = settings;
        _imageGeneration = imageGeneration;
        _attachmentStore = attachmentStore;
        _conversationRepo = conversationRepo;
        _messageRepo = messageRepo;
        _conversationId = conversationId;
        _createConversation = createConversation;
        _onGeneratingChanged = onGeneratingChanged;
        _notificationService = notificationService;

        InitializeComponent();
        PART_Results.ItemsSource = _results;
        PART_Gallery.ItemsSource = _gallery;
        PART_CurrentTab.Click += (_, _) => ShowCurrent();
        PART_GalleryTab.Click += (_, _) => ShowGallery();
        PART_ClearResults.Click += (_, _) => ClearResults();
        PART_NewTask.Click += (_, _) => NewTask();
        PART_Close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        PART_OpenSettings.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        PART_Generate.Click += OnGenerate;
        PART_ReferenceLatest.Click += (_, _) => SetReferenceMode(true);
        PART_FreshGenerate.Click += (_, _) => SetReferenceMode(false);
        PART_Size.SelectionChanged += OnSizeChanged;
        PART_Style.TextChanged += OnStyleChanged;
        PART_Prompt.TextChanged += (_, _) => UpdateGenerateButton();
        PART_Prompt.KeyDown += OnPromptKeyDown;
        _results.CollectionChanged += (_, _) => UpdateEmptyState();
        _gallery.CollectionChanged += (_, _) => UpdateEmptyState();
        _settings.PropertyChanged += OnSettingsChanged;
        DetachedFromVisualTree += (_, _) => _settings.PropertyChanged -= OnSettingsChanged;

        InitializeUi();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? OpenSettingsRequested;
    public bool IsGenerating => _cts is not null;
    public string? ConversationId => _conversationId;

    private bool SupportsEdit => _settings.SelectedWorkbenchImageGenerationModel?.SupportsEdit == true;

    private void InitializeUi()
    {
        _loading = true;
        try
        {
            var configuredSize = string.IsNullOrWhiteSpace(_settings.WorkbenchImageGenerationSize)
                ? "1024x1024"
                : _settings.WorkbenchImageGenerationSize;
            PART_Style.Text = _settings.WorkbenchImageGenerationStyle ?? string.Empty;
            SelectSize(configuredSize);
            PART_Size.SelectedItem ??= PART_Size.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        LoadStoredImages();
        ShowCurrent();
        UpdateStatus();
        UpdateOptionChips();
        UpdateGenerateButton();
        UpdateEmptyState();
    }

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        var prompt = PART_Prompt.Text?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            PART_Status.Text = "请先输入图像描述。";
            PART_Prompt.Focus();
            return;
        }

        EnsureConversation(prompt);
        var selected = _settings.SelectedWorkbenchImageGenerationModel;
        var providerId = selected?.ProviderId ?? _settings.WorkbenchImageGenerationProviderId;
        var modelId = selected?.ModelId ?? _settings.WorkbenchImageGenerationModelId;
        var modelLabel = selected?.Label ?? modelId;
        var editSource = SupportsEdit && _referenceLatest
            ? _results.LastOrDefault(result => result.HasImage)
            : null;
        var isEdit = editSource is not null;
        var taskTitle = CurrentTaskTitle();
        if (IsDefaultTaskTitle(taskTitle)) taskTitle = BuildTaskTitle(prompt);

        var pending = ImageWorkbenchResult.Pending(prompt, taskTitle, isEdit, modelLabel);
        _hiddenWhileGenerating = false;
        _hiddenNotificationShown = false;
        _results.Add(pending);
        ShowCurrent();
        ScrollResultsToEnd();

        var cts = new CancellationTokenSource();
        _cts = cts;
        SetGenerating(true);
        try
        {
            PART_Status.Text = isEdit ? "正在编辑图片。" : "正在生成图片。";
            var options = _settings.BuildWorkbenchImageGenerationOptions() with
            {
                Size = SelectedSize(),
                Style = string.IsNullOrWhiteSpace(PART_Style.Text) ? null : PART_Style.Text.Trim(),
                AsTool = false
            };
            var images = isEdit
                ? await _imageGeneration.EditAsync(options, prompt, editSource!.Bytes, editSource.MimeType, cts.Token)
                : await _imageGeneration.GenerateAsync(options, prompt, cts.Token);

            if (images.Count == 0)
            {
                ReplacePending(pending, ImageWorkbenchResult.Error(
                    prompt, taskTitle, isEdit, "未返回图片，请调整描述后重试。", modelLabel, modelId, providerId));
                PART_Status.Text = "未返回图片，请调整描述后重试。";
                return;
            }

            var insertIndex = _results.IndexOf(pending);
            if (insertIndex >= 0) _results.RemoveAt(insertIndex);
            else insertIndex = _results.Count;

            var added = 0;
            foreach (var image in images)
            {
                var fileName = $"generated-{DateTime.Now:yyyyMMdd-HHmmss}-{added + 1}{ExtensionForMime(image.MimeType)}";
                var localName = _attachmentStore.Save(image.Bytes, image.MimeType, fileName);
                var result = ImageWorkbenchResult.Completed(
                    fileName, image.MimeType, image.Bytes, localName, image.RevisedPrompt,
                    prompt, taskTitle, isEdit, modelLabel, modelId, providerId);
                _results.Insert(Math.Min(insertIndex + added, _results.Count), result);
                _gallery.Insert(0, result);
                Persist(prompt, result);
                added++;
            }

            PART_Status.Text = isEdit
                ? $"编辑完成，共 {added} 张图片。"
                : $"生成完成，共 {added} 张图片。";
            _notificationService?.ShowImageGenerationCompleted(
                _conversationId!, taskTitle, added, force: _hiddenWhileGenerating || !IsVisible);
            ScrollResultsToEnd();
        }
        catch (OperationCanceledException)
        {
            var error = ImageWorkbenchResult.Error(
                prompt, taskTitle, isEdit, "已取消本次生成。", modelLabel, modelId, providerId);
            ReplacePending(pending, error);
            Persist(prompt, error);
            PART_Status.Text = "已取消本次生成。";
            if (_notificationService is not null
                && !string.IsNullOrWhiteSpace(_conversationId)
                && (_hiddenWhileGenerating || !IsVisible))
            {
                _notificationService.ShowImageGenerationFailed(
                    _conversationId, taskTitle, "已取消本次生成。", force: true);
            }
        }
        catch (Exception ex)
        {
            var error = ImageWorkbenchResult.Error(
                prompt, taskTitle, isEdit, ex.Message, modelLabel, modelId, providerId);
            ReplacePending(pending, error);
            Persist(prompt, error);
            PART_Status.Text = "生成失败：" + ex.Message;
            if (_notificationService is not null && !string.IsNullOrWhiteSpace(_conversationId))
            {
                _notificationService.ShowImageGenerationFailed(
                    _conversationId, taskTitle, ex.Message, force: _hiddenWhileGenerating || !IsVisible);
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            SetGenerating(false);
        }
    }

    private void EnsureConversation(string prompt)
    {
        if (!string.IsNullOrWhiteSpace(_conversationId)) return;
        _conversationId = _createConversation(BuildTaskTitle(prompt), _settings.WorkbenchImageGenerationModelId);
    }

    private void ReplacePending(ImageWorkbenchResult pending, ImageWorkbenchResult replacement)
    {
        var index = _results.IndexOf(pending);
        if (index >= 0) _results[index] = replacement;
        else _results.Add(replacement);
    }

    private void SetGenerating(bool generating)
    {
        if (!string.IsNullOrWhiteSpace(_conversationId))
            _onGeneratingChanged(_conversationId, generating);
        PART_GenerateIcon.Text = generating ? "" : "";
        ToolTip.SetTip(PART_Generate, generating ? "停止" : "生成");
        PART_Generate.Classes.Set("stop", generating);
        UpdateGenerateButton();
        UpdateEmptyState();
    }

    private void UpdateGenerateButton() =>
        PART_Generate.IsEnabled = _cts is not null
            || (_settings.IsWorkbenchImageGenerationConfigured
                && !string.IsNullOrWhiteSpace(PART_Prompt.Text));

    private void OnPromptKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) return;
        e.Handled = true;
        if (PART_Generate.IsEnabled) OnGenerate(PART_Generate, new RoutedEventArgs());
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.WorkbenchImageGenerationProviderId)
            or nameof(SettingsViewModel.WorkbenchImageGenerationModelId))
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatus();
                UpdateGenerateButton();
            });
        }
    }

    private void UpdateStatus()
    {
        PART_EditModes.IsVisible = SupportsEdit;
        PART_ModeLabel.Text = SupportsEdit && _referenceLatest ? "图像编辑" : "图像生成";
        PART_ReferenceLatest.Classes.Set("active", SupportsEdit && _referenceLatest);
        PART_FreshGenerate.Classes.Set("active", SupportsEdit && !_referenceLatest);

        var provider = _settings.GetWorkbenchImageGenerationProvider();
        if (provider is null)
        {
            PART_ConfigSummary.Text = "暂无可用的图像服务";
            PART_Status.Text = "请在设置的模型服务中添加图像服务。";
            return;
        }

        PART_ConfigSummary.Text =
            $"服务：{provider.Name}\n模型：{_settings.WorkbenchImageGenerationModelId}\n尺寸：{ReadableSize(SelectedSize())}";
        PART_Status.Text = _settings.IsWorkbenchImageGenerationConfigured
            ? SupportsEdit
                ? "已就绪。当前模型支持在上一张图的基础上继续编辑。"
                : "已就绪。当前模型仅支持生成新图。"
            : "请补全图像服务的地址、密钥和模型。";
    }

    private void SetReferenceMode(bool reference)
    {
        _referenceLatest = reference;
        UpdateStatus();
    }

    private void OnSizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PART_Size.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag?.ToString() is { Length: > 0 } size)
            _settings.WorkbenchImageGenerationSize = size;
        UpdateOptionChips();
        UpdateStatus();
    }

    private void OnStyleChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _settings.WorkbenchImageGenerationStyle = string.IsNullOrWhiteSpace(PART_Style.Text)
            ? null
            : PART_Style.Text.Trim();
        UpdateOptionChips();
    }

    private void OnRatio(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string size }) SelectSize(size);
    }

    private void OnStyle(object? sender, RoutedEventArgs e)
    {
        PART_Style.Text = sender is Button { Tag: string style } ? style : string.Empty;
    }

    private void SelectSize(string size)
    {
        foreach (var item in PART_Size.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), size, StringComparison.OrdinalIgnoreCase)) continue;
            PART_Size.SelectedItem = item;
            break;
        }
        UpdateOptionChips();
    }

    private void UpdateOptionChips()
    {
        var size = SelectedSize();
        foreach (var button in PART_RatioChips.Children.OfType<Button>())
            button.Classes.Set("active", string.Equals(button.Tag?.ToString(), size, StringComparison.OrdinalIgnoreCase));
        var style = PART_Style.Text?.Trim() ?? string.Empty;
        foreach (var button in PART_StyleChips.Children.OfType<Button>())
            button.Classes.Set("active", string.Equals(button.Tag?.ToString() ?? string.Empty, style, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedSize() =>
        (PART_Size.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } size
            ? size
            : "1024x1024";

    private void NewTask()
    {
        if (_cts is not null) return;
        _conversationId = null;
        _results.Clear();
        PART_Prompt.Clear();
        ShowCurrent();
        PART_Status.Text = "已新建图像任务。";
    }

    private void ClearResults()
    {
        if (_cts is not null) return;
        _results.Clear();
        PART_Status.Text = "已清空当前结果，作品仍保留在画廊中。";
    }

    private void ShowCurrent()
    {
        PART_CurrentPane.IsVisible = true;
        PART_GalleryPane.IsVisible = false;
        PART_CurrentTab.Classes.Set("active", true);
        PART_GalleryTab.Classes.Set("active", false);
    }

    private void ShowGallery()
    {
        PART_CurrentPane.IsVisible = false;
        PART_GalleryPane.IsVisible = true;
        PART_CurrentTab.Classes.Set("active", false);
        PART_GalleryTab.Classes.Set("active", true);
    }

    private void UpdateEmptyState()
    {
        PART_EmptyResults.IsVisible = _results.Count == 0;
        PART_EmptyGallery.IsVisible = _gallery.Count == 0;
        PART_CurrentCount.Text = _results.Count.ToString();
        PART_GalleryCount.Text = _gallery.Count.ToString();
        PART_ClearResults.IsVisible = _results.Count > 0 && _cts is null;
    }

    private void ScrollResultsToEnd() =>
        Dispatcher.UIThread.Post(() => PART_ResultsScroll.ScrollToEnd(), DispatcherPriority.Loaded);

    private void LoadStoredImages()
    {
        _results.Clear();
        _gallery.Clear();

        if (!string.IsNullOrWhiteSpace(_conversationId))
        {
            var title = CurrentTaskTitle();
            foreach (var result in _messageRepo.List(_conversationId)
                         .SelectMany(row => ParseStored(row.Meta, row.Content, row.CreatedAt, title, true))
                         .OrderBy(result => result.CreatedAt))
            {
                _results.Add(result);
            }
        }

        foreach (var result in _messageRepo
                     .ListImageWorkbenchMessages(ConversationListViewModel.ImageWorkbenchProviderId)
                     .SelectMany(row => ParseStored(row.Meta, row.Content, row.CreatedAt, row.ConversationTitle, false))
                     .OrderByDescending(result => result.CreatedAt))
        {
            _gallery.Add(result);
        }

        if (_results.LastOrDefault() is { Prompt.Length: > 0 } latest)
            PART_Prompt.Text = latest.Prompt;
    }

    private IEnumerable<ImageWorkbenchResult> ParseStored(
        string? meta, string content, long createdAt, string taskTitle, bool includeErrors)
    {
        if (string.IsNullOrWhiteSpace(meta)) yield break;

        JsonDocument document;
        try { document = JsonDocument.Parse(meta); }
        catch (JsonException) { yield break; }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("image_workbench", out var marker) || marker.ValueKind != JsonValueKind.True)
                yield break;

            var prompt = ReadString(root, "prompt") ?? content;
            var revised = ReadString(root, "revised_prompt") ?? content;
            var modelLabel = ReadString(root, "model_label") ?? ReadString(root, "model") ?? ReadString(root, "model_id");
            var modelId = ReadString(root, "model_id");
            var providerId = ReadString(root, "provider_id");
            var isEdit = ReadBool(root, "image_edit");
            var isError = string.Equals(ReadString(root, "status"), "error", StringComparison.OrdinalIgnoreCase);
            var created = DateTimeOffset.FromUnixTimeMilliseconds(createdAt).ToLocalTime();

            if (isError)
            {
                if (includeErrors)
                    yield return ImageWorkbenchResult.Error(
                        prompt, taskTitle, isEdit, ReadString(root, "error_message") ?? content,
                        modelLabel, modelId, providerId, created);
                yield break;
            }

            if (!root.TryGetProperty("attachments", out var attachments)
                || attachments.ValueKind != JsonValueKind.Array) yield break;

            foreach (var attachment in attachments.EnumerateArray())
            {
                if (attachment.ValueKind != JsonValueKind.Object) continue;
                var localName = ReadString(attachment, "localName");
                var bytes = _attachmentStore.Load(localName);
                if (bytes is not { Length: > 0 }) continue;
                var mime = ReadString(attachment, "mime") ?? "image/png";
                var fileName = ReadString(attachment, "filename")
                    ?? localName
                    ?? $"generated-{createdAt}{ExtensionForMime(mime)}";
                yield return ImageWorkbenchResult.Completed(
                    fileName, mime, bytes, localName, revised, prompt, taskTitle, isEdit,
                    modelLabel, modelId, providerId, created);
            }
        }
    }

    private void Persist(string prompt, ImageWorkbenchResult result)
    {
        if (string.IsNullOrWhiteSpace(_conversationId)) return;
        if (!result.IsError && string.IsNullOrWhiteSpace(result.LocalName)) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var meta = new JsonObject
        {
            ["image_workbench"] = true,
            ["prompt"] = prompt,
            ["image_edit"] = result.IsEditMode,
            ["model_label"] = result.ModelLabel,
            ["model"] = result.ModelLabel,
            ["model_id"] = result.ModelId,
            ["provider_id"] = result.ProviderId,
            ["status"] = result.IsError ? "error" : "completed"
        };

        string content;
        if (result.IsError)
        {
            meta["error_message"] = result.ErrorDisplay;
            content = (result.IsEditMode ? "图像编辑失败：" : "图像生成失败：") + result.ErrorDisplay;
        }
        else
        {
            meta["revised_prompt"] = result.RevisedPrompt;
            meta["attachments"] = new JsonArray(new JsonObject
            {
                ["filename"] = result.FileName,
                ["label"] = "图片",
                ["localName"] = result.LocalName,
                ["mime"] = result.MimeType
            });
            content = string.IsNullOrWhiteSpace(result.RevisedPrompt) ? "图像生成完成" : result.RevisedPrompt;
        }

        _messageRepo.Insert(new MessageRow(
            Guid.NewGuid().ToString("N"), _conversationId, "assistant", content, meta.ToJsonString(), now));
        if (_conversationRepo.Get(_conversationId) is not { } row) return;
        _conversationRepo.Upsert(row with
        {
            Title = IsDefaultTaskTitle(row.Title) ? BuildTaskTitle(prompt) : row.Title,
            ModelId = result.ModelId ?? _settings.WorkbenchImageGenerationModelId,
            UpdatedAt = now
        });
    }

    private async void OnSaveResult(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageWorkbenchResult { HasImage: true } result }) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存图片",
            SuggestedFileName = result.FileName,
            DefaultExtension = ExtensionForMime(result.MimeType).TrimStart('.')
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(result.Bytes);
            PART_Status.Text = "已保存到 " + (file.TryGetLocalPath() ?? file.Name);
        }
        catch (Exception ex)
        {
            PART_Status.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnPreviewResult(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageWorkbenchResult { HasImage: true } result }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // The full bytes, not the list's thumbnail: this is the only place the
        // generated image is shown at the size it was actually produced at.
        _ = ImagePreviewWindow.ShowAsync(owner, result.Bytes, result.FileName);
    }

    private string CurrentTaskTitle() => string.IsNullOrWhiteSpace(_conversationId)
        ? "图像工作台"
        : _conversationRepo.Get(_conversationId)?.Title ?? "图像工作台";

    public void NotifyHiddenWhileGenerating()
    {
        if (_cts is null || _hiddenNotificationShown) return;

        _hiddenWhileGenerating = true;
        _notificationService?.ShowImageGenerationStarted(_conversationId ?? string.Empty, CurrentTaskTitle());
        _hiddenNotificationShown = true;
    }

    private static string BuildTaskTitle(string prompt)
    {
        var compact = string.Join(" ", prompt.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length == 0) return "图像工作台";
        return compact.Length <= 18 ? compact : compact[..18] + "...";
    }

    private static bool IsDefaultTaskTitle(string? title) => string.IsNullOrWhiteSpace(title)
        || string.Equals(title, "图像工作台", StringComparison.Ordinal)
        || string.Equals(title, "新对话", StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static string ExtensionForMime(string? mimeType) => mimeType?.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png"
    };

    private static string ReadableSize(string? size) => string.IsNullOrWhiteSpace(size)
        ? "1024×1024"
        : size.Trim().Replace("x", "×", StringComparison.OrdinalIgnoreCase);
}

public sealed record ImageWorkbenchResult(
    string FileName,
    string MimeType,
    byte[] Bytes,
    string? LocalName,
    string? RevisedPrompt,
    Bitmap? Thumbnail,
    string Prompt,
    string TaskTitle,
    DateTimeOffset CreatedAt,
    bool IsEditMode = false,
    bool IsPending = false,
    bool IsError = false,
    string? ErrorMessage = null,
    string? ModelLabel = null,
    string? ModelId = null,
    string? ProviderId = null)
{
    public bool HasImage => !IsPending && !IsError && Bytes.Length > 0 && Thumbnail is not null;
    public bool HasModelLabel => !string.IsNullOrWhiteSpace(ModelLabel);
    public string PromptHeader => IsEditMode ? "修改指令" : "生成提示词";
    public string ModeLabel => IsPending
        ? IsEditMode ? "编辑中" : "生成中"
        : IsError ? "生成失败" : IsEditMode ? "图像编辑" : "图像生成";
    public string CreatedAtText => CreatedAt.ToString("MM-dd HH:mm");
    public string PendingStatusText => IsEditMode ? "正在编辑图片" : "正在生成图片";
    public string ErrorDisplay => string.IsNullOrWhiteSpace(ErrorMessage) ? "本次任务未完成。" : ErrorMessage;
    public string RevisedPromptDisplay => string.IsNullOrWhiteSpace(RevisedPrompt)
        ? IsEditMode ? "图像编辑完成" : "图像生成完成"
        : (IsEditMode ? "编辑提示词：" : "修订提示词：") + RevisedPrompt;

    public static ImageWorkbenchResult Pending(string prompt, string title, bool edit, string? modelLabel) =>
        new(string.Empty, "image/png", [], null, null, null, prompt, title, DateTimeOffset.Now,
            edit, IsPending: true, ModelLabel: modelLabel);

    public static ImageWorkbenchResult Error(
        string prompt, string title, bool edit, string message, string? modelLabel,
        string? modelId, string? providerId, DateTimeOffset? createdAt = null) =>
        new(string.Empty, "image/png", [], null, null, null, prompt, title, createdAt ?? DateTimeOffset.Now,
            edit, IsError: true, ErrorMessage: message, ModelLabel: modelLabel,
            ModelId: modelId, ProviderId: providerId);

    public static ImageWorkbenchResult Completed(
        string fileName, string mimeType, byte[] bytes, string? localName, string? revisedPrompt,
        string prompt, string title, bool edit, string? modelLabel, string? modelId, string? providerId,
        DateTimeOffset? createdAt = null) =>
        new(fileName, mimeType, bytes, localName, revisedPrompt, CreateBitmap(bytes), prompt, title,
            createdAt ?? DateTimeOffset.Now, edit, ModelLabel: modelLabel, ModelId: modelId, ProviderId: providerId);

    private static Bitmap? CreateBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
