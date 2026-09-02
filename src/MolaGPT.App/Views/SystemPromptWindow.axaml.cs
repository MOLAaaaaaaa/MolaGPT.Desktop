using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.App.Views;

public partial class SystemPromptWindow : MolaContentWindow
{
    private ChatViewModel? _chat;
    private bool _hasPersona;
    private bool _loading;

    public SystemPromptWindow()
    {
        InitializeComponent();
        PART_Close.Click += (_, _) => Close(false);
        PART_Cancel.Click += (_, _) => Close(false);
        PART_Save.Click += (_, _) => Save();
        PART_Reset.Click += (_, _) => Reset();
        PART_CopyPersona.Click += (_, _) => CopyPersonaPrompt();
        PART_Inherit.IsCheckedChanged += (_, _) => ModeChanged();
        PART_Append.IsCheckedChanged += (_, _) => ModeChanged();
        PART_Override.IsCheckedChanged += (_, _) => ModeChanged();
        PART_NoSystem.IsCheckedChanged += (_, _) => ModeChanged();
        PART_CustomSystem.IsCheckedChanged += (_, _) => ModeChanged();
        PART_Prompt.TextChanged += (_, _) => PromptChanged();
        KeyDown += OnKeyDown;
    }

    public Task<bool> ShowForAsync(ChatViewModel chat, Window owner)
    {
        _chat = chat;
        LoadFromChat(chat);
        return ShowDialog<bool>(owner);
    }

    private void LoadFromChat(ChatViewModel chat)
    {
        _loading = true;
        try
        {
            PART_Prompt.Text = chat.ConversationSystemPrompt ?? string.Empty;
            var persona = chat.ActivePersona;
            _hasPersona = persona is not null && !string.IsNullOrWhiteSpace(persona.SystemPrompt);

            PART_PersonaAvatar.Text = persona?.DisplayAvatar ?? PersonaIconCatalog.DefaultGlyph;
            PART_PersonaName.Text = persona?.Name ?? "未选择";
            PART_PersonaHint.Text = persona is null
                ? "尚未为此对话绑定角色。可在输入区选择角色，或直接填写一次性提示词。"
                : _hasPersona
                    ? "选择如何与角色默认提示词配合，或直接替换。"
                    : "此角色未设置默认提示词。下方文本会作为唯一的 system 消息发送。";
            PART_PersonaPrompt.Text = persona?.SystemPrompt ?? string.Empty;

            PART_PersonaModes.IsVisible = _hasPersona;
            PART_NoPersonaModes.IsVisible = !_hasPersona;
            PART_PersonaPromptExpander.IsVisible = _hasPersona;

            var hasPrompt = !string.IsNullOrWhiteSpace(chat.ConversationSystemPrompt);
            if (_hasPersona)
            {
                if (!hasPrompt) PART_Inherit.IsChecked = true;
                else if (string.Equals(chat.SystemPromptMode, "append", StringComparison.OrdinalIgnoreCase))
                    PART_Append.IsChecked = true;
                else PART_Override.IsChecked = true;
            }
            else
            {
                if (hasPrompt) PART_CustomSystem.IsChecked = true;
                else PART_NoSystem.IsChecked = true;
            }
        }
        finally
        {
            _loading = false;
        }

        ApplyMode();
    }

    private void Save()
    {
        if (_chat is null)
        {
            Close(false);
            return;
        }

        string? prompt;
        string mode;
        if (_hasPersona)
        {
            if (PART_Inherit.IsChecked == true)
            {
                prompt = null;
                mode = "override";
            }
            else
            {
                prompt = Normalize(PART_Prompt.Text);
                mode = PART_Append.IsChecked == true ? "append" : "override";
            }
        }
        else
        {
            prompt = PART_CustomSystem.IsChecked == true ? Normalize(PART_Prompt.Text) : null;
            mode = "override";
        }

        _chat.SaveConversationSystemPrompt(prompt);
        _chat.SaveSystemPromptMode(mode);
        Close(true);
    }

    private void Reset()
    {
        _loading = true;
        PART_Prompt.Text = string.Empty;
        if (_hasPersona) PART_Inherit.IsChecked = true;
        else PART_NoSystem.IsChecked = true;
        _loading = false;
        ApplyMode();
    }

    private void CopyPersonaPrompt()
    {
        var prompt = _chat?.ActivePersona?.SystemPrompt;
        if (string.IsNullOrWhiteSpace(prompt)) return;

        _loading = true;
        PART_Prompt.Text = prompt;
        PART_Override.IsChecked = true;
        _loading = false;
        ApplyMode();
        PART_Prompt.CaretIndex = PART_Prompt.Text?.Length ?? 0;
        PART_Prompt.Focus();
    }

    private void ModeChanged()
    {
        if (!_loading) ApplyMode();
    }

    private void PromptChanged()
    {
        if (_loading || string.IsNullOrEmpty(PART_Prompt.Text)) return;
        if (!_hasPersona && PART_NoSystem.IsChecked == true)
            PART_CustomSystem.IsChecked = true;
    }

    private bool IsWritable() => _hasPersona
        ? PART_Append.IsChecked == true || PART_Override.IsChecked == true
        : PART_CustomSystem.IsChecked == true;

    private void ApplyMode()
    {
        var writable = IsWritable();
        PART_Prompt.IsEnabled = writable;
        PART_EditorBorder.Opacity = writable ? 1 : 0.55;
        PART_EditorLabel.Opacity = writable ? 1 : 0.55;

        PART_ModeHint.Text = !_hasPersona
            ? string.Empty
            : PART_Inherit.IsChecked == true
                ? "发送给模型的 system 内容 = 角色默认提示词。"
                : PART_Append.IsChecked == true
                    ? "发送给模型的 system 内容 = 角色提示词 + 空行 + 下方文本。"
                    : "发送给模型的 system 内容 = 下方文本（角色默认被忽略）。";
    }

    private void OnInsertVariable(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string token } || token.Length == 0) return;
        if (!IsWritable())
        {
            _loading = true;
            if (_hasPersona) PART_Append.IsChecked = true;
            else PART_CustomSystem.IsChecked = true;
            _loading = false;
            ApplyMode();
        }

        var text = PART_Prompt.Text ?? string.Empty;
        var start = Math.Min(PART_Prompt.SelectionStart, PART_Prompt.SelectionEnd);
        var end = Math.Max(PART_Prompt.SelectionStart, PART_Prompt.SelectionEnd);
        PART_Prompt.Text = text[..start] + token + text[end..];
        PART_Prompt.CaretIndex = start + token.Length;
        PART_Prompt.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save();
            e.Handled = true;
        }
    }

    private static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
