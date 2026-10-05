using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm;

/// <summary>
/// An editable piece of the system prompt kept as a file in the profile directory: the mechanics
/// shared by <see cref="PersonaFile"/> (<c>persona.md</c>, the persona),
/// <see cref="OperataFile"/> (<c>operata.md</c>, the operating rules) and <see cref="VocaliaFile"/>
/// (<c>vocalia.md</c>, the voice directive). When the file exists and has text, that text replaces
/// the default; absent or blank, the default applies.
///
/// <para>Read once per turn (<c>ChatScreen.PrepareTurn</c>), like <c>memory.json</c>: an edit is
/// in the next question's prompt with no restart. The read is a stat per turn and a re-read only
/// when the file's write time or length changed. A file that cannot be read, or one longer than the
/// subclass's cap, warns once (the warning lands in the transcript, which is where the operator
/// finds out the file was not honoured as written) and never throws.</para>
/// </summary>
public abstract class PromptFile
{
    private readonly Func<string> _filePath;
    private readonly string _defaultText;
    private readonly string _category;
    private readonly string _label;
    private readonly string _defaultLabel;
    private readonly int _maxLength;
    private DateTime _lastWriteUtc;
    private long _lastLength = -1;
    private string? _cachedPath;
    private string? _cached;
    private bool _warnedTruncated;
    private bool _warnedUnreadable;

    /// <param name="directory">The profile directory; the file is <paramref name="fileName"/> under it.</param>
    /// <param name="fileName">The file's name, e.g. <c>persona.md</c>.</param>
    /// <param name="defaultText">What the prompt carries without the file, and what <see cref="EnsureExists"/> seeds.</param>
    /// <param name="category">The <see cref="DiagnosticLog"/> category.</param>
    /// <param name="label">What the file holds, capitalised for a sentence start: <c>Persona</c>, <c>Operating rules</c>.</param>
    /// <param name="maxLength">Characters kept; a longer file is cut with an ellipsis and warned about once.</param>
    protected PromptFile(string directory, string fileName, string defaultText, string category, string label, int maxLength)
        : this(FixedPath(directory, fileName), defaultText, category, label, maxLength)
    {
    }

    /// <param name="filePath">Resolved on every <see cref="Read"/>: a file whose folder moves (<see cref="ProjectFile"/>, under the working directory) reads from where it is now, the cache forgotten when the path changed.</param>
    /// <param name="defaultText">What the prompt carries without the file, and what <see cref="EnsureExists"/> seeds; empty for a file with no default.</param>
    /// <param name="category">The <see cref="DiagnosticLog"/> category.</param>
    /// <param name="label">What the file holds, capitalised for a sentence start: <c>Persona</c>, <c>Operating rules</c>.</param>
    /// <param name="maxLength">Characters kept; a longer file is cut with an ellipsis and warned about once.</param>
    protected PromptFile(Func<string> filePath, string defaultText, string category, string label, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(defaultText);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        _filePath = filePath;
        _defaultText = defaultText;
        _category = category;
        _label = label;
        _defaultLabel = char.ToLowerInvariant(label[0]) + label[1..];
        _maxLength = maxLength;
    }

    private static Func<string> FixedPath(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string path = Path.Combine(Path.GetFullPath(directory), fileName);
        return () => path;
    }

    /// <summary>The file's full path right now.</summary>
    public string FilePath => _filePath();

    /// <summary>The name of the file in play (<c>persona.md</c>; for <see cref="ProjectFile"/> whichever of its two).</summary>
    public string CurrentFileName => Path.GetFileName(FilePath);

    /// <summary>Whether the last <see cref="Read"/> returned text (the file is present and not blank).</summary>
    public bool IsActive => _cached is not null;

    /// <summary>What the file replaces, for the middle of a sentence: <c>persona</c>, <c>operating rules</c>, <c>voice directive</c>.</summary>
    public string DefaultLabel => _defaultLabel;

    /// <summary>
    /// What is in force without the file, for the middle of a sentence: <c>the default persona</c>; <see cref="VocaliaFile"/>'s is
    /// <c>no voice directive</c> since it lost its default (2026-10-03). The notices of <c>/persona reset</c> and its siblings and
    /// the blank and unreadable log lines name it.
    /// </summary>
    public virtual string DefaultInUse => "the default " + _defaultLabel;

    /// <summary>
    /// Removes the file, so the default applies from the next turn (<c>/persona reset</c> and its
    /// siblings, after their confirmation). Returns false when there was no file. Throws
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> like any delete; the
    /// slash command reports those.
    /// </summary>
    public bool Delete()
    {
        string path = FilePath;
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        Forget();
        return true;
    }

    /// <summary>
    /// Copies the file, byte for byte, into <paramref name="directory"/> under its own name
    /// (<c>/persona copy &lt;profile&gt; [force]</c> and its siblings, 2026-09-21: another profile's
    /// folder), creating the directory and replacing a file already there. Whether replacing is
    /// allowed is the caller's rule (the <c>force</c> word, checked before the confirmation); this
    /// just copies. Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// (a missing source among them); the slash command reports those.
    /// </summary>
    public void CopyTo(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        File.Copy(FilePath, Path.Combine(root, CurrentFileName), overwrite: true);
    }

    /// <summary>
    /// The text for this turn: the file's, normalised, or null when the file is missing, blank or
    /// unreadable — null means the default.
    /// </summary>
    public string? Read()
    {
        FileInfo info;
        string path = FilePath;
        if (!string.Equals(path, _cachedPath, StringComparison.OrdinalIgnoreCase))
        {
            // Another file altogether (the working directory moved): nothing cached applies, and
            // its warnings are its own.
            Forget();
            _cachedPath = path;
            _warnedTruncated = false;
            _warnedUnreadable = false;
        }

        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                Forget();
                return null;
            }

            if (info.LastWriteTimeUtc == _lastWriteUtc && info.Length == _lastLength)
            {
                return _cached;
            }

            string raw = File.ReadAllText(path);
            _lastWriteUtc = info.LastWriteTimeUtc;
            _lastLength = info.Length;
            _warnedUnreadable = false;

            string text = Normalize(raw, _maxLength, out bool truncated);
            if (!truncated)
            {
                _warnedTruncated = false;
            }
            else if (!_warnedTruncated)
            {
                _warnedTruncated = true;
                DiagnosticLog.Warn(_category, TruncatedWarning(CurrentFileName, _maxLength, raw.Length));
            }

            _cached = text.Length == 0 ? null : text;
            DiagnosticLog.Info(_category, _cached is null
                ? $"{CurrentFileName} is blank; using {DefaultInUse}."
                : $"{_label} loaded from {CurrentFileName} ({_cached.Length.ToString(CultureInfo.InvariantCulture)} characters).");
            return _cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Forget();
            if (!_warnedUnreadable)
            {
                _warnedUnreadable = true;
                DiagnosticLog.Warn(_category, UnreadableWarningUsing(CurrentFileName, DefaultInUse, ex.Message));
            }

            return null;
        }
    }

    /// <summary>
    /// Creates the file with the default text when it is missing, so an editor opens on what is
    /// being replaced rather than on nothing (empty, for a file with no default: <see cref="VocaliaFile"/>
    /// since 2026-10-03); an existing file is left alone. <paramref name="seed"/>, when given, is written in the default's
    /// place: the caller's live text (2026-10-04, the user's ask: <c>/operata</c> seeds the operating rules the next reply would
    /// send, its tool sentences included, not the bare <see cref="Assistant.OperatingRules"/>). Returns true when it
    /// was created. Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// like any write; the slash command reports those.
    /// </summary>
    public bool EnsureExists(string? seed = null)
    {
        string path = FilePath;
        if (File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string text = seed ?? _defaultText;
        // ReplaceLineEndings: the default persona is lines since 2026-10-03, held LF; the file gets the platform's breaks throughout.
        File.WriteAllText(path, text.Length == 0 ? "" : text.ReplaceLineEndings() + Environment.NewLine);
        return true;
    }

    /// <summary>The text as the prompt carries it: CRLF folded to LF, trimmed, cut at <paramref name="maxLength"/> with an ellipsis. Pure; pinned.</summary>
    protected static string Normalize(string raw, int maxLength, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string text = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        truncated = text.Length > maxLength;
        if (truncated)
        {
            text = text[..(maxLength - 1)].TrimEnd() + "…";
        }

        return text;
    }

    protected static string TruncatedWarning(string fileName, int maxLength, int length) =>
        $"{fileName} is {length.ToString(CultureInfo.InvariantCulture)} characters; using the first {maxLength.ToString(CultureInfo.InvariantCulture)}.";

    protected static string UnreadableWarning(string fileName, string defaultLabel, string detail) =>
        UnreadableWarningUsing(fileName, "the default " + defaultLabel, detail);

    /// <summary>The unreadable warning naming what is in force instead (<see cref="DefaultInUse"/>). Pinned.</summary>
    protected static string UnreadableWarningUsing(string fileName, string inUse, string detail) =>
        $"Could not read {fileName}; using {inUse}: {detail}";

    private void Forget()
    {
        _cached = null;
        _lastLength = -1;
        _lastWriteUtc = default;
    }
}
