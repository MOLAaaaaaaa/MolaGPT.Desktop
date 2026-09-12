using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MolaGPT.Core.Models;

public static class CharacterCardWriter
{
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] WriteJson(string name, string systemPrompt, PersonaProfile profile, byte[]? original = null, string? exportSpec = null)
    {
        var root = original is null ? new JsonObject() : CharacterCardReader.ReadDocument(original);
        JsonObject data;
        if (root["spec"] is null && root["name"] is not null)
        {
            data = root;
            root = new JsonObject { ["data"] = data };
        }
        else data = root["data"] as JsonObject ?? new JsonObject();
        var spec = exportSpec ?? profile.CardSpec;
        root["spec"] = spec;
        root["spec_version"] = spec == "chara_card_v2" ? "2.0" : "3.0";
        if (data.Parent is null) root["data"] = data;
        var fields = new Dictionary<string, string>
        {
            ["name"] = name, ["system_prompt"] = systemPrompt,
            ["description"] = profile.Description, ["personality"] = profile.Personality,
            ["scenario"] = profile.Scenario, ["first_mes"] = profile.Greeting,
            ["mes_example"] = profile.ExampleDialogue, ["post_history_instructions"] = profile.PostHistoryInstructions,
            ["creator"] = profile.Creator, ["creator_notes"] = profile.CreatorNotes,
            ["character_version"] = profile.CharacterVersion
        };
        foreach (var (key, value) in fields)
        {
            data[key] = value;
            if (root.ContainsKey(key)) root[key] = value;
        }
        data["tags"] = JsonSerializer.SerializeToNode(profile.Tags);
        data["alternate_greetings"] = JsonSerializer.SerializeToNode(profile.AlternateGreetings);
        var ext = data["extensions"] as JsonObject ?? new JsonObject();
        if (ext.Parent is null) data["extensions"] = ext;
        if (profile.CharacterNote.Length > 0 || ext.ContainsKey("depth_prompt"))
        {
            var note = ext["depth_prompt"] as JsonObject ?? new JsonObject();
            note["prompt"] = profile.CharacterNote;
            note["depth"] = profile.CharacterNoteDepth;
            note["role"] = Role(profile.CharacterNoteRole);
            if (note.Parent is null) ext["depth_prompt"] = note;
        }
        if (spec == "chara_card_v3" || data.ContainsKey("nickname") || profile.Nickname.Length > 0)
            data["nickname"] = profile.Nickname;
        if (spec == "chara_card_v3")
        {
            data["group_only_greetings"] ??= new JsonArray();
        }
        var book = profile.Lorebooks.FirstOrDefault(b => b.Id == profile.EmbeddedLorebookId)
            ?? (profile.Lorebooks.Count == 1 ? profile.Lorebooks[0] : null);
        if (book is null && profile.Lorebooks.Count > 1)
            throw new InvalidOperationException("请选择随角色卡导出的世界书。");
        if (book is not null) data["character_book"] = WriteBook(book);
        else data.Remove("character_book");
        return Encoding.UTF8.GetBytes(root.ToJsonString(ExportJson));
    }

    public static byte[] WriteLorebook(Lorebook book) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["spec"] = "lorebook_v3", ["data"] = WriteBook(book)
    }.ToJsonString(ExportJson));

    private static JsonObject WriteBook(Lorebook book)
    {
        var data = book.CardData?.DeepClone().AsObject() ?? new JsonObject();
        data["name"] = book.Name;
        data["scan_depth"] = book.ScanScope == LoreScanScope.None ? 0
            : book.ScanScope == LoreScanScope.All || book.ScanScope is null && book.ScanDepth == 0 ? int.MaxValue : book.ScanDepth;
        data["token_budget"] = book.TokenBudget;
        data["recursive_scanning"] = book.RecursiveScanning;
        data["extensions"] ??= new JsonObject();
        var entries = new JsonArray();
        foreach (var entry in book.Entries)
        {
            var value = entry.CardData?.DeepClone().AsObject() ?? new JsonObject();
            value[value.ContainsKey("name") || !value.ContainsKey("comment") ? "name" : "comment"] = entry.Name;
            value["content"] = entry.Content;
            value["keys"] = JsonSerializer.SerializeToNode(entry.Keywords);
            value["secondary_keys"] = JsonSerializer.SerializeToNode(entry.SecondaryKeywords);
            value["enabled"] = entry.Enabled;
            value["constant"] = entry.Constant;
            value["case_sensitive"] = entry.CaseSensitive;
            value["selective"] = entry.Selective;
            value["use_regex"] = entry.UseRegex;
            value["insertion_order"] = entry.Order;
            if ((entry.BudgetPriority ?? (entry.InsertionOrder is null ? entry.Priority : null)) is { } priority)
                value["priority"] = priority;
            else value.Remove("priority");
            value["position"] = entry.Placement == LorePosition.BeforeCharacter ? "before_char" : "after_char";
            var ext = value["extensions"] as JsonObject ?? new JsonObject();
            if (ext.Parent is null) value["extensions"] = ext;
            ext["position"] = (int)entry.Placement;
            ext["depth"] = entry.Depth;
            ext["role"] = Role(entry.Role);
            ext["selectiveLogic"] = (int)entry.SelectiveLogic;
            ext["case_sensitive"] = entry.CaseSensitive;
            ext["match_whole_words"] = entry.MatchWholeWords;
            ext["exclude_recursion"] = entry.ExcludeRecursion;
            ext["prevent_recursion"] = entry.PreventRecursion;
            ext["delay_until_recursion"] = entry.DelayUntilRecursion;
            ext["probability"] = entry.Probability;
            ext["useProbability"] = entry.UseProbability;
            ext["group"] = entry.Group;
            ext["outlet_name"] = entry.OutletName;
            ext["group_override"] = entry.GroupOverride;
            ext["group_weight"] = entry.GroupWeight;
            ext["sticky"] = entry.Sticky;
            ext["cooldown"] = entry.Cooldown;
            ext["delay"] = entry.Delay;
            ext["ignore_budget"] = entry.IgnoreBudget;
            ext["scan_depth"] = entry.ScanScope switch
            {
                LoreScanScope.Inherit => null,
                LoreScanScope.None => JsonValue.Create(0),
                LoreScanScope.All => JsonValue.Create(int.MaxValue),
                null when entry.ScanDepth == 0 => JsonValue.Create(int.MaxValue),
                _ => entry.ScanDepth is { } depth ? JsonValue.Create(depth) : null
            };
            entries.Add(value);
        }
        data["entries"] = entries;
        return data;
    }

    public static byte[] WritePng(byte[] image, byte[] cardJson, byte[]? resourceImage = null)
    {
        if (!image.AsSpan().StartsWith(CharacterCardReader.PngSignature))
            throw new InvalidDataException("角色卡头像需要 PNG 图像。");
        var spec = JsonNode.Parse(cardJson)?["spec"]?.GetValue<string>();
        var key = spec == "chara_card_v3" ? "ccv3" : "chara";
        using var output = new MemoryStream();
        output.Write(CharacterCardReader.PngSignature);
        var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in CharacterCardReader.ReadChunks(image))
        {
            if (chunk.Type == "IEND") continue;
            var keyword = Keyword(image, chunk);
            if (keyword?.Equals("chara", StringComparison.OrdinalIgnoreCase) == true
                || keyword?.Equals("ccv3", StringComparison.OrdinalIgnoreCase) == true) continue;
            if (keyword?.StartsWith("chara-ext-asset_:", StringComparison.Ordinal) == true) assets.Add(keyword);
            output.Write(image.AsSpan(chunk.Offset, chunk.Length + 12));
        }
        if (resourceImage is not null && resourceImage.AsSpan().StartsWith(CharacterCardReader.PngSignature))
        {
            foreach (var chunk in CharacterCardReader.ReadChunks(resourceImage))
            {
                var keyword = Keyword(resourceImage, chunk);
                if (keyword?.StartsWith("chara-ext-asset_:", StringComparison.Ordinal) == true && assets.Add(keyword))
                    output.Write(resourceImage.AsSpan(chunk.Offset, chunk.Length + 12));
            }
        }
        WriteChunk(output, "tEXt", Encoding.UTF8.GetBytes(key + "\0" + Convert.ToBase64String(cardJson)));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static string? Keyword(byte[] image, CharacterCardReader.PngChunk chunk)
    {
        if (chunk.Type is not ("tEXt" or "iTXt")) return null;
        var data = image.AsSpan(chunk.Offset + 8, chunk.Length);
        var end = data.IndexOf((byte)0);
        return end < 0 ? null : Encoding.ASCII.GetString(data[..end]);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        var bytes = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private static int Role(string value) => value switch { "user" => 1, "assistant" => 2, _ => 0 };
}
