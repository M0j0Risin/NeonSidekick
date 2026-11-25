using System.Security.Cryptography;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>Why the conversation is private (2026-11-25).</summary>
public enum PrivateReason
{
    /// <summary>Not private.</summary>
    None,

    /// <summary><c>/private</c>, <c>/private new</c> or the page's buttons.</summary>
    Command,

    /// <summary><c>--private</c>: every conversation of the run, <c>/private off</c> refused.</summary>
    Flag,

    /// <summary><c>NEONSIDEKICK_PRIVATE</c>: as the flag.</summary>
    Variable,

    /// <summary><c>Private at start</c> alone: <c>/private off</c> starts a normal conversation.</summary>
    Setting,
}

/// <summary>What a private conversation's end did with its files (2026-11-25): counted for the line afterwards.</summary>
/// <param name="Pictures">Pictures deleted (or kept).</param>
/// <param name="Pdfs">The page's PDFs deleted (or kept).</param>
/// <param name="Open">Files held open, left where they are.</param>
/// <param name="Failed">Files a delete failed on, other than an open one.</param>
public sealed record PrivateFilesResult(int Pictures, int Pdfs, IReadOnlyList<string> Open, IReadOnlyList<string> Failed);

/// <summary>
/// A private conversation (2026-11-25, the private-conversations plan; the screen and headless alike): whether the conversation is private and why, the
/// stretch's random id (what a page, a queued line or a late job compares against, so one that has ended is never mistaken for the next), the two
/// values fixed at entry (<c>Private pictures</c>, <c>Private log text</c>), and the conversation's lists — the input lines it stored before a switch
/// (deleted by the switch), the pictures and PDFs the app itself wrote, the shell spill logs and temp copies — with the end's file deletes as one method.
/// The lists are kept for every conversation, private or not, and reset with it, so a switch mid-conversation knows what came before.
/// Thread-safe: a Kestrel thread, a tool's and the screen's read it.
/// </summary>
public sealed class PrivateConversation
{
    private readonly Lock _gate = new();
    private readonly List<CommandRow> _storedLines = new();
    private readonly List<string> _pictures = new();
    private readonly List<string> _spillLogs = new();
    private readonly List<string> _tempCopies = new();
    private string _stretch = "";

    /// <summary>Whether the conversation on screen is private.</summary>
    public bool IsPrivate => StretchId.Length > 0;

    /// <summary>The private stretch's id, <c>""</c> when not private. A new one each time a conversation goes private.</summary>
    public string StretchId
    {
        get
        {
            lock (_gate)
            {
                return _stretch;
            }
        }
    }

    /// <summary>Why it is private.</summary>
    public PrivateReason Reason { get; private set; }

    /// <summary>Whether the run itself is private (the flag or the variable): <c>/private off</c> is refused, <c>/new</c> starts a fresh private one.</summary>
    public bool Forced => Reason is PrivateReason.Flag or PrivateReason.Variable;

    /// <summary><c>Private pictures</c> as it was when the conversation went private.</summary>
    public PrivatePicturesMode Pictures { get; private set; }

    /// <summary><c>Private log text</c> as it was when the conversation went private: whether the log hides it.</summary>
    public bool HideLog { get; private set; }

    /// <summary>
    /// The conversation goes private: a new stretch id, the two values fixed, the process-wide switches on (<see cref="PrivateMode"/>). The lists stay:
    /// what the conversation wrote before is still its own to delete. Returns the new id.
    /// </summary>
    public string Enter(PrivateReason reason, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (reason == PrivateReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        lock (_gate)
        {
            _stretch = id;
            Reason = reason;
            Pictures = PrivatePictures.Resolve(effective);
            HideLog = PrivateLogText.Resolve(effective);
        }

        PrivateMode.Active = true;
        if (HideLog)
        {
            PrivateMode.HideLog = true;
        }

        return id;
    }

    /// <summary>
    /// The private stretch ends: no id, no reason, the clipboard's switch off. The log's hide switch is <see cref="ReleaseLog"/>'s, once the ended
    /// conversation's jobs have stopped. The lists are <see cref="Reset"/>'s.
    /// </summary>
    public void Leave()
    {
        lock (_gate)
        {
            _stretch = "";
            Reason = PrivateReason.None;
        }

        PrivateMode.Active = false;
    }

    /// <summary>The log's hide switch off: the ended conversation's jobs have stopped. Never while another stretch is on and hides.</summary>
    public void ReleaseLog()
    {
        if (!IsPrivate || !HideLog)
        {
            PrivateMode.HideLog = false;
        }
    }

    /// <summary>The conversation's lists emptied: a new conversation (not a private one carrying on).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _storedLines.Clear();
            _pictures.Clear();
            _spillLogs.Clear();
            _tempCopies.Clear();
        }
    }

    /// <summary>An input line this conversation stored (before it went private).</summary>
    public void TrackStoredLine(CommandRow row)
    {
        lock (_gate)
        {
            _storedLines.Add(row);
        }
    }

    /// <summary>The input lines this conversation stored, for the switch's delete.</summary>
    public IReadOnlyList<CommandRow> StoredLines
    {
        get
        {
            lock (_gate)
            {
                return [.. _storedLines];
            }
        }
    }

    /// <summary>The stored lines forgotten once the switch has deleted them.</summary>
    public void ForgetStoredLines()
    {
        lock (_gate)
        {
            _storedLines.Clear();
        }
    }

    /// <summary>
    /// A picture (or a page's PDF) the app itself wrote for this conversation: a regular file it created, never one it overwrote, never an attached file
    /// of the user's. Kept for every conversation; deleted at a private end under <c>delete</c>, or <c>ask</c>'s Delete.
    /// </summary>
    public void TrackPicture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            if (!_pictures.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _pictures.Add(path);
            }
        }
    }

    /// <summary>A shell spill log written for this conversation (deleted when private ends).</summary>
    public void TrackSpill(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            _spillLogs.Add(path);
        }
    }

    /// <summary>A temp copy made for this conversation (deleted when private ends, whatever <c>Private pictures</c> says).</summary>
    public void TrackTemp(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            _tempCopies.Add(path);
        }
    }

    /// <summary>The tracked pictures and PDFs still on disk, as (pictures, PDFs).</summary>
    public (int Pictures, int Pdfs) CountFiles()
    {
        lock (_gate)
        {
            var there = _pictures.Where(File.Exists).ToList();
            int pdfs = there.Count(IsPdf);
            return (there.Count - pdfs, pdfs);
        }
    }

    /// <summary>The tracked pictures and PDFs still on disk.</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_gate)
            {
                return _pictures.Where(File.Exists).ToList();
            }
        }
    }

    private static bool IsPdf(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The end's deletes (each guarded on its own, so one that fails never stops the rest): the spill logs and temp copies always, and the pictures and
    /// PDFs when <paramref name="deletePictures"/> — for good, never to the Recycle Bin; one <paramref name="isOpen"/> names (the viewer holds it) is left and
    /// counted. The lists are emptied as each file goes, so running it twice does nothing more.
    /// </summary>
    public PrivateFilesResult DeleteFiles(bool deletePictures, Func<string, bool>? isOpen = null)
    {
        List<string> pictures, others;
        lock (_gate)
        {
            pictures = deletePictures ? [.. _pictures] : [];
            others = [.. _spillLogs, .. _tempCopies];
            _spillLogs.Clear();
            _tempCopies.Clear();
            if (deletePictures)
            {
                _pictures.Clear();
            }
        }

        var open = new List<string>();
        var failed = new List<string>();
        int deletedPictures = 0, deletedPdfs = 0;
        foreach (string path in pictures)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (isOpen?.Invoke(path) == true)
            {
                open.Add(path);
                continue;
            }

            if (TryDelete(path))
            {
                if (IsPdf(path))
                {
                    deletedPdfs++;
                }
                else
                {
                    deletedPictures++;
                }
            }
            else
            {
                open.Add(path);   // held by another program: kept and named as open
            }
        }

        foreach (string path in others)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && !TryDelete(path))
            {
                failed.Add(path);
            }
        }

        if (open.Count > 0 || failed.Count > 0)
        {
            lock (_gate)
            {
                // What couldn't go is kept for the next try (the exit's).
                _pictures.AddRange(open.Where(p => !_pictures.Contains(p, StringComparer.OrdinalIgnoreCase)));
                _tempCopies.AddRange(failed);
            }
        }

        return new PrivateFilesResult(deletedPictures, deletedPdfs, open, failed);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(PrivateText.Category, PrivateText.DeleteFailedLogLine(ex.GetType().Name));
            return false;
        }
    }
}
