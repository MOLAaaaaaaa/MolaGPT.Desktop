using System.Windows;
using System.Windows.Input;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Conversation-level system prompt editor.
///
/// Two layout variants depending on whether a persona is bound:
///
/// **With persona** — three radios on top:
///   1. 使用角色默认提示词         → conversationPrompt = null, mode = "override"
///   2. 在角色提示词之后追加        → conversationPrompt = text,  mode = "append"
///   3. 完全替换为下方文本          → conversationPrompt = text,  mode = "override"
///
/// **Without persona** — two radios:
///   1. 不发送 system 消息          → conversationPrompt = null
///   2. 使用下方文本作为 system 消息 → conversationPrompt = text
///
/// The textbox is automatically disabled in modes that don't read from it
/// (defaults to gray italic placeholder), so users can't type into the void.
///
/// Persistence happens on Save. Cancel discards. Reset clears the textbox
/// and selects the "use default" radio.
/// </summary>
public partial class SystemPromptDialog : Window
{
    private ChatViewModel? _chatVm;
    private bool _hasPersona;
    private bool _suspendUpdates;

    public bool Confirmed { get; private set; }

    public SystemPromptDialog()
    {
        InitializeComponent();
    }

    /// <summary>Open the dialog for the given chat. Reads ActivePersona,
    /// ConversationSystemPrompt and SystemPromptMode for initialization;
    /// writes back via the corresponding Save* methods.</summary>
    public void Show(ChatViewModel chatVm, Window owner)
    {
        _chatVm = chatVm;
        Owner = owner;
        _suspendUpdates = true;
        try
        {
            PromptTextBox.Text = chatVm.ConversationSystemPrompt ?? string.Empty;

            var persona = chatVm.ActivePersona;
            _hasPersona = persona is not null && !string.IsNullOrWhiteSpace(persona.SystemPrompt);

            if (persona is not null)
            {
                PersonaAvatar.Text = persona.DisplayAvatar;
                PersonaName.Text = persona.Name;
                if (string.IsNullOrWhiteSpace(persona.SystemPrompt))
                {
                    PersonaHint.Text = "此角色未设置默认提示词。下方文本会作为唯一的 system 消息发送。";
                }
                else
                {
                    PersonaHint.Text = "选择如何与角色默认提示词配合，或直接替换。";
                    PersonaPromptBox.Text = persona.SystemPrompt;
                }
            }
            else
            {
                PersonaAvatar.Text = PersonaIconCatalog.DefaultGlyph;
                PersonaName.Text = "未选择";
                PersonaHint.Text = "尚未为此对话绑定角色。可在输入框工具栏的角色选择器中挑选，或直接在下方填写一次性提示词。";
            }

            // Layout switch: persona-mode shows the 3-radio panel + persona
            // prompt expander; no-persona-mode shows the 2-radio panel.
            ModePanel.Visibility = _hasPersona ? Visibility.Visible : Visibility.Collapsed;
            NoPersonaModePanel.Visibility = _hasPersona ? Visibility.Collapsed : Visibility.Visible;
            PersonaPromptExpander.Visibility = _hasPersona ? Visibility.Visible : Visibility.Collapsed;

            // Restore radio state from the persisted (prompt, mode) pair.
            var prompt = chatVm.ConversationSystemPrompt;
            var isAppend = string.Equals(chatVm.SystemPromptMode, "append", StringComparison.OrdinalIgnoreCase);
            if (_hasPersona)
            {
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    ModeInheritRadio.IsChecked = true;
                }
                else if (isAppend)
                {
                    ModeAppendRadio.IsChecked = true;
                }
                else
                {
                    ModeOverrideRadio.IsChecked = true;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(prompt))
                    NoPersonaEmptyRadio.IsChecked = true;
                else
                    NoPersonaCustomRadio.IsChecked = true;
            }
        }
        finally
        {
            _suspendUpdates = false;
        }

        ApplyEditorEnabledState();
        UpdateModeHint();
        ShowDialog();
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_chatVm is null) { Close(); return; }
        Confirmed = true;

        // Translate radio state back into (prompt, mode). The text in the
        // editor is only persisted when the active mode actually consumes it;
        // otherwise we save null so the conversation falls back to the
        // persona's default cleanly.
        string? prompt;
        string mode;
        if (_hasPersona)
        {
            if (ModeInheritRadio.IsChecked == true)
            {
                prompt = null;
                mode = "override";
            }
            else if (ModeAppendRadio.IsChecked == true)
            {
                prompt = NormalizePrompt(PromptTextBox.Text);
                mode = "append";
            }
            else
            {
                prompt = NormalizePrompt(PromptTextBox.Text);
                mode = "override";
            }
        }
        else
        {
            if (NoPersonaCustomRadio.IsChecked == true)
            {
                prompt = NormalizePrompt(PromptTextBox.Text);
                mode = "override";
            }
            else
            {
                prompt = null;
                mode = "override";
            }
        }

        _chatVm.SaveConversationSystemPrompt(prompt);
        _chatVm.SaveSystemPromptMode(mode);
        Close();
    }

    /// <summary>Reset the dialog to "use persona default" / "no system" state.
    /// Doesn't persist — user still has to click Save.</summary>
    private void ResetClick(object sender, RoutedEventArgs e)
    {
        _suspendUpdates = true;
        try
        {
            PromptTextBox.Text = string.Empty;
            if (_hasPersona)
                ModeInheritRadio.IsChecked = true;
            else
                NoPersonaEmptyRadio.IsChecked = true;
        }
        finally
        {
            _suspendUpdates = false;
        }
        ApplyEditorEnabledState();
        UpdateModeHint();
    }

    private void CopyPersonaToEditorClick(object sender, RoutedEventArgs e)
    {
        var source = _chatVm?.ActivePersona?.SystemPrompt;
        if (string.IsNullOrWhiteSpace(source)) return;

        // Switch to override mode so the copied text is actually used.
        _suspendUpdates = true;
        try
        {
            PromptTextBox.Text = source;
            ModeOverrideRadio.IsChecked = true;
        }
        finally
        {
            _suspendUpdates = false;
        }
        ApplyEditorEnabledState();
        UpdateModeHint();

        PromptTextBox.CaretIndex = PromptTextBox.Text.Length;
        PromptTextBox.Focus();
    }

    /// <summary>Insert the chip's <c>Tag</c> token at the current caret position
    /// inside the prompt editor — mirrors how PromptBuddy / LobeChat surface
    /// variable shortcuts. Auto-switches to a writable mode if the editor is
    /// currently read-only.</summary>
    private void InsertVariableClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var token = fe.Tag?.ToString();
        if (string.IsNullOrEmpty(token)) return;

        // If user clicks a variable chip while in inherit/empty mode, jump
        // them to the matching "writable" radio so the keystroke isn't lost.
        if (!IsEditorWritable())
        {
            _suspendUpdates = true;
            try
            {
                if (_hasPersona)
                    ModeAppendRadio.IsChecked = true;
                else
                    NoPersonaCustomRadio.IsChecked = true;
            }
            finally
            {
                _suspendUpdates = false;
            }
            ApplyEditorEnabledState();
            UpdateModeHint();
        }

        var start = PromptTextBox.SelectionStart;
        var len = PromptTextBox.SelectionLength;
        var text = PromptTextBox.Text ?? string.Empty;
        if (len > 0) text = text.Remove(start, len);
        text = text.Insert(start, token);
        PromptTextBox.Text = text;
        PromptTextBox.CaretIndex = start + token.Length;
        PromptTextBox.Focus();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SaveClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendUpdates) return;
        ApplyEditorEnabledState();
        UpdateModeHint();
    }

    private void NoPersonaModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendUpdates) return;
        ApplyEditorEnabledState();
    }

    private void PromptTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suspendUpdates || ResetButton is null) return;
        // Once the user starts typing, surface the change in the radio state
        // so they don't have to remember to flip it manually before saving.
        // Don't auto-switch when text is empty (could be from Reset).
        if (string.IsNullOrEmpty(PromptTextBox.Text)) return;
        if (!_hasPersona && NoPersonaEmptyRadio.IsChecked == true)
            NoPersonaCustomRadio.IsChecked = true;
    }

    private void ApplyEditorEnabledState()
    {
        if (PromptTextBox is null) return;
        var writable = IsEditorWritable();
        PromptTextBox.IsEnabled = writable;
        if (EditorBorder is not null)
            EditorBorder.Opacity = writable ? 1.0 : 0.55;
        if (EditorLabel is not null)
            EditorLabel.Opacity = writable ? 1.0 : 0.55;
    }

    private bool IsEditorWritable()
    {
        if (_hasPersona)
            return ModeAppendRadio?.IsChecked == true || ModeOverrideRadio?.IsChecked == true;
        return NoPersonaCustomRadio?.IsChecked == true;
    }

    private void UpdateModeHint()
    {
        if (ModeHint is null) return;
        if (!_hasPersona) { ModeHint.Text = string.Empty; return; }

        if (ModeInheritRadio?.IsChecked == true)
            ModeHint.Text = "发送给模型的 system 内容 = 角色默认提示词。";
        else if (ModeAppendRadio?.IsChecked == true)
            ModeHint.Text = "发送给模型的 system 内容 = 角色提示词 + 空行 + 下方文本。";
        else if (ModeOverrideRadio?.IsChecked == true)
            ModeHint.Text = "发送给模型的 system 内容 = 下方文本（角色默认被忽略）。";
    }

    private static string? NormalizePrompt(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
