using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Presentation.Artifacts;

namespace MolaGPT.ViewModels;

public enum ArtifactItemKind
{
    /// <summary>A file in the conversation's working directory.</summary>
    Physical,
    /// <summary>A fenced block the answer produced. Lives in the message, never on disk.</summary>
    Fence,
}

/// <summary>One revision of a fence artifact: a code block in one message.</summary>
public sealed class ArtifactVersion
{
    public required MessageViewModel Message { get; init; }
    /// <summary>Position among the message's artifact fences. With the message,
    /// this is what the transcript chip and the list agree on.</summary>
    public required int Ordinal { get; init; }
    /// <summary>Content identity from the parser; changes as a fence streams.</summary>
    public required string Key { get; init; }
    public required string Language { get; init; }
    public required string Content { get; init; }
    public required int LineCount { get; init; }
    /// <summary>The closing fence has arrived.</summary>
    public required bool IsComplete { get; init; }

    private long? _bytes;

    // Read by several list bindings per notification; the content never changes.
    public long Bytes => _bytes ??= Encoding.UTF8.GetByteCount(Content);
}

/// <summary>
/// One entry in the session's artifact list.
///
/// A fence artifact is a logical thing with versions: every block that declares
/// the same file name on its first line (<c>&lt;!-- solar.html --&gt;</c>) is a
/// revision of it, so "基于此修改" produces v2 of the same entry instead of a
/// second one. Instances are kept across updates — the canvas holds on to the
/// selected one while the next answer streams — and only their versions change.
/// </summary>
public sealed partial class ArtifactItemViewModel : ObservableObject
{
    private IReadOnlyList<ArtifactVersion> _versions = Array.Empty<ArtifactVersion>();

    public ArtifactItemViewModel(MolaGPT.Core.Chat.Tools.PythonExecution.WorkspaceArtifact artifact)
    {
        Kind = ArtifactItemKind.Physical;
        Id = "file:" + artifact.FullPath;
        _title = artifact.Name;
        RelativePath = artifact.RelativePath;
        FullPath = artifact.FullPath;
        _physicalBytes = artifact.Bytes;
        _physicalLastWriteUtc = artifact.LastWriteUtc;
        IsImage = artifact.IsImage;
        var extension = Path.GetExtension(artifact.Name);
        RenderKind = FenceArtifactCapture.MapExtension(extension);
        Extension = extension.TrimStart('.').ToUpperInvariant();
        IsText = IsTextExtension(extension);
        Language = extension.TrimStart('.').ToLowerInvariant();
    }

    public ArtifactItemViewModel(string id, ArtifactRenderKind kind, string? declaredName)
    {
        Kind = ArtifactItemKind.Fence;
        Id = id;
        RenderKind = kind;
        DeclaredName = declaredName;
        _title = declaredName ?? FenceArtifactCapture.KindLabel(kind);
        RelativePath = _title;
        Extension = FenceArtifactCapture.KindBadge(kind);
        IsText = true;
    }

    public string Id { get; }
    public ArtifactItemKind Kind { get; }
    public ArtifactRenderKind RenderKind { get; }
    public bool IsFence => Kind == ArtifactItemKind.Fence;

    /// <summary>The file name the model declared; the grouping key for versions.</summary>
    public string? DeclaredName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string _title;

    /// <summary>Kept for the list template, which predates versions.</summary>
    public string FileName => Title;

    public string RelativePath { get; }
    public string? FullPath { get; }
    public bool IsImage { get; }
    public string? ImagePreviewPath => IsImage ? FullPath : null;
    public string Extension { get; }
    public bool IsText { get; }
    private long _physicalBytes;
    private DateTime _physicalLastWriteUtc;

    public string? Language { get; }

    // ---- versions --------------------------------------------------------

    public IReadOnlyList<ArtifactVersion> Versions => _versions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentVersion))]
    [NotifyPropertyChangedFor(nameof(Content))]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    [NotifyPropertyChangedFor(nameof(VersionLabel))]
    [NotifyPropertyChangedFor(nameof(CanSelectPreviousVersion))]
    [NotifyPropertyChangedFor(nameof(CanSelectNextVersion))]
    [NotifyPropertyChangedFor(nameof(RenderToken))]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    [NotifyPropertyChangedFor(nameof(FenceLanguage))]
    private int _currentVersionIndex = -1;

    public ArtifactVersion? CurrentVersion =>
        CurrentVersionIndex >= 0 && CurrentVersionIndex < _versions.Count ? _versions[CurrentVersionIndex] : null;

    public ArtifactVersion? LatestVersion => _versions.Count > 0 ? _versions[^1] : null;

    public bool HasVersions => _versions.Count > 1;
    public string VersionLabel => HasVersions ? $"v{CurrentVersionIndex + 1}/{_versions.Count}" : string.Empty;
    public string LatestVersionLabel => HasVersions ? $"v{_versions.Count}" : string.Empty;
    public bool CanSelectPreviousVersion => CurrentVersionIndex > 0;
    public bool CanSelectNextVersion => CurrentVersionIndex >= 0 && CurrentVersionIndex < _versions.Count - 1;

    /// <summary>Fence source of the version on show. Physical files are read by
    /// the canvas on demand, so this is null for them.</summary>
    public string? Content => CurrentVersion?.Content;

    public string? FenceLanguage => CurrentVersion?.Language;

    public bool IsComplete => Kind == ArtifactItemKind.Physical || CurrentVersion?.IsComplete != false;

    /// <summary>Changes exactly when what the canvas would draw changes.</summary>
    public string RenderToken => Kind == ArtifactItemKind.Physical
        ? $"{Id}|{_physicalBytes}|{_physicalLastWriteUtc.Ticks}"
        : $"{Id}|{CurrentVersionIndex}|{CurrentVersion?.Key}|{CurrentVersion?.IsComplete}";

    public long Bytes => Kind == ArtifactItemKind.Physical ? _physicalBytes : CurrentVersion?.Bytes ?? 0;

    public string SizeLabel => Kind == ArtifactItemKind.Fence && CurrentVersion is { IsComplete: false } live
        ? $"生成中 · {live.LineCount} 行"
        : FormatSize(Bytes);

    /// <summary>Row subtitle in the list: size, plus the version count.</summary>
    public string ListSubtitle => HasVersions ? $"{LatestVersionLabel} · {FormatSize(LatestVersion?.Bytes ?? 0)}" : SizeLabel;

    public bool CanReveal => Kind == ArtifactItemKind.Physical && !string.IsNullOrEmpty(FullPath);
    public bool CanCopy => Kind == ArtifactItemKind.Fence || IsText;
    public bool CanShowSource => CanCopy;
    public bool CanRevise => Kind == ArtifactItemKind.Fence || IsText;

    public string ToolTip => Kind == ArtifactItemKind.Physical
        ? $"{Title}\n{FormatSize(_physicalBytes)}\n{FullPath}"
        : $"{Title}\n{FenceArtifactCapture.KindLabel(RenderKind)}{(HasVersions ? $" · {_versions.Count} 个版本" : string.Empty)}";

    internal void UpdatePhysical(MolaGPT.Core.Chat.Tools.PythonExecution.WorkspaceArtifact artifact)
    {
        if (_physicalBytes == artifact.Bytes && _physicalLastWriteUtc == artifact.LastWriteUtc) return;

        _physicalBytes = artifact.Bytes;
        _physicalLastWriteUtc = artifact.LastWriteUtc;
        OnPropertyChanged(nameof(RenderToken));
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(ListSubtitle));
        OnPropertyChanged(nameof(ToolTip));
    }

    /// <summary>
    /// Replaces the version list. A viewer parked on the newest version follows
    /// new ones as they arrive; one who stepped back to an older version stays
    /// there.
    /// </summary>
    internal void SetVersions(IReadOnlyList<ArtifactVersion> versions, string title)
    {
        // Every regroup hands every item a fresh list, but while one page
        // streams only that item's versions actually change. The rest stay
        // quiet: each notification here re-reads the list row's bindings.
        if (string.Equals(title, Title, StringComparison.Ordinal) && SameVersions(versions)) return;

        var wasOnLatest = CurrentVersionIndex < 0 || CurrentVersionIndex >= _versions.Count - 1;
        var countChanged = versions.Count != _versions.Count;
        _versions = versions;
        Title = title;

        var target = wasOnLatest ? versions.Count - 1 : Math.Min(CurrentVersionIndex, versions.Count - 1);
        if (target != CurrentVersionIndex)
        {
            CurrentVersionIndex = target;
        }
        else
        {
            // Same slot, new content (a streaming fence grew).
            OnPropertyChanged(nameof(CurrentVersion));
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(RenderToken));
            OnPropertyChanged(nameof(SizeLabel));
            OnPropertyChanged(nameof(FenceLanguage));
        }

        if (countChanged)
        {
            OnPropertyChanged(nameof(Versions));
            OnPropertyChanged(nameof(HasVersions));
            OnPropertyChanged(nameof(VersionLabel));
            OnPropertyChanged(nameof(LatestVersionLabel));
            OnPropertyChanged(nameof(CanSelectPreviousVersion));
            OnPropertyChanged(nameof(CanSelectNextVersion));
            OnPropertyChanged(nameof(ToolTip));
        }

        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(ListSubtitle));
    }

    private bool SameVersions(IReadOnlyList<ArtifactVersion> versions)
    {
        if (versions.Count != _versions.Count) return false;
        for (var i = 0; i < versions.Count; i++)
        {
            var (a, b) = (versions[i], _versions[i]);
            if (!ReferenceEquals(a.Message, b.Message) || a.Ordinal != b.Ordinal || a.IsComplete != b.IsComplete
                || !string.Equals(a.Key, b.Key, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public int IndexOfVersion(MessageViewModel message, int ordinal)
    {
        for (var i = 0; i < _versions.Count; i++)
        {
            if (ReferenceEquals(_versions[i].Message, message) && _versions[i].Ordinal == ordinal) return i;
        }

        return -1;
    }

    public void SelectVersion(int index)
    {
        if (index >= 0 && index < _versions.Count) CurrentVersionIndex = index;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:0.#} KB";
        var mb = kb / 1024d;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024d:0.#} GB";
    }

    private static bool IsTextExtension(string extension) => extension.ToLowerInvariant() is
        ".txt" or ".md" or ".markdown" or ".json" or ".csv" or ".tsv" or
        ".html" or ".htm" or ".svg" or ".mmd" or ".mermaid" or ".xml" or
        ".css" or ".js" or ".ts" or ".tsx" or ".jsx" or ".cs" or ".py" or
        ".java" or ".go" or ".rs" or ".sql" or ".yaml" or ".yml" or ".toml" or ".log";
}
