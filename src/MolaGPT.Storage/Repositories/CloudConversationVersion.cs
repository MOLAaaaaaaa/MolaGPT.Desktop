namespace MolaGPT.Storage.Repositories;

[Flags]
public enum CloudChanges
{
    None = 0,
    Metadata = 1,
    Messages = 2,
    AwaitingConfirmation = 4
}

public sealed record CloudConversationVersion(
    ConversationRow Row, string Revision, CloudChanges Changes, string? SyncTimestamp);
