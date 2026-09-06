using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Marks the row still being written, and holds the one global switch that turns
/// the effect off.
///
/// <see cref="IsTailProperty"/> inherits, so the transcript's row template can
/// set it once on the row container and every text control underneath picks it
/// up. The alternative — threading a flag through <c>ProseRow</c> into the block
/// template and out again into whichever control renders that block kind —
/// would put the same boolean in five places, four of which do not care.
///
/// The effect is one dimmed run at the end of the answer, not the gradient the
/// mobile client draws. Three things were measured before settling for the
/// plainer version, and each ruled out a smoother one:
///
///  1. An opacity mask — the mobile approach — forces the text into an offscreen
///     layer, and text composited offscreen loses subpixel antialiasing. On a
///     standard-density panel this app deliberately spends the LCD's stripes on
///     ClearType, so the paragraph being read would have gone soft exactly while
///     it was being read, and sharpened again when the answer settled.
///  2. A gradient brush on the trailing run maps to that run's bounds. While an
///     answer streams, the last twelve characters straddle a line break about
///     half the time, and every one of those frames paints the fade twice.
///  3. Splitting the tail into several runs to step the opacity per character
///     avoids both — but shaping is per run, so the accumulated advance differs
///     and the paragraph re-wraps by a character. Measured across 61 column
///     widths in both scripts: one extra run boundary never moved a line break,
///     two already did at some widths. So the tail is one run, and the fade is
///     one step.
///
/// The step is placed on a word boundary where the script has them, which is
/// what keeps it reading as "this part is still arriving" rather than as a
/// glyph that failed to load.
/// </summary>
public static class StreamTailFade
{
    /// <summary>How many characters at the end of the answer are dimmed.</summary>
    public const int TailLength = 12;

    /// <summary>How far back from the grapheme boundary a word break is worth
    /// looking for. Beyond this the dimmed run stops reading as "the newest
    /// words" and starts reading as "the newest half sentence".</summary>
    private const int WordSearchWindow = 8;

    /// <summary>Opacity of the tail. Deliberately not faint: with a single step
    /// rather than a ramp, too large a drop reads as a rendering fault.</summary>
    public const double TailOpacity = 0.5;

    public static readonly AttachedProperty<bool> IsTailProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsTail", typeof(StreamTailFade), defaultValue: false, inherits: true);

    public static void SetIsTail(Control element, bool value) => element.SetValue(IsTailProperty, value);

    public static bool GetIsTail(Control element) => element.GetValue(IsTailProperty);

    /// <summary>
    /// Blocks the inherited flag on every text control under <paramref name="root"/>
    /// except the last one.
    ///
    /// Inheritance carries the flag to the whole row, but a row is not always one
    /// piece of text: a list is one block holding one text control per item, a
    /// table one per cell, and an item split around a display equation several of
    /// its own. Left alone, every one of them dims its own final words, so a
    /// four-item list shows four dimmed tails scattered up the message instead of
    /// one at the end — which is what it looked like, and it read as flicker
    /// because each item re-dimmed as the list grew.
    ///
    /// A local value wins over an inherited one, so the losers are set explicitly
    /// to false and the survivor is cleared back to inheriting.
    /// </summary>
    public static void KeepTailOnLast(Visual root)
    {
        var blocks = root.GetVisualDescendants().OfType<MarkdownTextBlock>().ToList();

        for (var i = 0; i < blocks.Count; i++)
        {
            if (i == blocks.Count - 1) blocks[i].ClearValue(IsTailProperty);
            else blocks[i].SetValue(IsTailProperty, false);
        }
    }

    /// <summary>
    /// Whether the fade may draw at all: the user's setting, and the system's
    /// "reduce motion" preference.
    ///
    /// Read at render time rather than pushed through the tree, because it moves
    /// perhaps twice in a session. A toggle during a live stream is picked up on
    /// the next frame, which the stream is already producing; a toggle while
    /// nothing streams has nothing to repaint.
    /// </summary>
    public static bool IsEnabled { get; private set; } = true;

    /// <summary>Applies the user's preference, gated by the system's. Windows
    /// exposes "show animations" as SPI_GETCLIENTAREAANIMATION, which is what
    /// the accessibility setting of the same name writes to.</summary>
    public static void Configure(bool userPreference) =>
        IsEnabled = userPreference && SystemAllowsAnimation();

    private const uint SpiGetClientAreaAnimation = 0x1042;

    private static bool SystemAllowsAnimation()
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            return QueryClientAreaAnimation();
        }
        catch (DllNotFoundException)
        {
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    /// <summary>A failed query means the preference is unknown, and an unknown
    /// preference is not a reason to switch the effect off.</summary>
    [SupportedOSPlatform("windows")]
    private static bool QueryClientAreaAnimation() =>
        !SystemParametersInfoW(SpiGetClientAreaAnimation, 0, out var enabled, 0) || enabled != 0;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(
        uint action, uint parameter, out int value, uint update);

    /// <summary>
    /// Where the dimmed tail of <paramref name="text"/> starts, or 0 to dim all
    /// of it.
    ///
    /// Counted in grapheme clusters rather than UTF-16 units: splitting a
    /// surrogate pair across two runs draws two replacement glyphs, and
    /// splitting a combining sequence strips the mark off its base. Then nudged
    /// back to a word boundary if one is close, so scripts written with spaces
    /// do not get a tail that starts in the middle of a word.
    /// </summary>
    public static int TailStart(string text)
    {
        var boundary = GraphemeBoundary(text);
        if (boundary <= 0) return 0;

        for (var i = boundary; i > Math.Max(0, boundary - WordSearchWindow); i--)
        {
            if (char.IsWhiteSpace(text[i - 1])) return i;
        }

        return boundary;
    }

    /// <summary>Start of the last <see cref="TailLength"/> grapheme clusters.</summary>
    private static int GraphemeBoundary(string text)
    {
        var starts = new List<int>();
        var index = 0;
        while (index < text.Length)
        {
            starts.Add(index);
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            index += length > 0 ? length : 1;
        }

        return starts.Count <= TailLength ? 0 : starts[^TailLength];
    }
}
