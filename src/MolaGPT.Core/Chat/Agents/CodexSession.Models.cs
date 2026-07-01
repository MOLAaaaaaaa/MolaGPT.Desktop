using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Model discovery + live model switching for <see cref="CodexSession"/>.
///
/// Codex app-server exposes a real catalog via the <c>model/list</c> JSON-RPC
/// method (ModelListResponse.data: Model[] — id / model / displayName /
/// description / isDefault / supportedReasoningEfforts). Unlike Claude there is no
/// in-flight "set model" control message, but the app-server honours a per-turn
/// <c>model</c> override, so switching just updates <see cref="_currentModel"/> and
/// the next <c>turn/start</c> adopts it — no process restart, context preserved.
/// </summary>
internal sealed partial class CodexSession
{
    private volatile IReadOnlyList<AgentModelInfo> _availableModels = Array.Empty<AgentModelInfo>();
    private Task? _discoverTask;

    public IReadOnlyList<AgentModelInfo> AvailableModels => _availableModels;

    public Task<bool> SetModelAsync(string? model, CancellationToken ct)
    {
        if (_disposed != 0) return Task.FromResult(false);
        // Applied on the next turn/start via WithTurnOptions — no restart needed.
        _currentModel = string.IsNullOrWhiteSpace(model) ? null : model;
        return Task.FromResult(true);
    }

    private void BeginDiscoverModels()
        => _discoverTask ??= Task.Run(() => DiscoverModelsAsync(_lifetimeCts.Token));

    private async Task DiscoverModelsAsync(CancellationToken ct)
    {
        try
        {
            var result = await RequestAsync("model/list", new { }, ct).ConfigureAwait(false);
            _availableModels = ParseModels(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _turnChannel?.Writer.TryWrite(AgentEvent.Failure($"Codex model discovery failed: {ex.Message}"));
        }
    }

    /// <summary>Parse <c>model/list</c> result (<c>data: Model[]</c>) into
    /// <see cref="AgentModelInfo"/>. The switch value is <c>model</c> (the slug the
    /// app-server accepts as a turn override), falling back to <c>id</c>.</summary>
    private static IReadOnlyList<AgentModelInfo> ParseModels(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<AgentModelInfo>();

        var list = new List<AgentModelInfo>();
        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            // Hidden models aren't shown in the default picker.
            if (m.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True) continue;

            var slug = ReadStr(m, "model") ?? ReadStr(m, "id");
            if (string.IsNullOrWhiteSpace(slug)) continue;

            var display = ReadStr(m, "displayName") ?? slug;
            var description = ReadStr(m, "description");
            var isDefault = m.TryGetProperty("isDefault", out var def) && def.ValueKind == JsonValueKind.True;

            IReadOnlyList<string>? efforts = null;
            if (m.TryGetProperty("supportedReasoningEfforts", out var el) && el.ValueKind == JsonValueKind.Array)
            {
                var e = new List<string>();
                foreach (var opt in el.EnumerateArray())
                {
                    // Each option is { reasoningEffort, description }.
                    var eff = opt.ValueKind == JsonValueKind.Object ? ReadStr(opt, "reasoningEffort")
                            : (opt.ValueKind == JsonValueKind.String ? opt.GetString() : null);
                    if (!string.IsNullOrWhiteSpace(eff)) e.Add(eff!);
                }
                if (e.Count > 0) efforts = e;
            }

            list.Add(new AgentModelInfo(slug!, display, description, isDefault, efforts));
        }
        return list;
    }

    private static string? ReadStr(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
