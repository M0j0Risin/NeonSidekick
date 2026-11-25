using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.App;

// ── A headless run's start (2026-11-21, feature ideas #42 and #44) ─────────────────────────────────────────────────────────────────────
// Before anything connects: the one-shot prompt (--prompt, or --prompt-file read whole), the piped stdin it takes as context, and the session
// --session carries on, its folder taken into the flags' layer for this run. A problem here is a bad argument's exit 2, nothing run.

public sealed partial class SidekickApp
{
    /// <summary>A stored session a headless run carries on: its row, its turns, its messages and everything saved beside them.</summary>
    private sealed record HeadlessResume(long Id, int Turns, List<ChatMessage> Messages, Sessions.StoredHistory Stored);

    /// <summary>What the start found: the prompt (null for the line-by-line mode), whether it is a message whatever it says, the piped text, the session, the notices to show after the banner, or the exit code of a problem.</summary>
    private sealed record HeadlessStartState(string? Prompt, bool PromptIsMessage, PipedText? Piped, HeadlessResume? Resume, IReadOnlyList<string> Notices, int? Exit);

    /// <summary>
    /// What a redirected stdin held (2026-11-22, the review's #68): its start and its end, at most <see cref="PipedCeilingChars"/> of it between
    /// them, and how many characters came in all. <see cref="Whole"/> when nothing was left out between the two.
    /// </summary>
    internal sealed record PipedText(string Head, string Tail, long TotalChars)
    {
        public bool Whole => Head.Length + Tail.Length == TotalChars;

        public static PipedText Of(string text) => new(text, "", text.Length);
    }

    /// <summary>The most of a pipe kept while it is read (2026-11-22, the review's #68): half from its start, half its end, the middle counted only.</summary>
    internal const int PipedCeilingChars = 2_000_000;

    /// <summary>How long a redirected stdin may stay silent before a one-shot prompt runs alone (2026-11-21, as Claude Code's <c>-p</c> waits).</summary>
    internal static readonly TimeSpan PipedFirstByteWait = TimeSpan.FromSeconds(3);

    /// <summary>The piped context's cap when the model's window is unknown, in characters.</summary>
    internal const int PipedUnknownWindowChars = 100_000;

    /// <summary>The failure entry's names for the loops that report their own stops.</summary>
    private const string RalphWord = "/plan ralph";

    private const string AuditWord = "/code-audit";

    /// <summary>What failed before the turn being run, so a pass the app runs again takes back what its failed turn noted (<see cref="HeadlessFailures.BackTo"/>).</summary>
    private int _turnFailMark;

    private async Task<HeadlessStartState> HeadlessStartAsync(CancellationToken cancellationToken)
    {
        var notices = new List<string>();
        string? prompt = _options.Prompt;
        bool isMessage = false;
        if (_options.PromptFile is { } path)
        {
            try
            {
                // UTF-8, a BOM taken off (2026-11-21); one message, never cut: it is the question itself.
                prompt = await File.ReadAllTextAsync(path, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                if (prompt.Length > 0 && prompt[0] == '﻿')
                {
                    prompt = prompt[1..];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return await HeadlessStartErrorAsync(HeadlessText.PromptFileUnreadable(path, ex.Message)).ConfigureAwait(false);
            }

            isMessage = true;
        }

        if (prompt is not null && string.IsNullOrWhiteSpace(prompt))
        {
            return await HeadlessStartErrorAsync(HeadlessText.BlankPrompt).ConfigureAwait(false);
        }

        HeadlessResume? resume = null;
        if (_options.ResumesSession)
        {
            var (found, problem) = LoadHeadlessResume(notices);
            if (problem is not null)
            {
                return await HeadlessStartErrorAsync(problem).ConfigureAwait(false);
            }

            resume = found;
        }

        PipedText? piped = null;
        if (prompt is not null && _headlessInputRedirected)
        {
            try
            {
                (piped, string? silent) = await ReadPipedAsync(cancellationToken).ConfigureAwait(false);
                if (silent is not null)
                {
                    notices.Add(silent);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Text.DecoderFallbackException)
            {
                // A broken pipe is a bad start, never the crash handler's (2026-11-22, the review's #135).
                return await HeadlessStartErrorAsync(HeadlessText.PipedUnreadable(ex.Message)).ConfigureAwait(false);
            }
        }

        return new HeadlessStartState(prompt, isMessage, piped, resume, notices, null);
    }

    /// <summary>A start's problem: its <c>[error]</c> line, and a bad argument's exit code. Never a command's failure, so never exit 4.</summary>
    private async Task<HeadlessStartState> HeadlessStartErrorAsync(string problem)
    {
        DiagnosticLog.Info(Category, problem);
        await HeadlessInfoLineAsync("[error] " + problem).ConfigureAwait(false);
        return new HeadlessStartState(null, false, null, null, [], HeadlessBadAllowExitCode);
    }

    /// <summary>
    /// <c>--session</c>'s row and history, read through a store over the profile (the one the run then opens). The session's folder becomes this
    /// run's <c>--cwd</c> when none was given and it still exists; never saved.
    /// </summary>
    private (HeadlessResume? Resume, string? Problem) LoadHeadlessResume(List<string> notices)
    {
        using var store = new Sessions.SessionStore(_settings.ProfileDirectory, _time);
        if (!store.Available)
        {
            // A store that won't open (locked, newer, broken) is said as itself, never as "no such session" (2026-11-22, the review's #62).
            return (null, HeadlessText.SessionsUnavailable);
        }

        long id;
        if (_options.SessionLast)
        {
            if (store.List(1) is not [var newest, ..])
            {
                return (null, HeadlessText.NoSessions);
            }

            id = newest.Id;
        }
        else
        {
            id = _options.SessionId!.Value;
        }

        if (store.Load(id) is not { } record)
        {
            return (null, HeadlessText.NoSuchSession(id));
        }

        List<ChatMessage> messages;
        Sessions.StoredHistory stored;
        try
        {
            messages = Sessions.SessionHistory.FromJsonWhole(record.HistoryJson, out stored);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return (null, HeadlessText.SessionUnreadable(id, ex.Message));
        }

        notices.Add(HeadlessText.SessionResumed(id, record.Summary.Turns));
        if (record.Summary.Cwd is { Length: > 0 } folder && _options.WorkingDirectory is null)
        {
            // A folder written for the other system (/Users/… read on Windows, C:\… on a Mac) is one that's gone here, never tried as a path of
            // this one's (2026-11-22, the review's #82).
            if (Files.FolderMatch.Normal(folder) is not null && Directory.Exists(folder))
            {
                _options = _options with { WorkingDirectory = folder };
                notices.Add(HeadlessText.SessionFolder(folder));
            }
            else
            {
                notices.Add(HeadlessText.SessionFolderGone(folder));
            }
        }

        if (HeadlessPrivate)
        {
            notices.Add(PrivateText.CopyNotice(id));   // a private copy (2026-11-25): the row is read, never written
        }
        else if (!EffectiveSettings.SessionLogging)
        {
            notices.Add(HeadlessText.SessionNotSaved);
        }

        return (new HeadlessResume(id, record.Summary.Turns, messages, stored), null);
    }

    /// <summary>
    /// A redirected stdin, or nothing (and <see cref="HeadlessText.NoPipedInput"/>) when its first character doesn't come within
    /// <see cref="PipedFirstByteWait"/>: a parent that redirects stdin and never writes would otherwise hang the run. Once data starts, to the end,
    /// read in pieces (2026-11-22, the review's #68): the start and a rolling end kept, <see cref="PipedCeilingChars"/> in all, the rest counted, so
    /// a huge stream never sits in memory whole. A stream that never ends is never run (HEADLESS.md says so).
    /// </summary>
    private async Task<(PipedText? Text, string? Silent)> ReadPipedAsync(CancellationToken cancellationToken)
    {
        var first = new char[1];
        var read = _headlessInput.ReadAsync(first.AsMemory(), cancellationToken).AsTask();
        using var waitCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wait = Task.Delay(PipedFirstByteWait, _time, waitCancel.Token);
        if (await Task.WhenAny(read, wait).ConfigureAwait(false) != read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (null, HeadlessText.NoPipedInput(PipedFirstByteWait, OperatingSystem.IsWindows()));
        }

        await waitCancel.CancelAsync().ConfigureAwait(false);
        int count = await read.ConfigureAwait(false);
        if (count == 0)
        {
            return (null, null);
        }

        const int Half = PipedCeilingChars / 2;
        var head = new StringBuilder().Append(first[0]);
        var tail = new char[Half];
        int tailStart = 0, tailCount = 0;
        long total = 1;
        var buffer = new char[64 * 1024];
        int n;
        while ((n = await _headlessInput.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += n;
            int i = 0;
            if (head.Length < Half)
            {
                i = Math.Min(Half - head.Length, n);
                head.Append(buffer, 0, i);
            }

            for (; i < n; i++)
            {
                tail[(tailStart + tailCount) % Half] = buffer[i];
                if (tailCount < Half)
                {
                    tailCount++;
                }
                else
                {
                    tailStart = (tailStart + 1) % Half;
                }
            }
        }

        var end = new StringBuilder(tailCount);
        for (int i = 0; i < tailCount; i++)
        {
            end.Append(tail[(tailStart + i) % Half]);
        }

        return (new PipedText(head.ToString(), end.ToString(), total), null);
    }

    /// <summary>The fewest characters of a pipe a message keeps, however full the window is.</summary>
    internal const int PipedFloorChars = 2_000;

    /// <summary>
    /// The prompt with the piped text under <see cref="HeadlessText.PipedContextHeading"/>, in a Markdown fence one backtick longer than any run in
    /// it: cut first (head and tail) to half the room the model's window has left (<paramref name="roomTokens"/>, 2026-11-22, the review's #127: a
    /// resumed session's history is in the window too) at <see cref="Llm.ConversationCompactor.GuardCharsPerToken"/> characters a token, or
    /// <see cref="PipedUnknownWindowChars"/> with the window unknown, then fenced. A pipe read in part (<see cref="PipedText.Whole"/> false) is cut
    /// from the start and the end it kept, never across the gap between them; the note counts what came in all. The cut's notice, or null.
    /// </summary>
    internal static (string Text, string? Notice) HeadlessWithPiped(string prompt, PipedText piped, long? roomTokens)
    {
        ArgumentNullException.ThrowIfNull(piped);
        long cap = roomTokens is { } room ? Math.Max(PipedFloorChars, room * Llm.ConversationCompactor.GuardCharsPerToken / 2) : PipedUnknownWindowChars;
        int limit = (int)Math.Min(int.MaxValue, cap);
        string? note = null;
        string? notice = null;
        string body;
        if (piped.Whole)
        {
            body = (piped.Head + piped.Tail).TrimEnd('\r', '\n');
            if (body.Length > limit)
            {
                string cut = Llm.ConversationCompactor.CutText(body, limit);
                if (cut.Length < body.Length)
                {
                    note = HeadlessText.PipedCutNote(cut.Length, piped.TotalChars);
                    notice = HeadlessText.PipedCutNotice(cut.Length, piped.TotalChars);
                    body = cut;
                }
            }
        }
        else
        {
            string tail = piped.Tail.TrimEnd('\r', '\n');
            int headKeep = Math.Min(piped.Head.Length, limit * 2 / 3);
            int tailKeep = Math.Min(tail.Length, Math.Max(0, limit - headKeep));
            if (headKeep > 0 && char.IsHighSurrogate(piped.Head[headKeep - 1]))
            {
                headKeep--;
            }

            if (tailKeep > 0 && tailKeep < tail.Length && char.IsLowSurrogate(tail[^tailKeep]))
            {
                tailKeep--;
            }

            long left = piped.TotalChars - headKeep - tailKeep;
            body = piped.Head[..headKeep] + "\n" + Llm.TurnText.CutMarker(left) + "\n" + tail[^tailKeep..];
            note = HeadlessText.PipedCutNote(headKeep + tailKeep, piped.TotalChars);
            notice = HeadlessText.PipedCutNotice(headKeep + tailKeep, piped.TotalChars);
        }

        string fence = Audits.AuditReport.FenceFor(body);
        var text = new StringBuilder(prompt).Append("\n\n").Append(HeadlessText.PipedContextHeading);
        if (note is not null)
        {
            text.Append(' ').Append(note);
        }

        text.Append('\n').Append(fence).Append('\n').Append(body).Append('\n').Append(fence);
        return (text.ToString(), notice);
    }
}
