namespace MolaGPT.Desktop.Services;

public sealed class AppStatusService
{
    public event EventHandler<AppStatusEvent>? StatusChanged;

    public void Publish(string kind, string message)
    {
        StatusChanged?.Invoke(this, new AppStatusEvent(
            string.IsNullOrWhiteSpace(kind) ? "Idle" : kind,
            string.IsNullOrWhiteSpace(message) ? "MolaGPT" : message,
            DateTimeOffset.Now));
    }
}

public sealed record AppStatusEvent(string Kind, string Message, DateTimeOffset Timestamp);
