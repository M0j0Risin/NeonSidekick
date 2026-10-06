using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The pure side of <c>/draft</c> (2026-09-19): the temporary file the editor opens, the command
/// line a configured editor is run with, the blank test and the events that send what was saved.
/// The screen (<c>ChatScreen.HandleDraftAsync</c>) does the waiting and the wording;
/// <c>PersonaFile.EditAndWaitAsync</c> the launch. Every string here is pinned by tests.
/// </summary>
public static class DraftFile
{
    /// <summary>The file's name opens with this, so a stray one in the temp folder says whose it is.</summary>
    public const string FilePrefix = "neon-draft-";

    /// <summary>A text file: the editor Windows associates with <c>.txt</c> is the most predictable default (the user's pick).</summary>
    public const string Extension = ".txt";

    /// <summary>
    /// Makes an empty <see cref="FilePrefix"/>&lt;id&gt;<see cref="Extension"/> under
    /// <paramref name="directory"/> and returns its full path; the id is a fresh GUID, so two apps
    /// drafting at once never share one. Throws what <see cref="File.WriteAllText(string, string)"/> throws.
    /// </summary>
    public static string Create(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string path = Path.Combine(directory, FilePrefix + Guid.NewGuid().ToString("N") + Extension);
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>The file's name alone, for the log lines.</summary>
    public static string Name(string path) => Path.GetFileName(path);

    /// <summary>
    /// The arguments <c>cmd.exe</c> is started with for a configured editor:
    /// <c>/s /c "&lt;command&gt; "&lt;path&gt;""</c>. The outer quotes are the ones <c>/s</c> makes
    /// <c>cmd</c> strip, so a command that itself opens with a quoted executable path
    /// (<c>"C:\Program Files\…\notepad++.exe" -multiInst</c>) survives its quote rules intact;
    /// the path is quoted for its spaces. Pure; pinned.
    /// </summary>
    public static string CommandLine(string editorCommand, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editorCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return "/s /c \"" + editorCommand + " \"" + path + "\"\"";
    }

    /// <summary>
    /// The configured editor's start off Windows (2026-10-06, the macOS build): <c>/bin/sh -c '&lt;command&gt; "$1"' sh &lt;path&gt;</c>,
    /// the command read by the shell as typed (<c>code --wait</c>, <c>open -W -t</c>) and the path handed over as <c>$1</c>, never
    /// spliced into the text, so no character in it needs quoting. A terminal editor (vim, nano) is no good here: the app's own
    /// reader keeps the terminal. Pure; pinned.
    /// </summary>
    public static System.Diagnostics.ProcessStartInfo UnixStart(string editorCommand, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editorCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(editorCommand + " \"$1\"");
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add(path);
        return start;
    }

    /// <summary>The configured editor's start on this OS: <c>cmd.exe</c> with <see cref="CommandLine"/> on Windows, <see cref="UnixStart"/> elsewhere.</summary>
    public static System.Diagnostics.ProcessStartInfo EditorStart(string editorCommand, string path) =>
        OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = CommandLine(editorCommand, path) }
            : UnixStart(editorCommand, path);

    /// <summary>Nothing to send: the file was saved empty, with whitespace alone, or never saved.</summary>
    public static bool IsBlank(string text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Removes the file, quietly: a stray temporary file is not worth an error line, and an editor
    /// still holding it (the wait was cancelled) may refuse. True when it is gone.
    /// </summary>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// What sends the draft: the text as one paste, then Enter — replayed through the input line
    /// (<c>InputLine.ReadAsync</c>'s <c>replay</c>, the queue's path), so a long draft gets its
    /// <c>[Pasted text #n +L lines]</c> token, its preview under the sent line, its history entry
    /// and its expansion for the model exactly as a pasted block does, and a short one lands inline.
    /// </summary>
    public static IReadOnlyList<InputEvent> Events(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [new InputEvent.Paste(text), new InputEvent.Key(Keys.Enter)];
    }
}
