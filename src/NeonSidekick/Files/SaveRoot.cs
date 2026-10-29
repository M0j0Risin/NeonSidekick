namespace NeonSidekick.Files;

/// <summary>
/// Where a <c>to</c> save may write, asked per call (2026-10-15, the AD gaps plan, §1b; moved here from <c>DirectoryCsv</c> 2026-10-29, the
/// Office writing plan, when the database exports took it up too): the working directory, or why not (the model's tools need *File tools* and
/// no plan mode; a redacted twin has no root at all). Asked before anything is read, and again as the file is committed.
/// </summary>
public readonly record struct SaveRoot(WorkingDirectory? Root, string? Refusal)
{
    public static SaveRoot Refused(string why) => new(null, why);
}
