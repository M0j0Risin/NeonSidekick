using System.Net;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.WebUi;

/// <summary>
/// The phone's uploads held for its next message (2026-11-16, plan lucid-roaming-lantern #39): at most <see cref="MaxHeldPerPage"/> for each page
/// and <see cref="MaxHeldBytes"/> in all, counting the waiting lines' own, let go after <see cref="HeldFor"/> or on a profile switch
/// (<see cref="ForgetAttachments"/>). Under <c>_linesGate</c>, the lines' lock, so a send's take and its enqueue are one step and no path holds
/// two locks. Every change to a page's held list sends that page a <c>chips</c> event with its whole row; a snapshot carries it too.
/// </summary>
public sealed partial class WebUiSession
{
    /// <summary>The most uploads held for one page.</summary>
    public const int MaxHeldPerPage = 4;

    /// <summary>The most upload bytes held in all, the waiting lines' attachments counted.</summary>
    public const long MaxHeldBytes = 60_000_000;

    /// <summary>How long an unsent upload is held.</summary>
    public static readonly TimeSpan HeldFor = TimeSpan.FromMinutes(15);

    // Under _linesGate: the uploads held, oldest first, and the epoch a profile switch bumps.
    private readonly List<WebHeldAttachment> _held = [];
    private long _heldEpoch;

    public long AttachEpoch
    {
        get
        {
            lock (_linesGate)
            {
                return _heldEpoch;
            }
        }
    }

    /// <summary>The uploads held under <paramref name="page"/> now, as its chip row.</summary>
    public IReadOnlyList<WebChip> ChipsOf(string page)
    {
        lock (_linesGate)
        {
            return [.. _held.Where(h => h.Page == page).Select(h => h.Chip)];
        }
    }

    /// <summary>A notice for <paramref name="page"/> alone (2026-11-16, #39: a phone attachment left off or not saved), as its chip row's message.</summary>
    public void SayToPage(string page, string text)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(text);
        PublishChips(page, text);
    }

    /// <summary>How many uploads are held now, every page's.</summary>
    public int HeldCount
    {
        get
        {
            lock (_linesGate)
            {
                return _held.Count;
            }
        }
    }

    public WebAttachReply Attach(string page, byte[] body, long epoch, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!WebVideoPlayer.IsId(page))
        {
            return new WebAttachReply(400, WebUiText.BadRequest, null);
        }

        ExpireHeld();
        // The cheap refusals before the decode (2026-11-16, the review's #41): a fifth upload, the bytes in all or a profile switch never costs a
        // picture's decode and fit. Checked again under the lock after it, as the lists may have moved meanwhile.
        lock (_linesGate)
        {
            if (CapRefusalLocked(page, body.LongLength, epoch) is { } early)
            {
                return early;
            }
        }

        var read = WebAttachSniff.Read(body);
        if (read.Status != 0)
        {
            return new WebAttachReply(read.Status, read.Message, null);
        }

        var held = new WebHeldAttachment(WebVideoPlayer.NewId(), read.Kind, body, read.Picture, read.Picture is { } picture ? WebAttachSniff.Thumb(picture) : null, page)
        {
            Expires = Later(),
            MediaType = read.MediaType,
            Epoch = epoch,
        };
        lock (_linesGate)
        {
            if (CapRefusalLocked(page, body.LongLength, epoch) is { } refused)
            {
                return refused;
            }

            _held.Add(held);
        }

        var chip = held.Chip;
        DiagnosticLog.Info(Category, WebUiText.AttachLogLine(chip.Kind, chip.Size, remote));
        PublishChips(page);
        return new WebAttachReply(200, null, chip);
    }

    public WebReply ForgetAttachment(WebAttachForget request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!WebVideoPlayer.IsId(request.Page) || string.IsNullOrEmpty(request.Id))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        int removed;
        lock (_linesGate)
        {
            removed = _held.RemoveAll(h => h.Id == request.Id && h.Page == request.Page);
        }

        if (removed > 0)
        {
            PublishChips(request.Page!);
        }

        return WebReply.Ok;   // an unknown id (raced against the expiry, or another page's) is a quiet OK
    }

    /// <summary>
    /// Every upload let go (a profile switch, a pull, a reset): the epoch bumped, so one still arriving is dropped with a 409, and every page's
    /// chip row emptied.
    /// </summary>
    public void ForgetAttachments()
    {
        bool had;
        lock (_linesGate)
        {
            _heldEpoch++;
            had = _held.Count > 0;
            _held.Clear();
        }

        if (had)
        {
            Events.Publish(new WebEvent("chips", () => JsonSerializer.Serialize(new WebChips([]), WebUiJsonContext.Default.WebChips)));
        }
    }

    /// <summary>
    /// Uploads handed back with a line (withdrawn, dropped, taken off the Queue tab): held again under their own page, each with a fresh
    /// <see cref="HeldFor"/>, their saved path kept; past the caps the oldest of the pages handed back go (2026-11-16, the review's #46: another
    /// page's uploads are never let go for them), which the page is told. One held before a profile switch is dropped (#5). The pages touched get
    /// their rows.
    /// </summary>
    public void Rehold(IReadOnlyList<WebHeldAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
        {
            return;
        }

        var pages = new HashSet<string>(StringComparer.Ordinal);
        var trimmedPages = new HashSet<string>(StringComparer.Ordinal);
        long expires = Later();
        lock (_linesGate)
        {
            foreach (var attachment in attachments)
            {
                if (attachment.Epoch != _heldEpoch)
                {
                    continue;   // held under the last profile
                }

                _held.RemoveAll(h => h.Id == attachment.Id);
                _held.Add(attachment with { Expires = expires });
                pages.Add(attachment.Page);
            }

            foreach (string page in pages)
            {
                while (_held.Count(h => h.Page == page) > MaxHeldPerPage)
                {
                    _held.RemoveAt(_held.FindIndex(h => h.Page == page));
                    trimmedPages.Add(page);
                }
            }

            while (HeldBytesLocked() > MaxHeldBytes && _held.FindIndex(h => pages.Contains(h.Page)) is var at and >= 0)
            {
                trimmedPages.Add(_held[at].Page);
                _held.RemoveAt(at);
            }
        }

        foreach (string page in pages)
        {
            PublishChips(page, trimmedPages.Contains(page) ? WebUiText.AttachTrimmed : null);
        }
    }

    /// <summary>The uploads held past their time let go; the pages touched get their rows. On the sampler's tick, and on each upload and send.</summary>
    public void ExpireHeld()
    {
        long now = _time.GetTimestamp();
        List<string> pages;
        lock (_linesGate)
        {
            if (_held.Count == 0)
            {
                return;
            }

            pages = [.. _held.Where(h => h.Expires <= now).Select(h => h.Page).Distinct()];
            if (pages.Count == 0)
            {
                return;
            }

            _held.RemoveAll(h => h.Expires <= now);
        }

        foreach (string page in pages)
        {
            PublishChips(page);
        }
    }

    /// <summary>
    /// The uploads <paramref name="ids"/> taken for a line from <paramref name="page"/>'s held list, in the ids' order; an id held under another
    /// page, or no longer held, is skipped and counted in <paramref name="missing"/>. Under <c>_linesGate</c>.
    /// </summary>
    private List<WebHeldAttachment> TakeHeldLocked(string page, IReadOnlyList<string> ids, out int missing)
    {
        var taken = new List<WebHeldAttachment>(ids.Count);
        missing = 0;
        foreach (string id in ids)
        {
            int at = _held.FindIndex(h => h.Id == id && h.Page == page);
            if (at < 0)
            {
                missing++;
                continue;
            }

            taken.Add(_held[at]);
            _held.RemoveAt(at);
        }

        return taken;
    }

    /// <summary>Why an upload of <paramref name="length"/> bytes can't be held for <paramref name="page"/> now, or null. Under <c>_linesGate</c>.</summary>
    private WebAttachReply? CapRefusalLocked(string page, long length, long epoch) =>
        epoch != _heldEpoch ? new WebAttachReply(409, WebUiText.AttachProfileSwitched, null)
        : _held.Count(h => h.Page == page) >= MaxHeldPerPage ? new WebAttachReply(409, WebUiText.AttachFull, null)
        : HeldBytesLocked() + length > MaxHeldBytes ? new WebAttachReply(507, WebUiText.AttachTooMuch, null)
        : null;

    private long HeldBytesLocked() =>
        _held.Sum(h => h.Bytes.LongLength) + _cancelHeld.Concat(_lines).Sum(l => l.Attachments?.Sum(a => a.Bytes.LongLength) ?? 0);

    private long Later() => _time.GetTimestamp() + (long)(HeldFor.TotalSeconds * _time.TimestampFrequency);

    /// <summary>A page's whole chip row, sent to that page alone, with what went when something did.</summary>
    private void PublishChips(string page, string? message = null)
    {
        var chips = ChipsOf(page);
        Events.Publish(new WebEvent("chips", () => JsonSerializer.Serialize(new WebChips(chips, message), WebUiJsonContext.Default.WebChips)) { Page = page });
    }
}
