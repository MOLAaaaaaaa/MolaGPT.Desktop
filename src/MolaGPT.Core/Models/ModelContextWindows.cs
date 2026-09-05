using System.Text.RegularExpressions;

namespace MolaGPT.Core.Models;

/// <summary>
/// Best-effort context window for a wire model id, used only when nobody more
/// authoritative has said. The order that matters is:
/// <list type="number">
///   <item>what the user typed in Settings → the model row's 上下文窗口;</item>
///   <item>what the provider's own <c>/models</c> response reported;</item>
///   <item>this table;</item>
///   <item>a conservative default.</item>
/// </list>
///
/// <para><b>These are thresholds, not API limits.</b> They exist so the context
/// gauge has an honest denominator and so Pi's auto-compaction fires at roughly
/// the right moment — a model catalogued at a flat 128K compacts a 1M-token model
/// eight times too early, and one catalogued too generously does not compact until
/// the turn has already overflowed. Where sources disagree, these round <i>down</i>:
/// compacting early costs some detail, overflowing costs the whole answer.</para>
///
/// <para>Model families ship faster than this file can track, so a miss is normal
/// and harmless — it falls through to the default and the user can always type the
/// real number. Never guess a value to fill a gap; leaving a family out is the
/// conservative outcome, inventing a large window for it is not.</para>
/// </summary>
public static class ModelContextWindows
{
    private const int Small = 128_000;
    private const int Medium = 200_000;
    private const int Large = 400_000;

    /// <summary>
    /// One rounded constant for the whole 1M class. The real figures differ
    /// slightly (1,000,000 vs 1,048,576) and the difference is noise next to
    /// Pi's 16,384-token compaction reserve — a user who needs the exact number
    /// types it in Settings.
    /// </summary>
    private const int Huge = 1_000_000;

    /// <summary>
    /// First match wins, so the version-qualified rules precede the family
    /// catch-alls. Version separators are matched as <c>[.\-]</c> because the same
    /// model is written both ways (<c>sonnet-4.6</c> and <c>sonnet-4-6</c>).
    /// </summary>
    private static readonly (Regex Pattern, int Window)[] Rules =
    [
        // Alibaba Model Studio opts a model into the long window by suffix rather
        // than by a separate id, so this outranks every family rule below.
        (Rx(@"\[1m\]"), Huge),

        // Anthropic — 1M arrives at Opus 4.6 / Sonnet 4.6; everything older is 200K.
        // The version group has to fail closed: "claude-3-5-sonnet-20241022" must not
        // read its date as a version.
        (Rx(@"(opus|sonnet)[.\-]?(4[.\-][6-9]|[5-9])(\D|$)"), Huge),
        (Rx(@"claude|anthropic|opus|sonnet|haiku"), Medium),

        // Google — Gemini 3.x text models are 1M; the image variants are far smaller.
        (Rx(@"gemini.*(image|vision-preview)"), Small),
        (Rx(@"gemini[.\-]?[3-9]"), Huge),
        (Rx(@"gemini"), Small),

        // OpenAI — the chat tier and the reasoning tier of the same generation do
        // not share a window, so the narrower one is matched first.
        (Rx(@"gpt[.\-]?5[.\d]*[.\-]?chat"), Small),
        (Rx(@"gpt[.\-]?[5-9]|(^|/)o[1-9]"), Large),
        (Rx(@"gpt[.\-]?4\.1"), Huge),
        (Rx(@"gpt[.\-]?4"), Small),

        // DeepSeek — 1M is the default across the current official services.
        (Rx(@"deepseek"), Huge),

        // Moonshot / Kimi — K2 and K3 are both 1M class.
        (Rx(@"kimi|moonshot"), Huge),

        // Zhipu GLM — 1M from GLM-5 on; GLM-4 is 128K.
        (Rx(@"glm[.\-]?[5-9]"), Huge),
        (Rx(@"glm|chatglm"), Small),

        // Qwen — the 1M tiers are 3.6 and later; earlier ones are 128K.
        (Rx(@"qwen[.\-]?3[.\-][6-9]|qwen[.\-]?[4-9]"), Huge),
        (Rx(@"qwen|qwq"), Small),

        // Meta Llama — Llama 4 is 1M class. Scout advertises 10M, which is a
        // position-embedding ceiling rather than a window anyone should budget
        // against, so it is deliberately not encoded here.
        (Rx(@"llama[.\-]?4"), Huge),
        (Rx(@"llama"), Small),

        (Rx(@"mistral|magistral|devstral|codestral"), Small),
    ];

    /// <summary>
    /// The window to assume when the id matches nothing. Deliberately the smallest
    /// window still large enough to be useful: an unknown model is more likely to be
    /// a small one, and guessing high is the failure that loses an answer.
    /// </summary>
    public const int ConservativeDefault = Small;

    /// <summary>
    /// Returns the catalogued window for <paramref name="modelId"/>, or null when
    /// the id is unrecognised — null rather than the default so callers can tell
    /// "we know this one" from "we are falling back", which is what lets the gauge
    /// say whether its denominator is real.
    /// </summary>
    public static int? Resolve(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        foreach (var (pattern, window) in Rules)
        {
            if (pattern.IsMatch(modelId)) return window;
        }
        return null;
    }

    /// <summary>
    /// The window to hand a downstream consumer that needs a number no matter what:
    /// the caller's own value first (user setting or provider-reported), then the
    /// table, then the conservative default.
    /// </summary>
    public static int ResolveOrDefault(string? modelId, int? declared) =>
        declared is > 0 ? declared.Value : Resolve(modelId) ?? ConservativeDefault;

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
