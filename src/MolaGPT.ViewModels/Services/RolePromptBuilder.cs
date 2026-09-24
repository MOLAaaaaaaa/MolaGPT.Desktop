using System.Text;
using System.Text.RegularExpressions;
using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels.Services;

public sealed record RolePromptBuildResult(string SystemPrompt, RolePromptPlan Plan, LorebookEvaluation Lore,
    int StoryEstimatedTokens, int ContextBudget, string TemplateName);

public static partial class RolePromptBuilder
{
    /// <param name="defaultPrompt">The built-in 通用助手 prompt this persona's own
    /// prompt replaces. Only reachable through <c>{{original}}</c>; a persona with
    /// an empty prompt stays empty rather than inheriting it.</param>
    /// <param name="template">Where each part goes. Leading system blocks form the
    /// system prompt; everything else travels in the plan, in template order.</param>
    public static RolePromptBuildResult Build(PersonaItemViewModel persona, string? defaultPrompt,
        string? conversationPrompt, string? promptMode, ConversationRoleContext context,
        IReadOnlyList<MessageRow> history, IReadOnlyList<Lorebook> books, PromptTemplate template,
        PromptVariables variables, string sourceMessageId, int? reserveOutputTokens, bool continuation = false)
    {
        if (template.Validate() is { } invalid) throw new InvalidDataException(invalid);
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
        variables = variables with { CharacterName = character, RoleFields = fields, Original = defaultPrompt ?? "" };
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

        var blocks = template.Blocks.Where(block => block.Enabled || block.Kind == PromptBlockKind.History).ToArray();
        PromptBlock? Block(PromptBlockKind kind) => blocks.FirstOrDefault(block => block.Kind == kind);
        // The wrapper around a slot costs tokens too; the story budget has to pay for it.
        string Heading(PromptBlock block) => block.Text.Replace(PromptBlock.Slot, "", StringComparison.Ordinal);
        IEnumerable<string> Lore(LorePosition position) =>
            lore.Hits.Where(hit => (hit.Position ?? hit.Entry.Placement) == position).Select(hit => hit.Content);

        var contextBudget = (profile.Compatibility == RoleCompatibility.SillyTavern ? profile.LoreBudget
            : books.Where(book => book.Enabled).Sum(book => book.TokenBudget)) + 2048;
        var storyBudget = Math.Max(0, contextBudget - lore.EstimatedTokens);
        var remaining = storyBudget;
        var memories = new List<string>();
        var eventsBlock = Block(PromptBlockKind.Events);
        void AddMemory(StoryMemory memory)
        {
            if (eventsBlock is null || string.IsNullOrWhiteSpace(memory.Text)) return;
            var text = "- " + memory.Text + "\n";
            var tokens = LorebookMatcher.EstimateTokens((memories.Count == 0 ? Heading(eventsBlock) : "") + text);
            if (tokens > remaining) return;
            memories.Add(text);
            remaining -= tokens;
        }
        foreach (var memory in context.Memories.Where(memory => memory.Pinned)) AddMemory(memory);
        var summary = "";
        if (Block(PromptBlockKind.Summary) is { } summaryBlock)
        {
            var summaryRoom = remaining - LorebookMatcher.EstimateTokens(Heading(summaryBlock) + "\n");
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
                    summary = context.Summary[..length] + "\n";
                    remaining -= LorebookMatcher.EstimateTokens(Heading(summaryBlock) + summary);
                }
            }
        }
        var positions = history.Select((message, index) => (message.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        foreach (var memory in context.Memories.Where(memory => !memory.Pinned)
            .OrderByDescending(memory => memory.SourceMessageIds.Select(id => positions.GetValueOrDefault(id, -1)).DefaultIfEmpty(-1).Max())
            .ThenByDescending(memory => context.Memories.IndexOf(memory))) AddMemory(memory);

        var examples = new List<IReadOnlyList<RolePromptMessage>>();
        if (Block(PromptBlockKind.Examples) is not null)
        {
            foreach (var text in Lore(LorePosition.BeforeExamples))
                examples.AddRange(ParseExamples(text, character, variables.UserName, persona.Name));
            examples.AddRange(ParseExamples(profile.ExampleDialogue, character, variables.UserName, persona.Name, Expand));
            foreach (var text in Lore(LorePosition.AfterExamples))
                examples.AddRange(ParseExamples(text, character, variables.UserName, persona.Name));
        }

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
        var authorNote = Lore(LorePosition.BeforeNote).Concat([Expand(context.AuthorNote)]).Concat(Lore(LorePosition.AfterNote));
        Insert("对话补充", context.AuthorNoteRole, context.AuthorNoteDepth, 1,
            string.Join("\n\n", authorNote.Where(text => !string.IsNullOrWhiteSpace(text))));

        // Main and PostHistory follow SillyTavern: the role's own text wins, and
        // reaches the template's text through {{original}}.
        string Content(PromptBlock block)
        {
            switch (block.Kind)
            {
                case PromptBlockKind.Main:
                    var fallback = Expand(block.Text);
                    var main = string.IsNullOrWhiteSpace(persona.SystemPrompt) ? fallback
                        : SystemPromptInterpolator.Interpolate(persona.SystemPrompt,
                            block.Text.Length > 0 ? vars with { Original = fallback } : vars);
                    return SystemPromptInterpolator.Combine(main,
                        SystemPromptInterpolator.Interpolate(conversationPrompt, vars with { Original = main }), promptMode) ?? "";
                case PromptBlockKind.PostHistory:
                    var instruction = SystemPromptInterpolator.Interpolate(block.Text, vars with { Original = "" });
                    return string.IsNullOrWhiteSpace(profile.PostHistoryInstructions) ? instruction
                        : SystemPromptInterpolator.Interpolate(profile.PostHistoryInstructions, vars with { Original = instruction });
                case PromptBlockKind.Text: return Expand(block.Text);
                case PromptBlockKind.CharacterName: return character;
                case PromptBlockKind.Description: return Expand(profile.Description);
                case PromptBlockKind.Personality: return Expand(profile.Personality);
                case PromptBlockKind.Scenario: return Expand(context.Scenario ?? profile.Scenario);
                case PromptBlockKind.UserPersona: return Expand(fields["persona"]);
                case PromptBlockKind.LoreBefore: return string.Join("\n\n", Lore(LorePosition.BeforeCharacter));
                case PromptBlockKind.LoreAfter: return string.Join("\n\n", Lore(LorePosition.AfterCharacter));
                case PromptBlockKind.Summary: return summary;
                case PromptBlockKind.Events: return string.Concat(memories);
                default: return "";
            }
        }

        var system = new List<string>();
        var before = new List<RolePromptMessage>();
        var after = new List<RolePromptMessage>();
        var examplesAt = 0;
        var inSystem = true;
        var afterHistory = false;
        for (var index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            if (block.Kind == PromptBlockKind.History) { afterHistory = true; continue; }
            if (block.Kind == PromptBlockKind.Examples)
            {
                examplesAt = before.Count;
                inSystem &= examples.Count == 0;
                continue;
            }
            var content = Content(block);
            if (string.IsNullOrWhiteSpace(content)) continue;
            var text = block.UsesFormat && block.Text.Length > 0
                ? Expand(block.Text).Replace(PromptBlock.Slot, content, StringComparison.Ordinal) : content;
            if (block.Depth is { } depth) Insert(block.DisplayName, block.Role, depth, index, text);
            else if (afterHistory) after.Add(new(block.Role, text));
            else if (inSystem && block.Role == "system") system.Add(text);
            else
            {
                inSystem = false;
                before.Add(new(block.Role, text));
            }
        }
        if (after.Count > 0 && after[^1].Role == "assistant")
            throw new InvalidDataException("「" + template.Name + "」：对话历史之后实际发送的最后一块为角色身份，请调整顺序。");
        // An empty prompt would hand the turn back to Pi's own coding-assistant prompt.
        if (system.Count == 0) system.Add("角色：\n" + character);
        return new(string.Join("\n\n", system),
            new RolePromptPlan(examples, insertions, reserveOutputTokens, continuation, before, examplesAt, after), lore,
            storyBudget - remaining, contextBudget, template.Name);
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
