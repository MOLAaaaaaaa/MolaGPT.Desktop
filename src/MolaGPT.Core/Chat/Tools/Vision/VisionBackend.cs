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
    private readonly Func<HttpClient> _httpFactory;

    public VisionBackend(ProviderRegistry registry, Func<HttpClient> httpFactory)
    {
        _registry = registry;
        _httpFactory = httpFactory;
    }

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

        // Deliberately a one-shot HTTP call rather than the provider's streaming
        // path. This runs *inside* a tool callback of a turn that is already in
        // flight, so going back through the agent runtime would queue behind the
        // very turn waiting on this answer — and, for the agent providers, costs a
        // sidecar spawn to look at one picture.
        if (provider is not IOneShotTarget describable
            || describable.DescribeOneShot(model.Id) is not { } target)
        {
            return VisionAnswer.Failed(
                $"Vision provider '{provider.DisplayName}' cannot serve a direct request.");
        }

        try
        {
            var client = new OneShotCompletionClient(_httpFactory());
            var text = await client.CompleteAsync(
                target,
                model.Id,
                [
                    new ChatMessage(ChatMessage.RoleSystem, systemPrompt),
                    new ChatMessage(ChatMessage.RoleUser, userPrompt, Attachments: [image])
                ],
                // Describing a picture in 300 characters is not worth a reasoning
                // pass, and the caller shows this answer inside a tool card where a
                // thinking preamble has nowhere to go.
                useThinking: false,
                thinkingKind: ResolveThinkingKind(model),
                ct: ct).ConfigureAwait(false);
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
}
