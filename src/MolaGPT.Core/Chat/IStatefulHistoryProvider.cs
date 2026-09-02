namespace MolaGPT.Core.Chat;

/// <summary>
/// A provider that keeps the conversation history itself instead of being handed
/// it on every request.
///
/// The ordinary providers are stateless: whatever <see cref="ChatRequest.Messages"/>
/// says is the whole context, so regenerating an answer is just a matter of not
/// including it. A stateful provider ignores that list beyond the newest turn and
/// answers from its own transcript — which still contains the answer being
/// regenerated. Re-sending the prompt there produces a second turn on top of the
/// first, not a replacement: the model sees its own previous attempt (and every
/// tool result it collected) and says things like "我已经搜索过了".
///
/// So retry has to say so explicitly.
/// </summary>
public interface IStatefulHistoryProvider
{
    /// <summary>
    /// Drop the newest exchange (the last user turn and everything the provider
    /// produced for it) from <paramref name="conversationId"/>'s history, so the
    /// prompt that follows regenerates it instead of replying to it.
    ///
    /// Best-effort: returns false when there was nothing to forget. Callers should
    /// carry on either way — a retry that keeps the old turn is worse than the
    /// answer it replaces, but it is still an answer.
    /// </summary>
    Task<bool> ForgetLastTurnAsync(string? conversationId, CancellationToken ct = default);
}
