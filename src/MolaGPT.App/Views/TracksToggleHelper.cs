using MolaGPT.Core.Auth;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// The personalized-memory master toggle lives in two places (the settings
/// account page and the memory center window) but syncs one server setting.
/// One helper so the optimistic-update + rollback behavior cannot drift apart.
/// Local-only while logged out: the toggle is meaningless there, but refusing
/// the tap would change long-standing behavior for no sync benefit.
/// </summary>
internal static class TracksToggleHelper
{
    public static async Task<string?> SyncAsync(
        bool value,
        SettingsViewModel settings,
        MolaGptAuthService? auth,
        CloudSyncService? sync)
    {
        if (auth is null || sync is null || string.IsNullOrEmpty(auth.CurrentJwt))
            return null;
        try
        {
            await sync.UpdateTracksSettingAsync(value).ConfigureAwait(true);
            return null;
        }
        catch (Exception ex)
        {
            settings.TracksEnabled = !value;
            return $"设置同步失败：{ex.Message}";
        }
    }
}
