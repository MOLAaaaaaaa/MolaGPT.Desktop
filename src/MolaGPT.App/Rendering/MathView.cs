using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MolaGPT.App.Rendering.Tex;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A typeset LaTeX formula, with the source as a fallback.
///
/// Wraps AvaloniaMath's <see cref="FormulaBlock"/> rather than using it
/// directly for one reason: models emit invalid LaTeX regularly — a stray
/// <c>\mbox</c>, an unbalanced brace, a macro the engine does not know — and an
/// unguarded FormulaBlock throws out of Measure, which takes down the layout
/// pass for the whole transcript row. Here a parse failure degrades to showing
/// the source, which is what the old WPF renderer did too.
///
/// The formula is validated by parsing it before it is ever handed to the
/// control, so the failure is caught somewhere it can be handled rather than
/// inside a layout pass.
/// </summary>
public sealed class MathView : TemplatedControl
{
    public static readonly StyledProperty<string?> LatexProperty =
        AvaloniaProperty.Register<MathView, string?>(nameof(Latex));

    /// <summary>Font size for the typeset output. Formula scale is independent
    /// of surrounding prose, so this is explicit rather than inherited.</summary>
    public static readonly StyledProperty<double> FormulaSizeProperty =
        AvaloniaProperty.Register<MathView, double>(nameof(FormulaSize), 18d);

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

    private readonly ContentControl _host = new();

    static MathView()
    {
        LatexProperty.Changed.AddClassHandler<MathView>((x, _) => x.Rebuild());
        FormulaSizeProperty.Changed.AddClassHandler<MathView>((x, _) => x.Rebuild());
        ForegroundProperty.Changed.AddClassHandler<MathView>((x, _) => x.Rebuild());
    }

    public MathView()
    {
        Template = new FuncControlTemplate<MathView>((view, _) => view._host);
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Rebuild();
    }

    private void Rebuild()
    {
        var latex = Latex?.Trim();
        if (string.IsNullOrEmpty(latex))
        {
            _host.Content = null;
            return;
        }

        if (TryBuildFormula(latex) is { } formula)
        {
            _host.Content = formula;
            return;
        }

        _host.Content = new SelectableTextBlock
        {
            Text = latex,
            FontFamily = this.TryFindResource("Font.Mono", ActualThemeVariant, out var mono) && mono is FontFamily family
                ? family
                : FontFamily.Default,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Foreground
        };
    }

    /// <summary>
    /// Returns a rendered formula, or null when the LaTeX will not parse.
    ///
    /// The canvas parses during Measure, so it is measured once here against an
    /// unbounded size to settle whether the formula is valid while the answer
    /// can still change what gets shown.
    /// </summary>
    private Control? TryBuildFormula(string latex)
    {
        try
        {
            var canvas = new FormulaCanvas
            {
                Latex = latex,
                FormulaSize = FormulaSize,
                Foreground = Foreground,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // A formula that measures to nothing rendered nothing; the source is
            // more useful than a blank gap.
            if (!canvas.HasFormula) return null;
            if (canvas.DesiredSize.Width < 1 || canvas.DesiredSize.Height < 1) return null;

            return canvas;
        }
        catch
        {
            return null;
        }
    }
}
