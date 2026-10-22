using NeonSidekick.Files;

namespace NeonSidekick.App;

// ── The project file's allow (2026-10-22, review P1, the fix plan's D2) ─────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The project files "Allow for this session" was picked for, by full path; forgotten with the session (<see cref="ForgetSession"/>).</summary>
    private readonly HashSet<string> _projectFileAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The allow a model write to the project file (<c>NEON.md</c>, <c>AGENTS.md</c>, <c>CLAUDE.md</c>, which the system prompt reads every turn)
    /// waits on, on the turn task: a file allowed for the session goes at once; else the camera's allow pane — Deny, Allow once, Allow for
    /// this session — as <c>sqlite_execute</c>'s (<see cref="AllowSqliteWriteAsync"/>), the Web UI's allow ask beside it. Null when no pane
    /// could ask, which leaves the write refused. The turn asks only when it offers <c>ask_user</c> (<see cref="Llm.Assistant.ProjectFileAsk"/>):
    /// a Ralph pass with its asks barred and a gap or audit pass that may not ask never stop on a pane nobody watches.
    /// </summary>
    private async Task<bool?> AllowProjectFileAsync(string full, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        lock (_projectFileAllowed)
        {
            if (_projectFileAllowed.Contains(full))
            {
                return true;
            }
        }

        string name = Path.GetFileName(full);
        string caption = FileText.ProjectFileAllowCaption(name);
        var allow = await AllowBothAsync(FileText.ProjectFileAllowTitle, caption, null, token => _cameraMenu.AllowAsync(caption, token, FileText.ProjectFileAllowTitle), turnToken).ConfigureAwait(false);
        if (allow is not { } choice)
        {
            return null;
        }

        if (choice == CameraAllow.Session)
        {
            lock (_projectFileAllowed)
            {
                _projectFileAllowed.Add(full);
            }
        }

        return choice != CameraAllow.Deny;
    }
}
