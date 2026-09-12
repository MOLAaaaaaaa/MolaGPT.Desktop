using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace MolaGPT.Core.Models;

public sealed record ImportedCharacter(string Name, string SystemPrompt, PersonaProfile Profile, byte[]? Avatar);

public static class CharacterCardReader
{
    internal static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static ImportedCharacter Read(byte[] bytes)
    {
        var root = ReadDocument(bytes);
        var spec = Text(root, "spec");
        if (spec.Length > 0 && spec is not ("chara_card_v2" or "chara_card_v3"))
            throw new InvalidDataException("请选择 V1、V2 或 V3 角色卡。");
        var data = spec.Length == 0 ? root
            : root["data"]?.AsObject() ?? throw new InvalidDataException("角色卡缺少资料。");
        var name = Text(data, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("角色卡缺少名称。");
        if (spec.Length == 0 && !data.ContainsKey("description") && !data.ContainsKey("first_mes"))
            throw new InvalidDataException("这个文件不是角色卡。");
        var profile = new PersonaProfile
        {
            Mode = ConversationMode.Atmosphere,
            Compatibility = RoleCompatibility.SillyTavern,
            CardSpec = spec.Length == 0 ? "chara_card_v3" : spec,
            Nickname = Text(data, "nickname"), Creator = Text(data, "creator"),
            CreatorNotes = Text(data, "creator_notes"), CharacterVersion = Text(data, "character_version"),
            Tags = Strings(data["tags"]), Description = Text(data, "description"),
            Personality = Text(data, "personality"), Scenario = Text(data, "scenario"),
            Greeting = Text(data, "first_mes"), AlternateGreetings = Strings(data["alternate_greetings"]),
            ExampleDialogue = Text(data, "mes_example"), PostHistoryInstructions = Text(data, "post_history_instructions")
        };
        if (data["extensions"]?["depth_prompt"] is JsonObject note)
        {
            profile.CharacterNote = Text(note, "prompt");
            profile.CharacterNoteDepth = Number(note, "depth", 4);
            profile.CharacterNoteRole = Role(Number(note, "role", 0));
        }
        if (data["character_book"] is JsonObject book)
        {
            var lore = ReadBook(book);
            profile.Lorebooks.Add(lore);
            profile.EmbeddedLorebookId = lore.Id;
        }
        var extensions = data["extensions"] as JsonObject;
        if (extensions?["tavern_helper"] is JsonObject helper
                && (helper["scripts"] is JsonArray { Count: > 0 }
                    || helper["variables"] is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
            || extensions?["TavernHelper_scripts"] is JsonArray { Count: > 0 }
            || extensions?["TavernHelper_characterScriptVariables"] is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
            profile.ImportNotes.Add("脚本扩展已保留，暂不运行");
        if (extensions?["regex_scripts"] is JsonArray { Count: > 0 })
            profile.ImportNotes.Add("局部正则已保留，暂不运行");
        if (extensions?["risuai"] is JsonObject) profile.ImportNotes.Add("RisuAI 扩展已保留");
        var otherExtensions = extensions?.Count(pair => pair.Key is not
            ("depth_prompt" or "tavern_helper" or "TavernHelper_scripts" or "TavernHelper_characterScriptVariables" or "regex_scripts" or "risuai")) ?? 0;
        if (otherExtensions > 0) profile.ImportNotes.Add($"另有 {otherExtensions} 项卡片扩展已保留");
        if (data["assets"] is JsonArray { Count: > 0 }) profile.ImportNotes.Add("附带资源已保留");
        byte[]? avatar = bytes.AsSpan().StartsWith(PngSignature) ? bytes : null;
        if (IsZip(bytes) && data["assets"] is JsonArray assets)
        {
            var icon = assets.OfType<JsonObject>().FirstOrDefault(a => Text(a, "type") == "icon" && Text(a, "name") == "main")
                ?? assets.OfType<JsonObject>().FirstOrDefault(a => Text(a, "type") == "icon");
            var uri = icon is null ? "" : Text(icon, "uri");
            if (uri.StartsWith("embeded://", StringComparison.Ordinal))
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var entry = zip.GetEntry(uri["embeded://".Length..])
                    ?? throw new InvalidDataException("角色卡的头像资源不存在。");
                using var input = entry.Open();
                using var output = new MemoryStream();
                input.CopyTo(output);
                avatar = output.ToArray();
            }
        }
        return new ImportedCharacter(name, Text(data, "system_prompt"), profile, avatar);
    }

    public static JsonObject ReadDocument(byte[] bytes)
    {
        string json;
        if (bytes.AsSpan().StartsWith(PngSignature)) json = ReadPngMetadata(bytes);
        else if (IsZip(bytes))
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry("card.json") ?? throw new InvalidDataException("角色包缺少 card.json。");
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        else json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        return JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("角色卡内容为空。");
    }

    public static Lorebook ReadLorebook(byte[] bytes)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))?.AsObject()
            ?? throw new InvalidDataException("世界书内容为空。");
        if (Text(root, "spec") == "lorebook_v3")
            root = root["data"]?.AsObject() ?? throw new InvalidDataException("世界书缺少资料。");
        if (root["entries"] is not (JsonArray or JsonObject)) throw new InvalidDataException("世界书缺少条目。");
        return ReadBook(root);
    }

    private static Lorebook ReadBook(JsonObject book)
    {
        var raw = (JsonObject)book.DeepClone();
        raw.Remove("entries");
        var depth = Number(book, "scan_depth", 4);
        var lore = new Lorebook
        {
            Name = Text(book, "name") is { Length: > 0 } name ? name : "世界书", CardData = raw,
            ScanDepth = depth, ScanScope = depth switch { 0 => LoreScanScope.None, int.MaxValue => LoreScanScope.All, _ => LoreScanScope.Recent },
            TokenBudget = Number(book, "token_budget", 2048), RecursiveScanning = Flag(book, "recursive_scanning")
        };
        if (depth < 0 || lore.TokenBudget < 0) throw new InvalidDataException("世界书的扫描范围和预算不能为负数。");
        var native = book["entries"] is JsonObject;
        IEnumerable<JsonNode?> entries = book["entries"] switch
        {
            JsonObject map => map.Select(pair => pair.Value),
            JsonArray array => array, _ => []
        };
        foreach (var node in entries)
        {
            var entry = node?.AsObject() ?? throw new InvalidDataException("世界书条目为空。");
            var ext = native ? entry : entry["extensions"] as JsonObject ?? new JsonObject();
            var entryDepth = NullableNumber(ext, native ? "scanDepth" : "scan_depth");
            var position = NullableNumber(ext, "position") ?? (Text(entry, "position") == "before_char" ? 0 : 1);
            var delayKey = native ? "delayUntilRecursion" : "delay_until_recursion";
            var delayRecursion = ext[delayKey];
            lore.Entries.Add(new LoreEntry
            {
                Name = Text(entry, "name") is { Length: > 0 } entryName ? entryName : Text(entry, "comment"),
                CardData = (JsonObject)entry.DeepClone(), Content = Text(entry, "content"),
                Keywords = Strings(entry[native ? "key" : "keys"]),
                SecondaryKeywords = Strings(entry[native ? "keysecondary" : "secondary_keys"]),
                Enabled = native ? !Flag(entry, "disable") : Flag(entry, "enabled", true),
                Constant = Flag(entry, "constant"),
                CaseSensitive = Flag(ext, native ? "caseSensitive" : "case_sensitive", Flag(entry, "case_sensitive")),
                Selective = Flag(entry, "selective"),
                SelectiveLogic = (LoreSelectiveLogic)Number(ext, "selectiveLogic", 0),
                UseRegex = Flag(entry, "use_regex"),
                MatchWholeWords = Flag(ext, native ? "matchWholeWords" : "match_whole_words"),
                InsertionOrder = Number(entry, native ? "order" : "insertion_order", 100),
                BudgetPriority = NullableNumber(entry, "priority"), ScanDepth = entryDepth,
                ScanScope = entryDepth switch { null => LoreScanScope.Inherit, 0 => LoreScanScope.None, int.MaxValue => LoreScanScope.All, _ => LoreScanScope.Recent },
                Position = (LorePosition)position, BeforeCharacter = position == 0,
                Depth = Number(ext, "depth", 4), Role = Role(Number(ext, "role", 0)),
                ExcludeRecursion = Flag(ext, native ? "excludeRecursion" : "exclude_recursion"),
                PreventRecursion = Flag(ext, native ? "preventRecursion" : "prevent_recursion"),
                DelayUntilRecursion = delayRecursion is JsonValue value && value.TryGetValue<bool>(out var delay)
                    ? delay ? 1 : 0 : NullableNumber(ext, delayKey) ?? 0,
                Probability = Number(ext, "probability", 100), UseProbability = Flag(ext, "useProbability", true),
                Group = Text(ext, "group"), OutletName = Text(ext, native ? "outletName" : "outlet_name"),
                GroupOverride = Flag(ext, native ? "groupOverride" : "group_override"),
                GroupWeight = Number(ext, native ? "groupWeight" : "group_weight", 100),
                Sticky = Number(ext, "sticky", 0), Cooldown = Number(ext, "cooldown", 0),
                Delay = Number(ext, "delay", 0), IgnoreBudget = Flag(ext, native ? "ignoreBudget" : "ignore_budget")
            });
        }
        return lore;
    }

    internal static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
    private static int Number(JsonObject value, string key, int otherwise) => NullableNumber(value, key) ?? otherwise;
    private static int? NullableNumber(JsonObject value, string key) => value[key]?.GetValue<int>();
    private static bool Flag(JsonObject value, string key, bool otherwise = false) => value[key]?.GetValue<bool>() ?? otherwise;
    private static string Role(int value) => value switch { 1 => "user", 2 => "assistant", _ => "system" };
    private static List<string> Strings(JsonNode? value) => value is JsonArray items
        ? items.Select(item => item?.GetValue<string>() ?? "").Where(item => item.Length > 0).ToList() : [];
    internal static bool IsZip(byte[] bytes) => bytes.AsSpan().StartsWith(new byte[] { 80, 75, 3, 4 });

    internal readonly record struct PngChunk(int Offset, int Length, string Type);
    internal static IEnumerable<PngChunk> ReadChunks(byte[] bytes)
    {
        for (var offset = 8; offset + 12 <= bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            if (length > bytes.Length - offset - 12) throw new InvalidDataException("PNG 文件不完整。");
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            yield return new PngChunk(offset, length, type);
            offset += length + 12;
            if (type == "IEND") yield break;
        }
    }

    private static string ReadPngMetadata(byte[] bytes)
    {
        PngChunk? legacy = null, v3 = null;
        foreach (var chunk in ReadChunks(bytes))
        {
            if (chunk.Type is not ("tEXt" or "iTXt")) continue;
            var data = bytes.AsSpan(chunk.Offset + 8, chunk.Length);
            var end = data.IndexOf((byte)0);
            if (end < 0) continue;
            var key = Encoding.ASCII.GetString(data[..end]);
            if (key.Equals("ccv3", StringComparison.OrdinalIgnoreCase)) v3 ??= chunk;
            else if (key.Equals("chara", StringComparison.OrdinalIgnoreCase)) legacy ??= chunk;
        }
        var selected = v3 ?? legacy ?? throw new InvalidDataException("这张图片没有角色卡资料。");
        var text = bytes.AsSpan(selected.Offset + 8, selected.Length);
        var payload = text[(text.IndexOf((byte)0) + 1)..];
        if (selected.Type == "iTXt")
        {
            if (payload.Length < 4) throw new InvalidDataException("PNG 角色资料不完整。");
            var compressed = payload[0] == 1;
            payload = payload[2..];
            for (var i = 0; i < 2; i++)
            {
                var end = payload.IndexOf((byte)0);
                if (end < 0) throw new InvalidDataException("PNG 角色资料不完整。");
                payload = payload[(end + 1)..];
            }
            if (compressed)
            {
                using var input = new MemoryStream(payload.ToArray());
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.UTF8);
                return Encoding.UTF8.GetString(Convert.FromBase64String(reader.ReadToEnd()));
            }
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(payload)));
    }
}
