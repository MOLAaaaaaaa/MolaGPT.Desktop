using XamlMath;
using XamlMath.Boxes;
using XamlMath.Rendering;
using XamlMath.Rendering.Transformations;
using TexBrush = XamlMath.Rendering.IBrush;
using TexPoint = XamlMath.Rendering.Point;
using TexRectangle = XamlMath.Rendering.Rectangle;

namespace MolaGPT.App.Rendering.Tex;

/// <summary>
/// Measures a formula by pretending to draw it.
///
/// XamlMath.Shared does not expose <c>TexFormula.CreateBox</c> publicly — only
/// <c>RenderTo</c> — so the laid-out box tree cannot be obtained directly. This
/// renderer is handed to RenderTo, captures the root box the very first time
/// RenderElement is called, and then descends no further: nothing is drawn, and
/// the layout work that produced the box is not repeated.
///
/// <see cref="Box.Height"/> is the extent above the baseline and
/// <see cref="Box.Depth"/> the extent below, which is why the baseline offset is
/// reported separately — the caller needs it to place the origin when painting.
/// </summary>
internal sealed class TexMeasureRenderer : IElementRenderer
{
    public Box? Root { get; private set; }

    public double Width => Root?.TotalWidth ?? 0;

    public double Height => Root?.TotalHeight ?? 0;

    /// <summary>Distance from the top of the box down to the baseline.</summary>
    public double Baseline => Root?.Height ?? 0;

    public void RenderElement(Box box, double x, double y)
    {
        // Only the outermost call carries the whole formula; recursing would do
        // real work for a measurement that is already complete.
        Root ??= box;
    }

    public void RenderCharacter(CharInfo info, double x, double y, TexBrush? foreground) { }

    public void RenderLine(TexPoint point0, TexPoint point1, TexBrush? foreground) { }

    public void RenderRectangle(TexRectangle rectangle, TexBrush? foreground) { }

    public void RenderTransformed(Box box, IEnumerable<Transformation> transforms, double x, double y) =>
        RenderElement(box, x, y);

    public void FinishRendering() { }
}
