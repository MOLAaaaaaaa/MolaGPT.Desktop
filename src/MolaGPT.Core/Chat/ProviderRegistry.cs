using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Runtime registry of all active chat providers (MolaGPT proxy + BYOK).
/// The UI binds to <see cref="Providers"/> for the model selector.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly OrderedDictionary<string, IChatProvider> _providers = new(StringComparer.Ordinal);
    private readonly System.Threading.Lock _gate = new();

    /// <summary>Notified whenever the provider set changes (add / remove / refresh).</summary>
    public event EventHandler? Changed;

    public IReadOnlyCollection<IChatProvider> Providers
    {
        get
        {
            lock (_gate)
                return _providers.Values.ToArray();
        }
    }

    public void Register(IChatProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
            _providers[provider.Id] = provider;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string providerId)
    {
        bool removed;
        lock (_gate)
            removed = _providers.Remove(providerId);
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Publishes a batch after all of its replacements and removals are visible.</summary>
    public void ApplyChanges(IReadOnlyDictionary<string, IChatProvider?> changes)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var (id, provider) in changes)
            {
                if (provider is null)
                    changed |= _providers.Remove(id);
                else
                {
                    _providers[id] = provider;
                    changed = true;
                }
            }
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public IChatProvider? GetById(string providerId)
    {
        lock (_gate)
            return _providers.GetValueOrDefault(providerId);
    }

    /// <summary>
    /// Resolve the provider that currently owns a given model id. If multiple providers
    /// expose the same model id (rare), the first registered wins; UI should use the
    /// (providerId, modelId) tuple instead of model id alone.
    /// </summary>
    public (IChatProvider Provider, ProviderModel Model)? FindModel(string providerId, string modelId)
    {
        if (GetById(providerId) is not { } provider) return null;
        var model = provider.Models.FirstOrDefault(m => m.Id == modelId);
        return model is null ? null : (provider, model);
    }
}
