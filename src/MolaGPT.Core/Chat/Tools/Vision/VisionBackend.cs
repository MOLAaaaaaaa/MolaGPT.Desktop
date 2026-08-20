using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Tools.Vision;

/// <summary>Outcome of one vision call. <see cref="ModelId"/> is carried back so
/// the tool result can name the model that actually answered — with BYOK the
/// vision backend is often a different provider from the one driving the chat,
/// and "which model said this" is the first thing to check when it says
/// something wrong.</summary>
internal sealed record VisionAnswer(string? Text, string? ModelId, string? Error)
{
    public static VisionAnswer Failed(string error) => new(null, null, error);
}

/// <summary>
/// The one place that turns a picture plus a question into text through the
/// user's configured vision model.
///
/// Both vision tools route through here — <see cref="VisionProxyTool"/> for
/// images the user attached (addressed by ordinal) and
/// <see cref="ImageAnalysisTool"/> for images sitting in the conversation's
/// working directory (addressed by name) — so they cannot drift apart on
/// provider resolution, thinking-parameter handling, or what a failure looks
/// like to the model.
/// </summary>
internal sealed class VisionBackend
{
    private readonly ProviderRegistry _registry;

    public VisionBackend(ProviderRegistry registry) => _registry = registry;

    public async Task<VisionAnswer> AskAsync(
        VisionProxyOptions options,
        Attachment image,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct)
    {
        var resolved = Resolve(options);
        if (resolved is null)
            return VisionAnswer.Failed("No usable vision backend is configured.");

        var (provider, model) = resolved.Value;
        var request = new ChatRequest(
            ModelId: model.Id,
            Messages:
            [
                new ChatMessage(ChatMessage.RoleSystem, systemPrompt),
                new ChatMessage(ChatMessage.RoleUser, userPrompt, Attachments: [image])
            ],
            UseThinking: false,
            ThinkingParamKind: ResolveThinkingKind(model));

        try
        {
            var text = await CollectAsync(provider, request, ct).ConfigureAwait(false);
            return new VisionAnswer(text, model.Id, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VisionAnswer.Failed(ex.Message);
        }
    }

    private (IChatProvider Provider, ProviderModel Model)? Resolve(VisionProxyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderId) || string.IsNullOrWhiteSpace(options.ModelId))
            return null;

        var resolved = _registry.FindModel(options.ProviderId!, options.ModelId!);
        return resolved is null ? null : (resolved.Value.Provider, resolved.Value.Model);
    }

    private static ThinkingParamKind? ResolveThinkingKind(ProviderModel model)
    {
        var kind = model.ThinkingConfig?.Kind ?? ThinkingParamKindInference.InferFromModelId(model.Id);
        return kind == ThinkingParamKind.None ? null : kind;
    }

    private static async Task<string> CollectAsync(IChatProvider provider, ChatRequest request, CancellationToken ct)
    {
        var parts = new List<string>();
        await foreach (var chunk in provider.StreamChatAsync(request, ct).WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaText))
                parts.Add(chunk.DeltaText);
            if (chunk.FinishReason is not null)
                break;
        }
        return string.Concat(parts).Trim();
    }
}
