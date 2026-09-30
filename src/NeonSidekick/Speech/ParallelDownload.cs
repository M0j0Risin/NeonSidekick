using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Speech;

/// <summary>
/// What <see cref="ParallelDownload.RunAsync"/> came to: the whole file on <see cref="ModelStore.PartialPath"/> (<paramref name="Ok"/>),
/// a failure and why, or a server that answered a ranged request with the whole file (<paramref name="RangesIgnored"/>), which
/// sends the download back to one stream.
/// </summary>
internal readonly record struct ParallelOutcome(bool Ok, string Detail, bool RangesIgnored = false)
{
    public static readonly ParallelOutcome Done = new(true, "");

    public static ParallelOutcome Failed(string detail) => new(false, detail);
}

/// <summary>
/// One file fetched over several ranged connections at once (2026-09-30, Embedded HF download type <c>parallel</c>, the user's
/// ask): Hugging Face's <c>resolve</c> URL redirects to a CDN that honours <c>Range</c>, and eight connections took a 1 GB catalog
/// file from ~112 MB/s to ~206 MB/s on the user's line.
///
/// <para><b>On disk.</b> Part <c>i</c> of <c>n</c> is <c>&lt;file&gt;.partial.&lt;i&gt;-of-&lt;n&gt;</c>, an equal share of the exact
/// size (the last takes the remainder), appended in order, so its length is its progress and no sidecar file is needed. A cancel
/// or a broken connection keeps the parts; the next run asks each for its rest. When every part is whole they are joined into
/// <see cref="ModelStore.PartialPath"/> — part 0 renamed, the others appended in order and each deleted once flushed — and
/// <see cref="ModelStore"/> checks that file as it checks one stream's. A join cut short is picked up where it stopped: the
/// <c>.partial</c> beside the parts left is cut back to the next part's start and the appending goes on.</para>
///
/// <para><b>Failures.</b> A part retries up to <see cref="MaxAttempts"/> times running on a dropped connection, a server error or
/// a short body, the count starting over whenever a try added bytes; any other failure stops its siblings. A 200 to a ranged
/// request is a server that ignores ranges: <see cref="ParallelOutcome.RangesIgnored"/>, and the caller starts over on one stream.
/// A 206 for other bytes than asked deletes that part.</para>
/// </summary>
internal sealed class ParallelDownload
{
    /// <summary>Tries in a row a part may fail without adding a byte before the download gives up.</summary>
    public const int MaxAttempts = 3;

    private const int CopyBufferBytes = 64 * 1024;
    private const int JoinBufferBytes = 1 << 20;
    private const string PartMarker = ".partial.";
    private const string OfMarker = "-of-";

    private readonly HttpClient _http;
    private readonly string _category;
    private readonly string _display;
    private readonly Uri _url;
    private readonly string _path;
    private readonly long _size;

    /// <param name="path">The file's final path; the parts and the <c>.partial</c> sit beside it.</param>
    /// <param name="size">The file's exact size (a pinned spec's).</param>
    /// <param name="count">How many parts; never more than there are bytes.</param>
    public ParallelDownload(HttpClient http, string category, string display, Uri url, string path, long size, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        _http = http;
        _category = category;
        _display = display;
        _url = url;
        _path = path;
        _size = size;
        Count = (int)Math.Clamp(count, 1, Math.Min(size, int.MaxValue));
    }

    /// <summary>How many parts the file is fetched in.</summary>
    public int Count { get; }

    /// <summary>The first byte of part <paramref name="index"/>.</summary>
    public long Start(int index) => _size / Count * index;

    /// <summary>The last byte of part <paramref name="index"/> (inclusive, as <c>Range</c> counts).</summary>
    public long End(int index) => index == Count - 1 ? _size - 1 : Start(index + 1) - 1;

    /// <summary>The bytes of part <paramref name="index"/>.</summary>
    public long Length(int index) => End(index) - Start(index) + 1;

    /// <summary>The biggest part (the last, which takes the remainder): what the join holds twice at its peak.</summary>
    public long LargestPartBytes => Length(Count - 1);

    /// <summary>What this download holds so far, parts and joined file together, at most the file's size.</summary>
    public long HeldBytes() => Math.Min(_size, ModelStore.PartialBytes(_path));

    /// <summary>Where part <paramref name="index"/> of <paramref name="count"/> of <paramref name="path"/> collects its bytes.</summary>
    public static string PartPath(string path, int index, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{path}{PartMarker}{index}{OfMarker}{count}");

    private string PartPath(int index) => PartPath(_path, index, Count);

    /// <summary>
    /// How many parts a parallel download of <paramref name="path"/> already begun was split into, or null when none was begun.
    /// Parts of different splits (a hand-made mess) are deleted and read as none: the download starts over.
    /// </summary>
    public static int? BegunConnections(string path)
    {
        var parts = FindParts(path);
        if (parts.Count == 0)
        {
            return null;
        }

        int count = parts[0].Count;
        if (parts.Any(p => p.Count != count))
        {
            DeleteParts(path);
            return null;
        }

        return count;
    }

    /// <summary>The bytes the parts of <paramref name="path"/> hold (0 for none).</summary>
    public static long PartBytes(string path) => FindParts(path).Sum(p => new FileInfo(p.File).Length);

    /// <summary>Deletes every part of <paramref name="path"/>, best effort.</summary>
    public static void DeleteParts(string path)
    {
        foreach (var part in FindParts(path))
        {
            try
            {
                File.Delete(part.File);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static List<(string File, int Index, int Count)> FindParts(string path)
    {
        var found = new List<(string, int, int)>();
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null || !Directory.Exists(directory))
        {
            return found;
        }

        string prefix = Path.GetFileName(path) + PartMarker;
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            // Windows' pattern matching lets "x.partial.*" match "x.partial" itself; only a name past the prefix is a part.
            string name = Path.GetFileName(file);
            if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rest = name[prefix.Length..];
            int of = rest.IndexOf(OfMarker, StringComparison.Ordinal);
            if (of > 0
                && int.TryParse(rest.AsSpan(0, of), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                && int.TryParse(rest.AsSpan(of + OfMarker.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                && index < count)
            {
                found.Add((file, index, count));
            }
        }

        return found;
    }

    /// <summary>
    /// Fetches what the parts still lack, all at once, then joins them into <see cref="ModelStore.PartialPath"/>. Never throws but
    /// the caller's own cancel (the parts stay for the next run).
    /// </summary>
    public async Task<ParallelOutcome> RunAsync(IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken)
    {
        string partial = ModelStore.PartialPath(_path);
        bool joining = File.Exists(partial) && !File.Exists(PartPath(0));
        var reporter = new Reporter(progress, _size, HeldBytes());
        reporter.Add(0);
        if (!joining)
        {
            long held = reporter.Received;
            DiagnosticLog.Info(_category, held > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Resuming {_display} from {_url} over {Count} connections at {held} bytes.")
                : string.Create(CultureInfo.InvariantCulture, $"Downloading {_display} from {_url} over {Count} connections to {PartPath(_path, 0, Count)}…"));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, Count).Select(i => FetchPartAsync(i, reporter, linked))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (outcomes.FirstOrDefault(o => o.RangesIgnored) is { RangesIgnored: true } ignored)
            {
                return ignored;
            }

            if (outcomes.Any(o => !o.Ok))
            {
                // The siblings a failure stopped end with no word of their own; the failure's is the one to tell.
                var failed = outcomes.Where(o => !o.Ok && !string.IsNullOrEmpty(o.Detail)).Select(o => o.Detail).FirstOrDefault();
                return ParallelOutcome.Failed(failed ?? $"the download of {_display} failed; try again to resume");
            }
        }

        return await JoinAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One part to its end: whole already is done; otherwise its rest is asked for, again after a dropped connection, a server
    /// error or a short body, while tries keep adding bytes. A failure that ends it cancels <paramref name="linked"/>, which stops
    /// the siblings; a sibling's cancel ends it quietly.
    /// </summary>
    private async Task<ParallelOutcome> FetchPartAsync(int index, Reporter reporter, CancellationTokenSource linked)
    {
        string file = PartPath(index);
        long length = Length(index);
        long before = -1;
        int failures = 0;
        string lastError = "";
        while (true)
        {
            try
            {
                long held = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (held > length)
                {
                    DiagnosticLog.Warn(_category, $"Discarding {file}: longer than its part of {_display}.");
                    File.Delete(file);
                    reporter.Add(-held);
                    held = 0;
                }

                if (held == length)
                {
                    return ParallelOutcome.Done;
                }

                if (held > before && before >= 0)
                {
                    failures = 0;
                }

                before = held;
                if (failures >= MaxAttempts)
                {
                    return Stop(linked, ParallelOutcome.Failed(lastError));
                }

                var outcome = await FetchOnceAsync(index, file, held, reporter, linked.Token).ConfigureAwait(false);
                if (outcome.Final is { } final)
                {
                    return final.Ok ? final : Stop(linked, final);
                }

                failures++;
                lastError = outcome.Retry;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return new ParallelOutcome(false, "");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                failures++;
                lastError = Assistant.Explain(ex);
                DiagnosticLog.Warn(_category, string.Create(CultureInfo.InvariantCulture, $"Part {index + 1} of {Count} of {_display}: {lastError} (try {failures} of {MaxAttempts})."));
            }
            catch (Exception ex)
            {
                return Stop(linked, ParallelOutcome.Failed(Assistant.Explain(ex)));
            }
        }
    }

    private static ParallelOutcome Stop(CancellationTokenSource linked, ParallelOutcome outcome)
    {
        try
        {
            linked.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return outcome;
    }

    /// <summary>
    /// One request for part <paramref name="index"/>'s rest from byte <paramref name="held"/>: <c>Final</c> when it settles the
    /// part (whole, a hard failure, ranges ignored), else <c>Retry</c> says why another try is worth it (a server error, a short body).
    /// </summary>
    private async Task<(ParallelOutcome? Final, string Retry)> FetchOnceAsync(int index, string file, long held, Reporter reporter, CancellationToken cancellationToken)
    {
        long from = Start(index) + held;
        long to = End(index);
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return (new ParallelOutcome(false, "", RangesIgnored: true), "");
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            string error = $"HTTP {(int)response.StatusCode} from {_url}";
            return (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
                ? (null, error)
                : (ParallelOutcome.Failed(error), "");
        }

        if (response.Content.Headers.ContentRange is not { From: { } start } range || start != from
            || (range.To is { } end && end != to) || (range.Length is { } whole && whole != _size))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }

            reporter.Add(-held);
            return (ParallelOutcome.Failed($"{_url} sent other bytes than part {index + 1} of {_display} asked for; the part was deleted, try again"), "");
        }

        long want = to - from + 1;
        long got = 0;
        using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        using (var target = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true))
        {
            var buffer = new byte[CopyBufferBytes];
            int read;
            while (got < want && (read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, want - got)), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                got += read;
                reporter.Add(read);
            }
        }

        return got == want
            ? (ParallelOutcome.Done, "")
            : (null, string.Create(CultureInfo.InvariantCulture, $"part {index + 1} of {_display} ended at {held + got} of {Length(index)} bytes"));
    }

    /// <summary>
    /// Joins the whole parts into <see cref="ModelStore.PartialPath"/>: part 0 renamed, the rest appended in order, each flushed to
    /// disk and then deleted, so a join cut short leaves a <c>.partial</c> and the parts still to append. Parts that are missing or
    /// the wrong size, or a <c>.partial</c> shorter than the join reached, are deleted with it: the next run starts over.
    /// </summary>
    private async Task<ParallelOutcome> JoinAsync(CancellationToken cancellationToken)
    {
        string partial = ModelStore.PartialPath(_path);
        int next;
        if (File.Exists(PartPath(0)))
        {
            File.Move(PartPath(0), partial, overwrite: true);
            next = 1;
        }
        else
        {
            var left = FindParts(_path);
            next = left.Count == 0 ? Count : left.Min(p => p.Index);
        }

        bool sound = File.Exists(partial) && new FileInfo(partial).Length >= (next < Count ? Start(next) : _size);
        for (int i = next; sound && i < Count; i++)
        {
            sound = File.Exists(PartPath(i)) && new FileInfo(PartPath(i)).Length == Length(i);
        }

        if (!sound)
        {
            DeleteParts(_path);
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }

            return ParallelOutcome.Failed($"the parts of {_display} did not add up; deleted, try again");
        }

        if (next < Count)
        {
            DiagnosticLog.Info(_category, string.Create(CultureInfo.InvariantCulture, $"Joining the {Count} parts of {_display}."));
        }

        using (var target = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None, JoinBufferBytes, useAsync: true))
        {
            for (int i = next; i < Count; i++)
            {
                target.SetLength(Start(i));
                target.Seek(Start(i), SeekOrigin.Begin);
                using (var source = new FileStream(PartPath(i), FileMode.Open, FileAccess.Read, FileShare.Read, JoinBufferBytes, useAsync: true))
                {
                    await source.CopyToAsync(target, JoinBufferBytes, cancellationToken).ConfigureAwait(false);
                }

                target.Flush(flushToDisk: true);
                File.Delete(PartPath(i));
            }
        }

        return ParallelOutcome.Done;
    }

    /// <summary>The parts' bytes as one count, reported under a lock: the progress behind it is not thread-safe.</summary>
    private sealed class Reporter(IProgress<(long Received, long? Total)>? progress, long total, long start)
    {
        private readonly Lock _gate = new();

        public long Received { get; private set; } = start;

        public void Add(long bytes)
        {
            lock (_gate)
            {
                Received = Math.Max(0, Received + bytes);
                progress?.Report((Received, total));
            }
        }
    }
}
