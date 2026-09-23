using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// The one place that turns a request's thinking settings into wire parameters.
///
/// Providers disagree about how to ask for reasoning — DeepSeek wants
/// <c>thinking:{type}</c>, Qwen wants <c>enable_thinking</c> plus a budget, the
/// rest take <c>reasoning_effort</c> — so this is a dialect table, not a single
/// key. It lives here because both the direct provider and the Pi path need the
/// identical mapping: a second copy would drift, and the symptom would be a model
/// quietly not reasoning rather than an error.
///
/// Switching reasoning <em>off</em> needs saying out loud. Omitting the parameter
/// is not "off" on a hybrid model: most providers read the silence as "use the
/// server default", and the default is usually on. The mirror-image mistake is
/// just as real — <c>effort: "none"</c> is a value plenty of models reject — so
/// the off expression is picked from what the provider published about the model
/// (<see cref="ThinkingConfig.EffortLevels"/>, <see cref="ThinkingConfig.Mandatory"/>)
/// rather than sent blind.
/// </summary>
internal static class ThinkingParams
{
    /// <summary>
    /// Wire shapes whose thinking parameters this table owns.
    ///
    /// Anthropic and Google are left to the agent runtime: it already expresses both
    /// correctly — a disabled thinking block, and Gemini's per-generation
    /// <c>thinkingLevel</c>/<c>thinkingBudget</c>, which a merged body could not reach
    /// anyway because it only lands top-level keys.
    /// </summary>
    public static bool Owns(string? api) => api is "openai-completions" or "openai-responses";

    public static void Apply(
        IDictionary<string, object?> body,
        ChatRequest request,
        string? endpoint = null,
        ThinkingConfig? config = null,
        string api = "openai-completions")
    {
        var responses = api == "openai-responses";

        if (request.UseThinking == true)
        {
            // Switching reasoning *on* over the Responses api is left alone: the runtime
            // already writes the whole object — effort, summary, and the encrypted
            // reasoning include — and this table can only replace it wholesale, which
            // would drop the summary the thinking panel is rendered from.
            if (responses) return;

            if (request.ThinkingParamKind == ThinkingParamKind.DeepSeekV4)
            {
                body["thinking"] = new { type = "enabled" };
                body["reasoning_effort"] = request.ReasoningEffort ?? "high";
            }
            else if (request.ThinkingParamKind == ThinkingParamKind.QwenThinkingBudget)
            {
                body["enable_thinking"] = true;
                if (request.ThinkingBudgetTokens is { } budget)
                    body["thinking_budget"] = budget;
            }
            else if (request.ThinkingParamKind == ThinkingParamKind.GeminiBudget)
            {
                body["reasoning_effort"] = request.ReasoningEffort ?? "medium";
            }
            else if (request.ThinkingParamKind == ThinkingParamKind.GeminiThinkingLevel)
            {
                body["reasoning_effort"] = request.ReasoningEffort ?? "high";
            }
            else if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            {
                body["reasoning_effort"] = request.ReasoningEffort;
            }
        }
        else if (request.UseThinking == false)
        {
            ApplyOff(body, request, endpoint, config, responses);
        }
    }

    private static void ApplyOff(
        IDictionary<string, object?> body,
        ChatRequest request,
        string? endpoint,
        ThinkingConfig? config,
        bool responses)
    {
        // The provider says this model always reasons. There is no parameter that turns
        // it off, and the one that looks like it would is rejected, so send neither.
        if (config?.Mandatory == true) return;

        // An aggregating gateway normalises reasoning across every vendor behind it and
        // takes one boolean for off — which works on the models that have no "none"
        // effort in their vocabulary, and those are the ones this used to get wrong.
        if (OpenRouterAttribution.IsOpenRouterHost(endpoint))
        {
            body["reasoning"] = new { enabled = false };
            return;
        }

        if (responses)
        {
            if (AcceptsNoneEffort(config)) body["reasoning"] = new { effort = "none" };
            return;
        }

        if (request.ThinkingParamKind == ThinkingParamKind.DeepSeekV4)
            body["thinking"] = new { type = "disabled" };
        else if (request.ThinkingParamKind == ThinkingParamKind.QwenThinkingBudget)
            body["enable_thinking"] = false;
        else if (request.ThinkingParamKind is ThinkingParamKind.OpenAiReasoningEffort
                 or ThinkingParamKind.GeminiBudget
                 or ThinkingParamKind.GeminiThinkingLevel)
        {
            if (AcceptsNoneEffort(config)) body["reasoning_effort"] = "none";
        }
    }

    /// <summary>
    /// Whether <c>none</c> is in this model's effort vocabulary.
    ///
    /// A published effort list is taken at its word — that is the whole reason for
    /// reading it. Silence keeps the long-standing behaviour rather than inventing a
    /// name-based guess: endpoints that publish nothing (OpenAI's own among them) were
    /// already being sent <c>none</c>, and withdrawing it there would trade a known
    /// working disable for a silent one.
    /// </summary>
    private static bool AcceptsNoneEffort(ThinkingConfig? config)
    {
        var levels = config?.EffortLevels;
        if (levels is null || levels.Length == 0) return true;
        return levels.Any(level => string.Equals(level, "none", StringComparison.OrdinalIgnoreCase));
    }
}
