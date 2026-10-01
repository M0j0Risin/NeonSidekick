namespace NeonSidekick.Tests.Fakes;

/// <summary>The twelve UNC tools' names in <c>ChatScreen.UncTools</c> order (2026-09-30): the four reads, the fetch, the seven changes. Pinned.</summary>
internal static class UncToolNames
{
    public static readonly string[] All =
        ["unc_shares", "unc_search", "unc_info", "unc_read", "unc_fetch", "unc_write", "unc_patch", "unc_create_directory", "unc_move", "unc_copy", "unc_delete", "unc_put"];

    /// <summary>The reads plan mode keeps (<c>unc_fetch</c> writes the working directory, so it is not among them).</summary>
    public static readonly string[] Reads = ["unc_shares", "unc_search", "unc_info", "unc_read"];

    /// <summary>The changes, offered only under UNC writes with a readwrite share.</summary>
    public static readonly string[] Writes = ["unc_write", "unc_patch", "unc_create_directory", "unc_move", "unc_copy", "unc_delete", "unc_put"];
}
