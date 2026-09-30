using System.Text.Json.Nodes;
using MolaGPT.Core.Chat.Tools.Mcp;

namespace MolaGPT.Core.Chat.Tools;

/// <summary>
/// How the local agent (Pi) should present one tool, beyond its OpenAI definition.
/// Kept out of the definition itself: the direct providers send that to the model
/// API as is, and these fields are not part of any function-calling schema.
/// </summary>
/// <param name="Exposure">Pi's tool exposure: <c>direct</c>, <c>codemode</c> or
/// <c>deferred</c>.</param>
/// <param name="OutputSchema">Schema of the tool's result as codemode scripts receive
/// it, parsed from the JSON the tool returns.</param>
public sealed record AgentToolHints(
    string Exposure,
    AgentToolNamespace? Namespace,
    McpToolHints? Annotations,
    JsonObject? OutputSchema)
{
    /// <summary>The shape the sidecar extension reads, under <c>molagpt</c>.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["exposure"] = Exposure };
        if (Namespace is { } ns)
            json["namespace"] = new JsonObject { ["name"] = ns.Name, ["description"] = ns.Description };
        if (Annotations is { } hints)
        {
            var annotations = new JsonObject();
            if (hints.ReadOnly is { } readOnly) annotations["readOnlyHint"] = readOnly;
            if (hints.Destructive is { } destructive) annotations["destructiveHint"] = destructive;
            if (hints.Idempotent is { } idempotent) annotations["idempotentHint"] = idempotent;
            if (hints.OpenWorld is { } openWorld) annotations["openWorldHint"] = openWorld;
            json["annotations"] = annotations;
        }
        if (OutputSchema is not null) json["outputSchema"] = OutputSchema.DeepClone();
        return json;
    }
}

/// <summary>A group of tools, such as one MCP server's, listed under one heading.</summary>
public sealed record AgentToolNamespace(string Name, string? Description);
