namespace NeonSidekick.Llm;

/// <summary>
/// A tool whose calls a model may write out as text under another name (2026-10-11, the user's report from <c>/botchat</c>:
/// <c>juggernautxl_ragnarok_img2img image="C:\…" prompt="…" negative="…"</c> in a reply, the workflow's name where
/// <c>generate_image</c> belongs, so nothing caught it and the line was shown, spoken and passed on to the other bot).
/// <see cref="TextToolCallFilter"/> catches each alias as this tool's call, with <see cref="AliasArgument"/> set to the alias
/// unless the call names one itself. Read at each request, so the list is the one in force.
/// </summary>
public interface IWrittenCallAliases
{
    /// <summary>The argument an alias fills in: <c>workflow</c> for <c>generate_image</c>.</summary>
    string AliasArgument { get; }

    /// <summary>The names a written call may stand under, each the value of <see cref="AliasArgument"/>.</summary>
    IReadOnlyList<string> WrittenAliases { get; }
}
