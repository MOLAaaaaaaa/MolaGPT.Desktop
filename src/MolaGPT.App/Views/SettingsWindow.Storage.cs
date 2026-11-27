using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.App.Infrastructure;
using MolaGPT.Core.Disk;

namespace MolaGPT.App.Views;

public partial class SettingsWindow
{
    private const string StorageNotificationKey = "storage-cleanup";
    private readonly StorageService _storage;
    private StorageSnapshot? _storageSnapshot;
    private bool _showAllWorkspaces;
    private int _storageGeneration;

    private async Task RefreshStorageAsync()
    {
        var generation = ++_storageGeneration;
        PART_StorageStatus.Text = "正在计算…";
        PART_StorageProgress.IsVisible = true;
        PART_StorageLoading.IsVisible = true;
        PART_StorageContent.IsVisible = false;
        PART_RefreshStorage.IsEnabled = false;
        try
        {
            var snapshot = await Task.Run(_storage.Scan);
            if (generation != _storageGeneration) return;
            _storageSnapshot = snapshot;
            _storageScan = Task.FromResult(snapshot.PythonUsage);
            RenderStorage(snapshot);
            PART_StorageLoading.IsVisible = false;
            PART_StorageContent.IsVisible = true;
            if (PAGE_Sandbox.IsVisible) _ = RefreshRuntimeStatusAsync();
        }
        catch (Exception ex)
        {
            if (generation != _storageGeneration) return;
            PART_StorageStatus.Text = "无法计算存储空间";
            PART_StorageProgress.IsVisible = false;
            _notifications?.Error("存储空间读取失败", ex.Message, StorageNotificationKey);
        }
        finally
        {
            if (generation == _storageGeneration) PART_RefreshStorage.IsEnabled = true;
        }
    }

    private void RenderStorage(StorageSnapshot snapshot)
    {
        var safeBytes = snapshot.Categories.Sum(item => item.Bytes);
        var workspaceBytes = snapshot.Workspaces.Sum(item => item.Bytes);
        var otherBytes = snapshot.DatabaseBytes + snapshot.MediaBytes + snapshot.OtherBytes;
        PART_StorageTotal.Text = FormatBytes(snapshot.TotalBytes);
        PART_StorageLegend.Text = $"可清理 {FormatBytes(safeBytes)}  ·  对话文件 {FormatBytes(workspaceBytes)}  ·  运行环境 {FormatBytes(snapshot.RuntimeBytes)}  ·  其他 {FormatBytes(otherBytes)}";
        var segments = new[] { safeBytes, workspaceBytes, snapshot.RuntimeBytes, otherBytes };
        for (var i = 0; i < segments.Length; i++)
            PART_StorageBar.ColumnDefinitions[i].Width = new GridLength(
                snapshot.TotalBytes == 0 && i == 3 ? 1 : segments[i], GridUnitType.Star);

        PART_StorageSafeRows.Children.Clear();
        foreach (var category in snapshot.Categories)
        {
            var button = StorageButton("清理");
            button.IsEnabled = category.Bytes > 0;
            button.Click += async (_, _) => await CleanStorageAsync([category]);
            AddStorageRow(PART_StorageSafeRows, category.Title, FormatBytes(category.Bytes), button);
        }
        PART_CleanAllStorage.IsEnabled = safeBytes > 0;

        RenderWorkspaces(snapshot);
        PART_StorageDeletedTitle.Text = $"{snapshot.DeletedCount} 个对话";
        PART_PurgeDeleted.IsEnabled = snapshot.DeletedCount > 0;
        PART_StorageOtherRows.Children.Clear();
        var runtime = StorageButton("前往设置");
        runtime.Click += (_, _) => PART_Nav.SelectedItem = PART_SandboxNav;
        AddStorageRow(PART_StorageOtherRows, "运行环境", FormatBytes(snapshot.RuntimeBytes), runtime);
        var compact = StorageButton("整理");
        compact.Click += async (_, _) => await RunStorageActionAsync(() => _storage.CompactDatabase());
        AddStorageRow(PART_StorageOtherRows, "对话数据库", FormatBytes(snapshot.DatabaseBytes), compact);
        AddStorageRow(PART_StorageOtherRows, "附件与角色素材", FormatBytes(snapshot.MediaBytes));
        AddStorageRow(PART_StorageOtherRows, "其他文件", FormatBytes(snapshot.OtherBytes));
    }

    private void RenderWorkspaces(StorageSnapshot snapshot)
    {
        PART_StorageWorkspaceRows.Children.Clear();
        var items = _showAllWorkspaces ? snapshot.Workspaces : snapshot.Workspaces.Take(20);
        foreach (var workspace in items)
        {
            if (PART_StorageWorkspaceRows.Children.Count > 0)
                PART_StorageWorkspaceRows.Children.Add(new Border { Classes = { "divider" } });
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,72,86,Auto,Auto"),
                ColumnSpacing = 12,
                MinHeight = 44,
                Margin = new Thickness(16, 0)
            };
            var title = new Button
            {
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(workspace.Title) ? "未命名对话" : workspace.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                    MaxLines = 1
                },
                Classes = { "storageconversation" },
                IsEnabled = _conversations is not null
            };
            ToolTip.SetTip(title, $"查看对话：{workspace.Title}");
            title.Click += (_, _) => OpenStorageConversation(workspace.ConversationId);
            row.Children.Add(title);

            var size = new TextBlock
            {
                Text = FormatBytes(workspace.Bytes), Classes = { "hint" }, Margin = new Thickness(0),
                TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            var updated = new TextBlock
            {
                Text = DateTimeOffset.FromUnixTimeMilliseconds(workspace.UpdatedAt).ToLocalTime().ToString("yyyy-MM-dd"),
                Classes = { "hint" }, Margin = new Thickness(0), TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(updated, 2);
            row.Children.Add(updated);

            var open = StorageButton("打开文件夹");
            open.Classes.Add("storageaction");
            open.Margin = new Thickness(0);
            open.Click += (_, _) => OpenStoragePath(workspace.Path);
            Grid.SetColumn(open, 3);
            row.Children.Add(open);
            var delete = StorageButton("删除文件");
            delete.Classes.Add("storageaction");
            delete.Margin = new Thickness(0);
            delete.Click += async (_, _) => await DeleteWorkspaceAsync(workspace);
            Grid.SetColumn(delete, 4);
            row.Children.Add(delete);
            PART_StorageWorkspaceRows.Children.Add(row);
        }
        if (snapshot.Workspaces.Count == 0)
            AddStorageRow(PART_StorageWorkspaceRows, "暂无对话文件", string.Empty);
        PART_ShowAllWorkspaces.IsVisible = !_showAllWorkspaces && snapshot.Workspaces.Count > 20;
    }

    private void OpenStorageConversation(string conversationId)
    {
        if (_conversations is null) return;
        _conversations.SelectById(conversationId);
        var owner = Owner;
        Close();
        owner?.Activate();
    }

    private async Task DeleteWorkspaceAsync(ConversationWorkspace workspace)
    {
        var confirmed = false;
        var viewRequested = false;
        var dialog = new MolaDialogWindow("删除对话文件？");
        var deleteConversation = new CheckBox
        {
            Content = "同时删除该对话", IsChecked = false, IsEnabled = _conversations is not null
        };
        var view = new Button
        {
            Content = "查看对话", Classes = { "outline" }, Padding = new Thickness(10, 4), MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = _conversations is not null
        };
        var cancel = new Button { Content = "取消", Classes = { "outline" } };
        var confirm = new Button { Content = "删除文件", Classes = { "primary" } };
        Grid.SetColumn(view, 1);
        deleteConversation.IsCheckedChanged += (_, _) =>
            confirm.Content = deleteConversation.IsChecked == true ? "删除文件和对话" : "删除文件";
        view.Click += (_, _) => { viewRequested = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) => { confirmed = true; dialog.Close(); };
        dialog.SetBody(new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(workspace.Title) ? "未命名对话" : workspace.Title,
                            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        view
                    }
                },
                new TextBlock
                {
                    Text = "生成的图片和文件将被永久删除，无法恢复。",
                    Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap
                },
                deleteConversation,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, confirm }
                }
            }
        });
        await dialog.ShowDialog(this);
        if (viewRequested)
        {
            OpenStorageConversation(workspace.ConversationId);
            return;
        }
        if (!confirmed) return;
        await RunStorageActionAsync(() => _storage.DeleteWorkspace(workspace),
            deleteConversation.IsChecked == true ? workspace.ConversationId : null);
    }

    private static Button StorageButton(string text)
    {
        var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        button.Classes.Add("outline");
        return button;
    }

    private static void AddStorageRow(StackPanel panel, string title, string detail, params Button[] buttons)
    {
        if (panel.Children.Count > 0)
        {
            var divider = new Border();
            divider.Classes.Add("divider");
            panel.Children.Add(divider);
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 52 };
        row.Classes.Add("settingrow");
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Classes.Add("rowtitle");
        labels.Children.Add(name);
        if (!string.IsNullOrEmpty(detail))
        {
            var hint = new TextBlock { Text = detail };
            hint.Classes.Add("hint");
            labels.Children.Add(hint);
        }
        row.Children.Add(labels);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons) actions.Children.Add(button);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        panel.Children.Add(row);
    }

    private async Task CleanStorageAsync(IReadOnlyList<StorageCategory> categories)
    {
        await RunStorageActionAsync(() => _storage.Clean(categories));
    }

    private async Task RunStorageActionAsync(Func<DiskDeleteResult> action, string? deleteConversationId = null)
    {
        PAGE_Storage.IsEnabled = false;
        _notifications?.Progress(StorageNotificationKey, "正在清理存储空间");
        try
        {
            var result = await Task.Run(action);
            var detail = $"已清理 {FormatBytes(result.BytesFreed)}";
            if (deleteConversationId is not null && result.FilesSkipped == 0)
            {
                _conversations!.DeleteConversationCommand.Execute(deleteConversationId);
                detail += "，已删除对话";
            }
            if (result.FilesSkipped > 0)
                _notifications?.Warning("部分文件未能清理", $"{detail}，跳过 {result.FilesSkipped} 项"
                    + (deleteConversationId is not null ? "，对话已保留" : ""), StorageNotificationKey);
            else
                _notifications?.Success("清理完成", detail, StorageNotificationKey);
            await RefreshStorageAsync();
        }
        catch (Exception ex) { _notifications?.Error("清理失败", ex.Message, StorageNotificationKey); }
        finally { PAGE_Storage.IsEnabled = true; }
    }

    private async void OnPurgeDeleted(object? sender, RoutedEventArgs e)
    {
        if (!await Confirm.AskAsync(this, "彻底清除已删除的对话？",
                "清除后无法恢复，对话生成的文件也会被删除。", "彻底清除")) return;
        PAGE_Storage.IsEnabled = false;
        _notifications?.Progress(StorageNotificationKey, "正在清除已删除的对话");
        try
        {
            var result = await Task.Run(() => _storage.PurgeDeletedAsync(TimeSpan.FromSeconds(10), compact: true));
            var detail = $"已清除 {result.Removed} 个对话";
            if (result.Skipped > 0) detail += $"，{result.Skipped} 个暂未清除";
            if (result.Files.FilesSkipped > 0) detail += $"，{result.Files.FilesSkipped} 项文件未能删除";
            if (result.CompactError is not null) detail += "，数据库整理未完成";
            if (result.Skipped > 0 || result.Files.FilesSkipped > 0 || result.CompactError is not null)
                _notifications?.Warning("对话清除完成", detail, StorageNotificationKey);
            else _notifications?.Success("对话清除完成", detail, StorageNotificationKey);
            await RefreshStorageAsync();
        }
        catch (Exception ex) { _notifications?.Error("清除对话失败", ex.Message, StorageNotificationKey); }
        finally { PAGE_Storage.IsEnabled = true; }
    }

    private async void OnRefreshStorage(object? sender, RoutedEventArgs e) => await RefreshStorageAsync();
    private async void OnCleanAllStorage(object? sender, RoutedEventArgs e)
    {
        if (_storageSnapshot is { } snapshot) await CleanStorageAsync(snapshot.Categories);
    }
    private void OnShowAllWorkspaces(object? sender, RoutedEventArgs e)
    {
        _showAllWorkspaces = true;
        if (_storageSnapshot is { } snapshot) RenderWorkspaces(snapshot);
    }
    private void OnOpenStorageFolder(object? sender, RoutedEventArgs e)
    {
        OpenStoragePath(_storage.DataDirectory);
    }
    private void OpenStoragePath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { _notifications?.Error("无法打开文件夹", ex.Message, StorageNotificationKey); }
    }
}
