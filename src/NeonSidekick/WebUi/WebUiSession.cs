using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Shopping;
using NeonSidekick.UI;

namespace NeonSidekick.WebUi;

/// <summary>
/// A message the page sent, waiting for the idle line: its text and the page's id for it; where it came from, and whether a line that reads as a
/// command goes as a message (later on 2026-10-14).
/// </summary>
public sealed record WebLine(string Text, string? Id, IPAddress? Remote = null, bool AsText = false);

/// <summary>
/// The app's side of the Web UI (2026-10-14), one per screen and outliving the server's restarts: the transcript as the pages see it
/// (<see cref="Feed"/>), the open asks (<see cref="Asks"/>), the pages' event fan-out (<see cref="Events"/>), the phone's messages waiting
/// for the idle line (<see cref="TryTakeLine"/>), the notices waiting for the screen (<see cref="TakeNotices"/>), and the state sampled
/// once a second while a page is connected. The host (<see cref="WebUiHost"/>) calls it on Kestrel threads; it never writes to the
/// transcript and never waits on the screen — a message or a notice is queued and <see cref="Nudge"/> wakes the idle line, which takes
/// them on its own task. The screen supplies what only it knows through the hooks given at construction.
/// </summary>
public sealed class WebUiSession : IWebUiBackend, IDisposable
{
    /// <summary>The most bytes one message may hold (UTF-8), under the request's own 64 KB cap.</summary>
    public const int MaxMessageBytes = 48 * 1024;

    /// <summary>The most messages waiting at once.</summary>
    public const int MaxWaiting = 20;

    /// <summary>How many recent message ids are remembered, so a phone's retry is not sent twice.</summary>
    public const int RememberedIds = 50;

    /// <summary>How often the state is sampled while a page is connected.</summary>
    public static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(1);

    private const string Category = "WebUi";

    private readonly Func<WebState> _state;
    private readonly Func<IPAddress?, WebReply> _stop;
    private readonly Func<ShoppingList?> _shopping;
    private readonly Action _shoppingChanged;
    private readonly ConcurrentQueue<WebLine> _lines = new();
    private readonly ConcurrentQueue<(string Text, bool Error)> _notices = new();
    private readonly LinkedList<string> _ids = new();
    private readonly object _idGate = new();
    private readonly ITimer _sampler;
    private string _lastState = "";

    /// <param name="state">The state as the page's header shows it, read on a timer thread (reads only).</param>
    /// <param name="stop">The page's Stop: the screen ends what it may (its reply, not a model's load).</param>
    /// <param name="shopping">The screen's shopping list (it follows the profile), or null for none.</param>
    /// <param name="shoppingChanged">Tells the home bar's 🛒 a line changed (thread-safe).</param>
    /// <param name="time">The sampler's clock.</param>
    public WebUiSession(Func<WebState> state, Func<IPAddress?, WebReply> stop, Func<ShoppingList?> shopping, Action shoppingChanged, TimeProvider? time = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _shopping = shopping ?? throw new ArgumentNullException(nameof(shopping));
        _shoppingChanged = shoppingChanged ?? throw new ArgumentNullException(nameof(shoppingChanged));
        Feed.Changed += OnFeedChanged;
        Asks.Changed += PublishAsks;
        _sampler = (time ?? TimeProvider.System).CreateTimer(_ => SampleState(), null, StateInterval, StateInterval);
    }

    /// <summary>The transcript as the pages see it.</summary>
    public TranscriptFeed Feed { get; } = new();

    /// <summary>The asks open on the pages.</summary>
    public WebAskBroker Asks { get; } = new();

    public WebEventHub Events { get; } = new();

    private static readonly string s_pictureRun = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public string PictureRun => s_pictureRun;

    private WebCompletionSnapshot? _completions;
    private readonly SemaphoreSlim _completing = new(1);
    private int _completionFailed;

    /// <summary>The dropdown's lists as the screen last built them (later on 2026-10-14), on its own task; null answers nothing.</summary>
    public WebCompletionSnapshot? Completions
    {
        get => Volatile.Read(ref _completions);
        set => Volatile.Write(ref _completions, value);
    }

    public WebCompletion Complete(WebCompleteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_completing.Wait(0))
        {
            return WebCompletion.Empty(request.Seq, truncated: true);   // one at a time: the page asks again as it types
        }

        try
        {
            return Completions?.Complete(request.Text ?? "", request.Cursor, request.Seq) ?? WebCompletion.Empty(request.Seq);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A list that failed is quiet (once a run in the log), and asked again next time.
            if (Interlocked.Exchange(ref _completionFailed, 1) == 0)
            {
                DiagnosticLog.Debug(Category, "Web UI: a completion list could not be read: " + e.Message);
            }

            return WebCompletion.Empty(request.Seq);
        }
        finally
        {
            _completing.Release();
        }
    }

    /// <summary>The settings a command from the page is checked against, read at each send (later on 2026-10-14); null refuses every command.</summary>
    public Func<WebCommandSettings>? CommandSettings { get; set; }

    /// <summary>The page's lines the screen holds for the idle line (a command sent under a botchat), listed as waiting on the page.</summary>
    public Func<IReadOnlyList<string>>? HeldForPc { get; set; }

    /// <summary>The screen's picture by id (under its own lock), asked only for an id the feed holds; null for none.</summary>
    public Func<int, WebPictureSource?>? PictureSource { get; set; }

    public WebPictureSource? Picture(int id) => Feed.Holds(id) ? PictureSource?.Invoke(id) : null;

    /// <summary>A picture as an entry carries it: its three addresses under this run's tag. Pure but for the tag.</summary>
    public WebPicture Describe(int id, string name, int width, int height, bool tooLarge)
    {
        string root = "/api/picture/" + s_pictureRun + "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
        return new WebPicture(id, name, root + "thumb", root + "mid", root + "full", width, height, tooLarge);
    }

    /// <summary>Wakes the screen's idle line: a message or a notice is waiting (the screen's <c>SignalAlert</c>, and its act signal under a botchat).</summary>
    public Action? Nudge { get; set; }

    /// <summary>Whether a message waits.</summary>
    public bool LinesReady => !_lines.IsEmpty;

    /// <summary>Whether a notice waits.</summary>
    public bool NoticesReady => !_notices.IsEmpty;

    /// <summary>The messages waiting now, oldest first (the state's list): the queue's, then the screen's held ones.</summary>
    public IReadOnlyList<string> Waiting => [.. _lines.Select(l => l.Text), .. HeldForPc?.Invoke() ?? []];

    /// <summary>How many messages wait (the Status bar's Queued count).</summary>
    public int WaitingCount => _lines.Count;

    /// <summary>The next message for the idle line, oldest first.</summary>
    public bool TryTakeLine(out WebLine line) => _lines.TryDequeue(out line!);

    /// <summary>Every waiting message dropped and handed back to the pages' drafts (a conversation cleared); their count.</summary>
    public int DropLines()
    {
        int dropped = 0;
        while (_lines.TryDequeue(out var line))
        {
            dropped++;
            HandBack("dropped", line.Text);
        }

        if (dropped > 0)
        {
            PublishState();
        }

        return dropped;
    }

    /// <summary>A message the screen withdrew (stopped before the model answered): back into the page's draft.</summary>
    public void Withdrawn(string text) => HandBack("withdrawn", text);

    /// <summary>A line the screen held for the idle line, dropped with the conversation: back into the page's draft.</summary>
    public void Dropped(string text) => HandBack("dropped", text);

    private readonly List<string> _drafts = [];

    /// <summary>
    /// A text for the page's draft (later on 2026-10-14: <c>/prompts &lt;n&gt;</c>, <c>/rewind</c> from the page): sent to the pages connected
    /// now, or held until the next one connects, which takes it with its snapshot — delivered once either way.
    /// </summary>
    public void Draft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_drafts)
        {
            if (Events.Count == 0)
            {
                _drafts.Add(text);
                return;
            }
        }

        HandBack("draft", text);
    }

    private IReadOnlyList<string>? TakeDrafts()
    {
        lock (_drafts)
        {
            if (_drafts.Count == 0)
            {
                return null;
            }

            var taken = _drafts.ToList();
            _drafts.Clear();
            return taken;
        }
    }

    /// <summary>A notice (or, with <paramref name="error"/>, an error line) for the screen to print on its own task.</summary>
    public void Notice(string text, bool error = false)
    {
        _notices.Enqueue((text, error));
        Nudge?.Invoke();
    }

    /// <summary>The notices waiting, oldest first, taken.</summary>
    public IReadOnlyList<(string Text, bool Error)> TakeNotices()
    {
        var taken = new List<(string Text, bool Error)>();
        while (_notices.TryDequeue(out var notice))
        {
            taken.Add(notice);
        }

        return taken;
    }

    // ── The pages' calls (Kestrel threads) ────────────────────────────────────

    public WebSnapshot Snapshot()
    {
        // The connecting page's own copy: the other pages' last state stays what the sampler compares with.
        return new WebSnapshot(Feed.Snapshot(WebUiMarkdown.ToHtml), Asks.OpenAsks().Select(View).ToList(), _state(), WebUiText.Words, TakeDrafts());
    }

    public void SignedIn(IPAddress? remote)
    {
        DiagnosticLog.Info(Category, WebUiText.SignedInLogLine(remote));
        Notice(WebUiText.SignedInNotice(remote));
    }

    public WebReply Send(WebSendRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        string text = Clean(request.Text ?? "");
        if (text.Trim().Length == 0)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.EmptyMessage);
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxMessageBytes)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.TooLong(MaxMessageBytes));
        }

        // A command (later on 2026-10-14): checked here, and again as the screen takes it (the settings may change in between).
        if (!request.AsText && SlashCommands.Parse(text) is var (command, args) && command != SlashCommand.None)
        {
            var verdict = WebCommands.Check(command, args, text, CommandSettings?.Invoke() ?? default);
            DiagnosticLog.Info(Category, WebUiText.CommandLogLine(WebCommands.Word(text), remote, verdict.Allowed ? verdict.Kind.ToString().ToLowerInvariant() : "refused"));
            if (!verdict.Allowed)
            {
                return new WebReply(WebOutcome.Refused, verdict.Reason, verdict.AsText, verdict.Use);
            }
        }

        if (!Remember(request.Id))
        {
            return WebReply.Ok;   // a retry of a message already taken
        }

        if (_lines.Count >= MaxWaiting)
        {
            Forget(request.Id);
            return new WebReply(WebOutcome.TooMany, WebUiText.QueueFull(MaxWaiting));
        }

        _lines.Enqueue(new WebLine(text, request.Id, remote, request.AsText));
        PublishState();
        Nudge?.Invoke();
        return WebReply.Ok;
    }

    public WebReply Stop(IPAddress? remote) => _stop(remote);

    public WebReply Answer(WebAnswerRequest request, IPAddress? remote) => Asks.Answer(request, remote);

    public WebShopping Shopping() => _shopping() is { } list ? WebShoppingView.Of(list) : new WebShopping("", ShoppingText.Unavailable, [], ShoppingText.TickedHeading, [], ShoppingText.GuessMark);

    public WebReply Check(WebCheckRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_shopping() is not { } list)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchItem);
        }

        var reply = WebShoppingView.Check(list, request.Id, request.On, out string? line);
        if (reply.Outcome == WebOutcome.Ok && line is not null)
        {
            DiagnosticLog.Info(Category, WebUiText.CheckedLogLine(line, request.On, remote));
            _shoppingChanged();
        }

        return reply;
    }

    // ── Publishing ────────────────────────────────────────────────────────────

    /// <summary>The state now, to every page, when it differs from what they have (the sampler's tick; a message queued).</summary>
    public void PublishState()
    {
        if (Events.Count == 0)
        {
            return;
        }

        string json = Json(_state());
        if (json == Interlocked.Exchange(ref _lastState, json))
        {
            return;
        }

        Events.Publish(new WebEvent("state", new Lazy<string>(json)));
    }

    private void SampleState()
    {
        try
        {
            PublishState();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(Category, "Web UI: the state could not be read: " + e.Message);
        }
    }

    private static string Json(WebState state) => JsonSerializer.Serialize(state, WebUiJsonContext.Default.WebState);

    private void OnFeedChanged(FeedChange change)
    {
        switch (change)
        {
            case FeedChange.Appended appended:
                Events.Publish(new WebEvent("append", () => JsonSerializer.Serialize(appended.Entry, WebUiJsonContext.Default.WebEntry)));
                break;
            case FeedChange.Grew grew:
                Events.Publish(new WebEvent("delta", () => JsonSerializer.Serialize(new WebDelta(grew.Seq, grew.At, grew.Text), WebUiJsonContext.Default.WebDelta)));
                break;
            case FeedChange.Ended ended:
                Events.Publish(new WebEvent("end", () => JsonSerializer.Serialize(new WebEnd(ended.Seq, WebUiMarkdown.ToHtml(ended.Markdown)), WebUiJsonContext.Default.WebEnd)));
                break;
            case FeedChange.Cleared:
                Events.Publish(new WebEvent("reset", () => "{}"));
                break;
            case FeedChange.Removed removed:
                Events.Publish(new WebEvent("remove", () => JsonSerializer.Serialize(new WebRemoved(removed.Seq), WebUiJsonContext.Default.WebRemoved)));
                break;
        }
    }

    private void PublishAsks()
    {
        var asks = Asks.OpenAsks();
        Events.Publish(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks(asks.Select(View).ToList()), WebUiJsonContext.Default.WebAsks)));
    }

    private void HandBack(string name, string text) =>
        Events.Publish(new WebEvent(name, () => JsonSerializer.Serialize(new WebTextBack(text), WebUiJsonContext.Default.WebTextBack)));

    private static WebAskView View(WebAskHandle handle)
    {
        var spec = handle.Spec;
        return new WebAskView(handle.Id, spec.Kind, spec.Title, spec.Body, spec.Lines, spec.Markdown is { } markdown ? WebUiMarkdown.ToHtml(markdown) : null,
            spec.Choices, spec.TextChoice, spec.Questions, spec.SubmitLabel, spec.Items, spec.Current);
    }

    // ── Message ids ───────────────────────────────────────────────────────────

    /// <summary>The paste stand-ins the PC's draft uses, taken out: a web text never names one of the PC's pastes.</summary>
    public static string Clean(string text) => text.Any(PasteBlocks.IsToken) ? new string(text.Where(c => !PasteBlocks.IsToken(c)).ToArray()) : text;

    private bool Remember(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return true;
        }

        lock (_idGate)
        {
            if (_ids.Contains(id))
            {
                return false;
            }

            _ids.AddLast(id);
            if (_ids.Count > RememberedIds)
            {
                _ids.RemoveFirst();
            }

            return true;
        }
    }

    private void Forget(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_idGate)
        {
            _ids.Remove(id);
        }
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _completing.Dispose();
    }
}
