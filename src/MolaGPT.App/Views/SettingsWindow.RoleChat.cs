using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace MolaGPT.App.Views;

public partial class SettingsWindow
{
    private void OnPersonaSearchChanged(object? sender, TextChangedEventArgs e) => RefreshPersonaLibrary();

    private void UpdatePersonaViewport()
    {
        PART_PersonaLayout.Height = Math.Max(300, PART_ContentScroll.Bounds.Height
            - PART_Pages.Margin.Top - PART_Pages.Margin.Bottom
            - PART_PersonaPageHeader.Bounds.Height - PART_PersonaPageHeader.Margin.Bottom);
    }

    private void RefreshPersonaLibrary()
    {
        var search = PART_PersonaSearch.Text?.Trim() ?? "";
        var mode = PART_PersonaModeFilter.SelectedIndex;
        var rows = _personas.Personas.Where(persona => search.Length == 0
            || persona.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || persona.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
            || persona.TagsText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || persona.Profile.Creator.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(persona => mode <= 0 || (int)persona.Mode == mode - 1)
            .OrderByDescending(persona => persona.Pinned).ThenBy(persona => persona.SortOrder).ToArray();
        _loadingPersonaForm = true;
        PART_PersonaList.ItemsSource = rows;
        PART_PersonaList.SelectedItem = null;
        _loadingPersonaForm = false;
        PART_PersonaEmpty.IsVisible = rows.Length == 0;
        PART_PersonaEmpty.Text = search.Length > 0 ? "未找到匹配的角色"
            : mode == 1 ? "暂无对话角色" : mode == 2 ? "暂无氛围角色" : "暂无角色";
        PART_ClearPersonaSearch.IsVisible = search.Length > 0;
    }

    private void OnBackToPersonas(object? sender, RoutedEventArgs e)
    {
        PersistEditingPersona();
        _editingPersona = null;
        _editingPersonaIsDraft = false;
        LoadPersonaForm(null);
        RefreshPersonaLibrary();
    }

    private void OnPersonaModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingPersonaForm || _editingPersona is not { IsBuiltin: false } persona || PART_PersonaMode.SelectedIndex < 0) return;
        persona.Mode = (ConversationMode)PART_PersonaMode.SelectedIndex;
        PART_PersonaTabs.SelectedIndex = 0;
        OnPersonaFormChanged(sender, e);
    }

    private void LoadRoleProfile()
    {
        if (_editingPersona is not { } persona) return;
        var models = new List<RoleModelChoice> { new(null, null, "沿用当前模型", true, true) };
        models.AddRange(_settings.Providers.Where(provider => provider.Purpose == "chat")
            .SelectMany(provider => provider.Models.Select(model => new RoleModelChoice(
                provider.Id, model.Id, provider.Name + " · " + model.DisplayName,
                model.SupportsTemperature, model.SupportsTopP))));
        PART_RoleModel.ItemsSource = models;
        PART_RoleModel.SelectedItem = models.FirstOrDefault(model =>
            model.ProviderId == persona.Profile.ProviderId && model.ModelId == persona.Profile.ModelId);
        PART_RoleLorebooks.Content = LorebookButtonLabel(persona.Profile.Lorebooks.Count);
        PART_RoleBooks.ItemsSource = persona.Profile.Lorebooks;
        PART_RoleBooksEmpty.IsVisible = persona.Profile.Lorebooks.Count == 0;
        PART_RoleCompatibility.SelectedIndex = (int)persona.Profile.Compatibility;
        PART_GlobalLoreBudgetRow.IsVisible = persona.Profile.Compatibility == RoleCompatibility.SillyTavern;
        PART_RoleNoteRole.SelectedIndex = persona.Profile.CharacterNoteRole switch { "user" => 1, "assistant" => 2, _ => 0 };
        PART_ExportBook.ItemsSource = persona.Profile.Lorebooks;
        PART_ExportBook.SelectedItem = persona.Profile.Lorebooks.FirstOrDefault(book => book.Id == persona.Profile.EmbeddedLorebookId)
            ?? (persona.Profile.Lorebooks.Count == 1 ? persona.Profile.Lorebooks[0] : null);
        PART_ExportBookRow.IsVisible = persona.Profile.Lorebooks.Count > 1;
        PART_UpdateRoleCard.IsEnabled = !_editingPersonaIsDraft;
        PART_CardExportVersion.SelectedIndex = persona.Profile.CardSpec == "chara_card_v2" ? 1 : 0;
        var entryCount = persona.Profile.Lorebooks.Sum(book => book.Entries.Count);
        PART_CardSummary.Text = (persona.Profile.CardSpec == "chara_card_v2" ? "V2" : "V3")
            + (entryCount > 0 ? $" · 世界书 {entryCount} 条" : "");
        PART_ImportDetails.IsVisible = persona.Profile.ImportNotes.Count > 0;
        PART_ImportNotes.Text = string.Join("\n", persona.Profile.ImportNotes);
        PART_PersonaNetwork.SelectedIndex = persona.DefaultEnableNetwork == true ? 1 : 0;
        PART_PersonaWebFetch.SelectedIndex = persona.DefaultEnableWebFetch == true ? 1 : 0;
        PART_PersonaThinking.SelectedIndex = persona.DefaultThinking == true ? 1 : 0;
        PART_PersonaPython.SelectedIndex = ToolChoiceIndex(persona.Profile.EnablePython);
        PART_PersonaFileTools.SelectedIndex = ToolChoiceIndex(persona.Profile.EnableFileTools);
        PART_PersonaImageGeneration.SelectedIndex = ToolChoiceIndex(persona.Profile.EnableImageGeneration);
        PART_PersonaMcp.SelectedIndex = ToolChoiceIndex(persona.Profile.EnableMcp);
        RefreshAlternateGreetings();
        RefreshRoleLibraryOptions();
        UpdateRoleSamplingControls();
    }

    private static int ToolChoiceIndex(bool? value) => value switch { true => 1, false => 2, null => 0 };

    private static bool? ToolChoiceValue(int index) => index switch { 1 => true, 2 => false, _ => null };

    private void RefreshAlternateGreetings()
    {
        var greetings = _editingPersona?.Profile.AlternateGreetings.ToArray() ?? [];
        PART_AlternateGreetings.ItemsSource = greetings;
        PART_AlternateGreetings.SelectedIndex = greetings.Length == 0 ? -1 : 0;
        PART_AlternateGreetings.PlaceholderText = "暂无备选开场";
        // Both act on the selection; with nothing selected they were still
        // clickable and silently did nothing.
        PART_EditGreeting.IsEnabled = greetings.Length > 0;
        PART_DeleteGreeting.IsEnabled = greetings.Length > 0;
        PART_AlternateGreetingsExpander.Header = greetings.Length == 0
            ? "备选开场" : $"备选开场（{greetings.Length}）";
    }

    private async void OnAddGreeting(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is not { IsBuiltin: false } persona) return;
        var text = await new MessageEditorWindow { Title = "开场白" }.ShowForAsync("", this);
        if (string.IsNullOrWhiteSpace(text)) return;
        persona.Profile.AlternateGreetings.Add(text);
        RefreshAlternateGreetings();
        if (!_editingPersonaIsDraft) _personas.Save(persona);
    }

    private async void OnEditGreeting(object? sender, RoutedEventArgs e)
    {
        var index = PART_AlternateGreetings.SelectedIndex;
        if (_editingPersona is not { IsBuiltin: false } persona || index < 0) return;
        var text = await new MessageEditorWindow { Title = "开场白" }.ShowForAsync(persona.Profile.AlternateGreetings[index], this);
        if (text is null) return;
        persona.Profile.AlternateGreetings[index] = text;
        RefreshAlternateGreetings();
        if (!_editingPersonaIsDraft) _personas.Save(persona);
    }

    private void OnDeleteGreeting(object? sender, RoutedEventArgs e)
    {
        var index = PART_AlternateGreetings.SelectedIndex;
        if (_editingPersona is not { IsBuiltin: false } persona || index < 0) return;
        persona.Profile.AlternateGreetings.RemoveAt(index);
        RefreshAlternateGreetings();
        if (!_editingPersonaIsDraft) _personas.Save(persona);
    }

    private void OnRoleModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingPersonaForm || _editingPersona is not { IsBuiltin: false } persona
            || PART_RoleModel.SelectedItem is not RoleModelChoice model) return;
        persona.Profile.ProviderId = model.ProviderId;
        persona.Profile.ModelId = model.ModelId;
        UpdateRoleSamplingControls();
        OnPersonaFormChanged(sender, e);
    }

    private static string LorebookButtonLabel(int count) => count == 0 ? "添加" : "管理";

    private void RefreshRoleLibraryOptions()
    {
        if (_editingPersona is not { } persona) return;
        var choices = new List<RoleIdentityChoice> { new(null, "默认身份"), new("", "使用角色内设定") };
        choices.AddRange(_personas.Library.Identities.Select(identity => new RoleIdentityChoice(identity.Id, identity.Name)));
        PART_RoleIdentity.ItemsSource = choices;
        PART_RoleIdentity.SelectedItem = choices.FirstOrDefault(choice => choice.Id == persona.Profile.UserPersonaId);
        PART_RoleManualIdentity.IsVisible = _personas.Library.ResolveIdentity(persona.Profile.UserPersonaId) is null;
        PART_SharedBooks.Children.Clear();
        foreach (var book in _personas.Library.Books.OrderBy(book => book.Name))
        {
            var toggle = new CheckBox { Content = $"{book.Name} · {book.Entries.Count} 条" + (book.Enabled ? "" : " · 已停用"), Tag = book.Id,
                IsChecked = persona.Profile.SharedLorebookIds.Contains(book.Id) };
            toggle.Click += (_, _) =>
            {
                if (_loadingPersonaForm || _editingPersona is null) return;
                _editingPersona.Profile.SharedLorebookIds = PART_SharedBooks.Children.OfType<CheckBox>()
                    .Where(box => box.IsChecked == true).Select(box => (string)box.Tag!).ToList();
                OnPersonaFormChanged(toggle, new RoutedEventArgs());
            };
            PART_SharedBooks.Children.Add(toggle);
        }
        PART_SharedBooksEmpty.IsVisible = _personas.Library.Books.Count == 0;
    }

    private void OnRoleIdentityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingPersonaForm || _editingPersona is not { IsBuiltin: false } persona
            || PART_RoleIdentity.SelectedItem is not RoleIdentityChoice choice) return;
        persona.Profile.UserPersonaId = choice.Id;
        PART_RoleManualIdentity.IsEnabled = choice.Id == "";
        if (choice.Id == "") PART_RoleExtraDefinitions.IsExpanded = true;
        OnPersonaFormChanged(sender, e);
    }

    private async void OnManageIdentities(object? sender, RoutedEventArgs e)
    {
        await new UserPersonaWindow
        {
            CheckIdentityDeletion = identity => _editingPersona?.Profile.UserPersonaId == identity.Id ? "当前角色仍在使用这个身份。" : null
        }.ShowForAsync(_personas.Library, _notifications, this);
        _loadingPersonaForm = true;
        RefreshRoleLibraryOptions();
        _loadingPersonaForm = false;
    }

    private async void OnSharedLorebooks(object? sender, RoutedEventArgs e)
    {
        var window = new LorebookWindow(_notifications)
        {
            Title = "共享世界书",
            CheckBookDeletion = book => _editingPersona?.Profile.SharedLorebookIds.Contains(book.Id) == true
                ? "当前角色仍在使用这本世界书。" : _personas.Library.BookDeletionReason(book)
        };
        var books = await window.ShowForAsync(_personas.Library.Books.ToList(), this,
            _editingPersona?.Profile.Compatibility ?? RoleCompatibility.CharacterCardSpec);
        if (books is null) return;
        try { _personas.Library.SaveBooks(books); }
        catch (Exception ex) { _notifications?.Error("共享世界书保存失败", ex.Message, "lorebook-library"); }
        _loadingPersonaForm = true;
        RefreshRoleLibraryOptions();
        _loadingPersonaForm = false;
    }

    private void UpdateRoleSamplingControls()
    {
        var model = PART_RoleModel.SelectedItem as RoleModelChoice;
        PART_RoleTemperature.IsEnabled = model?.SupportsTemperature == true;
        PART_RoleTopP.IsEnabled = model?.SupportsTopP == true;
    }

    private async void OnPersonaImage(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is not { IsBuiltin: false } persona) return;
        PART_PersonaIconPicker.Flyout?.Hide();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择头像", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }]
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            persona.Avatar = PersonaAvatar.EncodeImage(bytes);
            persona.Profile.CardAvatarChanged = true;
            persona.PendingAvatarSource = bytes;
            persona.PendingAvatarFileName = files[0].Name;
            if (!_editingPersonaIsDraft) _personas.Save(persona);
        }
        catch (Exception ex) { _notifications?.Error("头像读取失败", ex.Message, "persona-image"); }
    }

    /// <summary>Back to the glyph the role had before, or the default one.</summary>
    private void OnPersonaImageClear(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is not { IsBuiltin: false } persona || !persona.HasImageAvatar) return;
        PART_PersonaIconPicker.Flyout?.Hide();
        persona.Avatar = null;
        persona.Profile.CardAvatarChanged = true;
        persona.Profile.AvatarSourceFile = null;
        persona.PendingAvatarSource = null;
        if (!_editingPersonaIsDraft) _personas.Save(persona);
    }

    private async void OnImportPersona(object? sender, RoutedEventArgs e) => await ImportPersonaAsync(false);

    private async void OnUpdateRoleCard(object? sender, RoutedEventArgs e) => await ImportPersonaAsync(true);

    private async Task ImportPersonaAsync(bool update)
    {
        var target = update ? _editingPersona : null;
        if (update && target is not { IsBuiltin: false }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入角色卡", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("角色卡") { Patterns = ["*.json", "*.png", "*.charx"] }]
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            var imported = CharacterCardReader.Read(bytes);
            var avatar = imported.Avatar is null ? null : PersonaAvatar.EncodeImage(imported.Avatar);
            PersistEditingPersona();
            _loadingPersonaForm = true;
            PART_PersonaList.SelectedItem = null;
            _loadingPersonaForm = false;
            var draft = target ?? _personas.CreateBlankDraft();
            _personas.ApplyImportedCard(draft, imported, bytes, files[0].Name, avatar, update);
            _editingPersona = draft;
            _editingPersonaIsDraft = !update;
            if (update) _personas.Save(draft);
            LoadPersonaForm(draft);
        }
        catch (Exception ex) { _notifications?.Error("角色卡导入失败", ex.Message, "persona-import"); }
    }

    private void OnRoleCardOptionsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingPersonaForm || _editingPersona is not { IsBuiltin: false } persona) return;
        if (PART_RoleCompatibility.SelectedIndex >= 0) persona.Profile.Compatibility = (RoleCompatibility)PART_RoleCompatibility.SelectedIndex;
        PART_GlobalLoreBudgetRow.IsVisible = persona.Profile.Compatibility == RoleCompatibility.SillyTavern;
        persona.Profile.CharacterNoteRole = PART_RoleNoteRole.SelectedIndex switch { 1 => "user", 2 => "assistant", _ => "system" };
        persona.Profile.EmbeddedLorebookId = (PART_ExportBook.SelectedItem as Lorebook)?.Id;
        OnPersonaFormChanged(sender, e);
    }

    private async void OnExportPersona(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is not { } persona) return;
        var spec = PART_CardExportVersion.SelectedIndex == 1 ? "chara_card_v2" : "chara_card_v3";
        SyncPersonaForm();
        var png = (sender as Button)?.Tag as string == "png";
        var extension = png ? "png" : "json";
        var name = string.Concat(persona.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出角色卡", SuggestedFileName = name + "." + extension,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(png ? "PNG 角色卡" : "JSON 角色卡") { Patterns = ["*." + extension] }]
        });
        if (file is null) return;
        try
        {
            var json = _personas.ExportCardJson(persona, spec);
            var output = png ? CharacterCardWriter.WritePng(CardImage(persona), json, _personas.ReadCardSource(persona)) : json;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(output);
            _notifications?.Success("角色卡已导出", persona.Name, key: "persona-export");
        }
        catch (Exception ex) { _notifications?.Error("角色卡导出失败", ex.Message, "persona-export"); }
    }

    private byte[] CardImage(PersonaItemViewModel persona)
    {
        if (!persona.Profile.CardAvatarChanged && _personas.ReadCardSource(persona) is { } source)
        {
            var originalAvatar = CharacterCardReader.Read(source).Avatar;
            if (originalAvatar is not null) return ToPng(originalAvatar);
        }
        if (_personas.ReadAvatarSource(persona) is { } fullImage) return ToPng(fullImage);
        if (persona.HasImageAvatar)
        {
            var avatar = persona.Avatar!;
            return Convert.FromBase64String(avatar[(avatar.IndexOf(',') + 1)..]);
        }
        var surface = new Border
        {
            Width = 512, Height = 512, Background = new SolidColorBrush(Color.Parse("#F2F3F6")),
            Child = new PersonaAvatar { Value = persona.Avatar, Width = 256, Height = 256,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
        };
        surface.Measure(new Size(512, 512));
        surface.Arrange(new Rect(0, 0, 512, 512));
        using var bitmap = new RenderTargetBitmap(new PixelSize(512, 512), new Vector(96, 96));
        bitmap.Render(surface);
        using var output = new MemoryStream();
        bitmap.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }

    private static byte[] ToPng(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return bytes;
        using var image = SKImage.FromEncodedData(bytes) ?? throw new InvalidDataException("无法读取角色头像。");
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private async void OnRoleLorebooks(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is not { IsBuiltin: false } persona) return;
        var window = new LorebookWindow(_notifications) { Title = "角色世界书" };
        var books = await window.ShowForAsync(persona.Profile.Lorebooks, this, persona.Profile.Compatibility);
        if (books is null) return;
        persona.Profile.Lorebooks = books;
        if (!books.Any(book => book.Id == persona.Profile.EmbeddedLorebookId))
            persona.Profile.EmbeddedLorebookId = books.Count == 1 ? books[0].Id : null;
        PART_RoleLorebooks.Content = LorebookButtonLabel(books.Count);
        _loadingPersonaForm = true;
        LoadRoleProfile();
        _loadingPersonaForm = false;
        if (!_editingPersonaIsDraft) _personas.Save(persona);
    }
}

public sealed record RoleModelChoice(
    string? ProviderId, string? ModelId, string Label, bool SupportsTemperature, bool SupportsTopP);

public sealed record RoleIdentityChoice(string? Id, string Label);
