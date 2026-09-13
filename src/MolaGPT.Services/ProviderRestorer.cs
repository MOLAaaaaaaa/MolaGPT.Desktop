using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Why a saved row did or did not end up in the registry.
///
/// The registry is what the model picker reads, so "saved" and "usable" are two
/// different states and the user can only see the first one. Every path that
/// registers a row reports which of these happened, so nothing can report
/// success while leaving the picker empty.
/// </summary>
public enum ProviderApplyOutcome
{
    /// <summary>In the registry, and therefore in the picker.</summary>
    Registered,

    /// <summary>Disabled, or an image row — deliberately not a chat provider.</summary>
    NotApplicable,

    /// <summary>No compatible agent runtime on this machine. Machine-wide: every
    /// other row is in the same state, and updating the runtime fixes all of them.</summary>
    RuntimeUnavailable,

    /// <summary>The row itself cannot be carried — unknown wire shape, missing
    /// credentials, or no models. Specific to this row.</summary>
    Unsupported
}

/// <summary>Tally of one <see cref="ProviderRestorer.Restore"/> pass.</summary>
public sealed class ProviderRestoreSummary
{
    public int Registered { get; private set; }
    public int RuntimeUnavailable { get; private set; }
    public int Unsupported { get; private set; }
    public int NotApplicable { get; private set; }

    /// <summary>True when the user has saved chat rows and not one of them reached
    /// the picker because the runtime is missing — the state where the settings
    /// page lists services the model selector does not have.</summary>
    public bool LostEverythingToRuntime => Registered == 0 && RuntimeUnavailable > 0;

    internal void Record(ProviderApplyOutcome outcome)
    {
        switch (outcome)
        {
            case ProviderApplyOutcome.Registered: Registered++; break;
            case ProviderApplyOutcome.RuntimeUnavailable: RuntimeUnavailable++; break;
            case ProviderApplyOutcome.Unsupported: Unsupported++; break;
            default: NotApplicable++; break;
        }
    }
}

/// <summary>
/// Rebuilds the BYOK provider registry from what the user saved.
///
/// This is entirely provider and credential logic with no UI dependency.
/// </summary>
public static class ProviderRestorer
{
    /// <summary>
    /// Registers every enabled, non-image provider row. Each row is guarded
    /// individually: one malformed saved provider must not cost the user all the
    /// others, which is why the try sits inside the loop.
    ///
    /// Returns how many rows landed where, so startup can tell "the user has no
    /// BYOK rows" from "the user has six and none of them made it".
    /// </summary>
    public static ProviderRestoreSummary Restore(IServiceProvider services, Action<string>? log = null)
    {
        var summary = new ProviderRestoreSummary();
        try
        {
            var repo = services.GetRequiredService<ProviderRepository>();
            var registry = services.GetRequiredService<ProviderRegistry>();
            var creds = services.GetRequiredService<CredentialStore>();
            var pi = services.GetService<PiByokProviderFactory>();

            foreach (var row in repo.List())
            {
                try
                {
                    if (!row.Enabled || SettingsViewModel.IsImagePurpose(row.Purpose))
                    {
                        summary.Record(ProviderApplyOutcome.NotApplicable);
                        continue;
                    }

                    var apiKey = row.ApiKeyEnc is { Length: > 0 }
                        ? creds.Decrypt(row.ApiKeyEnc) ?? string.Empty
                        : string.Empty;

                    var models = TryDeserializeModels(row.Models);
                    var headers = CustomParamConverter.ToHeaderListFromJson(row.CustomHeaders);

                    // The agent runtime is the only chat engine. A row that cannot be
                    // carried is left unregistered rather than quietly downgraded —
                    // an unusable model missing from the picker is a question the
                    // user can act on; one that answers without tools is not.
                    var provider = pi?.TryWrap(row.Type, row.Id, row.Name, row.BaseUrl, row.ApiPath,
                        apiKey, models, headers);

                    if (provider is not null)
                    {
                        registry.Register(provider);
                        summary.Record(ProviderApplyOutcome.Registered);
                        continue;
                    }

                    var outcome = Classify(pi);
                    summary.Record(outcome);
                    log?.Invoke(outcome == ProviderApplyOutcome.RuntimeUnavailable
                        ? $"服务「{row.Name}」未注册：本机没有可用的 Agent 运行环境。"
                        : $"服务「{row.Name}」({row.Type}) 无法由 Agent 运行时承载，已跳过。");
                }
                catch (Exception ex)
                {
                    summary.Record(ProviderApplyOutcome.Unsupported);
                    log?.Invoke($"恢复服务「{row.Name}」失败：{ex}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"恢复已保存的服务失败：{ex}");
        }
        return summary;
    }

    /// <summary>
    /// Re-registers one row and says what happened to it. The return value is the
    /// point: a caller that saves a row must not report success on the strength of
    /// the write alone, because the write is to the database and the picker reads
    /// the registry.
    /// </summary>
    public static ProviderApplyOutcome ApplyEntry(
        ProviderEntry entry,
        ProviderRegistry registry,
        Func<HttpClient> httpFactory,
        IChatToolHost? toolHost = null,
        PiByokProviderFactory? pi = null)
    {
        RemoveEntry(entry.Id, registry, pi);
        if (!entry.Enabled || SettingsViewModel.IsImagePurpose(entry.Purpose))
            return ProviderApplyOutcome.NotApplicable;

        var models = entry.Models.Select(ToProviderModel).ToList();
        var headers = CustomParamConverter.ToHeaderList(entry.CustomHeaders);
        var provider = pi?.TryWrap(
            entry.Type, entry.Id, entry.Name, entry.BaseUrl, entry.ApiPath,
            entry.ApiKey ?? string.Empty, models, headers);
        if (provider is null) return Classify(pi);

        registry.Register(provider);
        return ProviderApplyOutcome.Registered;
    }

    /// <summary>A null wrap is machine-wide when there is no runtime to wrap onto,
    /// and row-specific otherwise. Asked after the fact rather than threaded out of
    /// <c>TryWrap</c>: the runtime can only be absent for all rows at once, so one
    /// question here answers it for every caller.</summary>
    private static ProviderApplyOutcome Classify(PiByokProviderFactory? pi) =>
        pi is null || !pi.IsRuntimeAvailable
            ? ProviderApplyOutcome.RuntimeUnavailable
            : ProviderApplyOutcome.Unsupported;

    public static void RemoveEntry(string id, ProviderRegistry registry, PiByokProviderFactory? pi = null)
    {
        registry.Unregister(id);
        pi?.Retire(id);
    }

    public static List<ProviderModel> TryDeserializeModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var entries = JsonSerializer.Deserialize<List<ProviderModelEntry>>(json) ?? new();
            return entries.Select(ToProviderModel).ToList();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public static ProviderModel ToProviderModel(ProviderModelEntry entry)
    {
        ThinkingConfig? thinkingConfig = null;
        var kindStr = entry.ThinkingParamKind;

        if (entry.Thinking && string.IsNullOrWhiteSpace(kindStr))
        {
            var inferred = ThinkingParamKindInference.InferFromModelId(entry.Id);
            if (inferred != ThinkingParamKind.None) kindStr = inferred.ToString();
        }

        if (kindStr is { } && Enum.TryParse<ThinkingParamKind>(kindStr, true, out var kind))
        {
            thinkingConfig = new ThinkingConfig(
                kind,
                EffortLevels: ThinkingEffortLevels.Normalize(entry.EffortLevels) is { Length: > 0 } levels
                    ? levels
                    : null,
                MinBudget: entry.ThinkingBudgetMin,
                MaxBudget: entry.ThinkingBudgetMax,
                DefaultBudget: entry.ThinkingBudgetDefault,
                DefaultEffort: entry.DefaultEffort);
        }

        return new ProviderModel(
            entry.Id,
            NormalizeAutoModelDisplayName(entry.Id, entry.DisplayName),
            SupportsVision: entry.Vision,
            SupportsThinking: entry.Thinking,
            SupportsReasoningEffort: entry.ReasoningEffort,
            SupportsToolCalling: entry.Tools,
            ContextWindow: entry.ContextWindow,
            ThinkingConfig: thinkingConfig,
            CustomBody: CustomParamConverter.ToBodyDict(entry.CustomBody),
            SupportsTemperature: entry.SupportsTemperature,
            SupportsTopP: entry.SupportsTopP);
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

    private static string BeautifyModelName(string id)
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
