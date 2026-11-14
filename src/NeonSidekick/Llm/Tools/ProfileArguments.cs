using Microsoft.Extensions.AI;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The arguments every <c>*_profile</c> tool takes beside its family's <c>table</c> and <c>connection</c>/<c>database</c> (2026-11-14, the
/// database-tools plan's #14): <c>columns</c>, a subset by name, and <c>top</c>, the most common values a column shows. One schema text and
/// one reader, so the six tools can't drift apart.
/// </summary>
public static class ProfileArguments
{
    public const string ColumnsArgument = "columns";
    public const string TopArgument = "top";

    public const string ColumnsProperty = "\"columns\": { \"type\": \"array\", \"items\": { \"type\": \"string\" }, \"description\": \"The columns to profile, by name; all of them (up to 40) when left out.\" }";

    public const string TopProperty = "\"top\": { \"type\": \"integer\", \"description\": \"How many of each column's most common values to show, 0 to 10 (default 5).\" }";

    /// <summary>The two read: null and both set, or the <c>Error:</c> sentence for a wrong shape or a <c>top</c> out of range.</summary>
    public static string? Read(AIFunctionArguments arguments, out IReadOnlyList<string>? columns, out int top)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        columns = null;
        top = TableProfile.DefaultTop;
        if (!ToolArguments.TryReadStringList(arguments, ColumnsArgument, out var listed, out string rawColumns))
        {
            return TableProfileText.BadColumns(rawColumns);
        }

        if (!ToolArguments.TryReadInt32(arguments, TopArgument, out var asked, out string rawTop))
        {
            return ClockText.BadInteger(TopArgument, rawTop);
        }

        if (asked is < 0 or > TableProfile.MaxTop)
        {
            return TableProfileText.BadTop(TableProfile.MaxTop);
        }

        columns = listed.Count > 0 ? [.. listed.Where(c => !string.IsNullOrWhiteSpace(c))] : null;
        top = asked ?? TableProfile.DefaultTop;
        return null;
    }
}
