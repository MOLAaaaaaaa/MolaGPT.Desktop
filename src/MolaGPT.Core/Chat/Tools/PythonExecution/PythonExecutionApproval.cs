using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

public interface IPythonExecutionApprovalService
{
    Task<PythonExecutionApprovalDecision> RequestApprovalAsync(
        PythonExecutionApprovalRequest request,
        CancellationToken ct);
}

/// <param name="RequestedPaths">Folders the model declared it needs to write to
/// that are not already writable — resolved to absolute paths, so the dialog
/// shows the folder that will actually be granted rather than the "~/Desktop"
/// the model wrote. Empty when the code declared nothing new, which is the
/// common case.</param>
public sealed record PythonExecutionApprovalRequest(
    string Code,
    string? Description,
    PythonExecutionOptions Options,
    PythonExecutionRiskAnalysis Risk,
    ToolCapability Capabilities,
    IReadOnlyList<string>? RequestedPaths = null);

public enum PythonExecutionApprovalDecision
{
    Denied,
    Approved
}
