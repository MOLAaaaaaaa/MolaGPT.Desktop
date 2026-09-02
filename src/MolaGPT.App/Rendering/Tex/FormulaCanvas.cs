using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaMath.Parsers;
using AvaloniaMath.Rendering;
using XamlMath;
using XamlMath.Fonts;
using XamlMath.Rendering;
using XamlMath.Utils;
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
            //
            // The box itself is sized from XamlMath's own text metrics, but a
            // CJK run actually paints with the system-font fallback
            // (systemTextFontName above), whose real glyphs run deeper below
            // the baseline — especially shrunk further inside a subscript —
            // and whose italic neighbours (e.g. a lone P) carry right-side
            // bearing the box never reserved. Render still draws from the same
            // (0,0) origin, so padding here only grows blank space past the
            // glyphs; it never shifts them. Better a few spare pixels than a
            // clipped character.
            return new AvSize(
                measurer.Width * size + size * 0.2,
                measurer.Height * size + size * 0.35);
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

        latex = LatexNormalizer.ForAvaloniaMath(latex);

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
            var environment = AvaloniaTeXEnvironment.Create(
                TexStyle.Display,
                size,
                systemTextFontName: "avares://MolaGPT.Desktop/Assets/Fonts#Noto Sans SC",
                foreground: null,
                background: null);
            _environment = environment with
            {
                TextFont = new ScriptAwareTextFont(environment.TextFont, environment.MathFont)
            };
        }
        catch
        {
            // Models emit invalid LaTeX often enough that this is an expected
            // outcome, not an error worth surfacing.
            _formula = null;
            _environment = null;
        }
    }

    /// <summary>
    /// AvaloniaMath's system text font ignores the requested TeX style and
    /// always returns full-size glyphs. Apply the same scale its math font uses
    /// so text inside subscripts and superscripts participates in layout.
    /// </summary>
    private sealed class ScriptAwareTextFont(ITeXFont inner, ITeXFont mathFont) : ITeXFont
    {
        private readonly double _scriptScale =
            mathFont.GetDefaultCharInfo('M', TexStyle.Script).Value.Size;
        private readonly double _scriptScriptScale =
            mathFont.GetDefaultCharInfo('M', TexStyle.ScriptScript).Value.Size;

        public bool SupportsMetrics => inner.SupportsMetrics;
        public double Size => inner.Size;

        public Result<CharInfo> GetCharInfo(char character, string textStyle, TexStyle style) =>
            inner.GetCharInfo(character, textStyle, style).Map(info =>
            {
                var scale = style < TexStyle.Script
                    ? 1d
                    : style < TexStyle.ScriptScript
                        ? _scriptScale
                        : _scriptScriptScale;
                var metrics = info.Metrics;
                return new CharInfo(
                    info.Character,
                    info.Font,
                    scale,
                    info.FontId,
                    new TeXFontMetrics(
                        metrics.Width,
                        metrics.Height,
                        metrics.Depth,
                        metrics.Italic,
                        scale));
            });

        public ExtensionChar GetExtension(CharInfo charInfo, TexStyle style) => inner.GetExtension(charInfo, style);
        public CharFont? GetLigature(CharFont leftChar, CharFont rightChar) => inner.GetLigature(leftChar, rightChar);
        public CharInfo GetNextLargerCharInfo(CharInfo charInfo, TexStyle style) => inner.GetNextLargerCharInfo(charInfo, style);
        public Result<CharInfo> GetDefaultCharInfo(char character, TexStyle style) => inner.GetDefaultCharInfo(character, style);
        public Result<CharInfo> GetCharInfo(CharFont charFont, TexStyle style) => inner.GetCharInfo(charFont, style);
        public Result<CharInfo> GetCharInfo(string name, TexStyle style) => inner.GetCharInfo(name, style);
        public double GetKern(CharFont leftChar, CharFont rightChar, TexStyle style) => inner.GetKern(leftChar, rightChar, style);
        public double GetQuad(int fontId, TexStyle style) => inner.GetQuad(fontId, style);
        public double GetSkew(CharFont charFont, TexStyle style) => inner.GetSkew(charFont, style);
        public bool HasSpace(int fontId) => inner.HasSpace(fontId);
        public bool HasNextLarger(CharInfo charInfo) => inner.HasNextLarger(charInfo);
        public bool IsExtensionChar(CharInfo charInfo) => inner.IsExtensionChar(charInfo);
        public int GetMuFontId() => inner.GetMuFontId();
        public double GetXHeight(TexStyle style, int fontId) => inner.GetXHeight(style, fontId);
        public double GetSpace(TexStyle style) => inner.GetSpace(style);
        public double GetAxisHeight(TexStyle style) => inner.GetAxisHeight(style);
        public double GetBigOpSpacing1(TexStyle style) => inner.GetBigOpSpacing1(style);
        public double GetBigOpSpacing2(TexStyle style) => inner.GetBigOpSpacing2(style);
        public double GetBigOpSpacing3(TexStyle style) => inner.GetBigOpSpacing3(style);
        public double GetBigOpSpacing4(TexStyle style) => inner.GetBigOpSpacing4(style);
        public double GetBigOpSpacing5(TexStyle style) => inner.GetBigOpSpacing5(style);
        public double GetSub1(TexStyle style) => inner.GetSub1(style);
        public double GetSub2(TexStyle style) => inner.GetSub2(style);
        public double GetSubDrop(TexStyle style) => inner.GetSubDrop(style);
        public double GetSup1(TexStyle style) => inner.GetSup1(style);
        public double GetSup2(TexStyle style) => inner.GetSup2(style);
        public double GetSup3(TexStyle style) => inner.GetSup3(style);
        public double GetSupDrop(TexStyle style) => inner.GetSupDrop(style);
        public double GetNum1(TexStyle style) => inner.GetNum1(style);
        public double GetNum2(TexStyle style) => inner.GetNum2(style);
        public double GetNum3(TexStyle style) => inner.GetNum3(style);
        public double GetDenom1(TexStyle style) => inner.GetDenom1(style);
        public double GetDenom2(TexStyle style) => inner.GetDenom2(style);
        public double GetDefaultLineThickness(TexStyle style) => inner.GetDefaultLineThickness(style);
    }

}
