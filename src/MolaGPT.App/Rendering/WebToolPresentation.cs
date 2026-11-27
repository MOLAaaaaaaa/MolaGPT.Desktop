using MolaGPT.Presentation;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

internal static class WebToolPresentation
{
    public static Dictionary<string, (ThinkingSegmentViewModel Owner, MarkupUnitBlock Markup)> ThinkingTools(MessageViewModel message)
    {
        var owners = new Dictionary<string, ThinkingSegmentViewModel>(StringComparer.Ordinal);
        foreach (var segment in message.ThinkingSegments)
        {
            if (!segment.Source.Contains("data-tool-call", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var unit in MolaGptMarkupSplitter.Split(segment.Source))
                if (unit.ToolCallId is { } id) owners.TryAdd(id, segment);
        }
        var result = new Dictionary<string, (ThinkingSegmentViewModel, MarkupUnitBlock)>(StringComparer.Ordinal);
        if (owners.Count == 0) return result;
        var text = string.Join("\n\n", message.ThinkingSegments.Select(segment => segment.Source));
        foreach (var markup in MessageDocumentParser.Parse(text).Blocks.OfType<MarkupUnitBlock>())
            if (markup.Unit.ToolCallId is { } id && owners.TryGetValue(id, out var owner))
                result[id] = (owner, markup);
        return result;
    }

    public static void Add(List<IReadOnlyList<ToolCallViewModel>> pending, IReadOnlyList<ToolCallViewModel> tools)
    {
        var key = tools[0].GroupKey;
        if (key is not null && pending.LastOrDefault() is { Count: > 0 } previous && previous[0].GroupKey == key)
            pending[^1] = previous.Concat(tools).ToArray();
        else
            pending.Add(tools);
    }

    public static bool HandleSpecial(RenderBlock block, IReadOnlyList<IReadOnlyList<ToolCallViewModel>> pending,
        IDictionary<string, MarkupUnitBlock> markups)
    {
        if (block is not MarkupUnitBlock markup) return false;
        if (IsHiddenAnalysis(markup)) return true;
        if (markup.Unit.Kind is not (MarkupUnitKind.ImageErrorCard or MarkupUnitKind.ImagePendingSkeleton)
            || pending.LastOrDefault() is not { Count: > 0 } tools
            || !markups.TryGetValue(tools[0].Id, out var image)
            || (image.Unit.ToolName != "image_generation_and_editing"
                && image.Unit.Tag?.Contains("tool-image-blockquote", StringComparison.OrdinalIgnoreCase) != true))
            return false;
        if (markup.Unit.Kind == MarkupUnitKind.ImageErrorCard)
        {
            var error = markup.Unit.Inner ?? markup.Unit.Tag;
            tools[0].Status = "error";
            tools[0].Label = markup.Unit.Tag ?? "图片绘制失败";
            tools[0].Detail = error;
            markups[tools[0].Id] = image with
            {
                Unit = image.Unit with { ToolVariant = MolaGptMarkupSplitter.Variant.Error, ErrorText = error }
            };
        }
        return true;
    }

    public static bool IsTool(MarkupUnitBlock block) => block.Unit.Kind is
        MarkupUnitKind.ToolStatus or MarkupUnitKind.DsAnalysis or MarkupUnitKind.SteelStep
        or MarkupUnitKind.ImagePendingSkeleton or MarkupUnitKind.ImageErrorCard;

    public static bool IsHiddenAnalysis(MarkupUnitBlock block) =>
        block.Unit.Kind == MarkupUnitKind.DsAnalysis
        && block.Unit.Tag?.ToLowerInvariant() is not ("python" or "mcp" or "image-action" or "code-search")
        && string.IsNullOrWhiteSpace(block.Unit.StatusText);

    public static ToolCallViewModel Update(MessageViewModel message, MarkupUnitBlock markup,
        IDictionary<string, ToolCallViewModel> tools, string id, bool done, bool pending)
    {
        var unit = markup.Unit;
        var structured = unit.ToolCallId is { } originalId
            ? message.ToolCalls.FirstOrDefault(item => item.Id == originalId) : null;
        if (structured is not null)
        {
            tools[id] = structured;
            return structured;
        }
        var name = MolaGptMarkupSplitter.ToolName(unit);
        if (!tools.TryGetValue(id, out var tool)) tools[id] = tool = new ToolCallViewModel(id, name);
        tool.Name = name;
        var label = pending ? unit.RunningText : unit.StatusText;
        tool.Label = label ?? (unit.Kind == MarkupUnitKind.ImageErrorCard ? unit.Tag : unit.Kind == MarkupUnitKind.DsAnalysis ? null : unit.Inner)
            ?? (unit.Kind == MarkupUnitKind.ImagePendingSkeleton ? "正在生成图片" : "工具调用");
        tool.Detail = unit.ErrorText ?? (unit.Kind == MarkupUnitKind.ImageErrorCard ? unit.Inner : unit.Metadata);
        tool.Status = pending ? "running" : MolaGptMarkupSplitter.ToolPhase(unit, done);
        return tool;
    }
}
