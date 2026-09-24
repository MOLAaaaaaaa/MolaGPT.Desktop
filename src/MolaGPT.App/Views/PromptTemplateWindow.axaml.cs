using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class PromptTemplateWindow : MolaContentWindow
{
    private const string NotificationKey = "prompt-template";
    private RoleLibraryViewModel _library = null!;
    private NotificationCenter? _notifications;
    private List<PromptTemplate> _templates = [];
    private PromptTemplate? _template;
    private PromptBlock? _block;
    private string? _defaultId;
    private bool _loading;
    public Func<PromptTemplate, string?>? CheckTemplateDeletion { get; init; }

    public PromptTemplateWindow()
    {
        InitializeComponent();
        PART_Templates.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveTemplate();
            LoadTemplate(PART_Templates.SelectedItem as PromptTemplate);
        };
        PART_Blocks.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveBlock();
            LoadBlock(PART_Blocks.SelectedItem as PromptBlock);
        };
        PART_Name.LostFocus += (_, _) =>
        {
            SaveTemplate();
            _loading = true;
            PART_Templates.ItemsSource = _templates.ToArray();
            PART_Templates.SelectedItem = _template;
            _loading = false;
        };
        PART_Default.Click += (_, _) =>
        {
            if (_template is null) return;
            if (PART_Default.IsChecked == true) _defaultId = _template.Id;
            else if (_defaultId == _template.Id) _defaultId = null;
        };
        PART_BlockEnabled.IsCheckedChanged += (_, _) =>
        {
            if (!_loading && _block is not null) _block.Enabled = PART_BlockEnabled.IsChecked == true;
        };
        PART_BlockName.LostFocus += (_, _) => SaveBlock();
        PART_BlockText.LostFocus += (_, _) => SaveBlock();
        PART_BlockRole.SelectionChanged += (_, _) => SaveBlock();
        PART_BlockDepth.ValueChanged += (_, _) => SaveBlock();
        PART_BlockPosition.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveBlock();
            RefreshBlockOptions();
        };
        PART_Add.Click += (_, _) =>
        {
            SaveTemplate();
            var template = PromptTemplate.CreateDefault("新编排");
            _templates.Add(template);
            RefreshTemplates(template);
            Dispatcher.UIThread.Post(() => { PART_Name.Focus(); PART_Name.SelectAll(); });
        };
        PART_Delete.Click += (_, _) =>
        {
            if (_template is null) return;
            if ((CheckTemplateDeletion?.Invoke(_template) ?? _library.TemplateDeletionReason(_template)) is { } reason)
            {
                _notifications?.Error("无法删除编排", reason, NotificationKey);
                return;
            }
            var index = _templates.IndexOf(_template);
            if (_defaultId == _template.Id) _defaultId = null;
            _templates.Remove(_template);
            _template = null;
            RefreshTemplates(_templates.ElementAtOrDefault(index) ?? _templates.LastOrDefault());
        };
        PART_Import.Click += OnImport;
        PART_MoveUp.Click += (_, _) => MoveBlock(-1);
        PART_MoveDown.Click += (_, _) => MoveBlock(1);
        PART_AddBlock.Click += (_, _) =>
        {
            if (_template is null) return;
            var menu = new MenuFlyout();
            foreach (var kind in Enum.GetValues<PromptBlockKind>()
                .Where(kind => kind == PromptBlockKind.Text || _template.Blocks.All(block => block.Kind != kind)))
            {
                var item = new MenuItem { Header = PromptBlock.Label(kind) };
                item.Click += (_, _) => AddBlock(kind);
                menu.Items.Add(item);
            }
            menu.ShowAt(PART_AddBlock);
        };
        PART_DeleteBlock.Click += (_, _) =>
        {
            if (_template is null || _block is not { Kind: not PromptBlockKind.History } block) return;
            var index = _template.Blocks.IndexOf(block);
            _template.Blocks.Remove(block);
            _block = null;
            RefreshBlocks(_template.Blocks.ElementAtOrDefault(index) ?? _template.Blocks.LastOrDefault());
        };
        PART_Cancel.Click += (_, _) => Close(false);
        PART_Save.Click += (_, _) =>
        {
            SaveTemplate();
            try
            {
                _library.SaveTemplates(_templates, _defaultId);
                Close(true);
            }
            catch (Exception ex) { _notifications?.Error("编排保存失败", ex.Message, NotificationKey); }
        };
    }

    public Task<bool> ShowForAsync(RoleLibraryViewModel library, NotificationCenter? notifications, Window owner,
        string? selectedId = null)
    {
        _library = library;
        _notifications = notifications;
        _templates = RoleJson.Deserialize<List<PromptTemplate>>(RoleJson.Serialize(library.Templates));
        _defaultId = library.DefaultTemplateId;
        RefreshTemplates(_templates.FirstOrDefault(template => template.Id == selectedId) ?? _templates.FirstOrDefault());
        return ShowDialog<bool>(owner);
    }

    private void RefreshTemplates(PromptTemplate? selected)
    {
        _loading = true;
        PART_Templates.ItemsSource = _templates.ToArray();
        PART_Templates.SelectedItem = selected;
        _loading = false;
        LoadTemplate(selected);
    }

    private void LoadTemplate(PromptTemplate? template)
    {
        _template = template;
        _block = null;
        PART_Editor.IsVisible = template is not null;
        PART_Empty.IsVisible = template is null;
        PART_Delete.IsEnabled = template is not null;
        if (template is null) return;
        _loading = true;
        PART_Name.Text = template.Name;
        PART_Default.IsChecked = _defaultId == template.Id;
        _loading = false;
        RefreshBlocks(template.Blocks.FirstOrDefault());
    }

    private void RefreshBlocks(PromptBlock? selected)
    {
        var blocks = _template?.Blocks.ToArray() ?? [];
        _loading = true;
        PART_Blocks.ItemsSource = blocks;
        PART_Blocks.SelectedItem = selected;
        _loading = false;
        PART_BlockCount.Text = $"块 · {blocks.Length}";
        LoadBlock(selected);
    }

    private void LoadBlock(PromptBlock? block)
    {
        _block = block;
        var index = block is null || _template is null ? -1 : _template.Blocks.IndexOf(block);
        PART_BlockScroll.IsVisible = block is not null;
        PART_MoveUp.IsEnabled = index > 0;
        PART_MoveDown.IsEnabled = index >= 0 && index < _template!.Blocks.Count - 1;
        PART_DeleteBlock.IsEnabled = block is { Kind: not PromptBlockKind.History };
        if (block is null) return;
        _loading = true;
        PART_BlockScroll.Offset = default;
        PART_BlockEnabled.IsChecked = block.Enabled;
        PART_BlockEnabled.IsEnabled = block.Kind != PromptBlockKind.History;
        PART_BlockName.Text = block.Name;
        PART_BlockRole.SelectedIndex = block.Role switch { "user" => 1, "assistant" => 2, _ => 0 };
        PART_BlockPosition.SelectedIndex = block.Depth is null ? 0 : 1;
        PART_BlockDepth.Value = block.Depth ?? 4;
        PART_BlockText.Text = block.Text;
        PART_TextLabel.Text = block.Kind switch
        {
            PromptBlockKind.Text => "内容",
            PromptBlockKind.Main or PromptBlockKind.PostHistory => "默认内容",
            _ => "格式"
        };
        PART_BlockHint.Text = block.Kind switch
        {
            PromptBlockKind.Main => "角色的系统提示词优先；可用 {{original}} 引用此处内容。",
            PromptBlockKind.PostHistory => "角色的补充指令优先；可用 {{original}} 引用此处内容。",
            PromptBlockKind.History => "含按深度插入的对话补充、角色补充与世界书条目。",
            PromptBlockKind.Examples => "按上下文余量整组放入。",
            PromptBlockKind.Text => "",
            _ => "{{slot}} 为原内容；留空则原样发送。"
        };
        _loading = false;
        RefreshBlockOptions();
    }

    private void RefreshBlockOptions()
    {
        var kind = _block?.Kind;
        PART_TextNameRow.IsVisible = kind == PromptBlockKind.Text;
        PART_PlacementRow.IsVisible = _block?.HasRole == true;
        PART_DepthRow.IsVisible = PART_BlockPosition.SelectedIndex == 1;
        PART_TextRow.IsVisible = kind is not (PromptBlockKind.History or PromptBlockKind.Examples);
        PART_BlockHint.IsVisible = !string.IsNullOrEmpty(PART_BlockHint.Text);
    }

    private void SaveTemplate()
    {
        if (_loading) return;
        SaveBlock();
        if (_template is not null) _template.Name = PART_Name.Text?.Trim() ?? "";
    }

    private void SaveBlock()
    {
        if (_loading || _block is null) return;
        if (_block.Kind == PromptBlockKind.Text) _block.Name = PART_BlockName.Text?.Trim() ?? "";
        if (PART_TextRow.IsVisible) _block.Text = PART_BlockText.Text ?? "";
        if (_block.HasRole) _block.Role = PART_BlockRole.SelectedIndex switch { 1 => "user", 2 => "assistant", _ => "system" };
        _block.Depth = _block.HasRole && PART_BlockPosition.SelectedIndex == 1 ? (int)(PART_BlockDepth.Value ?? 4) : null;
    }

    private void AddBlock(PromptBlockKind kind)
    {
        if (_template is null) return;
        SaveBlock();
        var block = new PromptBlock { Kind = kind, Text = PromptTemplate.DefaultFormat(kind) };
        var at = _block is null ? _template.Blocks.Count : _template.Blocks.IndexOf(_block) + 1;
        _template.Blocks.Insert(at, block);
        RefreshBlocks(block);
        if (kind == PromptBlockKind.Text)
            Dispatcher.UIThread.Post(() => { PART_BlockName.Focus(); PART_BlockName.SelectAll(); });
    }

    private void MoveBlock(int offset)
    {
        if (_template is null || _block is null) return;
        SaveBlock();
        var index = _template.Blocks.IndexOf(_block);
        var target = index + offset;
        if (target < 0 || target >= _template.Blocks.Count) return;
        (_template.Blocks[index], _template.Blocks[target]) = (_template.Blocks[target], _template.Blocks[index]);
        RefreshBlocks(_block);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入预设", FileTypeFilter = [new FilePickerFileType("预设") { Patterns = ["*.json"] }]
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var import = PromptTemplateReader.Read(buffer.ToArray(), files[0].Name);
            SaveTemplate();
            _templates.Add(import.Template);
            RefreshTemplates(import.Template);
            if (import.Skipped.Count > 0)
                _notifications?.Info("部分内容未导入", string.Join("、", import.Skipped), NotificationKey);
        }
        catch (Exception ex) { _notifications?.Error("预设导入失败", ex.Message, NotificationKey); }
    }
}
