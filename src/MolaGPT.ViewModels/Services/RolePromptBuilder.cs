using System.Text;
using System.Text.RegularExpressions;
using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels.Services;

public sealed record RolePromptBuildResult(string SystemPrompt, RolePromptPlan Plan, LorebookEvaluation Lore,
    int StoryEstimatedTokens, int ContextBudget);

public static partial class RolePromptBuilder
{
    public static RolePromptBuildResult Build(PersonaItemViewModel persona, string? modelPrompt,
        string? conversationPrompt, string? promptMode, ConversationRoleContext context,
        IReadOnlyList<MessageRow> history, IReadOnlyList<Lorebook> books, PromptVariables variables,
        string sourceMessageId, int? reserveOutputTokens, bool continuation = false)
    {
        var profile = persona.Profile;
        var character = string.IsNullOrWhiteSpace(profile.Nickname) ? persona.Name : profile.Nickname;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = profile.Description, ["personality"] = profile.Personality,
            ["scenario"] = context.Scenario ?? profile.Scenario,
            ["persona"] = variables.RoleFields?.GetValueOrDefault("persona") ?? context.UserDescription ?? profile.UserDescription,
            ["charprompt"] = persona.SystemPrompt,
            ["charjailbreak"] = profile.PostHistoryInstructions
        };
        variables = variables with { CharacterName = character, RoleFields = fields, Original = modelPrompt ?? "" };
        var vars = variables;
        string Expand(string text) => SystemPromptInterpolator.Interpolate(text, vars);
        var scopes = context.SharedLorebookIds?.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => -1, StringComparer.Ordinal);
        var states = context.LoreStates.Where(pair => pair.Value.SourceMessageId != sourceMessageId)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var lore = LorebookMatcher.Evaluate(books, history.Select(message => message.Content).ToArray(), Expand,
            new(profile.Compatibility, profile.Compatibility == RoleCompatibility.SillyTavern ? profile.LoreBudget : null,
                states, sourceMessageId,
                vars.GenerationSeed ?? throw new InvalidOperationException("角色上下文缺少本轮生成标识。"),
                scopes, history.Count + (continuation ? 0 : 1)),
            interpolateScan: text => SystemPromptInterpolator.Interpolate(text, vars with { KeepHiddenKeys = true }));
        var outlets = lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.Outlet)
            .Where(hit => hit.Entry.OutletName.Length > 0).GroupBy(hit => hit.Entry.OutletName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join("\n", group.Select(hit => hit.Content)), StringComparer.Ordinal);
        vars = vars with { Outlets = outlets };

        var main = string.IsNullOrWhiteSpace(persona.SystemPrompt) ? Expand(modelPrompt ?? "") : Expand(persona.SystemPrompt);
        main = SystemPromptInterpolator.Combine(main,
            SystemPromptInterpolator.Interpolate(conversationPrompt, vars with { Original = main }), promptMode) ?? "";
        var sections = new List<string> { main };
        sections.AddRange(lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.BeforeCharacter).Select(hit => hit.Content));
        sections.Add("角色：\n" + character);
        foreach (var (label, value) in new[]
        {
            ("角色资料", profile.Description), ("性格与表达", profile.Personality),
            ("场景", context.Scenario ?? profile.Scenario), ("用户身份", fields["persona"])
        })
            if (!string.IsNullOrWhiteSpace(value)) sections.Add(label + "：\n" + Expand(value));
        sections.AddRange(lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.AfterCharacter).Select(hit => hit.Content));
        var contextBudget = (profile.Compatibility == RoleCompatibility.SillyTavern ? profile.LoreBudget
            : books.Where(book => book.Enabled).Sum(book => book.TokenBudget)) + 2048;
        var storyBudget = Math.Max(0, contextBudget - lore.EstimatedTokens);
        var remaining = storyBudget;
        var memories = new List<string>();
        const string memoryHeading = "已经发生的事件：\n";
        void AddMemory(StoryMemory memory)
        {
            if (string.IsNullOrWhiteSpace(memory.Text)) return;
            var text = "- " + memory.Text + "\n";
            var tokens = LorebookMatcher.EstimateTokens((memories.Count == 0 ? memoryHeading : "") + text);
            if (tokens > remaining) return;
            memories.Add(text);
            remaining -= tokens;
        }
        foreach (var memory in context.Memories.Where(memory => memory.Pinned)) AddMemory(memory);
        const string summaryHeading = "剧情摘要：\n";
        var summaryRoom = remaining - LorebookMatcher.EstimateTokens(summaryHeading + "\n");
        if (summaryRoom > 0 && !string.IsNullOrWhiteSpace(context.Summary))
        {
            var length = 0;
            var cost = 0d;
            while (length < context.Summary.Length)
            {
                var next = context.Summary[length] <= 127 ? 0.25 : 1;
                if (cost + next > summaryRoom) break;
                cost += next;
                length++;
            }
            if (length > 0 && char.IsHighSurrogate(context.Summary[length - 1])) length--;
            if (length > 0)
            {
                var summary = summaryHeading + context.Summary[..length] + "\n";
                sections.Add(summary);
                remaining -= LorebookMatcher.EstimateTokens(summary);
            }
        }
        var positions = history.Select((message, index) => (message.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        foreach (var memory in context.Memories.Where(memory => !memory.Pinned)
            .OrderByDescending(memory => memory.SourceMessageIds.Select(id => positions.GetValueOrDefault(id, -1)).DefaultIfEmpty(-1).Max())
            .ThenByDescending(memory => context.Memories.IndexOf(memory))) AddMemory(memory);
        if (memories.Count > 0) sections.Add(memoryHeading + string.Concat(memories));

        var examples = new List<IReadOnlyList<RolePromptMessage>>();
        foreach (var hit in lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.BeforeExamples))
            examples.AddRange(ParseExamples(hit.Content, character, variables.UserName, persona.Name));
        examples.AddRange(ParseExamples(profile.ExampleDialogue, character, variables.UserName, persona.Name, Expand));
        foreach (var hit in lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.AfterExamples))
            examples.AddRange(ParseExamples(hit.Content, character, variables.UserName, persona.Name));

        var insertions = new List<RolePromptInsertion>();
        void Insert(string source, string role, int depth, int order, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (depth == 0 && role == "assistant")
                throw new InvalidDataException("末尾补充请选择系统或用户身份。");
            insertions.Add(new(source, role, Math.Max(0, depth), order, text));
        }
        foreach (var hit in lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.AtDepth))
            Insert(hit.BookName + " · " + hit.Entry.Name, hit.Role ?? hit.Entry.Role,
                hit.Depth ?? hit.Entry.Depth, hit.Entry.Order, hit.Content);
        Insert("角色补充", profile.CharacterNoteRole, profile.CharacterNoteDepth, 0, Expand(profile.CharacterNote));
        var authorNote = lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.BeforeNote).Select(hit => hit.Content)
            .Concat([Expand(context.AuthorNote)])
            .Concat(lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == LorePosition.AfterNote).Select(hit => hit.Content));
        Insert("对话补充", context.AuthorNoteRole, context.AuthorNoteDepth, 1,
            string.Join("\n\n", authorNote.Where(text => !string.IsNullOrWhiteSpace(text))));
        Insert("后置指令", "system", 0, int.MaxValue,
            SystemPromptInterpolator.Interpolate(profile.PostHistoryInstructions, vars with { Original = "" }));
        return new(string.Join("\n\n", sections.Where(text => !string.IsNullOrWhiteSpace(text))),
            new RolePromptPlan(examples, insertions, reserveOutputTokens, continuation), lore,
            storyBudget - remaining, contextBudget);
    }

    private static IReadOnlyList<IReadOnlyList<RolePromptMessage>> ParseExamples(string text,
        string characterName, string? userName, string libraryName, Func<string, string>? expand = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var output = new List<IReadOnlyList<RolePromptMessage>>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = "user", ["assistant"] = "assistant", ["char"] = "assistant"
        };
        names[characterName] = "assistant";
        names[libraryName] = "assistant";
        names[string.IsNullOrWhiteSpace(userName) ? "用户" : userName] = "user";
        names["{{user}}"] = "user";
        names["{{char}}"] = "assistant";
        foreach (var block in ExampleSeparator().Split(text).Where(block => !string.IsNullOrWhiteSpace(block)))
        {
            var messages = new List<RolePromptMessage>();
            var content = new StringBuilder();
            string? role = null;
            void Flush()
            {
                if (content.Length > 0)
                {
                    var value = content.ToString().TrimEnd();
                    messages.Add(new(role ?? "system", expand is null ? value : expand(value)));
                }
                content.Clear();
            }
            foreach (var line in block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var colon = line.IndexOfAny([':', '：']);
                if (colon > 0 && names.TryGetValue(line[..colon].Trim(), out var speaker))
                {
                    Flush();
                    role = speaker;
                    content.AppendLine(line[(colon + 1)..].TrimStart());
                }
                else content.AppendLine(line);
            }
            Flush();
            if (messages.Count > 0) output.Add(messages);
        }
        return output;
    }

    [GeneratedRegex(@"(?:^|\r?\n)\s*<START>\s*(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExampleSeparator();
}
