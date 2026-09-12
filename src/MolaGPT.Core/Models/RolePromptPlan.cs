namespace MolaGPT.Core.Models;

public sealed record RolePromptMessage(string Role, string Text);
public sealed record RolePromptInsertion(string Source, string Role, int Depth, int Order, string Text);
public sealed record RolePromptPlan(
    IReadOnlyList<IReadOnlyList<RolePromptMessage>> Examples,
    IReadOnlyList<RolePromptInsertion> Insertions,
    int? ReserveOutputTokens,
    bool IsContinuation = false);
