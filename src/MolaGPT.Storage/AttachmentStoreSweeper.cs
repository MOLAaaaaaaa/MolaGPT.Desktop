using System.Text.Json;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.Storage;

/// <summary>
/// Removes attachment blobs no message references any more.
///
/// The store is append-only during normal use — deleting a conversation is a
/// soft delete with an undo window, so nothing can be reclaimed at that moment.
/// A sweep at startup is where it becomes safe: by then the undo window is long
/// gone and the surviving message rows are the complete set of live references.
///
/// Everything here is a copy of a file the user still has, so a mistaken delete
/// costs a re-attach, not data.
/// </summary>
public static class AttachmentStoreSweeper
{
    /// <summary>
    /// Deletes stored blobs that no message meta references. Returns the number
    /// of files removed. Never throws: a failed sweep must not stop startup.
    /// </summary>
    public static int Sweep(AttachmentStore store, MessageRepository messages)
    {
        try
        {
            var stored = store.EnumerateStoredNames();
            if (stored.Count == 0) return 0;

            var referenced = CollectReferencedNames(messages);

            // A store with files but no references at all is more likely a broken
            // read (locked DB, failed migration) than a genuinely empty history.
            // Refusing to sweep in that case trades disk for safety.
            if (referenced.Count == 0) return 0;

            var orphans = stored.Where(name => !referenced.Contains(name)).ToList();
            if (orphans.Count == 0) return 0;

            store.Delete(orphans);
            return orphans.Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static HashSet<string> CollectReferencedNames(MessageRepository messages)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in messages.ListAttachmentMetas())
        {
            if (string.IsNullOrWhiteSpace(meta)) continue;
            try
            {
                using var doc = JsonDocument.Parse(meta);
                if (!doc.RootElement.TryGetProperty("attachments", out var attachments)
                    || attachments.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var attachment in attachments.EnumerateArray())
                {
                    if (attachment.ValueKind != JsonValueKind.Object) continue;
                    if (attachment.TryGetProperty("localName", out var localName)
                        && localName.ValueKind == JsonValueKind.String
                        && localName.GetString() is { Length: > 0 } name)
                    {
                        referenced.Add(name);
                    }
                }
            }
            catch (JsonException)
            {
                // Unparseable meta: skip the row rather than risk treating its
                // attachments as unreferenced.
            }
        }
        return referenced;
    }
}
