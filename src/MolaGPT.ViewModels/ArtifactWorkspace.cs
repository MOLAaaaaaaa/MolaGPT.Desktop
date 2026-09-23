using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using MolaGPT.Presentation.Artifacts;

namespace MolaGPT.ViewModels;

/// <summary>A canvas-bound fence as the transcript found it.</summary>
public sealed record FenceSnapshot(
    int Ordinal,
    string Key,
    string Language,
    ArtifactRenderKind Kind,
    string Code,
    int LineCount,
    bool IsComplete);

public sealed class LiveArtifactEventArgs(ArtifactItemViewModel item, bool isNewFence) : EventArgs
{
    public ArtifactItemViewModel Item { get; } = item;
    /// <summary>This fence just started in the live answer — a new artifact, or
    /// a new version of one. False for later updates to the same fence.</summary>
    public bool IsNewFence { get; } = isNewFence;
}

/// <summary>
/// The conversation's artifacts: fences promoted from answers plus files in
/// the working directory.
///
/// Fences are not found by re-reading messages. The transcript already parses
/// every answer incrementally to render it, and publishes what it found here
/// per message — so the list, the transcript chips and the canvas agree by
/// construction, and a streaming delta costs a comparison instead of a parse
/// of the whole conversation.
///
/// Item instances survive updates. Whoever is holding one (the canvas, the
/// list selection) sees its versions change in place; only a logical artifact
/// that disappears entirely drops out.
/// </summary>
public sealed class ArtifactWorkspace
{
    private readonly Func<IReadOnlyList<MessageViewModel>> _messages;
    private readonly Dictionary<MessageViewModel, IReadOnlyList<FenceSnapshot>> _fences = new();
    private readonly Dictionary<string, ArtifactItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly HashSet<(MessageViewModel, int)> _seen = new();
    private readonly List<ArtifactItemViewModel> _physical = new();
    private readonly ConditionalWeakTable<MessageViewModel, string> _localIds = new();

    public ArtifactWorkspace(Func<IReadOnlyList<MessageViewModel>> messages) => _messages = messages;

    public ObservableCollection<ArtifactItemViewModel> Items { get; } = new();

    public bool HasPhysical => _physical.Count > 0;

    /// <summary>The list changed shape (items added, removed or reordered).</summary>
    public event EventHandler? Changed;

    /// <summary>A fence in a message that is still being written appeared or
    /// changed. Drives the canvas's auto-open.</summary>
    public event EventHandler<LiveArtifactEventArgs>? LiveArtifactChanged;

    public void PublishFences(MessageViewModel message, IReadOnlyList<FenceSnapshot> fences, bool live)
    {
        if (_fences.TryGetValue(message, out var previous) && SameFences(previous, fences)) return;
        if (fences.Count == 0 && previous is null) return;

        if (fences.Count == 0) _fences.Remove(message);
        else _fences[message] = fences;

        var isNewFence = false;
        foreach (var fence in fences)
            isNewFence = _seen.Add((message, fence.Ordinal));

        Regroup();

        if (!live || fences.Count == 0) return;

        // The fence the model is writing now is the last one in the message.
        var last = fences[^1];
        var item = FindItem(message, last.Ordinal);
        if (item is null) return;
        LiveArtifactChanged?.Invoke(this, new LiveArtifactEventArgs(item, isNewFence));
    }

    public void RemoveMessage(MessageViewModel message)
    {
        _seen.RemoveWhere(k => ReferenceEquals(k.Item1, message));
        if (_fences.Remove(message)) Regroup();
    }

    /// <summary>The transcript was rebuilt from scratch (conversation switch or a
    /// replaced tail). Everything it knows will be published again.</summary>
    public void ClearFences()
    {
        _seen.Clear();
        if (_fences.Count == 0) return;
        _fences.Clear();
        Regroup();
    }

    public void SetPhysical(IEnumerable<MolaGPT.Core.Chat.Tools.PythonExecution.WorkspaceArtifact> files)
    {
        var next = new List<ArtifactItemViewModel>();
        foreach (var file in files)
        {
            // Keep the selected instance when a later python run overwrites the file.
            var existing = _physical.FirstOrDefault(p =>
                string.Equals(p.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase));
            existing?.UpdatePhysical(file);
            next.Add(existing ?? new ArtifactItemViewModel(file));
        }

        if (next.SequenceEqual(_physical)) return;
        _physical.Clear();
        _physical.AddRange(next);
        Publish();
    }

    public ArtifactItemViewModel? FindItem(MessageViewModel message, int ordinal)
    {
        foreach (var item in _items.Values)
        {
            if (item.IndexOfVersion(message, ordinal) >= 0) return item;
        }

        return null;
    }

    // ---- grouping ----------------------------------------------------------

    private void Regroup()
    {
        var groups = new Dictionary<string, List<ArtifactVersion>>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, (ArtifactRenderKind Kind, string? Name)>(StringComparer.Ordinal);
        var lastSeenAt = new Dictionary<string, int>(StringComparer.Ordinal);

        var messages = _messages();
        for (var m = 0; m < messages.Count; m++)
        {
            var message = messages[m];
            if (!_fences.TryGetValue(message, out var fences)) continue;
            foreach (var fence in fences)
            {
                var name = FenceArtifactCapture.ParseFileName(fence.Code);
                var id = name is not null
                    ? $"name:{fence.Kind}:{name.ToLowerInvariant()}"
                    : $"fence:{LocalId(message)}:{fence.Ordinal}";

                if (!groups.TryGetValue(id, out var versions))
                {
                    versions = new List<ArtifactVersion>();
                    groups[id] = versions;
                    kinds[id] = (fence.Kind, name);
                }

                versions.Add(new ArtifactVersion
                {
                    Message = message,
                    Ordinal = fence.Ordinal,
                    Key = fence.Key,
                    Language = fence.Language,
                    Content = fence.Code,
                    LineCount = fence.LineCount,
                    IsComplete = fence.IsComplete,
                });
                lastSeenAt[id] = m;
            }
        }

        foreach (var stale in _items.Keys.Where(k => !groups.ContainsKey(k)).ToList())
            _items.Remove(stale);

        foreach (var (id, versions) in groups)
        {
            if (!_items.TryGetValue(id, out var item))
            {
                item = new ArtifactItemViewModel(id, kinds[id].Kind, kinds[id].Name);
                _items[id] = item;
            }

            var latest = versions[^1];
            var title = kinds[id].Name ?? FenceArtifactCapture.DisplayTitle(latest.Content, kinds[id].Kind);
            item.SetVersions(versions, title);
        }

        Publish(lastSeenAt);
    }

    private IReadOnlyDictionary<string, int>? _lastOrder;

    private void Publish(IReadOnlyDictionary<string, int>? order = null)
    {
        if (order is not null) _lastOrder = order;
        var positions = _lastOrder ?? new Dictionary<string, int>();

        // Newest answer first: the thing just made is the thing most likely wanted.
        var fences = _items.Values
            .OrderByDescending(i => positions.TryGetValue(i.Id, out var p) ? p : -1)
            .ThenBy(i => i.Id, StringComparer.Ordinal);
        DisambiguateTitles(fences);

        var next = fences.Concat(_physical).ToList();
        if (next.SequenceEqual(Items)) return;

        Items.Clear();
        foreach (var item in next) Items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Untitled fences all derive the same title ("网页"); number them
    /// so the list and the canvas header can tell them apart.</summary>
    private static void DisambiguateTitles(IEnumerable<ArtifactItemViewModel> items)
    {
        var groups = items.Where(i => i.DeclaredName is null).GroupBy(i => i.Title).Where(g => g.Count() > 1);
        foreach (var group in groups)
        {
            var ordered = group.Reverse().ToList();
            for (var i = 1; i < ordered.Count; i++)
                ordered[i].Title = $"{group.Key} ({i + 1})";
        }
    }

    private string LocalId(MessageViewModel message) =>
        _localIds.GetValue(message, _ => Guid.NewGuid().ToString("N")[..10]);

    private static bool SameFences(IReadOnlyList<FenceSnapshot> a, IReadOnlyList<FenceSnapshot> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Ordinal != b[i].Ordinal
                || a[i].IsComplete != b[i].IsComplete
                || !string.Equals(a[i].Key, b[i].Key, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

public enum ArtifactAction
{
    Open,
    Revise,
}

public sealed class ArtifactActionEventArgs(ArtifactItemViewModel item, int versionIndex, ArtifactAction action) : EventArgs
{
    public ArtifactItemViewModel Item { get; } = item;
    public int VersionIndex { get; } = versionIndex;
    public ArtifactAction Action { get; } = action;
}
