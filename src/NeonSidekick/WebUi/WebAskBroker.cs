using System.Net;

namespace NeonSidekick.WebUi;

/// <summary>Who answered an ask: the PC's pane or the Web UI.</summary>
public enum AskSource
{
    Pc,
    Web,
}

/// <summary>
/// An ask as the screen describes it for the page (2026-10-14): what it is (<see cref="Kind"/>: approval, allow, confirm, question,
/// plan, learn), its title, a plain body, a command or statement's lines (monospace), a plan's Markdown, the choices (the PC's own
/// wording), the choice that takes typed text, and <c>ask_user</c>'s questions answered with <see cref="SubmitLabel"/>.
/// </summary>
/// <remarks>
/// A <c>pick</c> (later on 2026-10-14: bare <c>/model</c>, <c>/reasoning</c>, <c>/rewind</c> from the page) carries <see cref="Items"/> — answered by an
/// item's id — and the one in use (<see cref="Current"/>); an <c>info</c> carries its <see cref="Lines"/> and one choice, <c>close</c>. A <c>botcheck</c>
/// (2026-10-17) is the wait while a bot check runs in the Browser window, its one choice Give up; <see cref="Live"/> puts the window's picture and
/// controls on its card (<i>Web UI bot check control</i>).
/// </remarks>
public sealed record WebAskSpec(
    string Kind,
    string Title,
    IReadOnlyList<WebChoice> Choices,
    string? Body = null,
    IReadOnlyList<string>? Lines = null,
    string? Markdown = null,
    string? TextChoice = null,
    IReadOnlyList<WebQuestion>? Questions = null,
    string? SubmitLabel = null,
    IReadOnlyList<WebPickItem>? Items = null,
    string? Current = null,
    bool Live = false);

/// <summary>
/// One open ask: its id, the answer the page gave (when the page claimed it), and the claim both sides race for. The first claim wins
/// (<see cref="TryClaim"/>); the loser's answer is dropped. A web answer claims first and only then cancels the PC's pane — the panes
/// answer Deny, No or Refine when canceled, which must never count.
/// </summary>
public sealed class WebAskHandle
{
    private readonly TaskCompletionSource<WebAnswerRequest> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _claim;   // 0 open, 1 the PC's, 2 the Web UI's

    internal WebAskHandle(string id, WebAskSpec spec)
    {
        Id = id;
        Spec = spec;
    }

    public string Id { get; }

    public WebAskSpec Spec { get; }

    /// <summary>Completes with the page's answer once the Web UI has claimed the ask.</summary>
    public Task<WebAnswerRequest> Answered => _answer.Task;

    /// <summary>Where the page's answer came from (the audit line).</summary>
    public IPAddress? Remote { get; private set; }

    /// <summary>Who claimed it, or null while it is open.</summary>
    public AskSource? Winner => Volatile.Read(ref _claim) switch
    {
        1 => AskSource.Pc,
        2 => AskSource.Web,
        _ => null,
    };

    /// <summary>Claims the ask for <paramref name="source"/>: true for the first claim, false once either side has it.</summary>
    public bool TryClaim(AskSource source) => Interlocked.CompareExchange(ref _claim, source == AskSource.Pc ? 1 : 2, 0) == 0;

    internal bool Deliver(WebAnswerRequest answer, IPAddress? remote)
    {
        if (!TryClaim(AskSource.Web))
        {
            return false;
        }

        Remote = remote;
        _answer.TrySetResult(answer);
        return true;
    }

    /// <summary>The label of the choice <paramref name="answer"/> names, or a short summary of a form's answers.</summary>
    public string Describe(WebAnswerRequest answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (Spec.Questions is { Count: > 0 })
        {
            return Spec.SubmitLabel ?? "answered";
        }

        string label = Spec.Choices.FirstOrDefault(c => c.Id == answer.Choice)?.Label
            ?? Spec.Items?.FirstOrDefault(i => i.Id == answer.Choice)?.Label
            ?? answer.Choice ?? "";
        return string.IsNullOrWhiteSpace(answer.Text) || answer.Choice != Spec.TextChoice ? label : label + ": " + answer.Text.Trim();
    }
}

/// <summary>
/// The asks open on the Web UI (2026-10-14): the screen opens one as its pane opens (<see cref="Open"/>), closes it as the pane ends
/// (<see cref="Close"/>), and the page answers through <see cref="Answer"/>, which checks the answer against the ask (a choice it has; every
/// question answered) before it claims. Several may be open at once, oldest first. Thread-safe; <see cref="Changed"/> runs on the
/// changing task.
/// </summary>
public sealed class WebAskBroker
{
    private readonly object _gate = new();
    private readonly List<WebAskHandle> _open = [];
    private long _next;

    /// <summary>Raised after an ask opens or closes.</summary>
    public event Action? Changed;

    /// <summary>The asks open now, oldest first.</summary>
    public IReadOnlyList<WebAskHandle> OpenAsks()
    {
        lock (_gate)
        {
            return [.. _open];
        }
    }

    /// <summary>Opens an ask for <paramref name="spec"/>.</summary>
    public WebAskHandle Open(WebAskSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var handle = new WebAskHandle("a" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture), spec);
        lock (_gate)
        {
            _open.Add(handle);
        }

        Changed?.Invoke();
        return handle;
    }

    /// <summary>Closes <paramref name="handle"/> (answered either way, or withdrawn); nothing when it was closed already.</summary>
    public void Close(WebAskHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        bool removed;
        lock (_gate)
        {
            removed = _open.Remove(handle);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The page's answer: refused when it does not fit the ask, a conflict when the ask is gone or the PC answered first.</summary>
    public WebReply Answer(WebAnswerRequest answer, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(answer);
        WebAskHandle? handle;
        lock (_gate)
        {
            handle = _open.FirstOrDefault(h => h.Id == answer.AskId);
        }

        if (handle is null)
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.AskGone);
        }

        if (Check(handle.Spec, answer) is { } problem)
        {
            return problem;
        }

        if (!handle.Deliver(answer, remote))
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.AskGone);
        }

        Close(handle);
        return WebReply.Ok;
    }

    /// <summary>Why <paramref name="answer"/> does not fit <paramref name="spec"/>, or null.</summary>
    public static WebReply? Check(WebAskSpec spec, WebAnswerRequest answer)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(answer);
        if (spec.Questions is { Count: > 0 } questions)
        {
            if (answer.Questions is not { } given || given.Count != questions.Count)
            {
                return new WebReply(WebOutcome.Refused, WebUiText.AskIncomplete);
            }

            for (int i = 0; i < questions.Count; i++)
            {
                var picked = given[i].Picked ?? [];
                bool typed = !string.IsNullOrWhiteSpace(given[i].Other);
                if (picked.Any(p => p < 0 || p >= questions[i].Options.Count) || picked.Distinct().Count() != picked.Count)
                {
                    return new WebReply(WebOutcome.Refused, WebUiText.NoSuchChoice);
                }

                if (picked.Count == 0 && !typed)
                {
                    return new WebReply(WebOutcome.Refused, WebUiText.AskIncomplete);
                }

                if (!questions[i].Multi && picked.Count + (typed ? 1 : 0) > 1)
                {
                    return new WebReply(WebOutcome.Refused, WebUiText.NoSuchChoice);
                }
            }

            return null;
        }

        return spec.Choices.Any(c => c.Id == answer.Choice) || spec.Items?.Any(i => i.Id == answer.Choice) == true
            ? null
            : new WebReply(WebOutcome.Refused, WebUiText.NoSuchChoice);
    }
}
