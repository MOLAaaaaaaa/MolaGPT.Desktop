using System.Net;

namespace MolaGPT.Core.Net;

/// <summary>
/// The one proxy decision for the whole app, swappable while it runs.
///
/// The HTTP clients are singletons whose handlers outlive any settings change, so the
/// proxy cannot be baked into them. They are handed <see cref="General"/> or
/// <see cref="Official"/> instead: <c>IWebProxy</c> objects that look the current
/// <see cref="ProxyConfig"/> up on every request. The connection pool is keyed by the
/// proxy each request resolves to, so a change takes effect on the next request with
/// no restart, and a stream already in flight finishes where it started.
/// </summary>
public sealed class AppProxy
{
    private volatile Snapshot _snapshot = Snapshot.From(ProxyConfig.Default);

    public AppProxy()
    {
        General = new ScopedProxy(this, official: false);
        Official = new ScopedProxy(this, official: true);
    }

    /// <summary>Model providers, search, MCP, downloads — everything that is not the account's.</summary>
    public IWebProxy General { get; }

    /// <summary>The MolaGPT account's own traffic; honours <see cref="ProxyConfig.ApplyToOfficial"/>.</summary>
    public IWebProxy Official { get; }

    public ProxyConfig Config => _snapshot.Config;

    public void Apply(ProxyConfig config) => _snapshot = Snapshot.From(config);

    private sealed class Snapshot
    {
        public required ProxyConfig Config { get; init; }
        public Uri? Server { get; init; }
        public NetworkCredential? Credentials { get; init; }
        public ProxyBypassList Bypass { get; init; } = ProxyBypassList.Parse(null);

        public static Snapshot From(ProxyConfig config)
        {
            var hasServer = config.TryGetServerUri(out var server);
            var usable = hasServer && config.Mode == ProxyMode.Manual;
            return new Snapshot
            {
                Config = config,
                Server = usable ? server : null,
                Credentials = usable && config.Username.Length > 0
                    ? new NetworkCredential(config.Username, config.Password)
                    : null,
                Bypass = ProxyBypassList.Parse(config.Bypass)
            };
        }
    }

    private sealed class ScopedProxy(AppProxy owner, bool official) : IWebProxy, ICredentials
    {
        // SocketsHttpHandler captures this object when its pool is created.
        // Resolve credentials at the challenge so settings changes remain effective.
        public ICredentials? Credentials
        {
            get => this;
            set { }
        }

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            var snapshot = owner._snapshot;
            if (official && !snapshot.Config.ApplyToOfficial) return null;

            return snapshot.Config.Mode switch
            {
                ProxyMode.System => HttpClient.DefaultProxy.Credentials?.GetCredential(uri, authType),
                ProxyMode.Manual when snapshot.Server == uri => snapshot.Credentials,
                _ => null
            };
        }

        public bool IsBypassed(Uri host) => Select(host) is null;

        public Uri GetProxy(Uri destination) => Select(destination) ?? destination;

        /// <summary>The proxy to use for <paramref name="destination"/>, or null to go direct.</summary>
        private Uri? Select(Uri destination)
        {
            if (ProxyBypassList.IsLoopback(destination)) return null;

            var snapshot = owner._snapshot;
            var config = snapshot.Config;
            if (official && !config.ApplyToOfficial) return null;

            switch (config.Mode)
            {
                case ProxyMode.Manual:
                    return snapshot.Server is null || snapshot.Bypass.IsMatch(destination) ? null : snapshot.Server;
                case ProxyMode.System:
                    var system = HttpClient.DefaultProxy;
                    if (system.IsBypassed(destination)) return null;
                    var chosen = system.GetProxy(destination);
                    return chosen is null || chosen == destination ? null : chosen;
                default:
                    return null;
            }
        }
    }
}
