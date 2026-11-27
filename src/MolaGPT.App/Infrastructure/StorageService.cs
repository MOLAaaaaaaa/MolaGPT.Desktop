using Avalonia.Threading;
using MolaGPT.App.Rendering.Canvas;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Disk;
using MolaGPT.CrashReporter;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

internal sealed record StorageCategory(string Key, string Title, long Bytes, IReadOnlyList<string> Paths);
internal sealed record ConversationWorkspace(string ConversationId, string Title, string Path, long Bytes, long UpdatedAt);
internal sealed record StorageSnapshot(long TotalBytes, long RuntimeBytes, long DatabaseBytes, long MediaBytes, long OtherBytes,
    IReadOnlyList<StorageCategory> Categories, IReadOnlyList<ConversationWorkspace> Workspaces,
    int DeletedCount, PythonRuntimeStorageUsage PythonUsage);
internal sealed record PurgeResult(int Removed, int Skipped, DiskDeleteResult Files, string? CompactError);

internal sealed class StorageService(
    MolaGptDatabase database,
    ConversationRepository conversations,
    MessageRepository messages,
    PythonRuntimeManager python,
    PiSidecarRuntimeManager pi,
    CloudSyncService cloud,
    ComposerViewModel composer,
    AgentTaskRegistry tasks,
    ImageWorkbenchTasks imageTasks,
    SettingsRepository settings,
    string? dataRoot = null,
    string? runtimeRoot = null)
{
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly string DataRoot = dataRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT");
    private readonly string RuntimeRoot = runtimeRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT Desktop");
    private string SessionsRoot => Path.Combine(DataRoot, "python-tool", "sessions");

    public string DataDirectory => DataRoot;

    public StorageSnapshot Scan()
    {
        var rows = conversations.ListAll();
        var directories = WorkspaceDirectories();
        var references = messages.ListWorkspaceReferences();
        var safe = SafePaths(rows, directories, references);
        var categories = safe.Select(item => new StorageCategory(item.Key, item.Title,
            DiskFootprint.Measure(item.Paths), item.Paths)).ToArray();
        var owners = WorkspaceOwners(rows);
        var byId = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var workspaces = directories
            .Select(directory =>
            {
                ConversationRow? row = null;
                if (owners.TryGetValue(directory.Name, out var matches) && matches.Count == 1)
                    row = matches[0];
                else if (PythonWorkspaceStore.IsScratchName(directory.Name)
                         && references.TryGetValue(directory.Name, out var ids) && ids.Count == 1)
                    byId.TryGetValue(ids.First(), out row);
                if (row is null || row.DeletedAt is not null) return null;
                return new ConversationWorkspace(row.Id, row.Title, directory.Path,
                    DiskFootprint.Measure(directory.Path), row.UpdatedAt);
            })
            .OfType<ConversationWorkspace>().OrderByDescending(item => item.Bytes).ToArray();

        var total = DiskFootprint.Measure(DataRoot) + DiskFootprint.Measure(RuntimeRoot);
        var pythonUsage = python.GetStorageUsage();
        var runtime = (IsOwnedPath(python.RuntimeBaseDirectory) ? DiskFootprint.Measure(python.RuntimeBaseDirectory) : 0)
                      + (IsOwnedPath(pi.BaseDirectory) ? DiskFootprint.Measure(pi.BaseDirectory) : 0)
                      - categories.First(category => category.Key == "runtime").Bytes;
        var db = database.GetBytesOnDisk();
        var attachments = Path.Combine(DataRoot, "attachments");
        var roles = Path.Combine(DataRoot, "role-assets");
        var media = (IsOwnedPath(attachments) ? DiskFootprint.Measure(attachments) : 0)
                    + (IsOwnedPath(roles) ? DiskFootprint.Measure(roles) : 0);
        var other = Math.Max(0, total - categories.Sum(category => category.Bytes)
                                - workspaces.Sum(item => item.Bytes) - Math.Max(0, runtime) - db - media);
        return new StorageSnapshot(total, Math.Max(0, runtime), db, media, other, categories, workspaces,
            rows.Count(row => row.DeletedAt is not null), pythonUsage);
    }

    public DiskDeleteResult Clean(IReadOnlyList<StorageCategory> selected)
    {
        _mutation.Wait();
        var pythonLocked = false;
        var piLocked = false;
        try
        {
            var runtimeSelected = selected.Any(category => category.Key == "runtime");
            if (runtimeSelected)
            {
                pythonLocked = python.TryBeginStorageCleanup();
                if (pythonLocked) piLocked = pi.TryBeginStorageCleanup();
            }
            var current = SafePaths(conversations.ListAll(), WorkspaceDirectories(), messages.ListWorkspaceReferences())
                .ToDictionary(item => item.Key, item => item.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase));
            var result = default(DiskDeleteResult);
            foreach (var category in selected)
            {
                if (category.Key == "runtime" && (!pythonLocked || !piLocked))
                {
                    result += new DiskDeleteResult(0, 1);
                    continue;
                }
                if (!current.TryGetValue(category.Key, out var allowed)) continue;
                foreach (var path in category.Paths)
                {
                    if (allowed.Contains(path)) result += DeleteOwned(path);
                    else if (File.Exists(path) || Directory.Exists(path)) result += new DiskDeleteResult(0, 1);
                }
            }
            return result;
        }
        finally
        {
            if (piLocked) pi.EndStorageCleanup();
            if (pythonLocked) python.EndStorageCleanup();
            _mutation.Release();
        }
    }

    public DiskDeleteResult DeleteWorkspace(ConversationWorkspace workspace)
    {
        _mutation.Wait();
        try
        {
            var rows = conversations.ListAll();
            var owners = WorkspaceOwners(rows);
            var references = messages.ListWorkspaceReferences();
            var name = Path.GetFileName(workspace.Path);
            var row = rows.FirstOrDefault(item => item.Id == workspace.ConversationId);
            var owned = owners.TryGetValue(name, out var matches) && matches.Count == 1
                        && matches[0].Id == workspace.ConversationId;
            var linked = PythonWorkspaceStore.IsScratchName(name)
                         && references.TryGetValue(name, out var linkedIds)
                         && linkedIds.Count == 1 && linkedIds.Contains(workspace.ConversationId);
            if (row is null || row.DeletedAt is not null || (!owned && !linked)
                || (references.TryGetValue(name, out var referencedBy)
                    && referencedBy.Any(id => id != workspace.ConversationId))
                || IsBusy(workspace.ConversationId)
                || !string.Equals(Path.Combine(SessionsRoot, name), workspace.Path,
                    StringComparison.OrdinalIgnoreCase))
                return new DiskDeleteResult(0, 1);
            return DeleteOwned(workspace.Path);
        }
        finally { _mutation.Release(); }
    }

    public async Task<PurgeResult> PurgeDeletedAsync(TimeSpan minimumAge, bool compact, CancellationToken ct = default)
    {
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(minimumAge).ToUnixTimeMilliseconds();
            var deleted = conversations.ListAll().Where(row => row.DeletedAt is not null).ToArray();
            var old = deleted.Where(row => row.DeletedAt < cutoff && !IsBusy(row.Id)).ToArray();
            IReadOnlySet<string> confirmed = new HashSet<string>();
            try
            {
                confirmed = await cloud.ConfirmDeletionsForPurgeAsync(
                    old.Where(cloud.RequiresCloudDeletion).ToArray(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                DiagnosticLog.Write("storage", "确认云端删除失败：" + ex.Message);
            }
            var eligible = old.Where(row => (!cloud.RequiresCloudDeletion(row) || confirmed.Contains(row.Id))
                                            && !IsBusy(row.Id)).ToArray();
            var keys = eligible.ToDictionary(row => row.Id,
                row => messages.ListSubagentSessionKeys(row.Id)?.Append(row.Id).ToArray());
            var scratch = eligible.ToDictionary(row => row.Id,
                row => messages.ListReferencedWorkspaceNames(row.Id).Where(PythonWorkspaceStore.IsScratchName).ToArray());
            var removed = conversations.PurgeDeleted(eligible.Select(row => row.Id).ToArray(), cutoff, confirmed);
            foreach (var id in removed) cloud.ForgetPurgedConversation(id);

            var remaining = conversations.ListAll();
            var owners = WorkspaceOwners(remaining);
            var referenced = messages.ListReferencedWorkspaceNames();
            var result = default(DiskDeleteResult);
            foreach (var id in removed)
            {
                var name = PythonWorkspaceStore.DirectoryNameFor(id);
                if (name is not null && !owners.ContainsKey(name) && !referenced.Contains(name) && !IsBusy(id))
                    result += DeleteOwned(Path.Combine(SessionsRoot, name));
            }
            foreach (var name in removed.SelectMany(id => scratch[id]).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!referenced.Contains(name)) result += DeleteOwned(Path.Combine(SessionsRoot, name));

            var sessionRoot = Path.Combine(DataRoot, "pi-sessions");
            if (removed.Count > 0 && IsOwnedPath(sessionRoot) && Directory.Exists(sessionRoot))
            {
                if (removed.All(id => keys[id] is not null) && messages.ListSubagentSessionKeys() is { } children)
                {
                    var retainedKeys = remaining.Select(row => row.Id).Concat(children).Select(PiWorkProvider.SanitizeSessionId).ToArray();
                    var removedKeys = removed.SelectMany(id => keys[id]!).Select(PiWorkProvider.SanitizeSessionId).ToArray();
                    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var path in Directory.EnumerateFiles(sessionRoot, "*.jsonl", options))
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        if (removedKeys.Any(key => MatchesSession(name, key)) && !retainedKeys.Any(key => MatchesSession(name, key)))
                            result += DeleteOwned(path);
                    }
                }
                else DiagnosticLog.Write("storage", "子会话引用扫描不完整，跳过 Pi 文件清理。");
            }

            string? compactError = null;
            if (compact && removed.Count > 0)
            {
                try
                {
                    if (AnyConversationBusy()) throw new InvalidOperationException("有对话正在运行，请稍后整理数据库。");
                    messages.CompactToolMetadata();
                    database.Compact();
                }
                catch (Exception ex) { compactError = ex.Message; }
            }
            return new PurgeResult(removed.Count, deleted.Length - removed.Count, result, compactError);
        }
        finally { _mutation.Release(); }
    }

    public DiskDeleteResult CompactDatabase()
    {
        _mutation.Wait();
        try
        {
            if (AnyConversationBusy()) throw new InvalidOperationException("有对话正在运行，请稍后整理数据库。");
            var before = database.GetBytesOnDisk();
            messages.CompactToolMetadata();
            database.Compact();
            var after = database.GetBytesOnDisk();
            return new DiskDeleteResult(Math.Max(0, before - after), 0);
        }
        finally { _mutation.Release(); }
    }

    public void StartupMaintenance()
    {
        _mutation.Wait();
        try
        {
            var updates = IsOwnedPath(Path.Combine(RuntimeRoot, "Updates"))
                ? AppAutoUpdateService.ListObsoletePackages() : [];
            var legacyLogs = IsOwnedPath(Path.Combine(DataRoot, "Logs"))
                ? DiagnosticLog.ListLegacyLogPaths() : [];
            foreach (var path in updates.Concat(legacyLogs))
                DeleteOwned(path);
            if (IsOwnedPath(python.RuntimeBaseDirectory) && python.TryBeginStorageCleanup())
            {
                try { foreach (var path in PythonSupersededPaths()) DeleteOwned(path); }
                finally { python.EndStorageCleanup(); }
            }
            if (IsOwnedPath(pi.BaseDirectory) && pi.TryBeginStorageCleanup())
            {
                try { foreach (var path in pi.ListSupersededPaths()) DeleteOwned(path); }
                finally { pi.EndStorageCleanup(); }
            }
        }
        finally { _mutation.Release(); }
    }

    private IReadOnlyList<(string Key, string Title, IReadOnlyList<string> Paths)> SafePaths(
        IReadOnlyList<ConversationRow> rows, IReadOnlyList<PythonWorkspaceDirectory> directories,
        IReadOnlyDictionary<string, HashSet<string>> referenced)
    {
        var owners = WorkspaceOwners(rows);
        var orphans = directories.Where(directory => !owners.ContainsKey(directory.Name)
                                                      && !referenced.ContainsKey(directory.Name)
                                                      && directory.LastWriteUtc < DateTime.UtcNow.AddHours(-24))
            .Select(directory => directory.Path).ToArray();
        var updates = IsOwnedPath(Path.Combine(RuntimeRoot, "Updates"))
            ? AppAutoUpdateService.ListObsoletePackages() : [];
        var pythonPaths = IsOwnedPath(python.RuntimeBaseDirectory)
            ? PythonSupersededPaths().Concat(python.ListArchiveCachePaths()) : [];
        var piPaths = IsOwnedPath(pi.BaseDirectory)
            ? pi.ListSupersededPaths().Concat(pi.ListArchiveCachePaths()) : [];
        var crash = IsOwnedPath(CrashReportStorage.RootDirectory) ? CrashReportStorage.ListReportPaths() : [];
        var canvas = IsOwnedPath(CdnProxy.CacheDirectory) ? CdnProxy.ListCachePaths() : [];
        var logs = IsOwnedPath(Path.Combine(DataRoot, "Logs"))
            ? DiagnosticLog.ListPastSessionPaths().Concat(DiagnosticLog.ListLegacyLogPaths()) : [];
        (string Key, string Title, IReadOnlyList<string> Paths)[] categories =
        [
            ("updates", "更新安装包", updates),
            ("runtime", "运行环境缓存", pythonPaths.Concat(piPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            ("crash", "崩溃报告", crash),
            ("canvas", "画布缓存", canvas),
            ("logs", "日志", logs.ToArray()),
            ("orphans", "无主对话文件", orphans)
        ];
        return categories.Select(item => (item.Key, item.Title,
            (IReadOnlyList<string>)item.Paths.Where(IsOwnedPath).ToArray())).ToArray();
    }

    private static Dictionary<string, List<ConversationRow>> WorkspaceOwners(IEnumerable<ConversationRow> rows) =>
        rows.Select(row => (Row: row, Name: PythonWorkspaceStore.DirectoryNameFor(row.Id)))
            .Where(item => item.Name is not null)
            .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Row).ToList(), StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PythonWorkspaceDirectory> WorkspaceDirectories() => IsOwnedPath(SessionsRoot)
        ? PythonWorkspaceStore.Enumerate(SessionsRoot).Where(directory => IsOwnedPath(directory.Path)).ToArray()
        : [];

    private IReadOnlyList<string> PythonSupersededPaths()
    {
        var selected = settings.Get("python_tool_executable_path")?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(selected) || !Path.IsPathFullyQualified(selected))
            return python.ListSupersededPaths();
        var selectedPath = Path.GetFullPath(selected);
        return python.ListSupersededPaths().Where(path =>
        {
            var full = Path.GetFullPath(path);
            return !selectedPath.Equals(full, StringComparison.OrdinalIgnoreCase)
                   && !selectedPath.StartsWith(Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }).ToArray();
    }

    private bool IsBusy(string id) => Dispatcher.UIThread.CheckAccess()
        ? IsBusyOnUi(id)
        : Dispatcher.UIThread.InvokeAsync(() => IsBusyOnUi(id)).GetAwaiter().GetResult();

    private bool IsBusyOnUi(string id) =>
        composer.IsConversationBusy(id) || tasks.RunningCount(id) > 0 || imageTasks.IsGenerating(id);

    private bool AnyConversationBusy()
    {
        var ids = conversations.ListAll().Select(row => row.Id).ToArray();
        bool Check() => ids.Any(IsBusyOnUi);
        return Dispatcher.UIThread.CheckAccess() ? Check() : Dispatcher.UIThread.InvokeAsync(Check).GetAwaiter().GetResult();
    }

    private static bool MatchesSession(string name, string key) => key.Length > 0
        && (string.Equals(name, key, StringComparison.Ordinal)
            || name.EndsWith("_" + key, StringComparison.Ordinal));

    private bool IsOwnedPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = new[] { DataRoot, RuntimeRoot }.FirstOrDefault(candidate =>
                full.StartsWith(Path.TrimEndingDirectorySeparator(candidate) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (root is null) return false;
            for (var part = full; part.Length >= root.Length; part = Path.GetDirectoryName(part)!)
            {
                if ((File.Exists(part) || Directory.Exists(part))
                    && File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint))
                    return false;
                if (string.Equals(part, root, StringComparison.OrdinalIgnoreCase)) break;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private DiskDeleteResult DeleteOwned(string path) => IsOwnedPath(path)
        ? DiskFootprint.Delete(path) : new DiskDeleteResult(0, 1);
}
