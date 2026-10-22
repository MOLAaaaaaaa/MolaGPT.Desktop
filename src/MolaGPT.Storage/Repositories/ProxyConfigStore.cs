using System.Globalization;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Net;

namespace MolaGPT.Storage.Repositories;

/// <summary>
/// 网络代理 in the settings table, with the password in the credential store rather
/// than beside the host it belongs to. Read at startup before any view model exists,
/// which is why it is not part of <c>SettingsViewModel</c>.
/// </summary>
public static class ProxyConfigStore
{
    private const string ModeKey = "network_proxy_mode";
    private const string SchemeKey = "network_proxy_scheme";
    private const string HostKey = "network_proxy_host";
    private const string PortKey = "network_proxy_port";
    private const string UsernameKey = "network_proxy_username";
    private const string BypassKey = "network_proxy_bypass";
    private const string OfficialKey = "network_proxy_official";
    private const string PasswordSecret = "network_proxy_password";

    /// <summary>What the manual form shows before anything was typed. Clash and v2rayN
    /// both listen here, so for most people it is already right.</summary>
    public const string SuggestedHost = "127.0.0.1";
    public const int SuggestedPort = 7890;

    public static ProxyConfig Load(SettingsRepository settings, CredentialStore? credentials)
    {
        return new ProxyConfig
        {
            Mode = Enum.TryParse<ProxyMode>(settings.Get(ModeKey), true, out var mode) && Enum.IsDefined(mode)
                ? mode
                : ProxyMode.System,
            Scheme = Enum.TryParse<ProxyScheme>(settings.Get(SchemeKey), true, out var scheme) && Enum.IsDefined(scheme)
                ? scheme
                : ProxyScheme.Http,
            Host = settings.Get(HostKey) ?? SuggestedHost,
            Port = int.TryParse(settings.Get(PortKey), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                ? port
                : SuggestedPort,
            Username = settings.Get(UsernameKey) ?? string.Empty,
            Password = credentials?.LoadSecret(PasswordSecret) ?? string.Empty,
            // An empty row is a deliberate "no exceptions"; only an absent one means untouched.
            Bypass = settings.Get(BypassKey) ?? ProxyConfig.DefaultBypass,
            ApplyToOfficial = !bool.TryParse(settings.Get(OfficialKey), out var official) || official
        };
    }

    public static void Save(SettingsRepository settings, CredentialStore? credentials, ProxyConfig config)
    {
        settings.Set(ModeKey, config.Mode.ToString());
        settings.Set(SchemeKey, config.Scheme.ToString());
        settings.Set(HostKey, config.Host);
        settings.Set(PortKey, config.Port.ToString(CultureInfo.InvariantCulture));
        settings.Set(UsernameKey, config.Username);
        settings.Set(BypassKey, config.Bypass);
        settings.Set(OfficialKey, config.ApplyToOfficial.ToString());

        if (credentials is null) return;
        if (config.Password.Length == 0)
            credentials.RemoveSecret(PasswordSecret);
        else
            credentials.SaveSecret(PasswordSecret, config.Password);
    }
}
