using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MolaGPT.App.Infrastructure;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class ImageGenerationWorkbenchView : UserControl
{
    /// <summary>How many gallery entries are loaded at once. Every entry holds a
    /// decoded thumbnail, so this is a memory ceiling, not a query nicety.</summary>
    private const int GalleryLimit = 200;

    private readonly SettingsViewModel _settings;
    private readonly ImageGenerationTool _imageGeneration;
    private readonly AttachmentStore _attachmentStore;
    private readonly ConversationRepository _conversationRepo;
    private readonly MessageRepository _messageRepo;
    private readonly Func<string, string?, string> _createConversation;
    private readonly Action<string, bool> _onGeneratingChanged;
    private readonly NotificationCenter? _notifications;
    private readonly ObservableCollection<ImageWorkbenchResult> _results = new();
    private readonly ObservableCollection<ImageWorkbenchResult> _gallery = new();
    private CancellationTokenSource? _cts;
    private string? _conversationId;
    private bool _loading;
    private string _size = "1024x1024";
    private WorkbenchMode _mode = WorkbenchMode.Conversation;
    private byte[]? _baseBytes;
    private string? _baseMime;
    private string? _baseName;
    private Bitmap? _baseThumbnail;

    /// <summary>
    /// True while the base image is newer than every result in the task, which
    /// makes it the head of the 对话模式 chain. Without this, dropping a picture
    /// into a task that already has results would load a base that nothing ever
    /// edits.
    /// </summary>
    private bool _baseIsChainHead;
    private bool _hiddenNotificationShown;

    /// <summary>
    /// What a run does with the images already in the task. The base image is
    /// *not* one of these — it is an input either mode can be given, which is
    /// why it lives in its own fields rather than as a third mode.
    /// </summary>
    private enum WorkbenchMode
    {
        /// <summary>One shot. Each run starts from the same place: the base
        /// image if there is one, otherwise nothing. Output never feeds back.</summary>
        Single,

        /// <summary>Iterative. Each run edits the newest image in the task —
        /// the last result, or the base image before anything is generated.</summary>
        Conversation
    }

    public ImageGenerationWorkbenchView(
        SettingsViewModel settings,
        ImageGenerationTool imageGeneration,
        AttachmentStore attachmentStore,
        ConversationRepository conversationRepo,
        MessageRepository messageRepo,
        string? conversationId,
        Func<string, string?, string> createConversation,
        Action<string, bool> onGeneratingChanged,
        NotificationCenter? notifications = null)
    {
        _settings = settings;
        _imageGeneration = imageGeneration;
        _attachmentStore = attachmentStore;
        _conversationRepo = conversationRepo;
        _messageRepo = messageRepo;
        _conversationId = conversationId;
        _createConversation = createConversation;
        _onGeneratingChanged = onGeneratingChanged;
        _notifications = notifications;

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
        PART_Stop.Click += (_, _) => _cts?.Cancel();
        PART_SingleMode.Click += (_, _) => SetMode(WorkbenchMode.Single);
        PART_ConversationMode.Click += (_, _) => SetMode(WorkbenchMode.Conversation);
        PART_UploadSource.Click += OnUploadSourceClick;
        PART_ReplaceSource.Click += OnUploadSourceClick;
        PART_ClearSource.Click += (_, _) => ClearBaseImage();
        PART_Style.TextChanged += OnStyleChanged;
        PART_Prompt.TextChanged += (_, _) => UpdateGenerateButton();

        // Tunnel: TextBox marks Enter handled in a class handler when
        // AcceptsReturn is on, so a plain `KeyDown +=` never sees it — the same
        // trap ComposerView documents.
        PART_Prompt.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);

        // Dropping onto the padding around the box is the same gesture to a user,
        // so the whole composer card accepts it.
        PART_ComposerShell.AddHandler(DragDrop.DragOverEvent, OnSourceDragOver);
        PART_ComposerShell.AddHandler(DragDrop.DropEvent, OnSourceDrop);

        _results.CollectionChanged += (_, _) =>
        {
            UpdateEmptyState();
            UpdateSourceUi();
        };
        _gallery.CollectionChanged += (_, _) => UpdateEmptyState();
        _settings.PropertyChanged += OnSettingsChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingsChanged;
            DisposeBaseThumbnail();
        };

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
            _size = string.IsNullOrWhiteSpace(_settings.WorkbenchImageGenerationSize)
                ? "1024x1024"
                : _settings.WorkbenchImageGenerationSize.Trim();
            PART_Style.Text = _settings.WorkbenchImageGenerationStyle ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }

        LoadStoredImages();
        ShowCurrent();
        UpdateOptionChips();
        UpdateStatus();
        UpdateGenerateButton();
        UpdateEmptyState();
    }

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        // Cancelling lives on PART_Stop now. This used to double as the cancel
        // path, which meant Enter mid-run aborted the image it had just started.
        if (_cts is not null) return;

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
        var editSource = ResolveEditSource();
        var isEdit = editSource is not null;
        var taskTitle = CurrentTaskTitle();
        if (IsDefaultTaskTitle(taskTitle)) taskTitle = BuildTaskTitle(prompt);

        var pending = ImageWorkbenchResult.Pending(prompt, taskTitle, isEdit, modelLabel);
        _hiddenNotificationShown = false;
        _results.Add(pending);
        ShowCurrent();
        ScrollResultsToEnd();

        var cts = new CancellationTokenSource();
        _cts = cts;
        SetGenerating(true);
        try
        {
            // HasEditSource can be true while the resolve came up empty — the
            // stored original was deleted between the last refresh and now.
            PART_Status.Text = isEdit
                ? "正在编辑图片。"
                : HasEditSource
                    ? "底图已不可用，本次改为直接生成。"
                    : "正在生成图片。";
            var options = _settings.BuildWorkbenchImageGenerationOptions() with
            {
                Size = SelectedSize(),
                Style = string.IsNullOrWhiteSpace(PART_Style.Text) ? null : PART_Style.Text.Trim(),
                AsTool = false
            };
            var images = isEdit
                ? await _imageGeneration.EditAsync(
                    options, prompt, editSource!.Value.Bytes, editSource.Value.MimeType, cts.Token)
                : await _imageGeneration.GenerateAsync(options, prompt, cts.Token);

            if (images.Count == 0)
            {
                ReplacePending(pending, ImageWorkbenchResult.Error(
                    prompt, taskTitle, isEdit, "未返回图片，请调整描述后重试。", modelLabel, modelId, providerId));
                PART_Status.Text = "未返回图片，请调整描述后重试。";

                // Still a terminal state: without it the 「正在后台生成」 banner
                // for this key would stay up for good.
                if (!string.IsNullOrWhiteSpace(_conversationId))
                {
                    _notifications?.Notify(new AppNotification
                    {
                        Key = "image-" + _conversationId,
                        Kind = NotifyKind.Warning,
                        Title = string.IsNullOrWhiteSpace(taskTitle) ? "未返回图片" : $"「{taskTitle}」未返回图片",
                        Body = "请调整描述后重试。",
                        ConversationId = _conversationId,
                        IsAnswerCompleted = true
                    });
                }
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
                    fileName, image.MimeType, DecodeThumbnail(image.Bytes),
                    ByteSourceFor(localName, image.Bytes), localName, image.RevisedPrompt,
                    prompt, taskTitle, isEdit, modelLabel, modelId, providerId);
                _results.Insert(Math.Min(insertIndex + added, _results.Count), result);
                _gallery.Insert(0, result);
                Persist(prompt, result);

                // The chain head moves to what was just produced; the base image
                // stays loaded for 生成模式, which always goes back to it.
                _baseIsChainHead = false;
                added++;
            }

            PART_Status.Text = isEdit
                ? $"编辑完成，共 {added} 张图片。"
                : $"生成完成，共 {added} 张图片。";
            _notifications?.Notify(new AppNotification
            {
                Key = "image-" + _conversationId,
                Kind = NotifyKind.Success,
                Title = string.IsNullOrWhiteSpace(taskTitle) ? "图像生成完成" : $"「{taskTitle}」生成完成",
                Body = added > 0 ? $"已生成 {added} 张图片" : null,
                ConversationId = _conversationId,
                IsAnswerCompleted = true
            });
            ScrollResultsToEnd();
        }
        catch (OperationCanceledException)
        {
            var error = ImageWorkbenchResult.Error(
                prompt, taskTitle, isEdit, "已取消本次生成。", modelLabel, modelId, providerId);
            ReplacePending(pending, error);
            Persist(prompt, error);
            PART_Status.Text = "已取消本次生成。";
            if (!string.IsNullOrWhiteSpace(_conversationId))
            {
                _notifications?.Notify(new AppNotification
                {
                    Key = "image-" + _conversationId,
                    Kind = NotifyKind.Warning,
                    Title = string.IsNullOrWhiteSpace(taskTitle) ? "图像生成已取消" : $"「{taskTitle}」已取消",
                    ConversationId = _conversationId,
                    IsAnswerCompleted = true
                });
            }
        }
        catch (Exception ex)
        {
            var error = ImageWorkbenchResult.Error(
                prompt, taskTitle, isEdit, ex.Message, modelLabel, modelId, providerId);
            ReplacePending(pending, error);
            Persist(prompt, error);
            PART_Status.Text = "生成失败：" + ex.Message;
            if (!string.IsNullOrWhiteSpace(_conversationId))
            {
                _notifications?.Notify(new AppNotification
                {
                    Key = "image-" + _conversationId,
                    Kind = NotifyKind.Error,
                    Title = string.IsNullOrWhiteSpace(taskTitle) ? "图像生成失败" : $"「{taskTitle}」生成失败",
                    Body = ex.Message,
                    ConversationId = _conversationId
                });
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
        PART_Generate.IsVisible = !generating;
        PART_Stop.IsVisible = generating;
        UpdateGenerateButton();
        UpdateEmptyState();
    }

    private void UpdateGenerateButton() =>
        PART_Generate.IsEnabled = _settings.IsWorkbenchImageGenerationConfigured
            && !string.IsNullOrWhiteSpace(PART_Prompt.Text);

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = TryPasteSourceAsync();
            return;
        }

        if (e.Key != Key.Enter) return;

        // Same contract as the chat composer, read from the same preference:
        // Ctrl+Enter always sends, and 「Enter 发送」 decides what a bare Enter does.
        var wantsSend = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || (_settings.EnterToSend && !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (!wantsSend) return;

        e.Handled = true;
        if (PART_Generate.IsEnabled) OnGenerate(PART_Generate, new RoutedEventArgs());
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.WorkbenchImageGenerationProviderId)
            or nameof(SettingsViewModel.WorkbenchImageGenerationModelId)
            or nameof(SettingsViewModel.EnterToSend))
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
        UpdateSourceUi();

        if (_settings.GetWorkbenchImageGenerationProvider() is null)
        {
            PART_Status.Text = "请在设置的模型服务中添加图像服务。";
            return;
        }

        PART_Status.Text = _settings.IsWorkbenchImageGenerationConfigured
            ? SupportsEdit
                ? "已就绪。当前模型支持在已有图片的基础上继续编辑。"
                : "已就绪。当前模型仅支持生成新图。"
            : "请补全图像服务的地址、密钥和模型。";
    }

    /// <summary>
    /// Says where each knob actually goes. The panel used to print
    /// 「尺寸：1024×1024」 for models whose endpoint has no size field at all —
    /// the value was dropped and the summary claimed otherwise.
    /// </summary>
    private void UpdateConfigSummary()
    {
        PART_SizeCaption.Text = string.Equals(SelectedSize(), "auto", StringComparison.OrdinalIgnoreCase)
            ? "由模型决定"
            : ReadableSize(SelectedSize());

        var provider = _settings.GetWorkbenchImageGenerationProvider();
        if (provider is null)
        {
            PART_ConfigSummary.Text = "暂无可用的图像服务";
            return;
        }

        var options = _settings.BuildWorkbenchImageGenerationOptions() with
        {
            Size = SelectedSize(),
            Style = string.IsNullOrWhiteSpace(PART_Style.Text) ? null : PART_Style.Text.Trim()
        };
        var delivery = ImagePromptComposer.Describe(options, isEdit: HasEditSource);

        var lines = new List<string>
        {
            $"服务：{provider.Name}",
            $"模型：{_settings.WorkbenchImageGenerationModelId}",
            $"画幅：{PART_SizeCaption.Text}{ChannelSuffix(delivery.Size)}"
        };
        if (options.Style is { Length: > 0 } style)
            lines.Add($"风格：{style}{ChannelSuffix(delivery.Style)}");

        PART_ConfigSummary.Text = string.Join("\n", lines);
    }

    private static string ChannelSuffix(ImageParameterChannel channel) => channel switch
    {
        ImageParameterChannel.Parameter => "（接口参数）",
        ImageParameterChannel.Prompt => "（写入提示词）",
        _ => "（本次不生效）"
    };

    /// <summary>
    /// The mode strip, the base-image chip and the 「本次：…」 label all read the
    /// same resolved state, so the label can never promise an edit the run would
    /// silently downgrade to a fresh generation.
    /// </summary>
    private void UpdateSourceUi()
    {
        // Without edit support there is exactly one possible behaviour and no
        // base image can be sent, so the whole strip is noise.
        PART_EditModes.IsVisible = SupportsEdit;
        PART_SingleMode.Classes.Set("active", SupportsEdit && _mode == WorkbenchMode.Single);
        PART_ConversationMode.Classes.Set("active", SupportsEdit && _mode == WorkbenchMode.Conversation);

        var hasBase = _baseBytes is { Length: > 0 };
        var chained = _mode == WorkbenchMode.Conversation && !_baseIsChainHead
                      && _results.Any(result => result.HasImage);

        PART_SourceChip.IsVisible = SupportsEdit && hasBase;
        PART_SourceThumb.Source = _baseThumbnail;
        PART_SourceName.Text = _baseName ?? string.Empty;
        PART_SourceHint.Text = !hasBase
            ? string.Empty
            : chained
                // Honest about being superseded rather than implying the base is
                // still what gets edited.
                ? $"已由最新结果接手 · {FormatSize(_baseBytes!.Length)}"
                : _mode == WorkbenchMode.Single
                    ? $"每次都从这张图出发 · {FormatSize(_baseBytes!.Length)}"
                    : $"下一张从这里开始 · {FormatSize(_baseBytes!.Length)}";

        // Drag-and-drop and paste have no button of their own; the placeholder
        // is the only place they are discoverable.
        PART_Prompt.PlaceholderText = SupportsEdit
            ? "描述你想要的画面，或拖入 / 粘贴一张图片作为底图…"
            : "描述你想要的画面…";

        // Reads the same preference OnPromptKeyDown does, so the hint cannot
        // describe a key that no longer sends.
        PART_Hint.Text = _settings.EnterToSend
            ? "Enter 发送 · Shift+Enter 换行"
            : "Enter 换行 · Ctrl+Enter 发送";

        PART_ModeLabel.Text = chained
            ? "本次：在上一张上继续修改"
            : SupportsEdit && hasBase
                ? "本次：编辑底图"
                : "本次：生成新图";

        // 画幅 delivery depends on whether this run is an edit, so the summary
        // has to follow the mode strip.
        UpdateConfigSummary();
    }

    /// <summary>
    /// Whether this run would be an edit — without touching the bytes. The UI
    /// asks this on every refresh, and <see cref="ResolveEditSource"/> reads the
    /// image back off disk.
    /// </summary>
    private bool HasEditSource => SupportsEdit
        && ((_mode == WorkbenchMode.Conversation && !_baseIsChainHead && _results.Any(result => result.HasImage))
            || _baseBytes is { Length: > 0 });

    /// <summary>
    /// The bytes this run will edit, or null for a plain generation.
    ///
    /// 对话模式 chains on the newest result and falls back to the base image
    /// before anything has been generated; 生成模式 always goes back to the base
    /// image, so five runs give five variants of the same source rather than a
    /// chain. Either can come up empty and then simply generates.
    /// </summary>
    private (byte[] Bytes, string MimeType)? ResolveEditSource()
    {
        if (!SupportsEdit) return null;

        if (_mode == WorkbenchMode.Conversation && !_baseIsChainHead
            && _results.LastOrDefault(result => result.HasImage) is { } latest
            && latest.LoadBytes() is { Length: > 0 } previous)
        {
            return (previous, latest.MimeType);
        }

        return _baseBytes is { Length: > 0 } bytes ? (bytes, _baseMime ?? "image/png") : null;
    }

    private void SetMode(WorkbenchMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        UpdateSourceUi();

        PART_Status.Text = mode == WorkbenchMode.Conversation
            ? _baseBytes is { Length: > 0 } && _baseIsChainHead
                ? "对话模式：先编辑底图，之后每次都接着上一张改。"
                : _results.Any(result => result.HasImage)
                    ? "对话模式：接下来会在最新一张的基础上继续修改。"
                    : "对话模式：第一张从头生成，之后每次都接着上一张改。"
            : _baseBytes is { Length: > 0 }
                ? "生成模式：每次都从这张底图重新出发，结果互不影响。"
                : "生成模式：每次都从头生成一张，互不影响。";
    }

    // ---- edit source: upload / drop / paste / reuse a result ---------------

    // The base image is an input, not a mode: picking one never changes 生成 /
    // 对话, and both modes make use of it.
    private async void OnUploadSourceClick(object? sender, RoutedEventArgs e) =>
        await PickBaseImageAsync();

    private async Task<bool> PickBaseImageAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return false;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要编辑的图片",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        if (files.Count == 0) return false;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return ApplyBaseImage(buffer.ToArray(), files[0].Name);
        }
        catch (Exception ex)
        {
            PART_Status.Text = "无法读取图片：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Every entry point (picker, drop, paste, "以这张图为底图") lands here, so the
    /// size ceiling, EXIF fix-up and 2000px cap of the chat attachment pipeline
    /// apply to the workbench too.
    /// </summary>
    private bool ApplyBaseImage(byte[] bytes, string fileName)
    {
        if (!SupportsEdit)
        {
            PART_Status.Text = "当前模型不支持图像编辑，无法使用底图。";
            return false;
        }

        var intake = AttachmentIntake.FromBytes(bytes, fileName, new AttachmentIntakeCapabilities(true, false));
        if (intake.Attachment is not { Kind: AttachmentKind.Image } image)
        {
            PART_Status.Text = intake.Error ?? "请选择图片文件（PNG / JPEG / WebP）。";
            return false;
        }

        DisposeBaseThumbnail();
        _baseBytes = image.Bytes;
        _baseMime = image.MimeType;
        _baseName = fileName;
        _baseThumbnail = DecodeThumbnail(image.Bytes);

        // Newer than anything already in the task, so 对话模式 continues from it
        // rather than from a result the user has just moved past.
        _baseIsChainHead = true;
        UpdateSourceUi();
        PART_Status.Text = $"已载入底图 {fileName}，描述你想要的修改。";
        return true;
    }

    /// <summary>Unbinds before disposing: an Image still pointing at a disposed
    /// bitmap is a crash waiting for the next render pass.</summary>
    private void DisposeBaseThumbnail()
    {
        PART_SourceThumb.Source = null;
        _baseThumbnail?.Dispose();
        _baseThumbnail = null;
    }

    private void ClearBaseImage()
    {
        DisposeBaseThumbnail();
        _baseBytes = null;
        _baseMime = null;
        _baseName = null;
        _baseIsChainHead = false;
        UpdateSourceUi();
        PART_Status.Text = "已移除底图。";
    }

    private void OnUseAsSource(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageWorkbenchResult { HasImage: true } result }) return;
        if (result.LoadBytes() is not { Length: > 0 } bytes)
        {
            PART_Status.Text = "原图已不在本地存储中，无法用作底图。";
            return;
        }

        if (ApplyBaseImage(bytes, result.FileName)) ShowCurrent();
    }

    private void OnSourceDragOver(object? sender, DragEventArgs e)
    {
        var accepted = SupportsEdit && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = accepted;
    }

    private void OnSourceDrop(object? sender, DragEventArgs e)
    {
        if (!SupportsEdit) return;
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;

        e.Handled = true;

        // Only the first image: the workbench edits one base image at a time,
        // and silently keeping the last of five dropped files is worse than
        // saying which one was taken.
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
        {
            PART_Status.Text = "请拖入单张图片文件。";
            return;
        }

        try
        {
            ApplyBaseImage(File.ReadAllBytes(path), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            PART_Status.Text = "无法读取图片：" + ex.Message;
        }
    }

    /// <summary>
    /// Ctrl+V with a screenshot on the clipboard loads it as the base image.
    /// Text keeps the TextBox's own paste — the handler stays unhandled because
    /// telling the two apart needs an await, by which time the key is delivered.
    /// </summary>
    private async Task TryPasteSourceAsync()
    {
        if (!SupportsEdit) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            using var data = await clipboard.TryGetDataAsync();
            if (data is null) return;

            if (data.Contains(DataFormat.File)
                && await data.TryGetFilesAsync() is { Length: > 0 } files
                && files[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                ApplyBaseImage(File.ReadAllBytes(path), Path.GetFileName(path));
                return;
            }

            if (data.Contains(DataFormat.Bitmap)
                && await data.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    using var buffer = new MemoryStream();
                    bitmap.Save(buffer, PngBitmapEncoderOptions.Default);
                    ApplyBaseImage(buffer.ToArray(), $"粘贴图片_{DateTime.Now:HHmmss}.png");
                }
            }
        }
        catch
        {
            // The clipboard is shared state and can be locked mid-read by
            // another process; a failed paste is not worth surfacing.
        }
    }

    private void OnStyleChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _settings.WorkbenchImageGenerationStyle = string.IsNullOrWhiteSpace(PART_Style.Text)
            ? null
            : PART_Style.Text.Trim();
        UpdateOptionChips();
        UpdateConfigSummary();
    }

    private void OnRatio(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string size }) return;
        _size = size;
        _settings.WorkbenchImageGenerationSize = size;
        UpdateOptionChips();
        UpdateConfigSummary();
    }

    private void OnStyle(object? sender, RoutedEventArgs e)
    {
        PART_Style.Text = sender is Button { Tag: string style } ? style : string.Empty;
    }

    /// <summary>
    /// Fills the prompt box from an empty-state starter and puts the caret at
    /// the end. Deliberately does not generate: the starter is a draft to edit,
    /// and a chip that spends money on one click is a chip nobody dares press.
    /// </summary>
    private void OnPromptStarter(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt } || prompt.Length == 0) return;
        PART_Prompt.Text = prompt;
        PART_Prompt.CaretIndex = prompt.Length;
        PART_Prompt.Focus();
    }

    private void UpdateOptionChips()
    {
        // A size stored before the chip set changed may match nothing; leaving
        // every chip dark is honest, and the caption still shows the value.
        foreach (var button in PART_RatioChips.Children.OfType<Button>())
            button.Classes.Set("active", string.Equals(button.Tag?.ToString(), _size, StringComparison.OrdinalIgnoreCase));

        var style = PART_Style.Text?.Trim() ?? string.Empty;
        foreach (var button in PART_StyleChips.Children.OfType<Button>())
            button.Classes.Set("active", string.Equals(button.Tag?.ToString() ?? string.Empty, style, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedSize() => string.IsNullOrWhiteSpace(_size) ? "1024x1024" : _size;

    private void NewTask()
    {
        if (_cts is not null) return;
        _conversationId = null;
        _results.Clear();
        PART_Prompt.Clear();
        ClearBaseImage();
        ShowCurrent();
        PART_Status.Text = "已新建图像任务。";
    }

    private void ClearResults()
    {
        if (_cts is not null) return;
        _results.Clear();
        PART_Status.Text = "已收起当前视图。作品仍在画廊中，重新打开这个任务会再次显示。";
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
        // The scroller and the starters share a cell; an empty scroller left
        // visible would sit on top of the chips and swallow their clicks.
        PART_ResultsScroll.IsVisible = _results.Count > 0;
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

        // The query is already newest-first, so taking the head keeps the cap on
        // the *recent* end — and stops before decoding a thumbnail for entry 201.
        var stored = _messageRepo
            .ListImageWorkbenchMessages(ConversationListViewModel.ImageWorkbenchProviderId)
            .SelectMany(row => ParseStored(row.Meta, row.Content, row.CreatedAt, row.ConversationTitle, false))
            .Take(GalleryLimit + 1)
            .ToList();

        foreach (var result in stored.Take(GalleryLimit).OrderByDescending(result => result.CreatedAt))
            _gallery.Add(result);

        PART_GalleryLimitNote.IsVisible = stored.Count > GalleryLimit;
        PART_GalleryLimitNote.Text = $"只显示最近 {GalleryLimit} 张";

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

                // Decoding from the file also doubles as the existence check the
                // eager Load() used to perform.
                if (DecodeStoredThumbnail(localName) is not { } thumbnail) continue;
                var mime = ReadString(attachment, "mime") ?? "image/png";
                var fileName = ReadString(attachment, "filename")
                    ?? localName
                    ?? $"generated-{createdAt}{ExtensionForMime(mime)}";
                yield return ImageWorkbenchResult.Completed(
                    fileName, mime, thumbnail, ByteSourceFor(localName, null), localName,
                    revised, prompt, taskTitle, isEdit, modelLabel, modelId, providerId, created);
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

        // The card is drawn from a thumbnail, so a card on screen is no longer
        // proof the original is still on disk.
        if (result.LoadBytes() is not { Length: > 0 } bytes)
        {
            PART_Status.Text = "原图已不在本地存储中，无法保存。";
            return;
        }

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
            await stream.WriteAsync(bytes);
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
        if (result.LoadBytes() is not { Length: > 0 } bytes)
        {
            PART_Status.Text = "原图已不在本地存储中，无法预览。";
            return;
        }

        _ = ImagePreviewWindow.ShowAsync(owner, bytes, result.FileName);
    }

    private string CurrentTaskTitle() => string.IsNullOrWhiteSpace(_conversationId)
        ? "图像工作台"
        : _conversationRepo.Get(_conversationId)?.Title ?? "图像工作台";

    public void NotifyHiddenWhileGenerating()
    {
        if (_cts is null || _hiddenNotificationShown) return;

        var pendingTitle = CurrentTaskTitle();
        _notifications?.Notify(new AppNotification
        {
            Key = "image-" + _conversationId,
            Kind = NotifyKind.Progress,
            Title = "图像正在后台生成",
            Body = string.IsNullOrWhiteSpace(pendingTitle) ? "完成后会通知你" : $"「{pendingTitle}」完成后会通知你",
            ConversationId = _conversationId
        });
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

    // ---- thumbnails and byte sources ---------------------------------------

    /// <summary>
    /// Cards render at most ~600×460, so a full 1024²-or-larger decode is pure
    /// waste: 4 MB of surface per image against roughly 1.6 MB at this width.
    /// </summary>
    private const int ThumbnailWidth = 640;

    /// <summary>Decodes straight off disk, so the full-size bytes never have to
    /// exist in memory just to draw a preview.</summary>
    private Bitmap? DecodeStoredThumbnail(string? localName)
    {
        if (!_attachmentStore.TryGetPath(localName, out var path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, ThumbnailWidth);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? DecodeThumbnail(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, ThumbnailWidth);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prefers the store, so nothing is retained but the file name. Only when a
    /// save failed does the closure keep the bytes alive.
    /// </summary>
    private Func<byte[]> ByteSourceFor(string? localName, byte[]? retainedFallback)
    {
        if (localName is { Length: > 0 } name)
            return () => _attachmentStore.Load(name) ?? [];

        var retained = retainedFallback ?? [];
        return () => retained;
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MB"
        : $"{bytes / 1024d:0.#} KB";

    private static string ReadableSize(string? size) => string.IsNullOrWhiteSpace(size)
        ? "1024×1024"
        : size.Trim().Replace("x", "×", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One card in the workbench. The full-resolution bytes are deliberately *not*
/// a field: the gallery holds every image ever generated, and keeping a 1–3 MB
/// PNG plus a full-size decoded surface per entry made a few hundred pictures
/// cost hundreds of megabytes. The bytes live on disk in the attachment store
/// and are read back only when something actually needs them.
/// </summary>
public sealed record ImageWorkbenchResult(
    string FileName,
    string MimeType,
    Func<byte[]>? BytesSource,
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
    /// <summary>Full-resolution bytes, read on demand. Only click handlers may
    /// call this — never a binding, or scrolling the gallery would page every
    /// image back into memory.</summary>
    public byte[] LoadBytes() => BytesSource?.Invoke() ?? [];

    // A decoded thumbnail is proof the bytes were readable; asking LoadBytes()
    // here would put a disk read behind a property the templates bind to.
    public bool HasImage => !IsPending && !IsError && Thumbnail is not null;
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
        new(string.Empty, "image/png", null, null, null, null, prompt, title, DateTimeOffset.Now,
            edit, IsPending: true, ModelLabel: modelLabel);

    public static ImageWorkbenchResult Error(
        string prompt, string title, bool edit, string message, string? modelLabel,
        string? modelId, string? providerId, DateTimeOffset? createdAt = null) =>
        new(string.Empty, "image/png", null, null, null, null, prompt, title, createdAt ?? DateTimeOffset.Now,
            edit, IsError: true, ErrorMessage: message, ModelLabel: modelLabel,
            ModelId: modelId, ProviderId: providerId);

    public static ImageWorkbenchResult Completed(
        string fileName, string mimeType, Bitmap? thumbnail, Func<byte[]> bytesSource,
        string? localName, string? revisedPrompt, string prompt, string title, bool edit,
        string? modelLabel, string? modelId, string? providerId, DateTimeOffset? createdAt = null) =>
        new(fileName, mimeType, bytesSource, localName, revisedPrompt, thumbnail, prompt, title,
            createdAt ?? DateTimeOffset.Now, edit, ModelLabel: modelLabel, ModelId: modelId, ProviderId: providerId);
}
