using System.Text.Json;
using MolaGPT.Core.Models;

namespace MolaGPT.Desktop.Services;

/// <summary>One provider as models.dev lists it.</summary>
public sealed record ModelsDevProvider(string Key, string Name, IReadOnlyDictionary<string, ModelPricing> Models)
{
    public override string ToString() => Name;
}

/// <summary>One provider's price for one model.</summary>
public sealed record ModelsDevPrice(string ProviderKey, string ProviderName, ModelPricing Pricing);

/// <summary>A models.dev provider offered as the tie-breaker, with how many of the
/// disputed models it can actually settle.</summary>
public sealed record PricingSource(string Key, string Name, int Covers)
{
    public override string ToString() => $"{Name}（{Covers} 个）";
}

/// <summary>
/// What a set of model ids resolved to against the catalogue.
///
/// Most ids need no decision at all — roughly 70% of the catalogue is served by a
/// single provider and another 8% by several that agree — so those are filled in
/// without asking. Only <see cref="Conflicts"/> is the user's problem.
/// </summary>
public sealed record PricingMatch(
    IReadOnlyDictionary<string, ModelPricing> Agreed,
    IReadOnlyDictionary<string, IReadOnlyList<ModelsDevPrice>> Conflicts,
    IReadOnlyList<string> Unmatched)
{
    /// <summary>
    /// The providers worth offering as the tie-breaker: only those that appear in
    /// an actual disagreement, ordered by how much they settle. This is the whole
    /// point of resolving first and asking second — the catalogue has 200+ providers,
    /// but a given endpoint's conflicts involve a handful, and picking from a
    /// handful is a question a user can answer.
    /// </summary>
    public IReadOnlyList<PricingSource> Sources =>
        Conflicts.Values
            .SelectMany(prices => prices)
            .GroupBy(price => price.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PricingSource(group.Key, group.First().ProviderName, group.Count()))
            .OrderByDescending(source => source.Covers)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Settles every disagreement the chosen provider has a price for.
    /// Models it does not carry stay unresolved rather than silently borrowing
    /// a number from some other provider the user did not pick.</summary>
    public IReadOnlyDictionary<string, ModelPricing> Resolve(string providerKey)
    {
        var resolved = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modelId, prices) in Conflicts)
        {
            var match = prices.FirstOrDefault(price =>
                price.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase));
            if (match is not null) resolved[modelId] = match.Pricing;
        }
        return resolved;
    }
}

/// <summary>
/// models.dev's open pricing database, read from <c>api.json</c>.
///
/// Its <c>cost</c> block is already USD per million tokens — the same unit
/// <see cref="ModelPricing"/> stores and Pi's own cost maths divides by — so
/// nothing is rescaled on the way in.
///
/// The file is ~4.7 MB across 200+ providers, which is why it is fetched on
/// demand rather than shipped, and cached on disk afterwards: a user adding
/// three providers in a row should pay for one download, not three.
/// </summary>
public sealed class ModelsDevCatalog(Func<HttpClient> httpFactory)
{
    public const string ApiUrl = "https://models.dev/api.json";

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT", "models-dev.json");

    /// <summary>Long enough that repeat use in one sitting never re-downloads,
    /// short enough that a price cut lands within the week.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private IReadOnlyList<ModelsDevProvider>? _providers;

    public IReadOnlyList<ModelsDevProvider> Providers => _providers ?? [];

    public DateTimeOffset? CachedAt =>
        File.Exists(CachePath) ? File.GetLastWriteTimeUtc(CachePath) : null;

    /// <param name="forceRefresh">Skip the disk cache even when it is still fresh.</param>
    public async Task<IReadOnlyList<ModelsDevProvider>> LoadAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (_providers is not null && !forceRefresh) return _providers;

        if (!forceRefresh && ReadCache() is { } cached)
        {
            _providers = cached;
            return cached;
        }

        using var http = httpFactory();
        var json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
        var parsed = Parse(json);
        // Only a parse that produced something is worth keeping; caching an error
        // page would poison every later lookup until the file expired.
        if (parsed.Count > 0) WriteCache(json);
        _providers = parsed;
        return parsed;
    }

    /// <summary>
    /// Looks every model id up across the whole catalogue at once.
    ///
    /// Asking which models.dev provider an endpoint corresponds to was the wrong
    /// question to put to a user: it is a 200-item list, and for most models the
    /// answer does not even change the price. Searching everywhere and only
    /// surfacing genuine disagreements turns it into a question about 20% of the
    /// models, against a list of the few providers that actually disagree.
    /// </summary>
    public PricingMatch Match(IEnumerable<string> modelIds) => Match(Providers, modelIds);

    /// <inheritdoc cref="Match(IEnumerable{string})"/>
    public static PricingMatch Match(IReadOnlyList<ModelsDevProvider> providers, IEnumerable<string> modelIds)
    {
        var agreed = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new Dictionary<string, IReadOnlyList<ModelsDevPrice>>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<string>();

        foreach (var rawId in modelIds)
        {
            var id = rawId?.Trim();
            if (string.IsNullOrEmpty(id) || agreed.ContainsKey(id) || conflicts.ContainsKey(id)) continue;

            var found = providers
                .Where(provider => provider.Models.ContainsKey(id))
                .Select(provider => new ModelsDevPrice(provider.Key, provider.Name, provider.Models[id]))
                .ToList();

            switch (found.Count)
            {
                case 0:
                    unmatched.Add(id);
                    break;
                case 1:
                    agreed[id] = found[0].Pricing;
                    break;
                default:
                    // Value equality over the four rates: two providers quoting the
                    // same numbers are not a decision to push onto anyone.
                    if (found.Select(price => price.Pricing).Distinct().Count() == 1)
                        agreed[id] = found[0].Pricing;
                    else
                        conflicts[id] = found;
                    break;
            }
        }

        return new PricingMatch(agreed, conflicts, unmatched);
    }

    /// <summary>The catalogue provider an endpoint's host points at, used only to
    /// preselect the tie-breaker — "Test" against <c>openrouter.ai</c> is still
    /// OpenRouter, and that is nearly always the price its user is paying.</summary>
    public static string? GuessProviderKey(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        return uri.Host.ToLowerInvariant()
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(label =>
                label is not ("www" or "api" or "com" or "org" or "net" or "ai" or "io" or "cn" or "co"));
    }

    private IReadOnlyList<ModelsDevProvider>? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(CachePath) > CacheLifetime) return null;
            var parsed = Parse(File.ReadAllText(CachePath));
            return parsed.Count > 0 ? parsed : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, json);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Shape: <c>{ providerKey: { name, models: { modelId: { cost: {...} } } } }</c>.
    /// Models without a cost block are dropped rather than stored at zero — 417 of
    /// the ~7,800 entries have no published price, and calling those free would be
    /// a lie the rest of the pipeline has no way to detect.
    /// </summary>
    public static IReadOnlyList<ModelsDevProvider> Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return []; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            var providers = new List<ModelsDevProvider>();
            foreach (var provider in doc.RootElement.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object) continue;
                if (!provider.Value.TryGetProperty("models", out var models)
                    || models.ValueKind != JsonValueKind.Object) continue;

                var prices = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
                foreach (var model in models.EnumerateObject())
                {
                    if (ReadCost(model.Value) is { } pricing) prices[model.Name] = pricing;
                }
                if (prices.Count == 0) continue;

                var name = provider.Value.TryGetProperty("name", out var nameNode)
                           && nameNode.ValueKind == JsonValueKind.String
                    ? nameNode.GetString()!
                    : provider.Name;
                providers.Add(new ModelsDevProvider(provider.Name, name, prices));
            }
            return providers.OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static ModelPricing? ReadCost(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) return null;
        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object) return null;
        var input = ReadDouble(cost, "input");
        var output = ReadDouble(cost, "output");
        if (input is null || output is null) return null;
        return new ModelPricing(
            input.Value,
            output.Value,
            ReadDouble(cost, "cache_read"),
            ReadDouble(cost, "cache_write"),
            ModelPricing.SourceModelsDev);
    }

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number
        && node.TryGetDouble(out var value) && value >= 0
            ? value
            : null;
}
