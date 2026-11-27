using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MolaGPT.App.Infrastructure;
using MolaGPT.App.Rendering;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;
using SkiaSharp;

namespace MolaGPT.App.Views;

public partial class ImageGenerationWorkbenchView : UserControl
{
    /// <summary>How many recent gallery entries are shown.</summary>
    private const int GalleryLimit = 200;

    private readonly SettingsViewModel _settings;
    private readonly ImageWorkbenchTasks _tasks;
    private readonly NotificationCenter _notifications;
    private readonly ObservableCollection<ImageWorkbenchRun> _runs = new();
    private readonly ObservableCollection<ImageWorkbenchResult> _gallery = new();
    private string? _conversationId;
    private bool _presented;
    private DateTimeOffset? _resultsClearedAt;
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

    /// <summary>
    /// What a run does with the images already in the task. The base image is
    /// *not* one of these — it is an input either mode can be given, which is
    /// why it lives in its own fields rather than as a third mode.
    /// </summary>
    private enum WorkbenchMode
    {
        /// <summary>Spread. One prompt, up to four pictures side by side, and
        /// each run starts from the same place: the base image if there is one,
        /// otherwise nothing. Output never feeds back.</summary>
        Single,

        /// <summary>Chain. One picture at a time, each editing the newest image
        /// in the task — the last result, or the base image before anything has
        /// been generated. A batch has no single newest picture to continue
        /// from, which is why the count is pinned to one here.</summary>
        Conversation
    }

    internal ImageGenerationWorkbenchView(
        SettingsViewModel settings,
        ImageWorkbenchTasks tasks,
        string? conversationId,
        NotificationCenter notifications)
    {
        _settings = settings;
        _tasks = tasks;
        _conversationId = conversationId;
        _notifications = notifications;

        InitializeComponent();
        PART_Results.ItemsSource = _runs;
        PART_Gallery.ItemsSource = _gallery;
        PART_CurrentTab.Click += (_, _) => ShowCurrent();
        PART_GalleryTab.Click += (_, _) => ShowGallery();
        PART_ClearResults.Click += (_, _) => ClearResults();
        PART_NewTask.Click += (_, _) => NewTask();
        PART_Close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        PART_OpenSettings.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        PART_Generate.Click += OnGenerate;
        PART_Stop.Click += (_, _) => _tasks.Cancel(_conversationId);
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

        _runs.CollectionChanged += (_, _) =>
        {
            UpdateEmptyState();
            UpdateSourceUi();
        };
        _gallery.CollectionChanged += (_, _) => UpdateEmptyState();
        DetachedFromVisualTree += (_, _) =>
        {
            SetPresented(false);
            DisposeBaseThumbnail();
        };

        InitializeUi();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? OpenSettingsRequested;

    /// <summary>A gallery cell asking to open the task it came from. Carries the
    /// conversation id, not the result: the host routes it through the sidebar
    /// so the list highlights the same task the workbench switches to.</summary>
    public event EventHandler<string>? OpenTaskRequested;
    public bool IsGenerating => _tasks.IsGenerating(_conversationId);
    public string? ConversationId => _conversationId;

    private bool SupportsEdit => _settings.SelectedWorkbenchImageGenerationModel?.SupportsEdit == true;

    /// <summary>
    /// The line above the prompt box. It collapses when empty, which is the
    /// normal case: this used to be a permanent paragraph in the 生成参数 rail
    /// that spent most of its height restating what the header's model chip
    /// already says.
    /// </summary>
    private string StatusText
    {
        set
        {
            PART_Status.Text = value;
            PART_Status.IsVisible = !string.IsNullOrWhiteSpace(value);
        }
    }

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

        ShowCurrent();
        UpdateOptionChips();
        UpdateStatus();
        UpdateGenerateButton();
        UpdateEmptyState();
    }

    private void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (IsGenerating || !_settings.IsWorkbenchImageGenerationConfigured) return;
        var prompt = PART_Prompt.Text?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            StatusText = "请先输入描述";
            PART_Prompt.Focus();
            return;
        }

        var selected = _settings.SelectedWorkbenchImageGenerationModel;
        var options = _settings.BuildWorkbenchImageGenerationOptions() with
        {
            Size = SelectedSize(),
            Style = string.IsNullOrWhiteSpace(PART_Style.Text) ? null : PART_Style.Text.Trim(),
            AsTool = false
        };
        var source = ResolveEditSource();
        try
        {
            _conversationId ??= _tasks.CreateTask(prompt, options.Model);
            StatusText = source is null && HasEditSource ? "底图已失效，本次直接生成" : string.Empty;
            _ = _tasks.Start(_conversationId, prompt, options, EffectiveCount, source,
                selected?.Label ?? options.Model, selected?.ProviderId ?? _settings.WorkbenchImageGenerationProviderId);
        }
        catch (Exception ex)
        {
            _notifications.Error("无法开始图像任务", ex.Message);
        }
    }

    internal void SetPresented(bool presented)
    {
        if (_presented == presented) return;
        _presented = presented;
        if (presented)
        {
            _tasks.RunChanged += OnRunChanged;
            _settings.PropertyChanged += OnSettingsChanged;
            var pending = _runs.LastOrDefault(run => run.IsPending);
            LoadStoredImages();
            if (pending is not null && _runs.LastOrDefault() is { IsPending: false } finished)
                ApplyFinishedRun(finished);
            UpdateStatus();
            RefreshGenerating();
        }
        else
        {
            _tasks.RunChanged -= OnRunChanged;
            _settings.PropertyChanged -= OnSettingsChanged;
            _tasks.NotifyHidden(_conversationId);
        }
    }

    private void OnRunChanged(string id, ImageWorkbenchRun run)
    {
        if (id == _conversationId)
        {
            if (run.IsPending)
            {
                _runs.Add(run);
                PART_Prompt.Clear();
                ShowCurrent();
            }
            else
            {
                var pending = _runs.FirstOrDefault(item => item.IsPending);
                if (pending is null) _runs.Add(run);
                else _runs[_runs.IndexOf(pending)] = run;
                ApplyFinishedRun(run);
            }
            RefreshGenerating();
            ScrollResultsToEnd();
        }

        if (!run.HasImages) return;
        foreach (var image in run.Images) _gallery.Insert(0, image);
        if (_gallery.Count <= GalleryLimit) return;
        PART_GalleryLimitNote.IsVisible = true;
        while (_gallery.Count > GalleryLimit) _gallery.RemoveAt(_gallery.Count - 1);
    }

    private void ApplyFinishedRun(ImageWorkbenchRun run)
    {
        if (run.HasImages) _baseIsChainHead = false;
        else if (run.IsError && string.IsNullOrWhiteSpace(PART_Prompt.Text))
        {
            PART_Prompt.Text = run.Prompt;
            PART_Prompt.CaretIndex = run.Prompt.Length;
        }
    }

    private void RefreshGenerating()
    {
        PART_Generate.IsVisible = !IsGenerating;
        PART_Stop.IsVisible = IsGenerating;
        UpdateGenerateButton();
        UpdateEmptyState();
    }

    private void UpdateGenerateButton() =>
        PART_Generate.IsEnabled = !IsGenerating && _settings.IsWorkbenchImageGenerationConfigured
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

        // Only problems get a line. 「已就绪」 is a state, not an event: the
        // header's model chip carries it, and whether the mode strip is there
        // at all says whether the model can edit.
        StatusText = _settings.GetWorkbenchImageGenerationProvider() is null
            ? "尚未配置图像服务"
            : _settings.IsWorkbenchImageGenerationConfigured
                ? string.Empty
                : "图像服务配置不完整";
    }

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

        // Hidden rather than pinned to 1 and greyed out: a chain has nowhere to
        // put a second picture, and the chip's label carries its own value, so
        // folding it away in 对话模式 hides no state.
        PART_CountChip.IsVisible = BatchAvailable;

        var hasBase = _baseBytes is { Length: > 0 };
        var chained = _mode == WorkbenchMode.Conversation && !_baseIsChainHead && HasResultImage;

        PART_SourceChip.IsVisible = SupportsEdit && hasBase;
        PART_SourceThumb.Source = _baseThumbnail;
        PART_SourceName.Text = _baseName ?? string.Empty;
        PART_SourceHint.Text = !hasBase
            ? string.Empty
            // Says what the next run starts from, which is the only question the
            // chip answers — and in the chained case that is honestly not this.
            : $"起点：{(chained ? "最新结果" : "这张")} · {FormatSize(_baseBytes!.Length)}";

        // Drag-and-drop and paste have no button of their own; the placeholder
        // is the only place they are discoverable.
        PART_Prompt.PlaceholderText = SupportsEdit
            ? "描述你想要的画面，或拖入图片作为底图…"
            : "描述你想要的画面…";

        // Reads the same preference OnPromptKeyDown does, so the hint cannot
        // describe a key that no longer sends.
        PART_Hint.Text = _settings.EnterToSend
            ? "Enter 发送 · Shift+Enter 换行"
            : "Enter 换行 · Ctrl+Enter 发送";

        // What one press of 生成 will actually do, in the same words the mode and
        // the source chip use. The count is appended rather than replacing the
        // verb: 「编辑底图」 and 「4 张」 are both true of the same run.
        var action = chained
            ? "续改上一张"
            : SupportsEdit && hasBase
                ? "编辑底图"
                : "生成新图";
        PART_ModeLabel.Text = EffectiveCount > 1
            ? $"本次：{action} · {EffectiveCount} 张"
            : $"本次：{action}";
    }

    /// <summary>Whether the task holds anything a chain could continue from.</summary>
    private bool HasResultImage => _runs.Any(run => run.HasImages);

    /// <summary>
    /// Whether a batch means anything right now. 对话模式 is always one picture:
    /// a chain has no single newest one to continue from otherwise, and picking
    /// one out of a batch is what the 底图 button on each picture is for.
    ///
    /// The chip's visibility and the count read this same property on purpose —
    /// two copies of the condition would eventually disagree, and the way they
    /// would disagree is a hidden chip that still bills for four pictures.
    /// </summary>
    private bool BatchAvailable => !SupportsEdit || _mode == WorkbenchMode.Single;

    private int EffectiveCount => BatchAvailable
        ? Math.Clamp(_settings.WorkbenchImageGenerationCount, 1, ImageGenerationTool.MaxBatchSize)
        : 1;

    /// <summary>
    /// Whether this run would be an edit — without touching the bytes. The UI
    /// asks this on every refresh, and <see cref="ResolveEditSource"/> reads the
    /// image back off disk.
    /// </summary>
    private bool HasEditSource => SupportsEdit
        && ((_mode == WorkbenchMode.Conversation && !_baseIsChainHead && HasResultImage)
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

        // The newest picture in the task, which for a batch means the last of
        // it. Continuing from a different one of the four is what the 底图
        // button on each picture is for.
        if (_mode == WorkbenchMode.Conversation && !_baseIsChainHead
            && _runs.LastOrDefault(run => run.HasImages)?.Images[^1] is { } latest
            && latest.LoadBytes() is { Length: > 0 } previous)
        {
            return (previous, latest.MimeType);
        }

        return _baseBytes is { Length: > 0 } bytes ? (bytes, _baseMime ?? "image/png") : null;
    }

    // No status line: the mode chip lights up, the 本次 label rewrites itself and
    // the 张数 chip appears or leaves. Three places already say it, and a banner
    // for a state the user just set is a banner nobody reads.
    private void SetMode(WorkbenchMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        UpdateSourceUi();
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
            return ApplyBaseImage(await BoundedRead.ReadAsync(stream, ImageSourceLoader.MaxEncodedBytes, default), files[0].Name);
        }
        catch (Exception ex)
        {
            StatusText = "无法读取图片 · " + ex.Message;
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
            StatusText = "当前模型不支持图像编辑";
            return false;
        }

        var intake = AttachmentIntake.FromBytes(bytes, fileName, new AttachmentIntakeCapabilities(true, false));
        if (intake.Attachment is not { Kind: AttachmentKind.Image } image)
        {
            StatusText = intake.Error ?? "请选择图片文件（PNG / JPEG / WebP）";
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

        // The chip that just appeared shows the thumbnail, the name and what the
        // next run starts from. A line repeating it would be the fourth.
        StatusText = string.Empty;
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
        StatusText = string.Empty;
    }

    private void OnUseAsSource(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageWorkbenchResult { HasImage: true } result }) return;
        if (result.LoadBytes() is not { Length: > 0 } bytes)
        {
            StatusText = "原图已丢失";
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

    private async void OnSourceDrop(object? sender, DragEventArgs e)
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
            StatusText = "请拖入单张图片文件";
            return;
        }

        try
        {
            ApplyBaseImage(await ImageSourceLoader.ReadFileAsync(path), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusText = "无法读取图片 · " + ex.Message;
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
                ApplyBaseImage(await ImageSourceLoader.ReadFileAsync(path), Path.GetFileName(path));
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
    }

    private void OnRatio(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string size }) return;
        _size = size;
        _settings.WorkbenchImageGenerationSize = size;
        UpdateOptionChips();
    }

    private void OnCount(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var count)) return;
        _settings.WorkbenchImageGenerationCount = count;
        UpdateOptionChips();

        // 本次 label lives with the source UI and reads EffectiveCount.
        UpdateSourceUi();
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

    /// <summary>
    /// Keeps the two flyouts and the chips that open them in step. The chip
    /// labels are what makes folding these settings away free: the current
    /// value stays on the composer, so nothing is hidden behind a click.
    /// </summary>
    private void UpdateOptionChips()
    {
        // A size stored before the chip set changed may match nothing; leaving
        // every chip dark is honest, and the caption still shows the value.
        Button? activeRatio = null;
        foreach (var button in PART_RatioChips.Children.OfType<Button>())
        {
            var active = string.Equals(button.Tag?.ToString(), _size, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("active", active);
            if (active) activeRatio = button;
        }

        var style = PART_Style.Text?.Trim() ?? string.Empty;
        Button? activeStyle = null;
        foreach (var button in PART_StyleChips.Children.OfType<Button>())
        {
            var active = string.Equals(button.Tag?.ToString() ?? string.Empty, style, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("active", active);
            if (active) activeStyle = button;
        }

        var count = Math.Clamp(_settings.WorkbenchImageGenerationCount, 1, ImageGenerationTool.MaxBatchSize);
        foreach (var button in PART_CountChips.Children.OfType<Button>())
            button.Classes.Set("active", string.Equals(button.Tag?.ToString(), count.ToString(), StringComparison.Ordinal));

        PART_SizeCaption.Text = string.Equals(SelectedSize(), "auto", StringComparison.OrdinalIgnoreCase)
            ? "由模型决定"
            : ReadableSize(SelectedSize());

        // Falls back to the raw value so a size or style that matches no chip
        // still shows on the composer rather than reading as unset.
        PART_SizeChipLabel.Text = activeRatio?.Content?.ToString() ?? ReadableSize(SelectedSize());
        PART_StyleChipLabel.Text = style.Length == 0
            ? "风格"
            : activeStyle?.Content?.ToString() ?? style;
        PART_CountChipLabel.Text = $"{count} 张";
    }

    private string SelectedSize() => string.IsNullOrWhiteSpace(_size) ? "1024x1024" : _size;

    private void NewTask()
    {
        if (IsGenerating) return;
        _conversationId = null;
        _resultsClearedAt = null;
        _runs.Clear();
        PART_Prompt.Clear();
        ClearBaseImage();
        ShowCurrent();

        // The empty state is now the whole pane. Announcing it too would be a
        // banner describing the screen behind it.
        StatusText = string.Empty;
    }

    private void ClearResults()
    {
        if (IsGenerating) return;
        _resultsClearedAt = DateTimeOffset.Now;
        _runs.Clear();
        StatusText = "已收起，作品仍在画廊";
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
        PART_EmptyResults.IsVisible = _runs.Count == 0;
        // The scroller and the starters share a cell; an empty scroller left
        // visible would sit on top of the chips and swallow their clicks.
        PART_ResultsScroll.IsVisible = _runs.Count > 0;
        PART_EmptyGallery.IsVisible = _gallery.Count == 0;
        // Pictures, not runs: 「当前任务 4」 next to 「画廊 16」 has to count the
        // same thing in both places.
        PART_CurrentCount.Text = _runs.Sum(run => run.Images.Count).ToString();
        PART_GalleryCount.Text = _gallery.Count.ToString();
        PART_ClearResults.IsVisible = _runs.Count > 0 && !IsGenerating;
    }

    private void ScrollResultsToEnd() =>
        Dispatcher.UIThread.Post(() => PART_ResultsScroll.ScrollToEnd(), DispatcherPriority.Loaded);

    private void LoadStoredImages()
    {
        _runs.Clear();
        _gallery.Clear();
        if (_conversationId is { } id)
            foreach (var run in _tasks.ReadTaskRuns(id))
                if (_resultsClearedAt is null || run.CreatedAt > _resultsClearedAt)
                    _runs.Add(run);

        var images = _tasks.ReadGallery(GalleryLimit + 1);
        foreach (var image in images.Take(GalleryLimit))
            _gallery.Add(image);

        PART_GalleryLimitNote.IsVisible = images.Count > GalleryLimit;
        PART_GalleryLimitNote.Text = $"只显示最近 {GalleryLimit} 张";
    }

    private async void OnSaveResult(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageWorkbenchResult { HasImage: true } result }) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        // The card is drawn from a thumbnail, so a card on screen is no longer
        // proof the original is still on disk.
        if (result.LoadBytes() is not { Length: > 0 } bytes)
        {
            StatusText = "原图已丢失";
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存图片",
            SuggestedFileName = result.FileName,
            DefaultExtension = ImageWorkbenchTasks.ExtensionForMime(result.MimeType).TrimStart('.')
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            StatusText = "已保存到 " + (file.TryGetLocalPath() ?? file.Name);
        }
        catch (Exception ex)
        {
            StatusText = "保存失败 · " + ex.Message;
        }
    }

    /// <summary>
    /// Opens the task a gallery picture came from, which is where the untrimmed
    /// prompt, the revised prompt and the rest of that task's runs already live.
    /// Already looking at it? Then just switch panes — re-opening would rebuild
    /// the view and lose the scroll position for no gain.
    /// </summary>
    private void OnOpenTask(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageWorkbenchResult result }) return;
        if (result.ConversationId is not { Length: > 0 } id)
        {
            StatusText = "这张图没有关联的任务";
            return;
        }

        if (string.Equals(id, _conversationId, StringComparison.Ordinal))
        {
            ShowCurrent();
            return;
        }

        OpenTaskRequested?.Invoke(this, id);
    }

    private void OnPreviewResult(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageWorkbenchResult { HasImage: true } result }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // The full bytes, not the list's thumbnail: this is the only place the
        // generated image is shown at the size it was actually produced at.
        if (result.LoadBytes() is not { Length: > 0 } bytes)
        {
            StatusText = "原图已丢失";
            return;
        }

        _ = ImagePreviewWindow.ShowAsync(owner, bytes, result.FileName);
    }

    // Cards and the gallery decode only what their viewport needs.
    private const int ThumbnailWidth = 640;

    private static Bitmap? DecodeThumbnail(byte[] bytes)
    {
        try
        {
            return ImageSourceLoader.Decode(bytes, ThumbnailWidth);
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
    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MB"
        : $"{bytes / 1024d:0.#} KB";

    private static string ReadableSize(string? size) => string.IsNullOrWhiteSpace(size)
        ? "1024×1024"
        : size.Trim().Replace("x", "×", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One run: what was asked for, and whatever came back. The unit used to be the
/// picture, which was fine while a run produced exactly one — 生成模式 now asks
/// for up to four at a time, and four pictures under four copies of the same
/// prompt is not a batch, it is four runs that happen to rhyme.
///
/// Pending and failed states live here rather than on the picture, because they
/// are properties of the request. A run can also be partly both: three pictures
/// and one timeout keeps the three and still says what went wrong.
/// </summary>
public sealed record ImageWorkbenchRun(
    string Prompt,
    string TaskTitle,
    DateTimeOffset CreatedAt,
    bool IsEditMode,
    string? ModelLabel,
    string? ModelId,
    string? ProviderId,
    IReadOnlyList<ImageWorkbenchResult> Images,
    bool IsPending = false,
    string? ErrorMessage = null)
{
    public bool IsError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasImages => Images.Count > 0;

    /// <summary>Whether the pictures need a grid. One picture is left at its own
    /// size; more than one shares the reading column.</summary>
    public bool IsGrid => Images.Count > 1;
    public ImageWorkbenchResult? SingleImage => Images.Count == 1 ? Images[0] : null;
    public bool HasSingleImage => Images.Count == 1;

    /// <summary>Replaces the card's row of badges: one muted line, the shape the
    /// transcript uses for everything that is about a message rather than in it.</summary>
    public string MetaLine
    {
        get
        {
            var kind = IsEditMode ? "编辑" : "生成";
            var time = CreatedAt.ToString("MM-dd HH:mm");
            return string.IsNullOrWhiteSpace(ModelLabel) ? $"{kind} · {time}" : $"{kind} · {ModelLabel} · {time}";
        }
    }

    public string PendingText => IsEditMode ? "编辑中" : "生成中";
    public string ErrorDisplay => string.IsNullOrWhiteSpace(ErrorMessage) ? "本次未完成" : ErrorMessage!;

    public static ImageWorkbenchRun Pending(
        string prompt, string title, bool edit, string? modelLabel, string? modelId, string? providerId) =>
        new(prompt, title, DateTimeOffset.Now, edit, modelLabel, modelId, providerId, [], IsPending: true);
}

/// <summary>
/// One stored picture; dimensions reserve its layout before the visible
/// thumbnail is decoded. Neither encoded bytes nor bitmaps are retained here.
/// </summary>
public sealed record ImageWorkbenchResult(
    string FileName,
    string MimeType,
    Func<byte[]>? BytesSource,
    string? LocalName,
    string? RevisedPrompt,
    string? ImagePath,
    PixelSize ImageSize,
    string Prompt,
    DateTimeOffset CreatedAt,
    string? ModelLabel = null,
    /// <summary>Which task produced this. Only the gallery needs it — it is the
    /// one view that spans tasks, so it is the only one that can be asked to
    /// jump to the run a picture came from.</summary>
    string? ConversationId = null)
{
    /// <summary>Full-resolution bytes, read on demand. Only click handlers may
    /// call this — never a binding, or scrolling the gallery would page every
    /// image back into memory.</summary>
    public byte[] LoadBytes() => BytesSource?.Invoke() ?? [];
    public Func<byte[]>? FallbackBytes => ImagePath is null ? BytesSource : null;

    public bool HasImage => ImageSize.Width > 0 && ImageSize.Height > 0;
    public bool HasRevisedPrompt => !string.IsNullOrWhiteSpace(RevisedPrompt);
    public string CreatedAtText => CreatedAt.ToString("MM-dd HH:mm");

    /// <summary>One caption line for a gallery cell, where the run's meta line
    /// would cost more height than the picture under it.</summary>
    public string GalleryMeta => string.IsNullOrWhiteSpace(ModelLabel)
        ? CreatedAtText
        : $"{CreatedAtText} · {ModelLabel}";

    public string RevisedPromptDisplay => "改写：" + RevisedPrompt;
}
