using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MolaGPT.Core.Models;

public sealed record PromptTemplateImport(PromptTemplate Template, IReadOnlyList<string> Skipped);

/// <summary>
/// Reads the prompt order out of a SillyTavern chat-completion preset or a RisuAI
/// JSON preset. Sampling parameters and connection settings are not part of a
/// template and are left behind.
/// </summary>
public static class PromptTemplateReader
{
    public static PromptTemplateImport Read(byte[] bytes, string fileName)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿')) as JsonObject
                ?? throw new InvalidDataException("无法识别的预设文件。");
        }
        catch (JsonException) { throw new InvalidDataException("无法识别的预设文件。"); }
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (root["promptTemplate"] is JsonArray risu)
            return FromRisu(risu, Text(root, "name") is { Length: > 0 } risuName ? risuName : name, root);
        if (root["prompts"] is JsonArray prompts && root["prompt_order"] is JsonArray order)
            return FromSillyTavern(prompts, order, name, root);
        throw new InvalidDataException("仅支持 SillyTavern 对话补全预设与 RisuAI JSON 预设。");
    }

    private static PromptTemplateImport FromSillyTavern(JsonArray prompts, JsonArray orders, string name, JsonObject root)
    {
        var definitions = prompts.OfType<JsonObject>().Where(prompt => Text(prompt, "identifier").Length > 0)
            .GroupBy(prompt => Text(prompt, "identifier")).ToDictionary(group => group.Key, group => group.First());
        // 100001 is the global order; 100000 is the legacy per-character slot.
        var order = orders.OfType<JsonObject>().FirstOrDefault(item => item["character_id"]?.ToString() == "100001")
            ?? orders.OfType<JsonObject>().FirstOrDefault();
        if (order?["order"] is not JsonArray items) throw new InvalidDataException("预设缺少提示词顺序。");
        var blocks = new List<PromptBlock>();
        var skipped = new List<string>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var id = Text(item, "identifier");
            definitions.TryGetValue(id, out var prompt);
            var kind = id switch
            {
                "main" => PromptBlockKind.Main,
                "jailbreak" => PromptBlockKind.PostHistory,
                "dialogueExamples" => PromptBlockKind.Examples,
                "chatHistory" => PromptBlockKind.History,
                "worldInfoBefore" => PromptBlockKind.LoreBefore,
                "worldInfoAfter" => PromptBlockKind.LoreAfter,
                "charDescription" => PromptBlockKind.Description,
                "charPersonality" => PromptBlockKind.Personality,
                "scenario" => PromptBlockKind.Scenario,
                "personaDescription" => PromptBlockKind.UserPersona,
                _ => PromptBlockKind.Text
            };
            if (kind == PromptBlockKind.Text && (prompt is null || prompt["marker"]?.GetValue<bool>() == true))
            {
                skipped.Add(prompt is null ? id : Text(prompt, "name") is { Length: > 0 } label ? label : id);
                continue;
            }
            if (kind != PromptBlockKind.Text && blocks.Any(block => block.Kind == kind)) continue;
            var block = new PromptBlock
            {
                Kind = kind,
                Enabled = item["enabled"]?.GetValue<bool>() != false,
                Role = Role(prompt?["role"])
            };
            if (kind is PromptBlockKind.Text or PromptBlockKind.Main or PromptBlockKind.PostHistory)
                block.Text = Text(prompt, "content");
            if (kind == PromptBlockKind.Text)
            {
                block.Name = Text(prompt, "name");
                if (prompt!["injection_position"]?.ToString() == "1")
                    block.Depth = int.TryParse(prompt["injection_depth"]?.ToString(), out var depth) ? Math.Max(0, depth) : 4;
            }
            blocks.Add(block);
        }
        if (Text(root, "assistant_prefill").Length > 0) skipped.Add("回复预填");
        return new(Complete(name, blocks, examples: false), skipped);
    }

    private static PromptTemplateImport FromRisu(JsonArray items, string name, JsonObject root)
    {
        var blocks = new List<PromptBlock>();
        var skipped = new List<string>();
        // Risu splits the chat into ranges to put blocks N messages from the end:
        // chat 0..-N, the blocks, chat -N..end. Those are depth insertions here.
        int? depth = null;
        void Add(PromptBlockKind kind, JsonObject item, string role, string text = "", string? label = null)
        {
            if (kind != PromptBlockKind.Text && blocks.Any(block => block.Kind == kind)) return;
            var block = new PromptBlock { Kind = kind, Role = role, Name = Text(item, "name") is { Length: > 0 } itemName ? itemName : label ?? "" };
            if (block.HasRole) block.Depth = depth;
            if (kind is PromptBlockKind.Text or PromptBlockKind.Main or PromptBlockKind.PostHistory) block.Text = text;
            else if (Text(item, "innerFormat") is { Length: > 0 } format && format.Contains(PromptBlock.Slot, StringComparison.Ordinal))
                block.Text = format;
            blocks.Add(block);
        }
        foreach (var item in items.OfType<JsonObject>())
        {
            var type = Text(item, "type");
            var role = Role(item["role"] ?? item["role2"]);
            switch (type)
            {
                case "plain" when Text(item, "type2") == "main":
                    Add(PromptBlockKind.Main, item, role, Text(item, "text"));
                    break;
                case "plain" when Text(item, "type2") == "globalNote":
                    Add(PromptBlockKind.PostHistory, item, role, Text(item, "text"));
                    break;
                case "plain" or "jailbreak" or "cot":
                    Add(PromptBlockKind.Text, item, role, Text(item, "text"),
                        type == "jailbreak" ? "越狱提示词" : type == "cot" ? "思维链" : null);
                    break;
                case "persona": Add(PromptBlockKind.UserPersona, item, role); break;
                case "description": Add(PromptBlockKind.Description, item, role); break;
                case "lorebook":
                    Add(PromptBlockKind.LoreBefore, item, role);
                    Add(PromptBlockKind.LoreAfter, item, role);
                    break;
                case "memory":
                    Add(PromptBlockKind.Summary, item, role);
                    Add(PromptBlockKind.Events, item, role);
                    break;
                case "chat":
                    Add(PromptBlockKind.History, item, "system");
                    depth = int.TryParse(item["rangeEnd"]?.ToString(), out var end) && end < 0 ? -end : null;
                    break;
                case "postEverything": break;
                case "authornote": skipped.Add("作者注"); break;
                case "chatML": skipped.Add("chatML"); break;
                case "cache": skipped.Add("缓存点"); break;
                default: skipped.Add(type); break;
            }
        }
        return new(Complete(name, blocks, examples: true), skipped.Distinct().ToArray());
    }

    /// <summary>
    /// Adds what the source format has no slot for but a role here still carries:
    /// the story summary and events always, the examples when the format has no
    /// examples block of its own. Without them those parts would silently stop
    /// reaching the model once the template is in use.
    /// </summary>
    private static PromptTemplate Complete(string name, List<PromptBlock> blocks, bool examples)
    {
        var history = blocks.FindIndex(block => block.Kind == PromptBlockKind.History);
        if (history < 0)
        {
            blocks.Add(new() { Kind = PromptBlockKind.History });
            history = blocks.Count - 1;
        }
        var missing = new List<PromptBlockKind> { PromptBlockKind.Summary, PromptBlockKind.Events };
        if (examples) missing.Add(PromptBlockKind.Examples);
        // Ahead of the examples, so the story joins the system prompt as it does by default.
        var at = blocks.FindIndex(block => block.Kind == PromptBlockKind.Examples) is var index and >= 0 && index < history
            ? index : history;
        blocks.InsertRange(at, missing.Where(kind => blocks.All(block => block.Kind != kind))
            .Select(kind => new PromptBlock { Kind = kind, Text = PromptTemplate.DefaultFormat(kind) }));
        return new() { Name = string.IsNullOrWhiteSpace(name) ? "导入的编排" : name.Trim(), Blocks = blocks };
    }

    private static string Text(JsonObject? value, string key) =>
        value?[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : "";

    private static string Role(JsonNode? value) =>
        (value is JsonValue node && node.TryGetValue<string>(out var role) ? role : "") switch
        {
            "user" => "user",
            "assistant" or "bot" or "char" => "assistant",
            _ => "system"
        };
}
