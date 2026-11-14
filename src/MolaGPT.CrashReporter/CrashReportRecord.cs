namespace MolaGPT.CrashReporter;

public sealed record CrashReportRecord
{
    public int SchemaVersion { get; init; } = 2;
    public required string ReportId { get; init; }
    public required string SessionId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string AppName { get; init; }
    public required string AppVersion { get; init; }
    public required int ProcessId { get; init; }
    public required string Source { get; init; }
    public string? ExceptionType { get; init; }
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    public bool CaptureMinidump { get; init; }
    public bool IncludeApplicationLog { get; init; }
    public string? LogDirectory { get; init; }
    public string? MinidumpFileName { get; init; }
    public string? MinidumpError { get; init; }
    public uint? FaultingThreadId { get; init; }
    public uint? NativeExceptionCode { get; init; }
    public ulong? NativeExceptionAddress { get; init; }
    public int? ExitCode { get; init; }
    public required string Runtime { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
}
