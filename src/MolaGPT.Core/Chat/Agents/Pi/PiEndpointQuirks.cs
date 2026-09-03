namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Per-endpoint corrections applied on the way out of <see cref="PiWorkLlmShim"/>.
///
/// Pi carries its own compatibility table, but it is keyed on the model's
/// <c>provider</c> id and <c>baseUrl</c> — and under this architecture both of
/// those are <em>ours</em>: every sidecar is registered as <c>molagpt-work</c>
/// pointing at a loopback shim address. Pi therefore cannot tell DeepSeek from
/// OpenRouter from Google, and every request is built to the plain OpenAI dialect.
///
/// Mostly that is harmless — the strict-OpenAI body is the widest common shape.
/// Where it is not, the mismatch is a hard 400 rather than a degraded answer:
/// Google's OpenAI-compatible endpoint rejects the whole request over a single
/// unknown field.
///
/// This only removes fields. Anything Pi would need to build <em>differently</em>
/// for an endpoint (DeepSeek's reasoning-content round-trip, the per-vendor
/// thinking dialects) cannot be fixed from here and has to be handed to Pi as an
/// explicit model <c>compat</c> profile instead.
/// </summary>
public static class PiEndpointQuirks
{
    /// <summary>Google's OpenAI-compatibility layer validates the payload strictly
    /// and rejects unknown names. Verified against
    /// <c>generativelanguage.googleapis.com/v1beta/openai/chat/completions</c>:
    /// <c>store</c> and <c>prompt_cache_key</c> are refused; <c>developer</c> role,
    /// <c>max_completion_tokens</c>, <c>stream_options</c>, <c>reasoning_effort</c>
    /// and non-strict tool definitions are all accepted.</summary>
    private static readonly string[] GoogleCompatDrops = ["store", "prompt_cache_key"];

    /// <summary>Body keys <paramref name="endpoint"/> refuses, or null when it takes
    /// whatever Pi sends.</summary>
    public static IReadOnlyList<string>? DropBodyKeysFor(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        return endpoint.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase)
            ? GoogleCompatDrops
            : null;
    }

    /// <summary>
    /// The compatibility profile Pi would have detected for <paramref name="endpoint"/>
    /// if it could see it, as a JSON fragment for the model's <c>compat</c> field.
    /// Null means "Pi's default profile is right", which is the common case.
    ///
    /// Every entry here mirrors a rule Pi already states in
    /// <c>pi-ai/dist/api/openai-completions.js</c> (<c>detectCompat</c>) and keys on
    /// the same hosts. This is not a second opinion about these providers — it is
    /// handing back the one input the shim took away. Only the flags whose absence
    /// changes the request are carried; the rest are left for Pi to default.
    /// </summary>
    public static string? CompatJsonFor(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        // Pi's "non-standard OpenAI" set: these reject `store` and the `developer`
        // role, and take max_tokens rather than max_completion_tokens.
        var host = endpoint!;
        var nonStandard =
            Has(host, "deepseek.com") || Has(host, "api.moonshot.") || Has(host, "api.z.ai")
            || Has(host, "open.bigmodel.cn") || Has(host, "api.together.ai") || Has(host, "api.together.xyz")
            || Has(host, "chutes.ai") || Has(host, "cerebras.ai") || Has(host, "api.x.ai")
            || Has(host, "integrate.api.nvidia.com") || Has(host, "api.ant-ling.com")
            || Has(host, "api.cloudflare.com") || Has(host, "gateway.ai.cloudflare.com")
            || Has(host, "opencode.ai");

        if (!nonStandard) return null;

        // DeepSeek additionally round-trips its reasoning as a distinct field, and
        // wants it echoed back on assistant messages across a multi-turn tool loop.
        var deepSeek = Has(host, "deepseek.com");

        return deepSeek
            ? """
              {"supportsStore":false,"supportsDeveloperRole":false,"maxTokensField":"max_tokens",
               "thinkingFormat":"deepseek","requiresReasoningContentOnAssistantMessages":true}
              """
            : """
              {"supportsStore":false,"supportsDeveloperRole":false,"maxTokensField":"max_tokens"}
              """;
    }

    private static bool Has(string endpoint, string fragment) =>
        endpoint.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
