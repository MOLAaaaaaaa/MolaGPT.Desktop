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

    /// <summary>Notified whenever the provider set changes (add / remove / refresh).</summary>
    public event EventHandler? Changed;

    public IReadOnlyCollection<IChatProvider> Providers => _providers.Values.ToList();

    public void Register(IChatProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Id] = provider;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string providerId)
    {
        var removed = _providers.TryRemove(providerId, out _);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
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
