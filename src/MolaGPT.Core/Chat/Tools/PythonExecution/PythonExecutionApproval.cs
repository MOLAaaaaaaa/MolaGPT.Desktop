using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

public interface IPythonExecutionApprovalService
{
    Task<PythonExecutionApprovalDecision> RequestApprovalAsync(
        PythonExecutionApprovalRequest request,
        CancellationToken ct);
}

public sealed record PythonExecutionApprovalRequest(
    string Code,
    string? Description,
    PythonExecutionOptions Options,
    PythonExecutionRiskAnalysis Risk,
    ToolCapability Capabilities);

public enum PythonExecutionApprovalDecision
{
    Denied,
    Approved
}
