using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using MemorySection = MolaGPT.Core.Personalization.MemorySection;

namespace MolaGPT.Core.Memory;

public enum MemoryOpType
{
    Add,
    Replace,
    Delete,
    Candidate
}

/// <summary>One proposed change from the consolidation model. Untrusted until
/// the application layer has checked every field of it.</summary>
public sealed record MemoryOp(
    MemoryOpType Type,
    MemorySection Section,
    string Text,
    string Quote,
    string? Target,
    double Confidence,
    string? Topic = null,
    string? Group = null,
    string? Summary = null);

/// <summary>
/// The consolidation model answers in XML tags, not JSON. BYOK users routinely
/// point this at whatever cheap small model they have, and structured output is
/// not something those reliably support — a half-written JSON object is
/// unparseable, while a stray tag only costs one op.
/// </summary>
public static partial class MemoryOpsParser
{
    /// <summary>Per window, not per message. A long window makes models treat
    /// every passing remark as a durable fact, so the ceiling stays.</summary>
    public const int MaxOpsPerWindow = 8;

    [GeneratedRegex(@"<memory_ops\b[^>]*/>|<memory_ops\b[^>]*>[\s\S]*?</memory_ops>", RegexOptions.IgnoreCase)]
    private static partial Regex EnvelopeRegex();

    [GeneratedRegex(@"<user_memory\b[^>]*>\s*(?<value>true|false)\s*</user_memory>", RegexOptions.IgnoreCase)]
    private static partial Regex GateRegex();

    /// <summary>
    /// The gatekeeper's answer. Null means "could not read it", which is not the
    /// same as false: treating an unreadable reply as 「没什么可记」 would push the
    /// watermark past a window nobody ever looked at, with no error anywhere.
    /// </summary>
    public static bool? ParseGate(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var match = GateRegex().Match(reply);
        if (match.Success) return string.Equals(match.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);

        // A model that answered the question but forgot the tag still answered it.
        var trimmed = reply.Trim().ToLowerInvariant();
        if (trimmed is "true" or "yes") return true;
        if (trimmed is "false" or "no") return false;
        return null;
    }

    /// <summary>
    /// Parse the whole envelope. Null distinguishes malformed output from a valid empty result.
    /// half-read XML is exactly the case where an op's text and its quote can
    /// come from different places.
    /// </summary>
    public static IReadOnlyList<MemoryOp>? Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var envelope = EnvelopeRegex().Match(reply);
        if (!envelope.Success) return null;

        XElement root;
        try
        {
            root = XElement.Parse(envelope.Value, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var ops = new List<MemoryOp>();
        foreach (var node in root.Elements("op"))
        {
            if (ops.Count >= MaxOpsPerWindow) break;
            if (ParseOp(node) is { } op) ops.Add(op);
        }
        return ops;
    }

    private static MemoryOp? ParseOp(XElement node)
    {
        var type = (node.Attribute("type")?.Value ?? "add").Trim().ToLowerInvariant() switch
        {
            "add" => MemoryOpType.Add,
            "replace" or "supersede" or "update" => MemoryOpType.Replace,
            "delete" or "forget" => MemoryOpType.Delete,
            "candidate" => MemoryOpType.Candidate,
            _ => (MemoryOpType?)null
        };
        if (type is not { } opType) return null;

        var text = Value(node, "text");
        var quote = Value(node, "quote");
        var target = node.Attribute("target")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(target)) target = Value(node, "target");

        if (!MemorySectionRules.TryParse(node.Attribute("section")?.Value, out var section))
            section = MemorySection.Identity;

        var confidence = 0.5;
        if (double.TryParse(node.Attribute("confidence")?.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed))
            confidence = Math.Clamp(parsed, 0, 1);

        if (opType != MemoryOpType.Delete && string.IsNullOrWhiteSpace(text)) return null;
        if (opType is MemoryOpType.Replace or MemoryOpType.Delete && string.IsNullOrWhiteSpace(target)) return null;

        return new MemoryOp(opType, section, (text ?? string.Empty).Trim(), (quote ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(target) ? null : target.Trim(), confidence,
            node.Attribute("topic")?.Value, node.Attribute("group")?.Value, Value(node, "summary"));
    }

    private static string? Value(XElement node, string name)
    {
        var child = node.Element(name);
        return child is null ? null : string.Concat(child.Nodes().OfType<XText>().Select(text => text.Value)).Trim();
    }
}
