using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.ViewModels;

public sealed partial class SettingsViewModel
{
    private static readonly TimeSpan AccountStatusFreshFor = TimeSpan.FromSeconds(30);
    private readonly MolaGptProxyProvider? _accountProvider;
    private long _accountStatusRequest;
    private (long Request, DateTimeOffset ReadAt, MolaGptStatus Status)? _accountStatus;

    public long AccountSessionGeneration { get; private set; } = -1;
    public bool IsLoggedIn { get; private set; }
    public string? MolaGptUsername { get; private set; }
    public MolaGptStatus? AccountStatus => _accountStatus?.Status;
    public bool CanReadAccountStatus => _accountProvider is not null;

    public void ApplyAccountSession(long generation, bool loggedIn, string? username)
    {
        if (generation <= AccountSessionGeneration) return;
        var previous = AccountSessionGeneration;
        var loginChanged = loggedIn != IsLoggedIn;
        var usernameChanged = username != MolaGptUsername;
        AccountSessionGeneration = generation;
        IsLoggedIn = loggedIn;
        MolaGptUsername = username;
        _accountStatus = null;
        if (previous >= 0 || !loggedIn) IsDonor = false;

        if (loginChanged) OnPropertyChanged(nameof(IsLoggedIn));
        if (usernameChanged) OnPropertyChanged(nameof(MolaGptUsername));
        OnPropertyChanged(nameof(AccountSessionGeneration));
        OnPropertyChanged(nameof(AccountStatus));
        RaiseSearchConfigurationChanged();
    }

    public async Task<MolaGptStatus?> RefreshAccountStatusAsync(bool force = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var generation = AccountSessionGeneration;
        if (!IsLoggedIn || _accountProvider is null || _accountProvider.AccountSessionGeneration != generation)
            return null;
        if (!force && _accountStatus is { } cached && DateTimeOffset.UtcNow - cached.ReadAt < AccountStatusFreshFor)
            return cached.Status;

        var request = ++_accountStatusRequest;
        var status = await _accountProvider.FetchStatusAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (generation != AccountSessionGeneration || generation != _accountProvider.AccountSessionGeneration)
            return null;
        if (_accountStatus is { } newer && newer.Request > request) return newer.Status;
        if (status is null) throw new InvalidOperationException("服务器未返回可用的账号状态。");

        _accountStatus = (request, DateTimeOffset.UtcNow, status);
        IsDonor = status.IsDonor;
        OnPropertyChanged(nameof(AccountStatus));
        return status;
    }
}
