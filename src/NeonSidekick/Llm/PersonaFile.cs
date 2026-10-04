using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm;

/// <summary>
/// The editable persona: <c>persona.md</c> in the profile directory. When the file exists and has
/// text, that text replaces <see cref="Assistant.DefaultPersona"/> (the repo's <c>assets/prompts/persona.md</c>
/// since 2026-10-03, what <c>/persona</c> seeds the file with) — only the persona;
/// the operating rules follow it (<see cref="Assistant.OperatingRules"/>, or <c>operata.md</c>
/// through <see cref="OperataFile"/>), so nothing the persona says can switch the clock tools off.
/// Absent or blank, the default applies. The mechanics are <see cref="PromptFile"/>'s.
/// </summary>
public sealed class PersonaFile : PromptFile
{
    public const string FileName = "persona.md";

    /// <summary>Characters kept. A persona is a paragraph or three; the prompt goes out on every request.</summary>
    public const int MaxLength = 4000;

    public const string Category = "Persona";

    /// <param name="directory">The profile directory; the file is <see cref="FileName"/> under it.</param>
    public PersonaFile(string directory)
        : base(directory, FileName, Assistant.DefaultPersona, Category, "Persona", MaxLength)
    {
    }

    /// <summary>
    /// The variable that tells an Electron editor (VS Code, Obsidian, Typora, …) not to attach to
    /// its parent's console. A GUI process started by the shell has no stdout of its own, so
    /// Electron calls <c>AttachConsole(ATTACH_PARENT_PROCESS)</c> at startup and logs into ours for
    /// as long as it runs (<c>[main …] StorageMainService</c>, <c>Unknown channel: …</c> in the
    /// transcript). VS Code's own <c>code</c> launcher sets this before spawning <c>Code.exe</c>.
    /// Pinned.
    /// </summary>
    public const string NoAttachConsoleVariable = "ELECTRON_NO_ATTACH_CONSOLE";

    /// <summary>
    /// Opens <paramref name="path"/> in whatever Windows associates with it (a shell execute, which
    /// also shows the "how do you want to open this" dialog when nothing is; Explorer for a folder);
    /// falls back to Notepad when the shell refuses. Does not wait: the operator edits, saves and
    /// asks the next question. Tells an Electron editor not to attach to this console, which would
    /// otherwise fill the transcript with its logging. Throws when neither launch worked; the
    /// screen prints the detail. <c>/persona</c>, <c>/operata</c>, <c>/vocalia</c>, <c>/explore</c>
    /// and the <c>open</c> tool all come here; a URL goes to <see cref="OpenInBrowser"/>, which
    /// shares <see cref="ShellOpen"/> and never the Notepad fallback.
    /// </summary>
    public static void OpenInEditor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            ShellOpen(path);
            return;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DiagnosticLog.Info(Category, $"Shell open of {Path.GetFileName(path)} failed ({ex.Message}); trying Notepad.");
        }

        var notepadStart = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        notepadStart.ArgumentList.Add(path);   // quoted for us: a settings path with a space in it
        using var notepad = System.Diagnostics.Process.Start(notepadStart);
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the user's default browser: the same shell execute as the
    /// editor, with no fallback — Notepad handed <c>https://…</c> would make a file of it. Throws
    /// (<see cref="System.ComponentModel.Win32Exception"/>) when the shell refuses; the
    /// <c>open_url</c> tool turns that into a sentence. Does not wait.
    /// </summary>
    public static void OpenInBrowser(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ShellOpen(url);
    }

    /// <summary>
    /// Opens a new Windows Terminal window in <paramref name="folder"/> (<c>/terminal</c> and Ctrl+., 2026-10-03, the user's ask:
    /// "similar to /explore", a new window rather than a tab in this one): <c>wt.exe -w new -d &lt;folder&gt;</c> through a shell
    /// execute, which finds the app-execution alias, so the window runs Windows Terminal's default profile. Where there is no
    /// Windows Terminal the shell refuses, and <c>cmd.exe</c> is shell-executed in the folder instead: a console window of its own
    /// (Windows Terminal itself where it is the default terminal). Not waited for. Throws when neither launch worked; the screen
    /// prints the detail. A deliberate launch at this process-start site, beside the editor and the shell open.
    /// </summary>
    public static void OpenTerminal(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            using var terminal = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wt.exe", TerminalArguments(folder)) { UseShellExecute = true });
            return;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DiagnosticLog.Info(Category, $"Windows Terminal did not start ({ex.Message}); opening a console window instead.");
        }

        using var console = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = folder });
    }

    /// <summary>
    /// <c>wt.exe</c>'s command line for <see cref="OpenTerminal"/>: a new window (<c>-w new</c>) starting in <paramref name="folder"/>
    /// (<c>-d</c>), the folder quoted by the argv rules (a backslash run before the closing quote doubled, so <c>D:\</c> stays
    /// <c>D:\</c>) and each <c>;</c> escaped as <c>\;</c>, since <c>wt</c> splits its commands at a bare one. Pure; pinned.
    /// </summary>
    public static string TerminalArguments(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var quoted = new System.Text.StringBuilder("-w new -d \"");
        int backslashes = 0;
        foreach (char c in folder.Replace(";", "\\;", StringComparison.Ordinal))
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // A quote cannot be in a Windows path; a backslash run before anything else stays as it is.
            quoted.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// The shell-execute call behind the editor, Explorer and the browser: whatever Windows
    /// associates with <paramref name="target"/> (<c>open</c> / <c>xdg-open</c> elsewhere), not waited
    /// for (<see cref="EditAndWaitAsync"/> is the one launch that waits). Sets <see cref="NoAttachConsoleVariable"/> first — the ONE environment write in the
    /// app, child-directed: a shell-execute child inherits our environment block and nothing in
    /// NeonSidekick reads it (<c>EnvironmentOverrides</c> stays the only reader).
    /// </summary>
    public static void ShellOpen(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Environment.SetEnvironmentVariable(NoAttachConsoleVariable, "1");
        using var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>
    /// Opens <paramref name="path"/> for editing and waits for the editor to exit (<c>/draft</c>,
    /// 2026-09-19). A blank <paramref name="editorCommand"/> is <see cref="ShellOpen"/>'s launch —
    /// whatever Windows associates with the file — waited for; a shell execute that hands the file
    /// to a running instance returns no process (Windows 11's Notepad, VS Code) and the wait ends at
    /// once, which is what the <c>Draft editor</c> setting is for: a command line
    /// (<c>code --wait</c>) run through <c>cmd.exe /s /c "&lt;command&gt; "&lt;path&gt;""</c>
    /// (<see cref="App.DraftFile.CommandLine"/>) with no window of its own, so <c>cmd</c> resolves
    /// the word through <c>PATH</c> and <c>PATHEXT</c> and nothing here reads the environment
    /// (<c>EnvironmentOverrides</c> stays its one reader). Sets <see cref="NoAttachConsoleVariable"/>
    /// first, as <see cref="ShellOpen"/> does. Throws <see cref="System.ComponentModel.Win32Exception"/>
    /// or <see cref="InvalidOperationException"/> when the launch fails; the token ends the wait
    /// (<see cref="OperationCanceledException"/>) and leaves the editor running.
    /// </summary>
    public static async Task EditAndWaitAsync(string path, string editorCommand, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(editorCommand);
        Environment.SetEnvironmentVariable(NoAttachConsoleVariable, "1");
        System.Diagnostics.ProcessStartInfo start = string.IsNullOrWhiteSpace(editorCommand)
            ? new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }
            : new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = App.DraftFile.CommandLine(editorCommand.Trim(), path) };
        using var editor = System.Diagnostics.Process.Start(start);
        if (editor is null)
        {
            // The shell handed the file to a process already running: nothing to wait for.
            return;
        }

        await editor.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a picture for editing, not waited for (later on 2026-09-24, the user's ask: a double-clicked thumbnail "in the
    /// default image editing application for the OS … overridden with a path to an editor similar to /draft"). A blank
    /// <paramref name="editorCommand"/> is a shell execute with the <c>edit</c> verb — the editor Windows registers for
    /// the type (Paint for png, jpg, bmp) — and, for a type with no edit verb, the plain open, its viewer. A command line
    /// (<c>mspaint</c>, a quoted GIMP path) runs through <c>cmd.exe /s /c "&lt;command&gt; "&lt;path&gt;""</c> with no window of
    /// its own, <see cref="EditAndWaitAsync"/>'s launch (<see cref="App.DraftFile.CommandLine"/>). Sets
    /// <see cref="NoAttachConsoleVariable"/> first. Throws <see cref="System.ComponentModel.Win32Exception"/> or
    /// <see cref="InvalidOperationException"/> when the launch fails; the screen prints the detail.
    /// </summary>
    public static void OpenImage(string path, string editorCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(editorCommand);
        Environment.SetEnvironmentVariable(NoAttachConsoleVariable, "1");
        if (!string.IsNullOrWhiteSpace(editorCommand))
        {
            using var editor = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = App.DraftFile.CommandLine(editorCommand.Trim(), path) });
            return;
        }

        try
        {
            using var edit = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DiagnosticLog.Info(Category, $"No edit verb for {Path.GetFileName(path)} ({ex.Message}); opening it instead.");
            ShellOpen(path);
        }
    }

    /// <summary>The persona text as the prompt carries it: CRLF folded to LF, trimmed, cut at <see cref="MaxLength"/> with an ellipsis. Pure; pinned.</summary>
    public static string Normalize(string raw) => Normalize(raw, out _);

    public static string Normalize(string raw, out bool truncated) => Normalize(raw, MaxLength, out truncated);

    public static string TruncatedWarning(int length) => TruncatedWarning(FileName, MaxLength, length);

    public static string UnreadableWarning(string detail) => UnreadableWarning(FileName, "persona", detail);
}
