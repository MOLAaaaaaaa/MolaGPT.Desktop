using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class SettingsWindow
{
    private readonly PersonalDataService? _personalData;
    private const string PersonalDataNotificationKey = "personal-data";
    private static readonly FilePickerFileType ModelConfigurationFileType = new("MolaGPT 模型配置")
    {
        Patterns = ["*.molaconfig"]
    };

    private async void OnExportConversations(object? sender, RoutedEventArgs e) =>
        await RunPersonalDataOperationAsync("聊天记录导出失败", async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出聊天记录",
                SuggestedFileName = $"MolaGPT-chats-{DateTime.Now:yyyyMMdd}.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON 聊天记录") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            var count = 0;
            await using (var stream = await file.OpenWriteAsync())
            {
                stream.SetLength(0);
                _notifications?.Progress(PersonalDataNotificationKey, "正在导出聊天记录");
                count = await Task.Run(() => _personalData!.ExportConversations(stream));
            }
            _notifications?.Success("聊天记录已导出", $"{count} 个对话 · {file.Name}", PersonalDataNotificationKey);
        });

    private async void OnExportModelConfigurations(object? sender, RoutedEventArgs e) =>
        await RunPersonalDataOperationAsync("模型配置导出失败", async () =>
        {
            if (_settings.Providers.Count == 0)
            {
                _notifications?.Info("暂无可导出的模型配置", key: PersonalDataNotificationKey);
                return;
            }
            var password = await RequestTransferPasswordAsync(exporting: true);
            if (password is null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出模型配置",
                SuggestedFileName = $"MolaGPT-models-{DateTime.Now:yyyyMMdd}.molaconfig",
                DefaultExtension = "molaconfig",
                FileTypeChoices = [ModelConfigurationFileType]
            });
            if (file is null) return;
            _notifications?.Progress(PersonalDataNotificationKey, "正在导出模型配置");
            var data = await Task.Run(() => _personalData!.ExportModelConfigurations(password));
            await using (var stream = await file.OpenWriteAsync())
            {
                stream.SetLength(0);
                await stream.WriteAsync(data);
            }
            _notifications?.Success("模型配置已导出", file.Name, PersonalDataNotificationKey);
        });

    private async void OnImportModelConfigurations(object? sender, RoutedEventArgs e) =>
        await RunPersonalDataOperationAsync("模型配置导入失败", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入模型配置",
                AllowMultiple = false,
                FileTypeFilter = [ModelConfigurationFileType]
            });
            if (files.Count == 0) return;
            (IReadOnlyList<ProviderRow> Rows, int Rejected)? read;
            do
            {
                var password = await RequestTransferPasswordAsync(exporting: false);
                if (password is null) return;
                await using (var stream = await files[0].OpenReadAsync())
                    read = await Task.Run(() => _personalData!.ReadModelConfigurations(stream, password));
                if (read is null)
                    _notifications?.Error("模型配置导入失败", "密码不正确，或模型配置文件已损坏。", PersonalDataNotificationKey);
            } while (read is null);
            var (rows, rejected) = read.Value;
            if (rows.Count == 0)
            {
                if (rejected > 0)
                    _notifications?.Error("模型配置导入失败", $"{rejected} 个服务无法导入。", PersonalDataNotificationKey);
                else
                    _notifications?.Info("文件中没有模型配置", key: PersonalDataNotificationKey);
                return;
            }
            var selectedRows = await ResolveModelConfigurationConflictsAsync(rows);
            if (selectedRows is null) return;
            if (selectedRows.Count == 0)
            {
                if (rejected > 0)
                    _notifications?.Warning("已保留现有配置", $"{rejected} 个服务无法导入。", PersonalDataNotificationKey);
                else
                    _notifications?.Info("已保留现有配置", key: PersonalDataNotificationKey);
                return;
            }

            _notifications?.Progress(PersonalDataNotificationKey, "正在导入模型配置");
            var summary = _providerRestorer.Import(selectedRows);
            var imported = selectedRows.Count - summary.Failed;
            var failed = rejected + summary.Failed;
            if (imported == 0)
                _notifications?.Error("模型配置导入失败", $"{failed} 个服务无法导入。", PersonalDataNotificationKey);
            else if (failed > 0 || summary.Unavailable > 0)
            {
                var detail = $"已导入 {imported} 个模型服务。";
                if (failed > 0) detail += $"{failed} 个服务导入失败。";
                if (summary.Unavailable > 0) detail += $"{summary.Unavailable} 个已导入服务暂无法运行。";
                _notifications?.Warning("模型配置已导入",
                    detail, PersonalDataNotificationKey);
            }
            else
                _notifications?.Success("模型配置已导入", $"{imported} 个模型服务", PersonalDataNotificationKey);
        });

    private async Task<IReadOnlyList<ProviderRow>?> ResolveModelConfigurationConflictsAsync(IReadOnlyList<ProviderRow> rows)
    {
        var existing = _settings.Providers.ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        var conflicts = rows.Where(row => existing.ContainsKey(row.Id)).ToList();
        if (conflicts.Count == 0) return rows;

        var choices = new Dictionary<string, RadioButton>(StringComparer.Ordinal);
        var list = new StackPanel { Spacing = 20 };
        foreach (var incoming in conflicts)
        {
            var current = existing[incoming.Id];
            var keep = new RadioButton
            {
                Content = "保留现有配置", GroupName = incoming.Id, IsChecked = true, Tag = incoming.Id
            };
            var replace = new RadioButton
            {
                Content = "使用导入配置", GroupName = incoming.Id, Tag = incoming.Id
            };
            AutomationProperties.SetName(keep, current.Name + "，保留现有配置");
            AutomationProperties.SetName(replace, incoming.Name + "，使用导入配置");
            choices.Add(incoming.Id, replace);

            var comparison = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
            comparison.Children.Add(Option(keep, current.Name, current.BaseUrl, current.Models.Select(model => model.Id)));
            var imported = Option(replace, incoming.Name, incoming.BaseUrl,
                ProviderRowCodec.ReadModels(incoming.Models).Select(model => model.Id));
            Grid.SetColumn(imported, 2);
            comparison.Children.Add(imported);
            list.Children.Add(comparison);
        }

        var confirmed = false;
        var dialog = new MolaDialogWindow("选择要保留的配置") { Width = 720, MaxWidth = 720 };
        var accept = new Button { Content = "导入", Classes = { "primary" } };
        var cancel = new Button { Content = "取消", Classes = { "outline" } };
        dialog.SetBody(new StackPanel
        {
            Margin = new Thickness(24), Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = "以下服务已存在，请分别选择要保留的配置。",
                    Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap
                },
                new ScrollViewer
                {
                    MaxHeight = 440, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = list
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, accept }
                }
            }
        });
        accept.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return confirmed
            ? rows.Where(row => !choices.TryGetValue(row.Id, out var choice) || choice.IsChecked == true).ToList()
            : null;

        static Border Option(RadioButton choice, string name, string? baseUrl, IEnumerable<string> modelIds)
        {
            var ids = modelIds.ToList();
            return new Border
            {
                Classes = { "settingscard" }, Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        choice,
                        new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = baseUrl, Classes = { "hint" }, Margin = default, TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = $"{ids.Count} 个模型：{string.Join("、", ids.Take(3))}" + (ids.Count > 3 ? "…" : string.Empty),
                            Classes = { "hint" }, Margin = default, TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
        }
    }

    private async Task RunPersonalDataOperationAsync(string failureTitle, Func<Task> operation)
    {
        if (_personalData is null || !PAGE_PersonalData.IsEnabled) return;
        PAGE_PersonalData.IsEnabled = false;
        try { await operation(); }
        catch (Exception ex) { _notifications?.Error(failureTitle, ex.Message, PersonalDataNotificationKey); }
        finally { PAGE_PersonalData.IsEnabled = true; }
    }

    private async Task<string?> RequestTransferPasswordAsync(bool exporting)
    {
        string? result = null;
        var password = new TextBox { PasswordChar = '•', Classes = { "field" } };
        var confirmation = new TextBox { PasswordChar = '•', Classes = { "field" } };
        AutomationProperties.SetName(password, "导出密码");
        AutomationProperties.SetName(confirmation, "确认密码");
        var hint = new TextBlock
        {
            Text = exporting ? "至少 8 个字符，请保管好密码，导入时需要使用。" : "输入导出此文件时设置的密码。",
            Classes = { "hint" }, TextWrapping = TextWrapping.Wrap
        };
        var accept = new Button { Content = exporting ? "继续" : "导入", Classes = { "primary" }, IsEnabled = false, IsDefault = true };
        var cancel = new Button { Content = "取消", Classes = { "outline" } };
        var dialog = new MolaDialogWindow(exporting ? "导出模型配置" : "导入模型配置");
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "导出密码", Classes = { "label" }, Margin = default });
        body.Children.Add(password);
        if (exporting)
        {
            body.Children.Add(new TextBlock { Text = "确认密码", Classes = { "label" }, Margin = default });
            body.Children.Add(confirmation);
        }
        body.Children.Add(hint);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, accept }
        });
        dialog.SetBody(body);
        password.TextChanged += (_, _) => UpdatePasswordAction();
        confirmation.TextChanged += (_, _) => UpdatePasswordAction();
        accept.Click += (_, _) => { result = password.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => password.Focus();
        await dialog.ShowDialog(this);
        password.Clear();
        confirmation.Clear();
        return result;

        void UpdatePasswordAction()
        {
            accept.IsEnabled = exporting
                ? password.Text is { Length: >= 8 } && password.Text == confirmation.Text
                : !string.IsNullOrEmpty(password.Text);
            if (exporting)
                hint.Text = !string.IsNullOrEmpty(confirmation.Text) && password.Text != confirmation.Text
                    ? "两次输入的密码不一致。"
                    : "至少 8 个字符，请保管好密码，导入时需要使用。";
        }
    }
}
