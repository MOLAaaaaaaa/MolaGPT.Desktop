using System.Text;
using MolaGPT.Core.Models;

namespace MolaGPT.ViewModels;

/// <summary>
/// One user turn as the transcript's turn rail lists it. <see cref="Message"/> is
/// null while the turn is still parked history (see <see cref="ChatViewModel.MaterializeTurn"/>).
/// </summary>
public sealed record ChatTurn(string Preview, MessageViewModel? Message);

public sealed partial class ChatViewModel
{
    /// <summary>How much of a prompt the preview reads. A pasted document can run
    /// to megabytes; the rail only ever shows a few lines of it.</summary>
    private const int TurnPreviewScan = 400;

    /// <summary>
    /// Every user turn of the conversation, oldest first — the parked history
    /// included, so a long conversation's outline is whole from the moment it
    /// opens rather than growing as the user scrolls up.
    /// </summary>
    public IReadOnlyList<ChatTurn> GetTurns()
    {
        var turns = new List<ChatTurn>();
        foreach (var parked in _pendingOlderMessages)
        {
            if (IsTurn(parked.Role, parked.Content))
                turns.Add(new ChatTurn(TurnPreview(parked.Content, parked.Attachments), null));
        }
        foreach (var message in _messages)
        {
            if (IsTurn(message))
                turns.Add(new ChatTurn(TurnPreview(message.Content, message.Attachments), message));
        }
        return turns;
    }

    /// <summary>
    /// The message that opens turn <paramref name="index"/>, materializing the
    /// parked history down to it first.
    /// </summary>
    /// <remarks>
    /// Published as one Reset rather than through <see cref="LoadOlderMessages"/>:
    /// that path inserts message by message, and the transcript pays per inserted
    /// row, which is fine for a page of twenty and not for a jump to the first
    /// turn of a long conversation. The caller is about to move the viewport
    /// anyway, so the scroll anchor a Reset loses is not missed.
    /// </remarks>
    public MessageViewModel? MaterializeTurn(int index)
    {
        if (index < 0) return null;

        var seen = 0;
        for (var i = 0; i < _pendingOlderMessages.Count; i++)
        {
            var parked = _pendingOlderMessages[i];
            if (!IsTurn(parked.Role, parked.Content) || seen++ != index) continue;

            var older = new List<MessageViewModel>(_pendingOlderMessages.Count - i);
            for (var j = i; j < _pendingOlderMessages.Count; j++)
                older.Add(CreateMessageViewModel(_pendingOlderMessages[j]));
            _pendingOlderMessages.RemoveRange(i, _pendingOlderMessages.Count - i);

            // ReplaceAll clears before it enumerates, so the current list is
            // copied out first.
            var current = _messages.ToList();
            _messages.ReplaceAll(older.Concat(current));
            ApplyTaskStates();
            OnPropertyChanged(nameof(HasOlderMessages));
            return older[0];
        }

        index -= seen;
        foreach (var message in _messages)
        {
            if (IsTurn(message) && index-- == 0) return message;
        }
        return null;
    }

    private static bool IsTurn(MessageViewModel message) =>
        string.Equals(message.Role, ChatMessage.RoleUser, StringComparison.OrdinalIgnoreCase)
        && !message.IsTaskNotification;

    /// <summary>A background task's notification is a user-role message the user
    /// never typed; the transcript draws it as a notice line, not a turn.</summary>
    private static bool IsTurn(string role, string content) =>
        string.Equals(role, ChatMessage.RoleUser, StringComparison.OrdinalIgnoreCase)
        && TaskNotice.TryParse(content) is null;

    private static string TurnPreview(string? content, IReadOnlyList<AttachmentChip>? attachments)
    {
        var text = content ?? string.Empty;
        if (text.Length > TurnPreviewScan) text = text[..TurnPreviewScan];
        text = MessageViewModel.StripSystemHints(text);

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        if (builder.Length > 0) return builder.ToString();

        return attachments is { Count: > 0 }
            ? string.Join("、", attachments.Select(a => a.FileName))
            : string.Empty;
    }
}
