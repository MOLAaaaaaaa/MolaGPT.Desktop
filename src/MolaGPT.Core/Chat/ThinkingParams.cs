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
/// </summary>
internal static class ThinkingParams
{
    public static void Apply(IDictionary<string, object?> body, ChatRequest request)
    {
        if (request.UseThinking == true)
        {
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
            if (request.ThinkingParamKind == ThinkingParamKind.DeepSeekV4)
                body["thinking"] = new { type = "disabled" };
            else if (request.ThinkingParamKind == ThinkingParamKind.QwenThinkingBudget)
                body["enable_thinking"] = false;
            else if (request.ThinkingParamKind is ThinkingParamKind.OpenAiReasoningEffort
                     or ThinkingParamKind.GeminiBudget
                     or ThinkingParamKind.GeminiThinkingLevel)
                body["reasoning_effort"] = "none";
        }
    }
}
