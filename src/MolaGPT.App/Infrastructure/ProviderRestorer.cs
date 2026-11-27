using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

/// <summary>Results of preparing a batch of provider configurations.</summary>
public sealed class ProviderRestoreSummary
{
    public int Registered { get; private set; }
    public int RuntimeUnavailable { get; private set; }
    public int Unsupported { get; private set; }
    public int NotApplicable { get; private set; }
    private readonly List<string> _failedNames = [];
    public IReadOnlyList<string> FailedNames => _failedNames;
    public int Failed => _failedNames.Count;
    public int Unavailable => RuntimeUnavailable + Unsupported;

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

    internal void RecordFailure(string name) => _failedNames.Add(name);

    public void ReportFailures(NotificationCenter notifications)
    {
        if (Failed > 0)
            notifications.Error("部分模型服务无法加载", string.Join("、", FailedNames), "provider-restore");
    }
}

/// <summary>Applies saved BYOK configurations and owns their runtime registrations.
/// Settings and runtime installation call this on the UI thread.</summary>
public sealed class ProviderRestorer(
    ProviderRepository repository,
    CredentialStore credentials,
    SettingsViewModel settings,
    ProviderRegistry registry,
    PiByokProviderFactory pi,
    Action<string>? log = null)
{
    private sealed record PreparedProvider(ProviderEntry Entry, PiWorkProvider? Provider, ProviderApplyOutcome Outcome);

    public ProviderRestoreSummary LoadSaved()
    {
        var summary = new ProviderRestoreSummary();
        var entries = new List<ProviderEntry>();
        var legacy = new List<ProviderRow>();
        foreach (var row in repository.List())
        {
            try
            {
                RequireByokId(row.Id);
                var entry = ProviderRowCodec.Decode(row, credentials);
                var secured = ProviderRowCodec.ProtectLegacyCredentials(row, entry, credentials);
                if (secured != row) legacy.Add(secured);
                entries.Add(entry);
            }
            catch (InvalidDataException ex)
            {
                RecordFailure(summary, row.Name, "读取", ex);
            }
        }

        var changes = PrepareSaved(entries, summary);
        if (legacy.Count > 0) repository.UpsertMany(legacy);
        settings.SetProviderEntries(entries);
        ApplyRuntime(changes);
        settings.RefreshProviderConfigurations();
        return summary;
    }

    public ProviderRestoreSummary Restore()
    {
        var summary = new ProviderRestoreSummary();
        ApplyRuntime(PrepareSaved(settings.Providers, summary));
        settings.RefreshProviderConfigurations();
        return summary;
    }

    private Dictionary<string, IChatProvider?> PrepareSaved(IEnumerable<ProviderEntry> entries, ProviderRestoreSummary summary)
    {
        var prepared = new Dictionary<string, IChatProvider?>();
        foreach (var entry in entries)
        {
            try
            {
                var result = Prepare(entry);
                prepared.Add(entry.Id, result.Provider);
                summary.Record(result.Outcome);
                if (result.Outcome is ProviderApplyOutcome.RuntimeUnavailable or ProviderApplyOutcome.Unsupported)
                    log?.Invoke($"服务「{entry.Name}」未注册 ({result.Outcome})。");
            }
            catch (Exception ex)
            {
                RecordFailure(summary, entry.Name, "恢复", ex);
            }
        }

        return prepared;
    }

    public ProviderApplyOutcome Save(ProviderEntry entry)
    {
        var prepared = Prepare(entry);
        Commit([prepared], [ProviderRowCodec.Encode(entry, credentials)], enableImageGeneration: ProviderPurpose.IsImage(entry.Purpose));
        return prepared.Outcome;
    }

    public ProviderRestoreSummary Save(IReadOnlyList<ProviderEntry> entries)
    {
        var prepared = entries.Select(Prepare).ToArray();
        Commit(prepared, entries.Select(entry => ProviderRowCodec.Encode(entry, credentials)).ToArray());
        var summary = new ProviderRestoreSummary();
        foreach (var item in prepared) summary.Record(item.Outcome);
        return summary;
    }

    public ProviderRestoreSummary Import(IReadOnlyList<ProviderRow> rows)
    {
        var existing = settings.Providers.ToDictionary(entry => entry.Id);
        var nextOrder = existing.Count == 0 ? 0 : existing.Values.Max(entry => entry.SortOrder) + 1;
        var summary = new ProviderRestoreSummary();
        var prepared = new List<PreparedProvider>();
        var accepted = new List<ProviderRow>();
        foreach (var row in rows)
        {
            try
            {
                var ordered = row with
                {
                    SortOrder = existing.TryGetValue(row.Id, out var current) ? current.SortOrder : nextOrder++
                };
                var result = Prepare(ProviderRowCodec.Decode(ordered, credentials));
                prepared.Add(result);
                accepted.Add(ordered);
                summary.Record(result.Outcome);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
            {
                RecordFailure(summary, row.Name, "导入", ex);
            }
        }
        Commit(prepared, accepted);
        return summary;
    }

    private void RecordFailure(ProviderRestoreSummary summary, string name, string operation, Exception error)
    {
        summary.RecordFailure(name);
        log?.Invoke($"{operation}服务「{name}」失败：{error.Message}");
    }

    public void Delete(string id)
    {
        repository.Delete(id);
        settings.SetProviderEntries(settings.Providers.Where(entry => entry.Id != id).ToArray());
        if (!MolaGptProviderIds.IsMolaGptAccount(id))
            ApplyRuntime(new Dictionary<string, IChatProvider?> { [id] = null });
        settings.RefreshProviderConfigurations(deletedId: id);
    }

    public void Deactivate()
    {
        ApplyRuntime(settings.Providers
            .Where(entry => !MolaGptProviderIds.IsMolaGptAccount(entry.Id))
            .ToDictionary(entry => entry.Id, _ => (IChatProvider?)null));
        settings.RefreshProviderConfigurations();
    }

    private PreparedProvider Prepare(ProviderEntry entry)
    {
        RequireByokId(entry.Id);
        if (!entry.Enabled || ProviderPurpose.IsImage(entry.Purpose))
            return new(entry, null, ProviderApplyOutcome.NotApplicable);

        var models = entry.Models.Select(model => ProviderModelResolver.ToProviderModel(model, entry.Type, entry.BaseUrl, entry.ApiPath)).ToList();
        var headers = CustomParamConverter.ToHeaderList(entry.CustomHeaders);
        var provider = pi.TryWrap(entry.Type, entry.Id, entry.Name, entry.BaseUrl, entry.ApiPath,
            entry.ApiKey, models, out var outcome, headers, entry.BuiltinSearch);
        return new(entry, provider, outcome);
    }

    private static void RequireByokId(string id)
    {
        if (MolaGptProviderIds.IsMolaGptAccount(id))
            throw new InvalidDataException("该服务 ID 由 MolaGPT 官方服务保留。");
    }

    private void Commit(IReadOnlyList<PreparedProvider> prepared, IReadOnlyList<ProviderRow> rows,
        bool enableImageGeneration = false)
    {
        if (rows.Count == 0) return;
        var entries = settings.Providers.ToDictionary(entry => entry.Id);
        foreach (var item in prepared) entries[item.Entry.Id] = item.Entry;
        var snapshot = entries.Values.OrderBy(entry => entry.SortOrder)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        var changes = prepared.ToDictionary(item => item.Entry.Id, item => (IChatProvider?)item.Provider);

        repository.UpsertMany(rows);
        settings.SetProviderEntries(snapshot);
        ApplyRuntime(changes);
        settings.RefreshProviderConfigurations(enableImageGeneration: enableImageGeneration);
    }

    private void ApplyRuntime(IReadOnlyDictionary<string, IChatProvider?> changes)
    {
        var retiring = new List<PiWorkProvider>();
        foreach (var (id, replacement) in changes)
        {
            if (registry.GetById(id) is PiWorkProvider current
                && (replacement is not PiWorkProvider next || !current.SharesSidecarSpec(next)))
                retiring.Add(current);
        }

        try { registry.ApplyChanges(changes); }
        finally
        {
            foreach (var provider in retiring) _ = RetireAsync(provider);
        }
    }

    private async Task RetireAsync(PiWorkProvider provider)
    {
        try { await provider.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { log?.Invoke("释放 BYOK sidecar 失败：" + ex.Message); }
    }


}
