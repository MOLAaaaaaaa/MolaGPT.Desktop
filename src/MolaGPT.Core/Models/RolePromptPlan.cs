namespace MolaGPT.Core.Models;

public sealed record RolePromptMessage(string Role, string Text);
public sealed record RolePromptInsertion(string Source, string Role, int Depth, int Order, string Text);

/// <param name="Before">Messages between the system prompt and the conversation, in
/// order. The example dialogue goes in at <paramref name="ExamplesAt"/>.</param>
/// <param name="After">Messages after the conversation, in order, following every
/// insertion at depth 0.</param>
public sealed record RolePromptPlan(
    IReadOnlyList<IReadOnlyList<RolePromptMessage>> Examples,
    IReadOnlyList<RolePromptInsertion> Insertions,
    int? ReserveOutputTokens,
    bool IsContinuation = false,
    IReadOnlyList<RolePromptMessage>? Before = null,
    int ExamplesAt = 0,
    IReadOnlyList<RolePromptMessage>? After = null);
