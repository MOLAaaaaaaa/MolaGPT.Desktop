using System.Collections.Concurrent;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Runtime registry of all active chat providers (MolaGPT proxy + BYOK).
/// The UI binds to <see cref="Providers"/> for the model selector.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, IChatProvider> _providers = new();
    private readonly System.Threading.Lock _orderGate = new();
    private readonly List<string> _providerOrder = new();

    /// <summary>Notified whenever the provider set changes (add / remove / refresh).</summary>
    public event EventHandler? Changed;

    public IReadOnlyCollection<IChatProvider> Providers
    {
        get
        {
            lock (_orderGate)
            {
                var providers = new List<IChatProvider>(_providers.Count);
                foreach (var id in _providerOrder)
                {
                    if (_providers.TryGetValue(id, out var provider))
                        providers.Add(provider);
                }

                foreach (var provider in _providers.Values)
                {
                    if (!_providerOrder.Contains(provider.Id))
                        providers.Add(provider);
                }

                return providers;
            }
        }
    }

    public void Register(IChatProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Id] = provider;
        lock (_orderGate)
        {
            if (!_providerOrder.Contains(provider.Id))
                _providerOrder.Add(provider.Id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string providerId)
    {
        var removed = _providers.TryRemove(providerId, out _);
        if (removed)
        {
            lock (_orderGate)
                _providerOrder.Remove(providerId);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    public IChatProvider? GetById(string providerId) =>
        _providers.TryGetValue(providerId, out var p) ? p : null;

    /// <summary>
    /// Resolve the provider that currently owns a given model id. If multiple providers
    /// expose the same model id (rare), the first registered wins; UI should use the
    /// (providerId, modelId) tuple instead of model id alone.
    /// </summary>
    public (IChatProvider Provider, ProviderModel Model)? FindModel(string providerId, string modelId)
    {
        if (!_providers.TryGetValue(providerId, out var provider)) return null;
        var model = provider.Models.FirstOrDefault(m => m.Id == modelId);
        return model is null ? null : (provider, model);
    }
}
