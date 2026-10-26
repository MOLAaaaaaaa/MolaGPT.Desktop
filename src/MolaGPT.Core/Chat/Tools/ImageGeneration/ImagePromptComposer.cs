using System.Globalization;
using MolaGPT.Core.Chat.LocalTools;

namespace MolaGPT.Core.Chat.Tools.ImageGeneration;

/// <summary>
/// How a knob reaches the model. <see cref="Parameter"/> means the endpoint has
/// a field for it; <see cref="Prompt"/> means it is folded into the prompt text
/// because the endpoint has none; <see cref="Ignored"/> means it is not applied
/// at all and the UI must not claim otherwise.
/// </summary>
public enum ImageParameterChannel
{
    Parameter,
    Prompt,
    Ignored
}

/// <summary>Per-request answer to "where do 尺寸 and 风格 actually go?".</summary>
public readonly record struct ImageParameterDelivery(
    ImageParameterChannel Size,
    ImageParameterChannel Style);

/// <summary>
/// Folds style into the prompt when the selected API has no style parameter.
/// </summary>
public static class ImagePromptComposer
{
    // 1792×1024 reduces to 7:4, which is not what the picker calls it. Named
    // ratios win within tolerance so the model is told the ratio the user
    // clicked on.
    private static readonly (double Value, string Name)[] NamedRatios =
    [
        (1d, "1:1"),
        (16d / 9d, "16:9"),
        (9d / 16d, "9:16"),
        (3d / 2d, "3:2"),
        (2d / 3d, "2:3"),
        (4d / 3d, "4:3"),
        (3d / 4d, "3:4")
    ];

    public static ImageParameterDelivery Describe(ImageGenerationOptions options, bool isEdit)
    {
        var chatDialect = ImageApiFormat.IsChatImage(options.Format);
        var hasStyle = !string.IsNullOrWhiteSpace(options.Style);
        var size = ImageParameterChannel.Parameter;

        var style = (chatDialect || isEdit || !IsDallE3(options.Model)) && hasStyle
            ? ImageParameterChannel.Prompt
            : ImageParameterChannel.Parameter;

        return new ImageParameterDelivery(size, style);
    }

    /// <summary>The prompt as it will be sent, knobs folded in.</summary>
    public static string Compose(ImageGenerationOptions options, string prompt, bool isEdit)
    {
        var delivery = Describe(options, isEdit);
        var body = prompt.Trim();
        var directives = new List<string>(2);

        if (delivery.Style == ImageParameterChannel.Prompt && options.Style?.Trim() is { Length: > 0 } style)
            directives.Add($"Style: {style}.");

        return directives.Count == 0 ? body : $"{body}\n\n{string.Join(" ", directives)}";
    }

    /// <summary>
    /// "1792x1024" → "16:9". Null for "auto", blank, or anything that is not a
    /// pixel pair — callers treat null as "no ratio to state".
    /// </summary>
    public static string? AspectRatio(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;

        var trimmed = size.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = trimmed.Split(['x', 'X', '×'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0)
        {
            return null;
        }

        var ratio = (double)width / height;
        foreach (var (value, name) in NamedRatios)
        {
            if (Math.Abs(ratio - value) / value <= 0.05) return name;
        }

        var divisor = Gcd(width, height);
        return $"{width / divisor}:{height / divisor}";
    }

    internal static bool IsDallE3(string? model) =>
        string.Equals(model, "dall-e-3", StringComparison.OrdinalIgnoreCase);

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
