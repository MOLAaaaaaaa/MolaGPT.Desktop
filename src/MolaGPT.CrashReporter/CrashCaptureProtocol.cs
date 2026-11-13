namespace MolaGPT.CrashReporter;

// One JSON line in each direction over a per-session, current-user-only pipe.
public sealed record CrashCaptureRequest(uint ThreadId, ulong ExceptionPointers);

public sealed record CrashCaptureResult(bool ReportSaved, bool DumpCaptured, string? DumpError);
