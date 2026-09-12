using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class UserPersonaWindow : MolaContentWindow
{
    private RoleLibraryViewModel _library = null!;
    private NotificationCenter? _notifications;
    private List<UserPersona> _drafts = [];
    private UserPersona? _selected;
    private string? _defaultId;
    private bool _loading;
    public Func<UserPersona, string?>? CheckIdentityDeletion { get; init; }

    public UserPersonaWindow()
    {
        InitializeComponent();
        PART_Identities.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveEditor();
            Load(PART_Identities.SelectedItem as UserPersona);
        };
        PART_Name.LostFocus += (_, _) => { SaveEditor(); Refresh(_selected); };
        PART_ChangeAvatar.Click += async (_, _) =>
        {
            if (_selected is not { } identity) return;
            SaveEditor();
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择头像", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }]
            });
            if (files.Count == 0) return;
            try
            {
                await using var stream = await files[0].OpenReadAsync();
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                identity.Avatar = PersonaAvatar.EncodeImage(bytes.ToArray());
                Load(identity);
            }
            catch (Exception ex) { _notifications?.Error("头像读取失败", ex.Message, "role-identity"); }
        };
        PART_ClearAvatar.Click += (_, _) =>
        {
            if (_selected is null) return;
            SaveEditor();
            _selected.Avatar = null;
            Load(_selected);
        };
        PART_Default.Click += (_, _) =>
        {
            if (_selected is null) return;
            if (PART_Default.IsChecked == true) _defaultId = _selected.Id;
            else if (_defaultId == _selected.Id) _defaultId = null;
        };
        PART_Add.Click += (_, _) =>
        {
            SaveEditor();
            var identity = new UserPersona { Name = "新身份" };
            _drafts.Add(identity);
            Refresh(identity);
            Dispatcher.UIThread.Post(() => { PART_Name.Focus(); PART_Name.SelectAll(); });
        };
        PART_Delete.Click += (_, _) =>
        {
            if (_selected is null) return;
            if ((CheckIdentityDeletion?.Invoke(_selected) ?? _library.IdentityDeletionReason(_selected)) is { } reason)
            { _notifications?.Error("无法删除身份", reason, "role-identity"); return; }
            if (_defaultId == _selected.Id) _defaultId = null;
            _drafts.Remove(_selected);
            Refresh(_drafts.FirstOrDefault());
        };
        PART_Cancel.Click += (_, _) => Close(false);
        PART_Save.Click += (_, _) =>
        {
            SaveEditor();
            try { _library.SaveIdentities(_drafts, _defaultId); Close(true); }
            catch (Exception ex) { _notifications?.Error("身份保存失败", ex.Message, "role-identity"); }
        };
    }

    public Task<bool> ShowForAsync(RoleLibraryViewModel library, NotificationCenter? notifications, Window owner)
    {
        _library = library;
        _notifications = notifications;
        _drafts = RoleJson.Deserialize<List<UserPersona>>(RoleJson.Serialize(library.Identities));
        _defaultId = library.DefaultIdentityId;
        Refresh(_drafts.FirstOrDefault());
        return ShowDialog<bool>(owner);
    }

    private void SaveEditor()
    {
        if (_loading || _selected is null) return;
        _selected.Name = PART_Name.Text?.Trim() ?? "";
        _selected.Description = PART_Description.Text ?? "";
    }

    private void Refresh(UserPersona? selected)
    {
        _loading = true;
        PART_Identities.ItemsSource = _drafts.ToArray();
        PART_Identities.SelectedItem = selected;
        _loading = false;
        Load(selected);
    }

    private void Load(UserPersona? selected)
    {
        _selected = selected;
        PART_Editor.IsVisible = selected is not null;
        PART_Empty.IsVisible = selected is null;
        PART_Delete.IsEnabled = selected is not null;
        if (selected is null) return;
        _loading = true;
        PART_Name.Text = selected.Name;
        PART_Description.Text = selected.Description;
        PART_Avatar.Value = selected.Avatar ?? "\uE77B";
        PART_ClearAvatar.IsVisible = selected.Avatar is not null;
        PART_ChangeAvatar.Content = selected.Avatar is null ? "选择头像" : "更换头像";
        PART_Default.IsChecked = _defaultId == selected.Id;
        _loading = false;
    }
}
