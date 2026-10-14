using System.Globalization;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using Vosk;

namespace NeonSidekick.Speech;

/// <summary>
/// The wake-word recogniser: Vosk's small English model, open vocabulary, fed 50 ms buffers on
/// the capture thread and reporting only <em>final</em> results.
///
/// <para><b>One lock around every native call, held across <c>AcceptWaveform</c>.</b> The capture
/// thread feeds the recogniser while the session thread resets or disposes it; audio landing on
/// a freed recogniser makes Kaldi assert and abort the process: no managed exception, nothing to
/// catch. About a millisecond per buffer, so nobody waits long for it.</para>
///
/// <para><b>The model directory is verified before Vosk sees it.</b> A failed native model load
/// returns a null handle that the next call dereferences, with the same abort. So <see cref="Load"/>
/// checks <see cref="ModelStore.VoskRequiredFiles"/> first, and refuses a path with any non-ASCII
/// character: the wrapper marshals paths as ANSI and a mangled path is a missing model.</para>
///
/// <para><b>The recogniser is recreated on <see cref="Reset"/>.</b> Vosk's word timings are
/// measured from the recogniser's creation and keep counting across results; a fresh recogniser
/// per arm keeps them aligned with <see cref="BytesFed"/>, which is how the seed is trimmed to the
/// phrase. The model, the expensive object, is loaded once.</para>
/// </summary>
public sealed class VoskWakeWordDetector : IWakeWordDetector
{
    private const string Category = "Voice";
    private const float SampleRate = 16000f;

    private readonly string _modelDirectory;
    private readonly string _phrase;
    private readonly object _gate = new();
    private readonly bool _ownsModel;

    /// <summary>What this stream's log lines are called: the microphone's are "Wake recogniser", a fork's "Echo probe recogniser".</summary>
    private readonly string _logName;

    private Model? _model;
    private VoskRecognizer? _recognizer;
    private long _bytesFed;
    private bool _disposed;
    private bool _failureLogged;
    private string _lastHeard = "";

    /// <param name="modelDirectory">The unpacked model (see <see cref="ModelStore.Vosk"/>).</param>
    /// <param name="phrase">The wake phrase; only used to warn once when a word of it is outside the model's vocabulary.</param>
    public VoskWakeWordDetector(string modelDirectory, string phrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        _modelDirectory = modelDirectory;
        _phrase = WakeWordMatch.NormalizePhrase(phrase);
        _ownsModel = true;
        _logName = "Wake recognizer";
    }

    /// <summary>A fork: its own recogniser over the parent's model, which it never frees.</summary>
    private VoskWakeWordDetector(Model model, string modelDirectory, string phrase)
    {
        _model = model;
        _modelDirectory = modelDirectory;
        _phrase = phrase;
        _ownsModel = false;
        _logName = "Echo probe recognizer";
        _recognizer = CreateRecognizer(model);
    }

    /// <summary>Whether this instance loaded the model itself (false for a <see cref="Fork"/>).</summary>
    public bool OwnsModel => _ownsModel;

    /// <summary>
    /// A second recogniser over the same model. Vosk's model is read-only and shared between
    /// recognisers by design (its server runs one model and a recogniser per connection); each
    /// fork has its own lock, so the probe's thread and the capture thread never wait on each
    /// other. The fork must be disposed before the parent frees the model: a recogniser over
    /// released weights aborts the process on its next call.
    /// </summary>
    public IWakeWordDetector Fork()
    {
        lock (_gate)
        {
            if (_disposed || _model is not { } model)
            {
                throw new InvalidOperationException("The wake-word model is not loaded.");
            }

            return new VoskWakeWordDetector(model, _modelDirectory, _phrase);
        }
    }

    public string ModelDirectory => _modelDirectory;

    public bool IsLoaded => _recognizer is not null;

    public long BytesFed => Volatile.Read(ref _bytesFed);

    /// <summary>The detail for a path the native library cannot open. Pinned.</summary>
    public static string NonAsciiPathDetail(string path) =>
        $"model path has non-ASCII characters, which the Vosk library cannot open: {path}; set {EnvironmentOverrides.HomeVariable} to an ASCII path";

    public static string IncompleteDirectoryDetail(string path) => $"model directory missing or incomplete: {path}";

    public static bool IsAsciiPath(string path)
    {
        foreach (char c in path)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }

    public bool Load(out string detail)
    {
        if (_disposed)
        {
            detail = "disposed";
            return false;
        }

        if (_recognizer is not null)
        {
            detail = "already loaded";
            return true;
        }

        if (!IsAsciiPath(_modelDirectory))
        {
            detail = NonAsciiPathDetail(_modelDirectory);
            return false;
        }

        if (!ModelStore.IsCompleteModelDirectory(_modelDirectory, ModelStore.VoskRequiredFiles))
        {
            detail = IncompleteDirectoryDetail(_modelDirectory);
            return false;
        }

        try
        {
            try
            {
                Vosk.Vosk.SetLogLevel(-1);
            }
            catch (Exception ex) when (ex is not DllNotFoundException)
            {
                // Some native builds throw here; the log level is cosmetic.
            }

            lock (_gate)
            {
                _model = new Model(_modelDirectory);
                _recognizer = CreateRecognizer(_model);
                _bytesFed = 0;
            }

            WarnAboutVocabulary();
            detail = $"loaded {Path.GetFileName(_modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Unload();
            detail = "native runtime missing: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Unload();
            detail = Assistant.Explain(ex);
            return false;
        }
    }

    /// <summary>The mode of the current stream; <see cref="WakeDetectorMode.Utterance"/> after <see cref="Load"/> and <see cref="Reset()"/>.</summary>
    public WakeDetectorMode Mode { get; private set; }

    /// <summary>The grammar the current stream was created with; null in utterance mode (open vocabulary).</summary>
    private string? _grammar;

    /// <summary>The last partial text reported, so a partial repeated twenty times a second is returned once.</summary>
    private string _lastPartial = "";

    public void Reset() => Reset(WakeDetectorMode.Utterance, "");

    public void Reset(WakeDetectorMode mode, string phrase)
    {
        lock (_gate)
        {
            _bytesFed = 0;
            _lastHeard = "";
            _lastPartial = "";
            Mode = mode;
            _grammar = mode == WakeDetectorMode.Keyword ? KeywordGrammar(phrase) : null;
            if (_disposed || _model is null)
            {
                return;
            }

            try
            {
                _recognizer?.Dispose();
                _recognizer = CreateRecognizer(_model, _grammar);
            }
            catch (Exception ex)
            {
                _recognizer = null;
                DiagnosticLog.Warn(Category, "Wake-word recognizer could not be reset: " + Assistant.Explain(ex), ex);
            }
        }
    }

    public WakeUtterance? Feed(byte[] pcm, int count)
    {
        if (pcm is null || count <= 0)
        {
            return null;
        }

        count = Math.Min(count, pcm.Length);
        try
        {
            string? json;
            bool final;
            lock (_gate)
            {
                if (_disposed || _recognizer is not { } recognizer)
                {
                    return null;
                }

                // The length is count, never the array's: the capture reuses a fixed buffer.
                final = recognizer.AcceptWaveform(pcm, count);
                _bytesFed += count;
                json = final ? recognizer.Result() : Mode == WakeDetectorMode.Keyword ? recognizer.PartialResult() : null;
            }

            if (json is null)
            {
                return null;
            }

            if (!final)
            {
                // Keyword mode: the interim text, once per change. Under the speakers this is
                // the only result that ever arrives before Vosk's utterance cap.
                string? partial = ParsePartial(json);
                if (partial is null || string.Equals(partial, _lastPartial, StringComparison.Ordinal))
                {
                    return null;
                }

                _lastPartial = partial;
                DiagnosticLog.Debug(Category, _logName + " heard (partial): " + partial);
                return new WakeUtterance(partial, Array.Empty<WakeWordTiming>(), IsPartial: true);
            }

            _lastPartial = "";
            var utterance = ParseResult(json);
            if (utterance is null)
            {
                return null;
            }

            if (!string.Equals(utterance.Text, _lastHeard, StringComparison.Ordinal))
            {
                _lastHeard = utterance.Text;
                DiagnosticLog.Debug(Category, _logName + " heard: " + utterance.Text);
            }

            return utterance;
        }
        catch (Exception ex)
        {
            // Fires twenty times a second: the first failure is news, the rest are the same news.
            if (!_failureLogged)
            {
                _failureLogged = true;
                DiagnosticLog.Error(Category, "Wake-word recognition failed: " + Assistant.Explain(ex) + " Further occurrences are suppressed.", ex);
            }

            return null;
        }
    }

    /// <summary>
    /// Vosk's final result: <c>{"text": "...", "result": [{"word","start","end","conf"}, ...]}</c>.
    /// Null for empty text (silence finalises as <c>""</c>) or unparsable JSON; the timings are
    /// optional. Public so the shape is pinned without a model.
    /// </summary>
    public static WakeUtterance? ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string text = (textElement.GetString() ?? "").Trim();
            if (text.Length == 0)
            {
                return null;
            }

            var words = new List<WakeWordTiming>();
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in result.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("word", out var word) && word.ValueKind == JsonValueKind.String
                        && entry.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number
                        && entry.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Number)
                    {
                        words.Add(new WakeWordTiming(word.GetString() ?? "", TimeSpan.FromSeconds(start.GetDouble()), TimeSpan.FromSeconds(end.GetDouble())));
                    }
                }
            }

            return new WakeUtterance(text, words);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Unload();
        }
    }

    /// <summary>
    /// Vosk's partial result: <c>{"partial": "..."}</c>. The trimmed text, or null when empty or
    /// unparsable. Public so the shape is pinned without a model.
    /// </summary>
    public static string? ParsePartial(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("partial", out var partial) || partial.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string text = (partial.GetString() ?? "").Trim();
            return text.Length == 0 ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The keyword grammar for <paramref name="phrase"/>: a JSON array of the normalised phrase
    /// and Vosk's unknown-word token, <c>["neon","[unk]"]</c>, so everything that is not the phrase
    /// decodes as <c>[unk]</c>. Written with <see cref="Utf8JsonWriter"/>, never by hand. Pinned.
    /// </summary>
    public static string KeywordGrammar(string phrase)
    {
        string normalised = WakeWordMatch.NormalizePhrase(phrase ?? "");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            if (normalised.Length > 0)
            {
                writer.WriteStringValue(normalised);
            }

            writer.WriteStringValue("[unk]");
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static VoskRecognizer CreateRecognizer(Model model, string? grammar = null)
    {
        var recognizer = grammar is null ? new VoskRecognizer(model, SampleRate) : new VoskRecognizer(model, SampleRate, grammar);
        recognizer.SetMaxAlternatives(0);
        recognizer.SetWords(true);
        return recognizer;
    }

    /// <summary>A phrase word the model has no entry for can never be heard; say so once rather than let the feature look broken.</summary>
    private void WarnAboutVocabulary()
    {
        if (_model is not { } model || _phrase.Length == 0)
        {
            return;
        }

        foreach (var word in _phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int index;
            try
            {
                index = model.FindWord(word);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Debug(Category, "Vocabulary check failed: " + ex.Message);
                return;
            }

            if (index < 0)
            {
                DiagnosticLog.Warn(Category, string.Create(CultureInfo.InvariantCulture, $"Wake phrase word \"{word}\" is not in the Vosk model's vocabulary; the phrase will never be heard."));
            }
        }
    }

    // Recogniser before model: the recogniser holds the model, and freeing the model first would
    // leave it pointing at released weights. Caller holds the gate.
    private void Unload()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        if (_ownsModel)
        {
            _model?.Dispose();
        }

        _model = null;
    }
}
