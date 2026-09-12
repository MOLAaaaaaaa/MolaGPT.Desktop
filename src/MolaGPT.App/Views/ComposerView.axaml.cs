using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();

        // Tunnel, not bubble. TextBox handles Enter in a class handler — with
        // AcceptsReturn it inserts the newline and marks the event handled — and
        // class handlers run before instance ones, so a plain `KeyDown +=` never
        // saw the key at all: "Enter 发送" was on, and Enter still broke the line.
        // This is the phase WPF's PreviewKeyDown gave for free.
        PART_Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        PART_Attach.Click += OnAttachClick;
        PART_ClearPersona.Click += (_, _) => ClearPersona();
        PART_NewPersona.Click += (_, _) => OpenPersonaSettings(true);
        PART_ManagePersonas.Click += (_, _) => OpenPersonaSettings(false);
        PART_PersonaSearch.TextChanged += (_, _) => RefreshPersonaRows();
        PART_PersonaSearch.AddHandler(KeyDownEvent, OnPersonaSearchKeyDown, RoutingStrategies.Tunnel);
        PART_ClearPersonaSearch.Click += (_, _) =>
        {
            PART_PersonaSearch.Clear();
            PART_PersonaSearch.Focus();
        };
        PART_RoleNewChat.Click += (_, _) =>
        {
            if (DataContext is not ComposerViewModel vm || vm.Chat.ActivePersonaId is not { } id) return;
            PART_Persona.Flyout?.Hide();
            try { vm.Chat.StartRoleConversation(id); }
            catch (Exception ex) { PersonaSelectionFailed?.Invoke(this, ex.Message); }
            FocusInput();
        };
        if (PART_Persona.Flyout is { } personaFlyout)
            personaFlyout.Opened += (_, _) =>
            {
                RefreshPersonaRows();
                Dispatcher.UIThread.Post(() =>
                {
                    PART_PersonaSearch.Focus();
                    PART_PersonaSearch.SelectAll();
                });
            };

        // Drop is handled on the whole card, not just the text box: dropping on
        // the padding around the input is the same gesture to a user.
        PART_Shell.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        PART_Shell.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Left-hand hint text in the action bar.</summary>
    public void SetHint(string? hint) => PART_Hint.Text = hint ?? string.Empty;

    public void FocusInput() => PART_Input.Focus();

    public event EventHandler<bool>? PersonaSettingsRequested;
    public event EventHandler<string>? PersonaSelectionFailed;

    private void RefreshPersonaRows()
    {
        if (DataContext is not ComposerViewModel vm) return;

        PART_ClearPersona.IsVisible = !string.IsNullOrWhiteSpace(vm.Chat.ActivePersonaId);
        PART_RoleNewChat.IsEnabled = vm.Chat.CanEditHistory && vm.Chat.ActivePersonaId is not null;
        PART_ClearPersonaSearch.IsVisible = !string.IsNullOrEmpty(PART_PersonaSearch.Text);
        var search = PART_PersonaSearch.Text?.Trim() ?? "";
        var rows = vm.Personas?.Personas
            .Where(persona => search.Length == 0 || persona.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || persona.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
                || persona.TagsText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || persona.Profile.Creator.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(persona => persona.Pinned).ThenBy(persona => persona.SortOrder)
            .Select(persona => new PersonaSelectorRow(
                persona.Id,
                persona.Avatar ?? persona.DisplayAvatar,
                persona.Name,
                persona.Preview,
                persona.ModeLabel,
                string.Equals(persona.Id, vm.Chat.ActivePersonaId, StringComparison.Ordinal)))
            .ToArray() ?? [];
        PART_PersonaList.ItemsSource = rows;
        // Two different empties, two different sentences: "you have no roles" is
        // an invitation, "your search matched nothing" is a dead end.
        PART_PersonaEmpty.IsVisible = rows.Length == 0;
        PART_PersonaEmpty.Text = search.Length > 0 ? "未找到匹配的角色" : "暂无角色";
    }

    private void ClearPersona()
    {
        if (DataContext is not ComposerViewModel vm) return;
        vm.Chat.SaveActivePersona(null);
        PART_Persona.Flyout?.Hide();
        FocusInput();
    }

    private void OnPickPersona(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id } || string.IsNullOrWhiteSpace(id)) return;
        PickPersona(id);
    }

    private void OnPersonaSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Down or Key.Up or Key.Enter)) return;
        var rows = PART_PersonaList.GetVisualDescendants().OfType<Button>();
        var row = e.Key == Key.Up ? rows.LastOrDefault() : rows.FirstOrDefault();
        if (row is null) return;

        e.Handled = true;
        if (e.Key == Key.Enter && row.Tag is string id) PickPersona(id);
        else
        {
            row.Focus(NavigationMethod.Directional);
            row.BringIntoView();
        }
    }

    private void PickPersona(string id)
    {
        if (DataContext is not ComposerViewModel vm) return;

        try { vm.Chat.SaveActivePersona(id); }
        catch (Exception ex) { PersonaSelectionFailed?.Invoke(this, ex.Message); }
        PART_Persona.Flyout?.Hide();
        FocusInput();
    }

    private void OpenPersonaSettings(bool startNew)
    {
        PART_Persona.Flyout?.Hide();
        PersonaSettingsRequested?.Invoke(this, startNew);
    }

    /// <summary>
    /// Enter sends, Shift+Enter inserts a newline — unless the user turned that
    /// off in settings, in which case the roles swap. The preference is read
    /// through the view model rather than cached, because a local copy of it
    /// silently drifted from the settings page once already.
    /// </summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;

        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = TryPasteAttachmentsAsync(vm);
            return;
        }

        if (e.Key != Key.Enter) return;

        // Ctrl+Enter always sends, in both preference modes.
        var wantsSend = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || (vm.EnterToSend && !e.KeyModifiers.HasFlag(KeyModifiers.Shift));

        if (!wantsSend) return;

        e.Handled = true;
        if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    // ---- attachments -------------------------------------------------------

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片或文件",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { Length: > 0 } path) AddFromPath(vm, path);
        }
    }

    /// <summary>
    /// Read one file from disk and queue it. Shared by the attach button,
    /// clipboard paste and drag-and-drop so all three enforce the same limits.
    /// </summary>
    private void AddFromPath(ComposerViewModel vm, string path) =>
        Queue(vm, AttachmentIntake.FromFile(path, Capabilities(vm)));

    /// <summary>
    /// A refused attachment reports why and is skipped; the caller keeps going
    /// with the rest of the batch. Silently dropping one file out of five is the
    /// failure mode this avoids.
    /// </summary>
    private void Queue(ComposerViewModel vm, AttachmentIntakeResult result)
    {
        if (result.Attachment is { } attachment)
        {
            vm.Attachments.Add(attachment);
            return;
        }

        Notify(result.Error ?? "无法添加该附件。");
    }

    private static AttachmentIntakeCapabilities Capabilities(ComposerViewModel vm) =>
        new(vm.CanAcceptImageAttachments, vm.CanProcessOpaqueFiles);

    private void Notify(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new MolaDialogWindow("附件");
        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
            }
        };

        var ok = new Button
        {
            Content = "好",
            Classes = { "primary" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        ok.Click += (_, _) => dialog.Close();
        body.Children.Add(ok);
        dialog.SetBody(body);

        _ = dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Ctrl+V: queue clipboard files or a pasted bitmap. Plain text is left
    /// alone so the TextBox pastes it normally — the handler does not mark the
    /// event handled, because deciding requires an await and by then the key
    /// event has already been delivered.
    /// </summary>
    private async Task TryPasteAttachmentsAsync(ComposerViewModel vm)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            // Avalonia 12 hands back an IAsyncDataTransfer that the caller owns.
            using var data = await clipboard.TryGetDataAsync();
            if (data is null) return;

            if (data.Contains(DataFormat.File)
                && await data.TryGetFilesAsync() is { Length: > 0 } files)
            {
                foreach (var item in files)
                {
                    if (item.TryGetLocalPath() is { Length: > 0 } path) AddFromPath(vm, path);
                }
                return;
            }

            // A screenshot arrives as a bitmap, not a file. Re-encode it to PNG
            // because that is what the attachment pipeline stores and sends.
            if (data.Contains(DataFormat.Bitmap)
                && await data.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    using var buffer = new MemoryStream();
                    bitmap.Save(buffer, PngBitmapEncoderOptions.Default);
                    Queue(vm, AttachmentIntake.FromBytes(
                        buffer.ToArray(), $"粘贴图片_{DateTime.Now:HHmmss}.png", Capabilities(vm)));
                }
            }

            // Anything else (plain text) is left for the TextBox's own paste.
        }
        catch
        {
            // The clipboard is shared state and can be locked by another process
            // mid-read; a failed paste is not worth surfacing.
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var isFileDrop = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = isFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = isFileDrop;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;

        e.Handled = true;
        foreach (var item in files)
        {
            if (item.TryGetLocalPath() is not { Length: > 0 } path) continue;

            // Dropping a folder is a common mis-drop; say so instead of failing
            // with a bare read error.
            if (Directory.Exists(path))
            {
                Notify($"{Path.GetFileName(path)} 是文件夹，请拖入具体的文件。");
                continue;
            }
            AddFromPath(vm, path);
        }
    }

    private void OnRemoveAttachment(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;
        if (sender is Control { DataContext: Attachment attachment })
            vm.Attachments.Remove(attachment);

        // Stop the click reaching the card, which would open the preview.
        e.Handled = true;
    }

    /// <summary>Clicking an image chip opens it full size; other kinds do
    /// nothing, matching the original.</summary>
    private void OnAttachmentCardClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (sender is not Control { DataContext: Attachment attachment }) return;
        if (!attachment.IsImage || attachment.Bytes is not { Length: > 0 } bytes) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        _ = ImagePreviewWindow.ShowAsync(owner, bytes, attachment.FileName);
        e.Handled = true;
    }
}

public sealed record PersonaSelectorRow(
    string Id,
    string Avatar,
    string Name,
    string Preview,
    string ModeLabel,
    bool IsActive)
{
    /// <summary>Collapses the second line instead of leaving an empty one: a
    /// blank sub-label still occupies its line box, so rows for personas with no
    /// summary came out taller than their content.</summary>
    public bool HasPreview => Preview.Length > 0;
}
