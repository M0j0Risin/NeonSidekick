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

    /// <param name="images">
    /// The profile's images folder (2026-10-09), for the picture tools: a full path inside it is read and written there
    /// (<see cref="FilesFor"/>); null = the working directory alone.
    /// </param>
    protected FileTool(WorkingDirectory files, WorkingDirectory? images = null)
    {
        Files = files ?? throw new ArgumentNullException(nameof(files));
        Images = images;
    }

    protected WorkingDirectory Files { get; }

    /// <summary>The profile's images folder the picture tools reach besides the working directory; null for every other tool.</summary>
    protected WorkingDirectory? Images { get; }

    /// <summary>
    /// Where <paramref name="path"/> lives (2026-10-09, the user's call: the model reaches the camera's, the screen's, ComfyUI's and
    /// the pasted pictures by the full paths their results give): the profile's images folder for a full path inside it — unless it
    /// is inside the working directory too, which comes first — else the working directory, which judges every other path as before.
    /// </summary>
    protected WorkingDirectory FilesFor(string path) => For(Files, Images, path);

    /// <summary><see cref="FilesFor"/> for a caller without a tool: <paramref name="images"/> when <paramref name="path"/> is a full path in it and not in <paramref name="files"/>. Pure over the two roots.</summary>
    public static WorkingDirectory For(WorkingDirectory files, WorkingDirectory? images, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        return images is not null && !files.Contains(path) && images.Contains(path) ? images : files;
    }

    protected static string ReadPath(AIFunctionArguments arguments) => ToolArguments.ReadString(arguments, PathArgument);

    /// <summary>
    /// The shopping list's tripwire for a write onto <paramref name="path"/> (2026-10-17, the user's report: the model now and then kept the list
    /// in a file): <see cref="Shopping.ShoppingText.NotAFile"/> while the working directory's <see cref="WorkingDirectory.ShoppingListGuard"/>
    /// says the shopping tools are offered and the name is a list's (<see cref="Shopping.ShoppingFileGuard"/>), else null. Read on the tool's
    /// own <see cref="Files"/> whatever folder <see cref="FilesFor"/> picks, so a write routed to the images folder is judged the same.
    /// </summary>
    protected string? ShoppingListRefusal(string path) =>
        Files.ShoppingListGuard?.Invoke() == true && Shopping.ShoppingFileGuard.IsShoppingListName(path) ? Shopping.ShoppingText.NotAFile(path) : null;

    /// <summary>
    /// <see cref="ShoppingListRefusal(string)"/> for a copy or move's destination: <paramref name="to"/> itself, or — when it names a folder
    /// (ends in a separator, or is an existing folder) — the source's own name inside it, the name the file will have.
    /// </summary>
    protected string? ShoppingListRefusal(string from, string to)
    {
        if (Files.ShoppingListGuard?.Invoke() != true)
        {
            return null;
        }

        bool folder = to.EndsWith('/') || to.EndsWith('\\')
            || (Files.Resolve(to, forWrite: false, out string full) == FileOutcome.Ok && Directory.Exists(full));
        string name = folder ? to.TrimEnd('/', '\\') + "/" + Path.GetFileName(from.TrimEnd('/', '\\')) : to;
        return ShoppingListRefusal(name);
    }

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
