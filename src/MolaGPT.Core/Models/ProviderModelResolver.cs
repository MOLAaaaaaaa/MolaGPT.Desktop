using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;

namespace MolaGPT.Core.Models;

public static class ProviderModelResolver
{
    public static ProviderModelEntry ResolveModelCapabilities(
        ProviderModelEntry entry, string? providerType, string? baseUrl, string? apiPath = null)
    {
        ThinkingParamKind? explicitKind = Enum.TryParse<ThinkingParamKind>(entry.ThinkingParamKind, true, out var kind)
            && Enum.IsDefined(kind) ? kind : null;
        if (explicitKind == ThinkingParamKind.None)
            return entry with { Thinking = false, ReasoningEffort = false, ReasoningMandatory = false };

        // Saved official model settings may predate the endpoint's new effort vocabulary.
        if ((entry.Thinking || entry.ReasoningEffort || entry.ReasoningMandatory)
            && ThinkingParamKindInference.OfficialPresetFor(entry.Id, baseUrl, apiPath) is { } native)
        {
            var levels = native.EffortLevels!;
            var defaultEffort = entry.DefaultEffort?.Trim().ToLowerInvariant();
            return entry with
            {
                Thinking = true, ReasoningEffort = true,
                ThinkingParamKind = native.Kind.ToString(),
                EffortLevels = levels.ToList(),
                DefaultEffort = defaultEffort is not null && levels.Contains(defaultEffort, StringComparer.Ordinal) ? defaultEffort : native.DefaultEffort,
                ReasoningMandatory = native.Mandatory,
                ThinkingBudgetMin = null, ThinkingBudgetMax = null, ThinkingBudgetDefault = null
            };
        }

        var google = GeminiModelPolicy.IsRoute(providerType, baseUrl, apiPath);
        var policy = google ? GeminiModelPolicy.DefaultThinkingFor(entry.Id) : null;
        if (explicitKind is null && policy is null && !entry.Thinking && !entry.ReasoningEffort && !entry.ReasoningMandatory)
            return entry;

        kind = explicitKind ?? policy?.Kind ?? ThinkingParamKindInference.InferFromModelId(entry.Id, providerType switch
        {
            "anthropic" => ThinkingParamKind.AnthropicAdaptive,
            "gemini" when google => ThinkingParamKind.GeminiThinkingLevel,
            _ when entry.ReasoningEffort || OpenRouterAttribution.IsOpenRouterHost(baseUrl) => ThinkingParamKind.OpenAiReasoningEffort,
            _ => ThinkingParamKind.None
        });

        if (google && kind == ThinkingParamKind.GeminiThinkingLevel)
        {
            var levelsPolicy = policy is { Kind: ThinkingParamKind.GeminiThinkingLevel }
                ? policy : new ThinkingConfig(kind, Mandatory: true);
            var allowedLevels = GeminiModelPolicy.OfficialLevelPolicy(entry.Id, providerType, baseUrl, apiPath)?.EffortLevels;
            var (levels, defaultEffort) = GeminiModelPolicy.ResolveEfforts(
                levelsPolicy, entry.EffortLevels, entry.DefaultEffort, allowedLevels);
            return entry with
            {
                Thinking = true,
                ReasoningEffort = true,
                ThinkingParamKind = kind.ToString(),
                EffortLevels = levels.ToList(),
                DefaultEffort = defaultEffort,
                ReasoningMandatory = true,
                SupportsTemperature = false,
                SupportsTopP = false
            };
        }

        var matchingPolicy = policy?.Kind == kind ? policy : null;
        return entry with
        {
            Thinking = entry.Thinking || matchingPolicy is not null,
            ReasoningEffort = entry.ReasoningEffort || matchingPolicy is not null,
            ThinkingParamKind = kind == ThinkingParamKind.None ? null : kind.ToString(),
            ThinkingBudgetMin = entry.ThinkingBudgetMin ?? matchingPolicy?.MinBudget,
            ReasoningMandatory = entry.ReasoningMandatory || matchingPolicy?.Mandatory == true
        };
    }

    public static ProviderModel ToProviderModel(
        ProviderModelEntry entry, string? providerType, string? baseUrl, string? apiPath = null)
    {
        entry = ResolveModelCapabilities(entry, providerType, baseUrl, apiPath);
        if (!Enum.TryParse<ThinkingParamKind>(entry.ThinkingParamKind, true, out var kind) || !Enum.IsDefined(kind))
            kind = ThinkingParamKind.None;

        var thinking = new ThinkingConfig(
            kind,
            EffortLevels: ThinkingEffortLevels.Normalize(entry.EffortLevels) is { Length: > 0 } levels ? levels : null,
            MinBudget: entry.ThinkingBudgetMin,
            MaxBudget: entry.ThinkingBudgetMax,
            DefaultBudget: entry.ThinkingBudgetDefault,
            DefaultEffort: entry.DefaultEffort,
            Mandatory: entry.ReasoningMandatory);
        var supportsBuiltinSearch = entry.BuiltinSearch
            ?? (BuiltinWebSearch.DetectVendor(baseUrl) is { } vendor
                && BuiltinWebSearch.DefaultSupportsModel(vendor, entry.Id, providerType));
        return new ProviderModel(
            entry.Id,
            NormalizeAutoModelDisplayName(entry.Id, entry.DisplayName),
            SupportsVision: entry.Vision,
            SupportsThinking: entry.Thinking,
            SupportsReasoningEffort: entry.ReasoningEffort,
            SupportsToolCalling: entry.Tools,
            ContextWindow: entry.ContextWindow,
            ThinkingConfig: thinking,
            CustomBody: CustomParamConverter.ToBodyDict(entry.CustomBody),
            SupportsTemperature: entry.SupportsTemperature,
            SupportsTopP: entry.SupportsTopP,
            Pricing: entry.Pricing,
            SupportsBuiltinSearch: supportsBuiltinSearch);
    }

    /// <summary>
    /// Keeps a hand-edited display name, but re-beautifies one that still matches
    /// the old auto-generated form — the legacy rule also replaced hyphens, which
    /// mangled ids like "gpt-4o" into "gpt 4o".
    /// </summary>
    private static string NormalizeAutoModelDisplayName(string id, string displayName)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) return BeautifyModelName(id);

        return string.Equals(trimmed, LegacyBeautifyModelName(id), StringComparison.Ordinal)
            ? BeautifyModelName(id)
            : trimmed;
    }

    public static string BeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('_', ' ');
    }

    private static string LegacyBeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('-', ' ').Replace('_', ' ');
    }
}
