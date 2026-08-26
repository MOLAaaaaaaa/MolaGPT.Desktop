using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using AvaloniaMath.Rendering;
using XamlMath;
using XamlMath.Boxes;
using XamlMath.Rendering;
using XamlMath.Rendering.Transformations;
using AvBrush = Avalonia.Media.IBrush;
using TexBrush = XamlMath.Rendering.IBrush;
using TexPoint = XamlMath.Rendering.Point;
using TexRectangle = XamlMath.Rendering.Rectangle;

namespace MolaGPT.App.Rendering.Tex;

/// <summary>
/// Draws a XamlMath box tree with Avalonia 12 APIs.
///
/// This exists because AvaloniaMath 2.1.0 cannot paint on Avalonia 12: its
/// glyph path calls <c>Typeface.GlyphTypeface</c>, which was removed, so a
/// formula measures correctly and then throws <see cref="MissingMethodException"/>
/// from inside the render pass. Measuring alone does not reveal it — only an
/// actual paint does — which is why the headless render test exists.
///
/// Everything except glyph drawing is reused: AvaloniaMath still parses, still
/// supplies the Computer Modern fonts, and XamlMath.Shared still does the
/// layout. Only <see cref="RenderCharacter"/> is reimplemented, against
/// <c>FontManager.TryGetGlyphTypeface</c>, which is the Avalonia 12 replacement
/// for the removed property.
/// </summary>
internal sealed class AvaloniaTexRenderer : IElementRenderer
{
    private readonly DrawingContext _context;
    private readonly double _scale;

    public AvaloniaTexRenderer(DrawingContext context, double scale = 1.0)
    {
        _context = context;
        _scale = scale;
    }

    public void RenderElement(Box box, double x, double y) => box.RenderTo(this, x, y);

    public void RenderCharacter(CharInfo info, double x, double y, TexBrush? foreground)
    {
        var typeface = TypefaceOf(info.Font);
        if (typeface is null) return;

        if (!FontManager.Current.TryGetGlyphTypeface(typeface.Value, out var glyphTypeface)) return;

        var codepoint = info.Character;
        if (!glyphTypeface.CharacterToGlyphMap.TryGetGlyph(codepoint, out var glyph)) return;
        if (glyph == 0) return;

        var size = info.Size * _scale;

        // The baseline is what XamlMath positions against, so the run's origin
        // is y itself, not the top of the em box.
        using var run = new GlyphRun(
            glyphTypeface,
            size,
            new[] { codepoint }.AsMemory(),
            new ushort[] { glyph },
            baselineOrigin: new Avalonia.Point(x * _scale, y * _scale));

        _context.DrawGlyphRun(Convert(foreground), run);
    }

    public void RenderLine(TexPoint point0, TexPoint point1, TexBrush? foreground)
    {
        // XamlMath emits lines for radical bars and similar rules; a hairline at
        // the current scale keeps them visually consistent with rectangles.
        var pen = new Pen(Convert(foreground), System.Math.Max(1.0, _scale));
        _context.DrawLine(
            pen,
            new Avalonia.Point(point0.X * _scale, point0.Y * _scale),
            new Avalonia.Point(point1.X * _scale, point1.Y * _scale));
    }

    public void RenderRectangle(TexRectangle rectangle, TexBrush? foreground)
    {
        _context.FillRectangle(
            Convert(foreground),
            new Rect(
                rectangle.X * _scale,
                rectangle.Y * _scale,
                rectangle.Width * _scale,
                rectangle.Height * _scale));
    }

    public void RenderTransformed(Box box, IEnumerable<Transformation> transforms, double x, double y)
    {
        var applied = 0;
        var states = new Stack<DrawingContext.PushedState>();

        foreach (var transform in transforms)
        {
            switch (transform)
            {
                case Transformation.Translate translate:
                    states.Push(_context.PushTransform(
                        Matrix.CreateTranslation(translate.X * _scale, translate.Y * _scale)));
                    applied++;
                    break;

                case Transformation.Rotate rotate:
                    states.Push(_context.PushTransform(
                        Matrix.CreateRotation(Matrix.ToRadians(rotate.RotationDegrees))));
                    applied++;
                    break;
            }
        }

        RenderElement(box, x, y);

        // Unwind in reverse; a leaked pushed state corrupts everything drawn
        // afterwards, including the rest of the transcript.
        while (states.Count > 0) states.Pop().Dispose();
        _ = applied;
    }

    public void FinishRendering() { }

    /// <summary>Black is the documented default when a box carries no brush.</summary>
    private static AvBrush Convert(TexBrush? brush) =>
        brush?.ToAvalonia() ?? Brushes.Black;

    // ---- typeface access ---------------------------------------------------

    // AvaloniaMath's IFontTypeface implementation is internal, but it is a
    // record with a public Typeface property. Reaching it by reflection is the
    // narrowest possible coupling: one property, resolved once per type.
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> TypefaceProperties = new();

    private static Typeface? TypefaceOf(XamlMath.Fonts.IFontTypeface font)
    {
        var property = TypefaceProperties.GetOrAdd(
            font.GetType(),
            type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.PropertyType == typeof(Typeface)));

        return property?.GetValue(font) as Typeface?;
    }
}
