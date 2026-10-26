namespace MolaGPT.Core.Chat.Tools;

public static class ToolActivity
{
    public const string Reviewing = "reviewing";
    public const string ReviewingExtended = "reviewing_extended";
    public const string AutoApprovedPrefix = "auto_approved:";

    public static string AutoApproved(string reason) => AutoApprovedPrefix + reason;
}
