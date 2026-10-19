using MolaGPT.Core.Remote;

namespace MolaGPT.Desktop.Services.Remote;

/// <summary>
/// The approval service's handle on remote control. The approval service exists
/// long before remote control can (it is built with the tool host, which the chat
/// view models depend on), so it holds this and remote control plugs in later.
/// </summary>
public sealed class RemoteApprovalRelay : IRemoteApprovalChannel
{
    private volatile IRemoteApprovalChannel? _target;

    public void Attach(IRemoteApprovalChannel? target) => _target = target;

    public IRemoteApprovalTicket? Offer(RemoteApprovalPrompt prompt, bool desktopVisible) =>
        _target?.Offer(prompt, desktopVisible);
}
