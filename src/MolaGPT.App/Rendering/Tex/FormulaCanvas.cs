using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaMath.Parsers;
using AvaloniaMath.Rendering;
using XamlMath;
using XamlMath.Rendering;
using AvBrush = Avalonia.Media.IBrush;
using AvSize = Avalonia.Size;

namespace MolaGPT.App.Rendering.Tex;

/// <summary>
/// Renders one LaTeX formula.
///
/// Replaces AvaloniaMath's own FormulaBlock, which cannot paint on Avalonia 12
/// (see <see cref="AvaloniaTexRenderer"/>). Parsing, the Computer Modern fonts
/// and the layout all still come from AvaloniaMath and XamlMath.Shared; only the
/// drawing is ours.
///
/// Parse results are cached per (latex, size) on the instance, so a row
/// scrolling out and back does not re-parse. <see cref="HasFormula"/> lets the
/// caller fall back to the source without catching anything.
/// </summary>
public sealed class FormulaCanvas : Control
{
    public static readonly StyledProperty<string?> LatexProperty =
        AvaloniaProperty.Register<FormulaCanvas, string?>(nameof(Latex));

    public static readonly StyledProperty<double> FormulaSizeProperty =
        AvaloniaProperty.Register<FormulaCanvas, double>(nameof(FormulaSize), 18d);

    public static readonly StyledProperty<AvBrush?> ForegroundProperty =
        AvaloniaProperty.Register<FormulaCanvas, AvBrush?>(nameof(Foreground));

    public string? Latex
    {
        get => GetValue(LatexProperty);
        set => SetValue(LatexProperty, value);
    }

    public double FormulaSize
    {
        get => GetValue(FormulaSizeProperty);
        set => SetValue(FormulaSizeProperty, value);
    }

    public AvBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>False when the LaTeX did not parse; the caller shows the source.</summary>
    public bool HasFormula => _formula is not null;

    private TexFormula? _formula;
    private TexEnvironment? _environment;
    private string? _builtFrom;
    private double _builtSize;

    static FormulaCanvas()
    {
        AffectsMeasure<FormulaCanvas>(LatexProperty, FormulaSizeProperty);
        AffectsRender<FormulaCanvas>(ForegroundProperty);
    }

    protected override AvSize MeasureOverride(AvSize availableSize)
    {
        Build();
        if (_formula is null || _environment is null) return default;

        var size = FormulaSize;

        try
        {
            var measurer = new TexMeasureRenderer();
            _formula.RenderTo(measurer, _environment, 0, 0);
            if (measurer.Root is null) return default;

            // XamlMath lays out in relative units — a box is about 4x1 for
            // "E = mc^2" regardless of size — and the renderer is what applies
            // the scale. So the control's size is the box times the font size,
            // and Render must use the same factor or the glyphs land elsewhere.
            return new AvSize(measurer.Width * size, measurer.Height * size);
        }
        catch
        {
            _formula = null;
            return default;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_formula is null || _environment is null) return;

        try
        {
            var scale = FormulaSize;
            var renderer = new AvaloniaTexRenderer(context, scale);

            // RenderTo receives the logical top-left. It adds box.Height itself
            // before handing the baseline to the renderer.
            _formula.RenderTo(renderer, _environment, 0, 0);
        }
        catch
        {
            // A formula that parsed but cannot draw must not take down the
            // render pass for the entire transcript.
            _formula = null;
        }
    }

    private void Build()
    {
        var latex = Latex?.Trim();
        var size = FormulaSize;

        if (string.IsNullOrEmpty(latex))
        {
            _formula = null;
            _environment = null;
            _builtFrom = null;
            return;
        }

        latex = PrepareLatex(latex);

        if (_formula is not null
            && string.Equals(_builtFrom, latex, StringComparison.Ordinal)
            && System.Math.Abs(_builtSize - size) < 0.01)
        {
            return;
        }

        _builtFrom = latex;
        _builtSize = size;

        try
        {
            _formula = AvaloniaTeXFormulaParser.Instance.Parse(latex);
            _environment = AvaloniaTeXEnvironment.Create(
                TexStyle.Display,
                size,
                systemTextFontName: "Segoe UI",
                foreground: null,
                background: null);
        }
        catch
        {
            // Models emit invalid LaTeX often enough that this is an expected
            // outcome, not an error worth surfacing.
            _formula = null;
            _environment = null;
        }
    }

    private static string PrepareLatex(string latex) =>
        latex
            .Replace(@"\dfrac", @"\frac", StringComparison.Ordinal)
            .Replace(@"\tfrac", @"\frac", StringComparison.Ordinal)
            .Replace(@"\qquad", @"\;\;\;\;", StringComparison.Ordinal)
            .Replace(@"\quad", @"\;\;", StringComparison.Ordinal)
            .Replace(@"\displaystyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\textstyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\scriptstyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\scriptscriptstyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\operatorname*", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\operatorname", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathbf", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathbb", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathsf", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathtt", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathfrak", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\boldsymbol", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathscr", @"\mathcal", StringComparison.Ordinal)
            .Replace(@"\textrm", @"\text", StringComparison.Ordinal)
            .Replace(@"\textbf", @"\text", StringComparison.Ordinal)
            .Replace(@"\textit", @"\text", StringComparison.Ordinal)
            .Replace(@"\texttt", @"\text", StringComparison.Ordinal)
            .Replace(@"\mbox", @"\text", StringComparison.Ordinal);
}
