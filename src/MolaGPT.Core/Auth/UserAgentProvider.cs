using System.Reflection;
using System.Runtime.InteropServices;

namespace MolaGPT.Core.Auth;

/// <summary>
/// Stable client identification, retained for legacy UA-bound sessions.
/// This value and ClientMarker are public metadata, never authorization.
/// </summary>
public static class UserAgentProvider
{
    /// <summary>
    /// Recognizable, app-stable User-Agent. The legacy stack label is retained
    /// because the complete value participates in the account token hash.
    /// </summary>
    public const string FixedUa = "MolaGPT-Desktop (Windows; .NET 8 WPF)";

    /// <summary>
    /// Public client marker for protocol handling and diagnostics.
    /// </summary>
    public const string ClientMarker = "MolaGPT-Desktop";
}
