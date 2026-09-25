using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the file tools share: the <see cref="WorkingDirectory"/> they act on and the argument
/// names every one of them reads. Each tool is still a hand-written <see cref="AIFunction"/>
/// with its own schema literal and a pinned <c>Describe</c>; never <c>AIFunctionFactory</c>.
/// </summary>
public abstract class FileTool : AIFunction
{
    public const string PathArgument = "path";
    public const string OverwriteArgument = "overwrite";

    protected FileTool(WorkingDirectory files)
    {
        Files = files ?? throw new ArgumentNullException(nameof(files));
    }

    protected WorkingDirectory Files { get; }

    protected static string ReadPath(AIFunctionArguments arguments) => ToolArguments.ReadString(arguments, PathArgument);

    /// <summary>
    /// A path argument a tool cannot do without (2026-09-18: a <c>write_file</c> sent with <c>content</c>
    /// alone was answered <c>'' is a folder, not a file</c>): false with <see cref="FileText.PathRequired"/>
    /// in <paramref name="error"/> when it is missing or blank, else the trimmed value. <c>search_files</c>,
    /// <c>file_info</c>, <c>delete</c>, <c>zip</c> and <c>open</c> keep reading a blank as the root.
    /// </summary>
    protected static bool RequirePath(AIFunctionArguments arguments, string name, out string path, out string error)
    {
        path = ToolArguments.ReadString(arguments, name).Trim();
        error = path.Length == 0 ? FileText.PathRequired(name) : "";
        return path.Length > 0;
    }

    /// <summary>The <c>overwrite</c> flag, false when absent; null when the model sent something that is not a boolean (the caller answers with <see cref="FileText.BadBoolean"/>).</summary>
    protected static bool? ReadOverwrite(AIFunctionArguments arguments, out string raw) => ReadFlag(arguments, OverwriteArgument, out raw);

    /// <summary>Any boolean flag the same way: false when absent, null when not a boolean.</summary>
    protected static bool? ReadFlag(AIFunctionArguments arguments, string name, out string raw) =>
        ToolArguments.TryReadBoolean(arguments, name, out var value, out raw) ? value ?? false : null;
}
