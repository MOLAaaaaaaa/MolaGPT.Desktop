using MolaGPT.Core.Chat.LocalTools;

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
    PythonExecutionRiskAnalysis Risk);

public enum PythonExecutionApprovalDecision
{
    Denied,
    Approved
}
