using System.IO;
using System.Text.Json;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Desktop-layer implementation of <see cref="IAgentConfigProvider"/>. Reads
/// agent settings from <see cref="SettingsRepository"/> and tracks each
/// conversation's chosen working directory. Keeps Core free of any storage
/// dependency.
/// </summary>
public sealed class DesktopAgentConfigProvider : IAgentConfigProvider
{
    public const string KeyClaudePath = "agent.claudeCodePath";
    public const string KeyCodexPath = "agent.codexPath";
    public const string KeyPermissionMode = "agent.permissionMode";
    public const string KeyBridgeEnabled = "agent.bridgeEnabled";
    public const string KeyMachineId = "agent.machineId";
    private const string WorkdirKeyPrefix = "agent.workdir.";
    private const string SessionStubKeyPrefix = "agent.session.";
    private static readonly JsonSerializerOptions StubJson = new(JsonSerializerDefaults.Web);

    private readonly SettingsRepository _settings;
    private readonly object _machineIdGate = new();
    private string? _cachedMachineId;

    public DesktopAgentConfigProvider(SettingsRepository settings) => _settings = settings;

    public string? ClaudeCodePath => Normalize(_settings.Get(KeyClaudePath));

    public string? CodexPath => Normalize(_settings.Get(KeyCodexPath));

    /// <summary>Whether the cloud relay bridge is enabled. Opt-in: default false, so
    /// nothing about local agent sessions is sent to the relay server until the user
    /// turns it on (and accepts the privacy disclosure).</summary>
    public bool BridgeEnabled
    {
        get => string.Equals(_settings.Get(KeyBridgeEnabled), "true", StringComparison.OrdinalIgnoreCase);
        set => _settings.Set(KeyBridgeEnabled, value ? "true" : "false");
    }

    /// <inheritdoc />
    public string MachineId
    {
        get
        {
            lock (_machineIdGate)
            {
                if (!string.IsNullOrWhiteSpace(_cachedMachineId))
                    return _cachedMachineId;

                var existing = Normalize(_settings.Get(KeyMachineId));
                if (existing is not null)
                {
                    _cachedMachineId = existing;
                    return existing;
                }

                var created = Guid.NewGuid().ToString("N");
                _settings.Set(KeyMachineId, created);
                _cachedMachineId = created;
                return created;
            }
        }
    }

    /// <inheritdoc />
    public string MachineName => Environment.MachineName;

    public AgentPermissionMode PermissionMode =>
        Enum.TryParse<AgentPermissionMode>(_settings.Get(KeyPermissionMode), ignoreCase: true, out var mode)
            ? mode
            : AgentPermissionMode.AcceptEdits; // P0 default

    public string? GetWorkingDirectory(string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return null;
        var dir = Normalize(_settings.Get(WorkdirKeyPrefix + conversationId));
        return dir is not null && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Persist the working directory chosen for a conversation.</summary>
    public void SetWorkingDirectory(string conversationId, string directory)
    {
        if (string.IsNullOrEmpty(conversationId)) return;
        _settings.Set(WorkdirKeyPrefix + conversationId, directory);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    // ---- Durable session stubs --------------------------------------------

    public void SaveSession(AgentPersistedSession session)
    {
        if (string.IsNullOrEmpty(session.ConversationId)) return;
        _settings.Set(SessionStubKeyPrefix + session.ConversationId, JsonSerializer.Serialize(session, StubJson));
    }

    public void ForgetSession(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return;
        _settings.Remove(SessionStubKeyPrefix + conversationId);
    }

    public IReadOnlyList<AgentPersistedSession> ListPersistedSessions()
    {
        var list = new List<AgentPersistedSession>();
        foreach (var (_, value) in _settings.GetByPrefix(SessionStubKeyPrefix))
        {
            AgentPersistedSession? stub = null;
            try { stub = JsonSerializer.Deserialize<AgentPersistedSession>(value, StubJson); }
            catch { /* skip corrupt stub */ }
            if (stub is not null && !string.IsNullOrEmpty(stub.ConversationId))
                list.Add(stub);
        }
        return list;
    }
}
