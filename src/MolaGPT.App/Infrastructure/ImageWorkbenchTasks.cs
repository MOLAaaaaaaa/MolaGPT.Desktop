using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using MolaGPT.App.Rendering;
using MolaGPT.App.Views;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Runtime;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;
using SkiaSharp;

namespace MolaGPT.App.Infrastructure;

/// <summary>Runs image requests and saves their results independently of the displayed workbench.</summary>
internal sealed class ImageWorkbenchTasks(
    ImageGenerationTool tool,
    AttachmentStore attachments,
    ConversationRepository conversations,
    MessageRepository messages,
    ConversationListViewModel sidebar,
    NotificationCenter notifications,
    IActivityTracker activity,
    Action<string>? log = null)
{
    private readonly Dictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ImageWorkbenchRun>> _unsaved = new(StringComparer.Ordinal);

    private sealed class ActiveRun(ImageWorkbenchRun pending)
    {
        public ImageWorkbenchRun Pending { get; } = pending;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool BackgroundAnnounced { get; set; }
    }

    public event Action<string, ImageWorkbenchRun>? RunChanged;

    public bool IsGenerating(string? id) => id is not null && _active.ContainsKey(id);

    public string CreateTask(string prompt, string? modelId) => sidebar.CreateImageWorkbenchConversation(BuildTaskTitle(prompt), modelId);

    public string TitleOf(string? id) => id is null ? "图像工作台" : conversations.Get(id)?.Title ?? "图像工作台";

    public Task? Start(string id, string prompt, ImageGenerationOptions options, int count,
        (byte[] Bytes, string MimeType)? source, string? modelLabel, string? providerId)
    {
        if (IsGenerating(id)) return null;
        var title = TitleOf(id);
        if (IsDefaultTaskTitle(title)) title = BuildTaskTitle(prompt);
        var run = new ActiveRun(ImageWorkbenchRun.Pending(prompt, title, source is not null, modelLabel, options.Model, providerId));
        _active.Add(id, run);
        sidebar.SetGenerating(id, true);
        RunChanged?.Invoke(id, run.Pending);
        return RunAsync(id, run, options, count, source);
    }

    public void Cancel(string? id)
    {
        if (id is not null && _active.TryGetValue(id, out var run))
            run.Cancellation.Cancel();
    }

    public void NotifyHidden(string? id)
    {
        if (id is null || !_active.TryGetValue(id, out var run) || run.BackgroundAnnounced) return;
        run.BackgroundAnnounced = true;
        notifications.Notify(new AppNotification
        {
            Key = "image-" + id,
            Kind = NotifyKind.Progress,
            Title = "图像正在后台生成",
            Body = $"「{run.Pending.TaskTitle}」· 完成后提醒",
            ConversationId = id
        });
    }

    private async Task RunAsync(string id, ActiveRun run, ImageGenerationOptions options, int count,
        (byte[] Bytes, string MimeType)? source)
    {
        using var lease = activity.Acquire("image-workbench");
        using var cts = run.Cancellation;
        var result = run.Pending with { IsPending = false };
        NotifyKind kind;
        string headline;
        string? detail = null;
        var answerCompleted = false;
        try
        {
            var images = source is { } edit
                ? await tool.EditAsync(options, result.Prompt, edit.Bytes, edit.MimeType, count, cts.Token)
                : await tool.GenerateAsync(options, result.Prompt, count, cts.Token);
            if (images.Count > 0)
                result = result with { Images = await Task.Run(() => StoreResults(id, result, images)) };

            var cancelled = cts.IsCancellationRequested;
            var empty = result.Images.Count == 0;
            kind = cancelled || empty ? NotifyKind.Warning : NotifyKind.Success;
            headline = cancelled ? "已取消" : empty ? "未返回图片" : "生成完成";
            detail = cancelled
                ? empty ? null : $"已保留返回的 {result.Images.Count} 张图片"
                : empty ? "换个描述再试" : result.Images.Count > 1 ? $"共 {result.Images.Count} 张" : null;
            if (cancelled || empty) result = result with { ErrorMessage = cancelled ? "已取消" : "未返回图片，换个描述再试" };
            answerCompleted = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            kind = NotifyKind.Warning;
            headline = "已取消";
            result = result with { ErrorMessage = "已取消" };
            answerCompleted = true;
        }
        catch (Exception ex)
        {
            kind = NotifyKind.Error;
            headline = "生成失败";
            detail = ex.Message;
            result = result with { ErrorMessage = ex.Message };
        }

        try
        {
            if (!TryPersist(id, result, out var saveError))
            {
                if (!_unsaved.TryGetValue(id, out var retained)) _unsaved[id] = retained = [];
                retained.Add(result);
                kind = NotifyKind.Error;
                headline = result.HasImages ? "图片保存失败" : "结果保存失败";
                detail = saveError;
                answerCompleted = false;
            }
        }
        finally
        {
            _active.Remove(id);
            sidebar.SetGenerating(id, false);
        }

        RunChanged?.Invoke(id, result);
        notifications.Notify(new AppNotification
        {
            Key = "image-" + id,
            Kind = kind,
            Title = $"「{result.TaskTitle}」{headline}",
            Body = detail,
            ConversationId = id,
            IsAnswerCompleted = answerCompleted
        });
    }

    public IReadOnlyList<ImageWorkbenchRun> ReadTaskRuns(string id)
    {
        // On re-entry retry local storage once. An already-billed request is never sent again.
        if (_unsaved.TryGetValue(id, out var retained))
        {
            for (var i = 0; i < retained.Count;)
            {
                var run = retained[i];
                var results = run.Images.Select(RetryAttachment).ToArray();
                run = run with { Images = results };
                if (TryPersist(id, run, out _)) retained.RemoveAt(i);
                else retained[i++] = run;
            }
            if (retained.Count == 0)
            {
                _unsaved.Remove(id);
                if (!IsGenerating(id)) notifications.Dismiss("image-" + id);
            }
        }

        var title = TitleOf(id);
        var runs = messages.List(id)
            .Select(row => ParseStored(row.Meta, row.Content, row.CreatedAt, title, id, true))
            .OfType<ImageWorkbenchRun>().ToList();
        if (_unsaved.TryGetValue(id, out retained)) runs.AddRange(retained);
        if (_active.TryGetValue(id, out var active)) runs.Add(active.Pending);
        return runs.OrderBy(run => run.CreatedAt).ToArray();
    }

    public IReadOnlyList<ImageWorkbenchResult> ReadGallery(int limit)
    {
        var stored = messages.ListImageWorkbenchMessages(ConversationListViewModel.ImageWorkbenchProviderId)
            .Select(row => ParseStored(row.Meta, row.Content, row.CreatedAt, row.ConversationTitle, row.ConversationId, false))
            .OfType<ImageWorkbenchRun>().SelectMany(run => run.Images).Take(limit);
        return stored.Concat(_unsaved.Values.SelectMany(runs => runs).SelectMany(run => run.Images))
            .OrderByDescending(image => image.CreatedAt).Take(limit).ToArray();
    }

    private List<ImageWorkbenchResult> StoreResults(string id, ImageWorkbenchRun run, IReadOnlyList<GeneratedImage> images)
    {
        var results = new List<ImageWorkbenchResult>(images.Count);
        var index = 0;
        foreach (var image in images)
        {
            var filename = $"generated-{DateTime.Now:yyyyMMdd-HHmmss}-{++index}{ExtensionForMime(image.MimeType)}";
            var item = new ImageWorkbenchResult(filename, image.MimeType, () => image.Bytes,
                null, image.RevisedPrompt, null, default, run.Prompt, run.CreatedAt, run.ModelLabel, id);
            results.Add(RetryAttachment(item));
        }
        return results;
    }

    private ImageWorkbenchResult RetryAttachment(ImageWorkbenchResult result)
    {
        if (result.LocalName is not null) return result;
        var bytes = result.LoadBytes();
        try
        {
            var name = attachments.Save(bytes, result.MimeType, result.FileName);
            if (name is not null && ReadStoredImageInfo(name) is { } stored)
                return result with { LocalName = name, ImagePath = stored.Path, ImageSize = stored.Size, BytesSource = ByteSourceFor(name) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("附件保存失败：" + ex.Message);
        }
        using var stream = new MemoryStream(bytes, writable: false);
        return result with { ImageSize = ReadImageSize(stream) };
    }

    private bool TryPersist(string id, ImageWorkbenchRun run, out string? error)
    {
        try
        {
            if (run.Images.Any(image => image.LocalName is null))
                throw new IOException("图像附件尚未写入，已保留本次结果。");
            Persist(id, run);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("图像任务记录保存失败：" + ex.Message);
            error = ex.Message;
            return false;
        }
    }


    private ImageWorkbenchRun? ParseStored(
        string? meta, string content, long createdAt, string taskTitle,
        string? conversationId, bool includeErrors)
    {
        if (string.IsNullOrWhiteSpace(meta)) return null;

        JsonDocument document;
        try { document = JsonDocument.Parse(meta); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("image_workbench", out var marker) || marker.ValueKind != JsonValueKind.True)
                return null;

            var prompt = ReadString(root, "prompt") ?? content;
            var modelLabel = ReadString(root, "model_label") ?? ReadString(root, "model") ?? ReadString(root, "model_id");
            var modelId = ReadString(root, "model_id");
            var providerId = ReadString(root, "provider_id");
            var isEdit = ReadBool(root, "image_edit");
            var isError = string.Equals(ReadString(root, "status"), "error", StringComparison.OrdinalIgnoreCase);
            var created = DateTimeOffset.FromUnixTimeMilliseconds(createdAt).ToLocalTime();

            if (isError)
            {
                return includeErrors
                    ? new ImageWorkbenchRun(prompt, taskTitle, created, isEdit, modelLabel, modelId, providerId,
                        [], ErrorMessage: ReadString(root, "error_message") ?? content)
                    : null;
            }

            if (!root.TryGetProperty("attachments", out var attachments)
                || attachments.ValueKind != JsonValueKind.Array) return null;

            // Rows written before the batch existed carry one revised prompt for
            // the whole run; newer ones carry one per picture.
            var sharedRevised = ReadString(root, "revised_prompt");
            var images = new List<ImageWorkbenchResult>();
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (attachment.ValueKind != JsonValueKind.Object) continue;
                var localName = ReadString(attachment, "localName");

                if (ReadStoredImageInfo(localName) is not { } stored) continue;
                var mime = ReadString(attachment, "mime") ?? "image/png";
                var fileName = ReadString(attachment, "filename")
                    ?? localName
                    ?? $"generated-{createdAt}{ExtensionForMime(mime)}";
                images.Add(new ImageWorkbenchResult(
                    fileName, mime, ByteSourceFor(localName!), localName,
                    ReadString(attachment, "revised_prompt") ?? sharedRevised,
                    stored.Path, stored.Size, prompt, created, modelLabel, conversationId));
            }

            return images.Count == 0
                ? null
                : new ImageWorkbenchRun(prompt, taskTitle, created, isEdit, modelLabel, modelId, providerId, images, ErrorMessage: ReadString(root, "error_message"));
        }
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

    internal static string ExtensionForMime(string? mimeType) => mimeType?.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png"
    };

    private void Persist(string id, ImageWorkbenchRun run)
    {
        if (!run.IsError && !run.HasImages) return;
        var now = run.CreatedAt.ToUnixTimeMilliseconds();
        var meta = new JsonObject
        {
            ["image_workbench"] = true,
            ["prompt"] = run.Prompt,
            ["image_edit"] = run.IsEditMode,
            ["model_label"] = run.ModelLabel,
            ["model"] = run.ModelLabel,
            ["model_id"] = run.ModelId,
            ["provider_id"] = run.ProviderId,
            ["status"] = run.HasImages ? "completed" : "error"
        };
        if (run.IsError) meta["error_message"] = run.ErrorDisplay;

        string content;
        if (run.HasImages)
        {
            meta["revised_prompt"] = run.Images[0].RevisedPrompt;
            meta["attachments"] = new JsonArray(run.Images.Select(image => (JsonNode?)new JsonObject
            {
                ["filename"] = image.FileName,
                ["label"] = "图片",
                ["localName"] = image.LocalName,
                ["mime"] = image.MimeType,
                ["revised_prompt"] = image.RevisedPrompt
            }).ToArray());
            content = run.Images[0].RevisedPrompt is { Length: > 0 } revised ? revised : "图像生成完成";
        }
        else
            content = (run.IsEditMode ? "图像编辑失败：" : "图像生成失败：") + run.ErrorDisplay;

        messages.InsertImageWorkbenchRun(
            new MessageRow(Guid.NewGuid().ToString("N"), id, "assistant", content, meta.ToJsonString(), now),
            BuildTaskTitle(run.Prompt), run.ModelId);
    }

    private (string Path, PixelSize Size)? ReadStoredImageInfo(string? localName)
    {
        if (!attachments.TryGetPath(localName, out var path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var size = ReadImageSize(stream);
            return size.Width > 0 ? (path, size) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PixelSize ReadImageSize(Stream stream)
    {
        if (stream.Length > ImageSourceLoader.MaxEncodedBytes) return default;
        using var codec = SKCodec.Create(stream);
        return codec is null || !ImageSourceLoader.ValidSize(codec.Info.Width, codec.Info.Height)
            ? default : new PixelSize(codec.Info.Width, codec.Info.Height);
    }

    private Func<byte[]> ByteSourceFor(string name) =>
        () => attachments.Load(name, ImageSourceLoader.MaxEncodedBytes) ?? [];
}
