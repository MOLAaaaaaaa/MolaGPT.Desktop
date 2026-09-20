using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// The 记忆 page. The switches bind to the window's <see cref="SettingsViewModel"/>;
/// the entry list binds to <see cref="MemoryPageViewModel"/>, which reads through
/// the same service a request does so the counts on screen are the counts on the
/// wire.
/// </summary>
public partial class SettingsWindow
{
    private const string MemoryConsolidationNotificationKey = "memory-consolidation";
    private MemoryPageViewModel? _memoryPage;

    private void InitializeMemoryPage(MemoryPageViewModel? page)
    {
        _memoryPage = page;
        if (page is null)
        {
            PAGE_Memory.IsVisible = false;
            return;
        }

        PART_MemoryBody.DataContext = page;
        SyncMemoryBudgetChoice();
        page.Refresh();
        PART_MemorySettings.IsVisible = page.IsOverview;
        page.PropertyChanged += OnMemoryPageStateChanged;
        Closed += (_, _) => page.PropertyChanged -= OnMemoryPageStateChanged;
    }

    private void OnMemoryPageStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MemoryPageViewModel.IsOverview))
            PART_MemorySettings.IsVisible = _memoryPage!.IsOverview;
    }

    /// <summary>The budget is four named steps rather than a number box: the
    /// only judgement the user actually makes is 「记忆占多少上下文算合适」.</summary>
    private void SyncMemoryBudgetChoice()
    {
        var current = _settings.MemoryBudgetTokens.ToString();
        foreach (var item in PART_MemoryBudget.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string != current) continue;
            PART_MemoryBudget.SelectedItem = item;
            return;
        }
        PART_MemoryBudget.SelectedIndex = 1;
    }

    private void OnMemoryBudgetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PART_MemoryBudget.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (!int.TryParse(tag, out var budget)) return;
        if (_settings.MemoryBudgetTokens == budget) return;
        _settings.MemoryBudgetTokens = budget;
        _memoryPage?.Refresh();
    }

    private void OnMemoryAdd(object? sender, RoutedEventArgs e) => _memoryPage?.Add();

    /// <summary>On commit rather than per keystroke: every save rewrites
    /// profile.md, and the identity drop-down reads it on the next open.</summary>
    private void OnMemoryProfileCommit(object? sender, RoutedEventArgs e) => _memoryPage?.SaveProfile();

    private void OnMemoryTopicOpen(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LocalMemoryTopicRow topic }) _memoryPage?.OpenTopic(topic);
    }

    private void OnMemoryTopicBack(object? sender, RoutedEventArgs e) => _memoryPage?.BackToOverview();
    private void OnMemoryNewTopic(object? sender, RoutedEventArgs e) => _memoryPage?.BeginTopicEdit(create: true);
    private void OnMemoryTopicEdit(object? sender, RoutedEventArgs e) => _memoryPage?.BeginTopicEdit(create: false);
    private void OnMemoryTopicSave(object? sender, RoutedEventArgs e) => _memoryPage?.SaveTopic();
    private void OnMemoryTopicCancel(object? sender, RoutedEventArgs e) => _memoryPage?.CancelTopicEdit();

    private async void OnMemoryTopicDelete(object? sender, RoutedEventArgs e)
    {
        if (_memoryPage?.SelectedTopic is not { } topic) return;
        if (await Confirm.AskAsync(this, "删除主题", $"删除“{topic.Title}”及其中的 {topic.Entries.Count} 条记录？", "删除"))
            _memoryPage.DeleteSelectedTopic();
    }

    private void OnMemoryEntryEdit(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LocalMemoryEntryRow row }) row.IsEditing = true;
    }

    /// <summary>
    /// Explicit save avoids committing a partially edited record when navigating away.
    /// </summary>
    private void OnMemoryEntryCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LocalMemoryEntryRow row }) _memoryPage?.Save(row);
    }

    private void OnMemoryEntryDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LocalMemoryEntryRow row }) _memoryPage?.Delete(row);
    }

    private void OnMemoryCandidateAccept(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LocalMemoryCandidateRow row }) _memoryPage?.Accept(row);
    }

    private void OnMemoryCandidateIgnore(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LocalMemoryCandidateRow row }) _memoryPage?.Ignore(row);
    }

    private async void OnMemoryConsolidate(object? sender, RoutedEventArgs e)
    {
        if (_memoryPage is null) return;
        _notifications?.Progress(
            MemoryConsolidationNotificationKey,
            "正在整理记忆",
            "现有记忆仍可查看");
        try
        {
            var report = await _memoryPage.ConsolidateAsync();
            if (report.Ran)
                _notifications?.Success("记忆整理完成", report.Describe(), MemoryConsolidationNotificationKey);
            else
                _notifications?.Warning("暂时无法整理记忆", report.Describe(), MemoryConsolidationNotificationKey);
        }
        catch (Exception ex)
        {
            _notifications?.Error("记忆整理失败", ex.Message, MemoryConsolidationNotificationKey);
        }
    }

    private async void OnMemoryRebuildIndex(object? sender, RoutedEventArgs e)
    {
        if (_memoryPage is null) return;
        await _memoryPage.RebuildIndexAsync();
    }

    private void OnMemoryOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_memoryPage is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(_memoryPage.MemoryFolder);
            Process.Start(new ProcessStartInfo(_memoryPage.MemoryFolder) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer refusing to open is not worth a dialog; the path is
            // already printed on the row next to the button.
        }
    }

    private async void OnMemoryClearAll(object? sender, RoutedEventArgs e)
    {
        if (_memoryPage is null) return;
        var confirmed = await Confirm.AskAsync(
            this,
            "清除全部记忆",
            "将删除记忆文件和待确认内容，历史消息不会再次用于整理。",
            "清除");
        if (confirmed) _memoryPage.ClearAll();
    }
}
