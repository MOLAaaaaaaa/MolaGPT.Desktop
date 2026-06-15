using System.Windows;
using System.Windows.Controls;

namespace MolaGPT.Desktop.Controls;

/// <summary>
/// A secret/API-key input that masks its content like a <see cref="PasswordBox"/>
/// but can be toggled to plain text (selectable / copyable) via an eye button.
/// Exposes a <see cref="Password"/> string property and a
/// <see cref="PasswordChanged"/> event mirroring <see cref="PasswordBox"/>, so
/// hosting code can swap a PasswordBox for this control with no logic changes.
/// </summary>
public partial class RevealableSecretBox : UserControl
{
    private const string GlyphReveal = ""; // Fluent "RedEye" — click to show
    private const string GlyphHide = "";   // Fluent "Hide" — click to mask

    private bool _syncing;
    private bool _revealed;

    public RevealableSecretBox() => InitializeComponent();

    /// <summary>Raised whenever the secret changes through either field.</summary>
    public event RoutedEventHandler? PasswordChanged;

    /// <summary>The current secret. The masked and plain fields are kept in sync,
    /// so this always reflects the latest value regardless of reveal state.</summary>
    public string Password
    {
        get => Pw.Password;
        set
        {
            var v = value ?? string.Empty;
            _syncing = true;
            Pw.Password = v;
            Tb.Text = v;
            _syncing = false;
        }
    }

    private void Pw_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Tb.Text = Pw.Password;
        _syncing = false;
        PasswordChanged?.Invoke(this, e);
    }

    private void Tb_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Pw.Password = Tb.Text;
        _syncing = false;
        PasswordChanged?.Invoke(this, new RoutedEventArgs());
    }

    private void ToggleReveal(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        if (_revealed)
        {
            Tb.Visibility = Visibility.Visible;
            Pw.Visibility = Visibility.Collapsed;
            EyeGlyph.Text = GlyphHide;
            EyeBtn.ToolTip = "隐藏";
            Tb.Focus();
            Tb.CaretIndex = Tb.Text.Length;
        }
        else
        {
            Pw.Visibility = Visibility.Visible;
            Tb.Visibility = Visibility.Collapsed;
            EyeGlyph.Text = GlyphReveal;
            EyeBtn.ToolTip = "显示";
        }
    }
}
