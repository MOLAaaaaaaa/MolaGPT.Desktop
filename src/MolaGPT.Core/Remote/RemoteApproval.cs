namespace MolaGPT.Core.Remote;

/// <summary>
/// Which conversation the tool call now asking for approval belongs to.
///
/// Set where a tool call enters the tool host and read where approval is asked
/// for, without threading a conversation id through every request type in
/// between. Flows with the call across awaits and nowhere else.
/// </summary>
public static class ApprovalScope
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? ConversationId => Current.Value;

    public static IDisposable Enter(string? conversationId)
    {
        var previous = Current.Value;
        Current.Value = conversationId;
        return new Exit(previous);
    }

    private sealed class Exit(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public static class RemoteApprovalDecisions
{
    public const string Once = "once";
    public const string Session = "session";
    public const string Deny = "deny";
}

/// <summary>An approval as a phone shows it. Only what is needed to decide: the
/// full arguments stay on the desktop.</summary>
public sealed record RemoteApprovalPrompt(
    string RequestId,
    string ConversationId,
    string Kind,
    string ToolName,
    string DisplayName,
    string? Summary,
    string? ArgumentsPreview,
    string? CodePreview,
    string? ResolvedPath,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Options,
    long CreatedAtMs);

/// <summary>
/// One approval offered to paired phones. Whichever side answers first decides;
/// the other is told it is settled.
/// </summary>
public interface IRemoteApprovalTicket
{
    /// <summary>The phone's decision, one of <see cref="RemoteApprovalDecisions"/>.</summary>
    Task<string> Answer { get; }

    /// <summary>The desktop dialog answered first.</summary>
    void SettleOnDesktop(string decision);

    /// <summary>Nobody needs the answer any more: the turn stopped.</summary>
    void Abandon();
}

/// <summary>Where the approval service offers a request to phones.</summary>
public interface IRemoteApprovalChannel
{
    /// <summary>
    /// Offer <paramref name="prompt"/> to paired phones, or return null when no
    /// phone should be asked: remote control is off, nothing is paired, or nobody
    /// on a phone has reason to be watching this conversation.
    /// </summary>
    IRemoteApprovalTicket? Offer(RemoteApprovalPrompt prompt, bool desktopVisible);
}
