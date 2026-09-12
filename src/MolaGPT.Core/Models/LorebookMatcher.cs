using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Models;

public sealed record LorebookHit(string BookName, LoreEntry Entry, string Content, int EstimatedTokens,
    LorePosition? Position = null, int? Depth = null, string? Role = null);
public sealed record LorebookDecision(string BookName, string EntryName, string Reason, bool Included, int EstimatedTokens);
public sealed record LorebookEvaluation(IReadOnlyList<LorebookHit> Hits, IReadOnlyList<LorebookDecision> Decisions,
    IReadOnlyDictionary<string, LoreActivationState> States, int EstimatedTokens);
public sealed record LorebookOptions(
    RoleCompatibility Compatibility = RoleCompatibility.CharacterCardSpec,
    int? TotalBudget = null,
    IReadOnlyDictionary<string, LoreActivationState>? States = null,
    string SourceMessageId = "",
    string GenerationId = "",
    IReadOnlyDictionary<string, int>? SourcePriorities = null,
    int? ActivationMessageCount = null);

public static class LorebookMatcher
{
    public static IReadOnlyList<LorebookHit> Select(IReadOnlyList<Lorebook> books,
        IReadOnlyList<string> messages, Func<string, string> interpolate) =>
        Evaluate(books, messages, interpolate).Hits;

    public static LorebookEvaluation Evaluate(IReadOnlyList<Lorebook> books,
        IReadOnlyList<string> messages, Func<string, string> interpolate,
        LorebookOptions? options = null, Func<string, int>? countTokens = null, Func<string, string>? interpolateScan = null)
    {
        options ??= new();
        countTokens ??= EstimateTokens;
        var st = options.Compatibility == RoleCompatibility.SillyTavern;
        // SillyTavern budgets the whole turn, not each book, so the caller has to say
        // what the turn's budget is. Inventing one here would silently drop entries.
        if (st && options.TotalBudget is null or < 0)
            throw new InvalidOperationException("SillyTavern 兼容模式需要世界书总预算。");
        var hits = new List<LorebookHit>();
        var decisions = new Dictionary<string, LorebookDecision>(StringComparer.Ordinal);
        var states = options.States is null ? new Dictionary<string, LoreActivationState>()
            : new Dictionary<string, LoreActivationState>(options.States);
        var candidates = books.DistinctBy(b => b.Id).SelectMany(book => book.Entries.Select(entry => new Candidate(book, entry))).ToList();
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var regexes = new Dictionary<(string, RegexOptions), Regex>();
        var selectedGroups = new HashSet<string>(StringComparer.Ordinal);
        var usedByBook = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedText = "";
        var recursionText = "";
        var overflow = false;
        var round = 0;

        void Decide(Candidate c, string reason, bool included = false, int tokens = 0) =>
            decisions[c.Key] = new(c.Book.Name, c.Entry.DisplayName, reason, included, tokens);

        bool Sticky(Candidate c) => states.TryGetValue(c.Key, out var state)
            && c.Entry.Sticky > 0 && messages.Count >= state.ActivatedAt
            && messages.Count - state.ActivatedAt <= c.Entry.Sticky;

        // The same generation must keep the same probability and group decisions.
        double Draw(string key)
        {
            if (options.GenerationId.Length == 0)
                throw new InvalidOperationException("世界书的概率与分组需要本轮生成标识。");
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(options.GenerationId + ":" + key));
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) / 4294967296d;
        }

        bool Matches(string key, string context, LoreEntry entry)
        {
            key = interpolate(key).Trim();
            if (key.Length == 0) return false;
            var pattern = key;
            var flags = RegexOptions.CultureInvariant;
            var slash = key.StartsWith('/') ? key.LastIndexOf('/') : -1;
            var literalRegex = slash > 0;
            if (literalRegex)
            {
                pattern = key[1..slash];
                foreach (var flag in key[(slash + 1)..])
                    flags |= flag switch
                    {
                        'i' => RegexOptions.IgnoreCase, 'm' => RegexOptions.Multiline,
                        's' => RegexOptions.Singleline, 'g' or 'u' => RegexOptions.None,
                        _ => throw new InvalidDataException($"世界书「{entry.Name}」的正则标志 {flag} 暂不支持。")
                    };
            }
            else
            {
                if (!entry.CaseSensitive) flags |= RegexOptions.IgnoreCase;
                if (!entry.UseRegex)
                {
                    if (!entry.MatchWholeWords) return context.Contains(key,
                        entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    pattern = @"(?<![\p{L}\p{N}_])" + Regex.Escape(key) + @"(?![\p{L}\p{N}_])";
                }
            }
            try
            {
                if (!regexes.TryGetValue((pattern, flags), out var regex))
                    regexes[(pattern, flags)] = regex = new Regex(pattern, flags, TimeSpan.FromMilliseconds(150));
                return regex.IsMatch(context);
            }
            catch (ArgumentException ex) { throw new InvalidDataException($"世界书「{entry.Name}」的正则无效：{ex.Message}", ex); }
            catch (RegexMatchTimeoutException ex) { throw new InvalidDataException($"世界书「{entry.Name}」的正则匹配超时。", ex); }
        }

        foreach (var c in candidates)
        {
            if (!c.Book.Enabled || !c.Entry.Enabled)
            {
                Decide(c, "未启用");
                processed.Add(c.Key);
                continue;
            }
            c.Parse(interpolate(c.Entry.Content), interpolateScan?.Invoke(c.Entry.Content));
            if (c.Position == LorePosition.Outlet && string.IsNullOrWhiteSpace(c.Entry.OutletName))
            { Decide(c, "命名位置为空"); processed.Add(c.Key); continue; }
            if (c.Unsupported is { } unsupported)
            {
                Decide(c, unsupported);
                processed.Add(c.Key);
            }
        }

        while (true)
        {
            var active = new List<Candidate>();
            foreach (var c in candidates)
            {
                if (processed.Contains(c.Key)) continue;
                var e = c.Entry;
                if (c.ForceOff && !c.ForceOn) { Decide(c, "控制项已停用"); processed.Add(c.Key); continue; }
                if (messages.Count < e.Delay) { Decide(c, "尚未到激活时间"); continue; }
                if (round < e.DelayUntilRecursion) { Decide(c, "等待递归扫描"); continue; }
                if (round > 0 && (!c.Book.RecursiveScanning || e.ExcludeRecursion)) continue;
                var sticky = Sticky(c);
                if (!sticky && states.TryGetValue(c.Key, out var state) && e.Cooldown > 0
                    && messages.Count >= state.ActivatedAt
                    && messages.Count - state.ActivatedAt <= e.Sticky + e.Cooldown)
                { Decide(c, "冷却中"); continue; }
                var scope = c.ScanScope ?? e.ScanScope;
                var depth = c.ScanDepth ?? e.ScanDepth;
                if (scope is null)
                    scope = depth is null ? LoreScanScope.Inherit : depth == 0 ? LoreScanScope.All : LoreScanScope.Recent;
                if (scope == LoreScanScope.Inherit)
                {
                    scope = c.Book.ScanScope ?? (c.Book.ScanDepth == 0 ? LoreScanScope.All : LoreScanScope.Recent);
                    depth = c.Book.ScanDepth;
                }
                var recent = scope switch
                {
                    LoreScanScope.None => "",
                    LoreScanScope.All => string.Join("\n", messages),
                    _ => string.Join("\n", messages.TakeLast(Math.Max(0, depth ?? c.Book.ScanDepth)))
                };
                var context = recent + (round > 0 ? "\n" + recursionText : "");
                // CCV3 #constant: use_regex=true SHOULD ignore constant; ST activates it directly.
                // https://github.com/kwaroran/character-card-spec-v3/blob/main/SPEC_V3.md#constant
                var triggered = c.ForceOn || sticky || e.Constant && (st || !e.UseRegex);
                if (!triggered)
                {
                    triggered = e.Keywords.Any(k => Matches(k, context, e));
                    if (triggered && e.Selective && e.SecondaryKeywords.Count > 0)
                    {
                        var matches = e.SecondaryKeywords.Count(k => Matches(k, context, e));
                        triggered = e.SelectiveLogic switch
                        {
                            LoreSelectiveLogic.AndAll => matches == e.SecondaryKeywords.Count,
                            LoreSelectiveLogic.NotAny => matches == 0,
                            LoreSelectiveLogic.NotAll => matches < e.SecondaryKeywords.Count,
                            _ => matches > 0
                        };
                    }
                }
                if (!triggered) { Decide(c, "未命中"); continue; }
                if (string.IsNullOrWhiteSpace(c.Content)) { Decide(c, "内容为空"); processed.Add(c.Key); continue; }
                active.Add(c);
            }
            active = active.OrderByDescending(Sticky)
                .ThenBy(c => options.SourcePriorities?.GetValueOrDefault(c.Book.Id) ?? 0)
                .ThenByDescending(c => st ? c.Entry.Order : c.Entry.SelectionPriority).ToList();

            foreach (var c in active.Where(c => c.Groups.Any(selectedGroups.Contains)).ToArray())
            { Decide(c, "已采用同组条目"); processed.Add(c.Key); active.Remove(c); }
            foreach (var group in active.SelectMany(c => c.Groups).Distinct().ToArray())
            {
                var members = active.Where(c => c.Groups.Contains(group)).ToList();
                if (members.Count <= 1) continue;
                var winner = members.FirstOrDefault(c => Sticky(c))
                    ?? members.Where(c => c.Entry.GroupOverride).OrderByDescending(c => c.Entry.Order).FirstOrDefault();
                if (winner is null)
                {
                    var weight = members.Sum(c => Math.Max(0, c.Entry.GroupWeight));
                    if (weight == 0)
                    {
                        foreach (var member in members) { Decide(member, "同组权重为零"); processed.Add(member.Key); active.Remove(member); }
                        continue;
                    }
                    var roll = Draw("group:" + group) * weight;
                    foreach (var member in members)
                    {
                        roll -= Math.Max(0, member.Entry.GroupWeight);
                        if (roll < 0) { winner = member; break; }
                    }
                }
                if (winner is null) throw new InvalidOperationException("世界书组选择失败。");
                foreach (var member in members.Where(c => c != winner))
                { Decide(member, "已采用同组条目"); processed.Add(member.Key); active.Remove(member); }
            }

            var added = new List<Candidate>();
            var candidateText = "";
            var baseTokens = countTokens(usedText);
            foreach (var c in active)
            {
                processed.Add(c.Key);
                var e = c.Entry;
                if (st && overflow && !e.IgnoreBudget) { Decide(c, "总预算已用完"); continue; }
                if (!Sticky(c) && e.UseProbability && (e.Probability <= 0
                    || e.Probability < 100 && Draw(c.Key) * 100 >= e.Probability))
                { Decide(c, "本轮未触发"); continue; }
                var entryText = c.Content + "\n";
                var tokens = countTokens(entryText);
                if (st)
                {
                    candidateText += entryText;
                    if (!e.IgnoreBudget && baseTokens + countTokens(candidateText) >= options.TotalBudget!.Value)
                    { overflow = true; Decide(c, "超出总预算", tokens: tokens); continue; }
                }
                else
                {
                    var bookText = usedByBook.GetValueOrDefault(c.Book.Id, "") + entryText;
                    if (!e.IgnoreBudget && (countTokens(bookText) > c.Book.TokenBudget
                        || options.TotalBudget is { } total && countTokens(usedText + entryText) > total))
                    { Decide(c, "超出预算", tokens: tokens); continue; }
                    usedByBook[c.Book.Id] = bookText;
                }
                usedText += entryText;
                hits.Add(new(c.Book.Name, e, c.Content, tokens, c.Position, c.Depth, c.Role));
                Decide(c, Sticky(c) ? "持续生效" : c.ForceOn || e.Constant ? "常驻" : round > 0 ? "递归命中" : "关键词命中", true, tokens);
                added.Add(c);
                foreach (var group in c.Groups) selectedGroups.Add(group);
                if (options.SourceMessageId.Length > 0 && !Sticky(c) && (e.Sticky > 0 || e.Cooldown > 0))
                    states[c.Key] = new LoreActivationState(options.ActivationMessageCount ?? messages.Count + 1, options.SourceMessageId);
            }
            if (st && overflow) break;
            var recursive = added.Where(c => !c.Entry.PreventRecursion).ToList();
            if (recursive.Count > 0 && candidates.Any(c => !processed.Contains(c.Key) && c.Book.RecursiveScanning))
            {
                recursionText += "\n" + string.Join("\n", recursive.Select(c => c.ScanContent));
                round++;
                continue;
            }
            var nextDelay = candidates.Where(c => !processed.Contains(c.Key) && c.Book.RecursiveScanning
                && c.Entry.DelayUntilRecursion > round).Select(c => c.Entry.DelayUntilRecursion).DefaultIfEmpty(-1).Min();
            if (nextDelay < 0) break;
            round = nextDelay;
        }
        var valid = candidates.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in states.Keys.Where(key => !valid.Contains(key)).ToArray()) states.Remove(key);
        foreach (var c in candidates)
            if (!decisions.ContainsKey(c.Key)) Decide(c, overflow ? "总预算已用完" : "未命中");
        return new(hits.OrderBy(h => h.Entry.Order).ToArray(), decisions.Values.ToArray(), states, countTokens(usedText));
    }

    public static int EstimateTokens(string text) =>
        (int)Math.Ceiling(text.Sum(c => c <= 127 ? 0.25 : 1.0));

    private sealed class Candidate(Lorebook book, LoreEntry entry)
    {
        public Lorebook Book { get; } = book;
        public LoreEntry Entry { get; } = entry;
        public string Key => Book.Id + ":" + Entry.Id;
        public string Content { get; private set; } = "";
        public string ScanContent { get; private set; } = "";
        public LorePosition Position { get; private set; } = entry.Placement;
        public int Depth { get; private set; } = entry.Depth;
        public string Role { get; private set; } = entry.Role;
        public LoreScanScope? ScanScope { get; private set; }
        public int? ScanDepth { get; private set; }
        public bool ForceOn { get; private set; }
        public bool ForceOff { get; private set; }
        public string? Unsupported { get; private set; }
        public string[] Groups { get; } = entry.Group.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        public void Parse(string content, string? scanContent)
        {
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var offset = 0;
            while (offset < lines.Length && lines[offset].StartsWith("@@", StringComparison.Ordinal))
            {
                var parts = lines[offset].Split(' ', 2, StringSplitOptions.TrimEntries);
                var value = parts.Length == 2 ? parts[1] : "";
                switch (parts[0])
                {
                    case "@@activate": ForceOn = true; break;
                    case "@@dont_activate": ForceOff = true; break;
                    case "@@depth": Depth = Math.Max(0, ParseNumber(value)); Position = LorePosition.AtDepth; break;
                    case "@@role":
                        if (value is not ("system" or "user" or "assistant")) throw new InvalidDataException($"世界书「{Entry.Name}」的消息身份无效。");
                        Role = value; break;
                    case "@@scan_depth":
                        ScanDepth = Math.Max(0, ParseNumber(value));
                        ScanScope = ScanDepth == 0 ? LoreScanScope.None : LoreScanScope.Recent; break;
                    case "@@position":
                        if (value is "before_desc") Position = LorePosition.BeforeCharacter;
                        else if (value is "after_desc") Position = LorePosition.AfterCharacter;
                        else Unsupported = "暂未支持的位置：" + value;
                        break;
                    default: Unsupported = "暂未支持的控制项：" + parts[0]; break;
                }
                offset++;
            }
            Content = offset == 0 ? content : string.Join("\n", lines.Skip(offset)).Trim('\r', '\n');
            ScanContent = scanContent is null ? Content : offset == 0 ? scanContent
                : string.Join("\n", scanContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Skip(offset));
        }

        private int ParseNumber(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : throw new InvalidDataException($"世界书「{Entry.Name}」的控制项数值无效。");
    }
}
