using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.App;

// ── A headless run's start (2026-11-21, feature ideas #42 and #44) ─────────────────────────────────────────────────────────────────────
// Before anything connects: the one-shot prompt (--prompt, or --prompt-file read whole), the piped stdin it takes as context, and the session
// --session carries on, its folder taken into the flags' layer for this run. A problem here is a bad argument's exit 2, nothing run.

public sealed partial class SidekickApp
{
    /// <summary>A stored session a headless run carries on: its row, its messages and everything saved beside them.</summary>
    private sealed record HeadlessResume(long Id, List<ChatMessage> Messages, Sessions.StoredHistory Stored);

    /// <summary>What the start found: the prompt (null for the line-by-line mode), whether it is a message whatever it says, the piped text, the session, the notices to show after the banner, or the exit code of a problem.</summary>
    private sealed record HeadlessStartState(string? Prompt, bool PromptIsMessage, string? Piped, HeadlessResume? Resume, IReadOnlyList<string> Notices, int? Exit);

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

        string? piped = null;
        if (prompt is not null && _headlessInputRedirected)
        {
            (piped, string? silent) = await ReadPipedAsync(cancellationToken).ConfigureAwait(false);
            if (silent is not null)
            {
                notices.Add(silent);
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
            if (Directory.Exists(folder))
            {
                _options = _options with { WorkingDirectory = folder };
                notices.Add(HeadlessText.SessionFolder(folder));
            }
            else
            {
                notices.Add(HeadlessText.SessionFolderGone(folder));
            }
        }

        if (!EffectiveSettings.SessionLogging)
        {
            notices.Add(HeadlessText.SessionNotSaved);
        }

        return (new HeadlessResume(id, messages, stored), null);
    }

    /// <summary>
    /// The whole of a redirected stdin, or nothing (and <see cref="HeadlessText.NoPipedInput"/>) when its first character doesn't come within
    /// <see cref="PipedFirstByteWait"/>: a parent that redirects stdin and never writes would otherwise hang the run. Once data starts, to the end.
    /// </summary>
    private async Task<(string? Text, string? Silent)> ReadPipedAsync(CancellationToken cancellationToken)
    {
        var first = new char[1];
        var read = _headlessInput.ReadAsync(first.AsMemory(), cancellationToken).AsTask();
        using var waitCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wait = Task.Delay(PipedFirstByteWait, _time, waitCancel.Token);
        if (await Task.WhenAny(read, wait).ConfigureAwait(false) != read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (null, HeadlessText.NoPipedInput);
        }

        await waitCancel.CancelAsync().ConfigureAwait(false);
        int count = await read.ConfigureAwait(false);
        if (count == 0)
        {
            return (null, null);
        }

        string rest = await _headlessInput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return (first[0] + rest, null);
    }

    /// <summary>
    /// The prompt with the piped text under <see cref="HeadlessText.PipedContextHeading"/>, in a Markdown fence one backtick longer than any run in
    /// it: cut first (head and tail) to half the model's window at <see cref="Llm.ConversationCompactor.GuardCharsPerToken"/> characters a token, or
    /// <see cref="PipedUnknownWindowChars"/> with the window unknown, then fenced. The cut's notice, or null.
    /// </summary>
    internal static (string Text, string? Notice) HeadlessWithPiped(string prompt, string piped, long? windowTokens)
    {
        long cap = windowTokens is > 0 and { } tokens ? tokens * Llm.ConversationCompactor.GuardCharsPerToken / 2 : PipedUnknownWindowChars;
        int limit = (int)Math.Min(int.MaxValue, cap);
        string body = piped.TrimEnd('\r', '\n');
        string? note = null;
        string? notice = null;
        if (body.Length > limit)
        {
            string cut = Llm.ConversationCompactor.CutText(body, limit);
            if (cut.Length < body.Length)
            {
                note = HeadlessText.PipedCutNote(cut.Length, body.Length);
                notice = HeadlessText.PipedCutNotice(cut.Length, body.Length);
                body = cut;
            }
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
