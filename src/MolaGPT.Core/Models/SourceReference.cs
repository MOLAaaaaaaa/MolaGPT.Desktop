namespace MolaGPT.Core.Models;

/// <summary>
/// Source metadata used by MolaGPT citation tags:
/// <c>&lt;ref source="1" /&gt;</c> maps to one of these records.
/// </summary>
public sealed record SourceReference(int Id, string Title, string Url);
