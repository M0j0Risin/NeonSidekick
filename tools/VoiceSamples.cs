#:package KokoroSharp@0.8.0

// VoiceSamples: speaks one line in every voice preset under assets/voices and writes the samples and the atlas,
// so a preset can be heard before it is copied into the home's voices folder.
//
//     assets/voices/samples/<folder>/<name>.wav    one per preset: mono 16-bit, 16 kHz (Kokoro's 24 kHz resampled, to keep the repo light)
//     assets/voices/Voice Atlas.html               Theme Atlas.html's sibling: every preset, its voices, mix, speed, description and sample
//
// A .NET 10 file-based app: no csproj, not in the solution, not run by build.ps1. The output is committed; run this when a
// preset is added or changed, from the repository root:
//
//     dotnet run tools/VoiceSamples.cs                               # every preset, then the atlas
//     dotnet run tools/VoiceSamples.cs -- --only lucia,mateo          # those presets' samples again, then the atlas
//     dotnet run tools/VoiceSamples.cs -- --atlas-only                # the atlas alone (a description edited)
//     dotnet run tools/VoiceSamples.cs -- --model <kokoro.onnx> --content <dir with voices\ and espeak\> --assets <assets\voices>
//
// The model is the one the app downloads (%USERPROFILE%\.neonsidekick\models\kokoro.onnx); the voices and espeak-ng come from
// the KokoroSharp package in the NuGet cache, the same files the app ships. A preset sounds as the app plays it: the blend is
// VoiceMix.Spec's rule and KokoroInProcessSynthesizer.TryResolveVoice's mix (src/NeonSidekick/Speech), the model is opened by
// its absolute path and KokoroSharp's own playback is never created. Keep the package version in step with the csproj's.

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.ML.OnnxRuntime;

const int SourceRate = 24_000;
const int TargetRate = 16_000;
const string BuiltIn = "built-in";
const string SamplesFolder = "samples";
string[] folderOrder = [BuiltIn, "narrators", "brisk", "british", "characters", "duets", "accents"];

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string modelPath = Path.GetFullPath(Arg("--model") ?? Path.Combine(home, ".neonsidekick", "models", "kokoro.onnx"));
string content = Path.GetFullPath(Arg("--content") ?? Path.Combine(home, ".nuget", "packages", "kokorosharp", "0.8.0", "content"));
string assets = Path.GetFullPath(Arg("--assets") ?? Path.Combine("assets", "voices"));
var only = (Arg("--only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
bool atlasOnly = args.Contains("--atlas-only");

var presets = ReadPresets(assets, folderOrder);
Console.WriteLine($"{presets.Count} presets under {assets}");

if (!atlasOnly)
{
    if (!File.Exists(modelPath))
    {
        Console.Error.WriteLine($"No model at {modelPath}; run the app once with TTS in-process, or pass --model.");
        return 1;
    }

    if (!Directory.Exists(Path.Combine(content, "voices")) || !Directory.Exists(Path.Combine(content, "espeak")))
    {
        Console.Error.WriteLine($"No voices\\ and espeak\\ under {content}; restore the app once (dotnet restore), or pass --content.");
        return 1;
    }

    Tokenizer.eSpeakNGPath = Path.Combine(content, "espeak");
    using var options = new SessionOptions();
    using var engine = new KokoroWavSynthesizer(modelPath, options);
    var voices = new Dictionary<string, KokoroVoice>(StringComparer.Ordinal);
    foreach (var preset in presets.Where(p => only.Count == 0 || only.Contains(p.Name)))
    {
        var embedding = Resolve(preset);
        string line = $"Hi, I'm {char.ToUpperInvariant(preset.Name[0])}{preset.Name[1..]}. This is how I sound reading a reply out loud.";
        byte[] bytes = await engine.SynthesizeAsync(line, embedding, new KokoroTTSPipelineConfig { Speed = (float)preset.Speed });
        var pcm = Resample(Payload(bytes), SourceRate, TargetRate);
        string path = Path.Combine(assets, SamplesFolder, preset.Folder, preset.Name + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Wav(pcm, TargetRate));
        Console.WriteLine($"  {preset.Folder}/{preset.Name}: {Spec(preset)} at {preset.Speed.ToString(CultureInfo.InvariantCulture)} → {pcm.Length / 2 / (double)TargetRate:0.0} s");
    }

    // The blend as the app makes it: one voice, or the two mixed by KokoroSharp (weights normalised there).
    KokoroVoice Resolve(Preset preset)
    {
        var parts = Parts(preset);
        var weighted = parts.Select(p => (Load(p.Name), (float)p.Percent)).ToArray();
        return weighted.Length == 1 ? weighted[0].Item1 : KokoroVoiceManager.Mix(weighted);
    }

    KokoroVoice Load(string name)
    {
        if (!voices.TryGetValue(name, out var voice))
        {
            string file = Path.Combine(content, "voices", name + ".npy");
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"No voice {name} at {file}.");
            }

            voices[name] = voice = KokoroVoice.FromPath(file);
        }

        return voice;
    }
}

string atlas = Path.Combine(assets, "Voice Atlas.html");
File.WriteAllText(atlas, Atlas(presets, folderOrder), new UTF8Encoding(false));
Console.WriteLine($"Wrote {atlas}");
return 0;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Every preset file: the built-ins first in the picker's order, then each category folder in the collection's order, by name.
static List<Preset> ReadPresets(string assets, string[] order)
{
    string[] builtInOrder = ["amanda", "neon", "richard", "hunter", "larry", "jack", "willow"];
    var folders = Directory.GetDirectories(assets).Select(Path.GetFileName).OfType<string>()
        .Where(f => f != SamplesFolder)
        .OrderBy(f => Array.IndexOf(order, f) is var i && i < 0 ? int.MaxValue : i).ThenBy(f => f, StringComparer.Ordinal);
    var list = new List<Preset>();
    foreach (string folder in folders)
    {
        var files = Directory.GetFiles(Path.Combine(assets, folder), "*.json").Select(f => (Name: Path.GetFileNameWithoutExtension(f), Path: f));
        files = folder == BuiltIn
            ? files.OrderBy(f => Array.IndexOf(builtInOrder, f.Name) is var i && i < 0 ? int.MaxValue : i)
            : files.OrderBy(f => f.Name, StringComparer.Ordinal);
        foreach (var (name, path) in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            list.Add(new Preset(folder, name,
                root.GetProperty("TtsVoice").GetString()!.Trim(),
                root.TryGetProperty("TtsVoice2", out var second) && second.ValueKind == JsonValueKind.String ? second.GetString()!.Trim() : "",
                root.GetProperty("TtsVoiceMix").GetInt32(),
                root.GetProperty("TtsSpeed").GetDouble(),
                root.TryGetProperty("Description", out var description) ? description.GetString() ?? "" : ""));
        }
    }

    return list;
}

// VoiceMix.Spec's rule: the primary alone when the second is blank, the same voice or the mix 100+; the second alone at 0-; else both.
static List<(string Name, int Percent)> Parts(Preset p)
{
    if (p.Voice2.Length == 0 || p.Voice2 == p.Voice || p.Mix >= 100)
    {
        return [(p.Voice, 100)];
    }

    return p.Mix <= 0 ? [(p.Voice2, 100)] : [(p.Voice, p.Mix), (p.Voice2, 100 - p.Mix)];
}

static string Spec(Preset p) => string.Join(" + ", Parts(p).Select(x => x.Percent == 100 ? x.Name : $"{x.Name} {x.Percent}%"));

// KokoroSharp 0.8.0 hands back raw 16-bit PCM despite the class's name; a RIFF header, if a later version adds one, is walked past (WavBytes.Payload).
static byte[] Payload(byte[] bytes)
{
    if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
    {
        return bytes;
    }

    int offset = 12;
    while (offset + 8 <= bytes.Length)
    {
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
        bool data = bytes.AsSpan(offset, 4).SequenceEqual("data"u8);
        offset += 8;
        if (data)
        {
            return bytes.AsSpan(offset, Math.Min(length, bytes.Length - offset)).ToArray();
        }

        offset += length + (length & 1);
    }

    return bytes;
}

// 16-bit mono from one rate to another: a short box filter against aliasing, then linear interpolation.
static byte[] Resample(byte[] pcm, int from, int to)
{
    int count = pcm.Length / 2;
    var source = new short[count];
    for (int i = 0; i < count; i++)
    {
        source[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));
    }

    var smoothed = new double[count];
    for (int i = 0; i < count; i++)
    {
        int a = Math.Max(0, i - 1), b = Math.Min(count - 1, i + 1);
        smoothed[i] = (source[a] + 2.0 * source[i] + source[b]) / 4.0;
    }

    int outCount = (int)((long)count * to / from);
    var output = new byte[outCount * 2];
    for (int j = 0; j < outCount; j++)
    {
        double position = (double)j * from / to;
        int i = (int)position;
        double t = position - i;
        double value = i + 1 < count ? smoothed[i] * (1 - t) + smoothed[i + 1] * t : smoothed[Math.Min(i, count - 1)];
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(j * 2, 2), (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
    }

    return output;
}

static byte[] Wav(byte[] pcm, int rate)
{
    var bytes = new byte[44 + pcm.Length];
    var span = bytes.AsSpan();
    "RIFF"u8.CopyTo(span);
    BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + pcm.Length));
    "WAVEfmt "u8.CopyTo(span[8..]);
    BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
    BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)rate);
    BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(rate * 2));
    BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
    BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
    "data"u8.CopyTo(span[36..]);
    BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)pcm.Length);
    pcm.CopyTo(span[44..]);
    return bytes;
}

// The atlas: one dark page in Theme Atlas.html's look, a filter per folder, a card per preset with its sample.
static string Atlas(List<Preset> presets, string[] order)
{
    static string E(string s) => WebUtility.HtmlEncode(s);
    var groups = presets.GroupBy(p => p.Folder).ToList();
    var sb = new StringBuilder();
    sb.Append("""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Sidekick Voice Atlas</title>
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
        <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;600&family=IBM+Plex+Sans:wght@400;500;600&family=Martian+Mono:wght@600;800&display=swap">
        <style>
        /* Written by tools/VoiceSamples.cs; edit the presets and rerun it rather than this file. */
        :root {
          --page: #0c0d10; --panel: #14161b; --line: #262a32; --ink: #e8e9ed; --muted: #9298a4; --accent: #ff2e97;
          --display: "Martian Mono", "Cascadia Mono", Consolas, monospace;
          --body: "IBM Plex Sans", "Segoe UI", system-ui, sans-serif;
          --mono: "IBM Plex Mono", "Cascadia Mono", Consolas, monospace;
          color-scheme: dark;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--page); color: var(--ink); font: 15px/1.55 var(--body); }
        .wrap { max-width: 1400px; margin: 0 auto; padding: 28px 20px 64px; display: grid; gap: 28px; }
        header { display: grid; gap: 10px; }
        .eyebrow { font: 500 12px/1 var(--mono); letter-spacing: .08em; text-transform: uppercase; color: var(--muted); }
        h1 { margin: 0; font: 800 clamp(28px, 4.2vw, 46px)/1.05 var(--display); letter-spacing: -.02em; }
        h1 span { color: var(--accent); }
        .lede { margin: 0; max-width: 72ch; color: var(--muted); }
        .lede code, .card code { font-family: var(--mono); color: var(--ink); font-size: .92em; }
        .filters { display: flex; flex-wrap: wrap; gap: 8px; }
        .filters button { font: 500 13px/1 var(--body); color: var(--muted); background: var(--panel); border: 1px solid var(--line); border-radius: 999px; padding: 8px 14px; cursor: pointer; }
        .filters button span { font-family: var(--mono); font-size: 12px; opacity: .75; margin-left: 4px; }
        .filters button[aria-pressed="true"] { color: var(--page); background: var(--accent); border-color: var(--accent); }
        .filters button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
        section { display: grid; gap: 14px; }
        h2 { margin: 0; font: 600 18px/1.2 var(--display); }
        h2 small { font: 400 13px var(--mono); color: var(--muted); margin-left: 8px; }
        .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 14px; }
        .card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 14px 16px; display: grid; gap: 8px; min-width: 0; }
        .card h3 { margin: 0; font: 600 17px/1.2 var(--mono); color: var(--accent); }
        .card p { margin: 0; color: var(--muted); }
        .mix { font: 13px/1.4 var(--mono); color: var(--ink); overflow-wrap: anywhere; }
        .bar { height: 6px; border-radius: 3px; background: #4b5361; overflow: hidden; }
        .bar i { display: block; height: 100%; background: var(--accent); }
        audio { width: 100%; height: 34px; }
        [hidden] { display: none !important; }
        </style>
        </head>
        <body>
        <div class="wrap">
        <header>
          <div class="eyebrow">NeonSidekick · TTS voice presets</div>
          <h1>Voice <span>Atlas</span></h1>
          <p class="lede">Every preset in <code>assets/voices</code>, speaking one line at its own speed. To use one, copy its file into
          <code>%USERPROFILE%\.neonsidekick\voices</code> and pick it on the TTS tab's <code>TTS voice preset</code> row. A file named like a
          built-in replaces it. The bar is the first voice's share of the blend.</p>
        </header>
        <nav class="filters" aria-label="Folders">
        """);
    sb.Append($"  <button type=\"button\" aria-pressed=\"true\" data-folder=\"\">all<span>{presets.Count}</span></button>\n");
    foreach (var group in groups)
    {
        sb.Append($"  <button type=\"button\" aria-pressed=\"false\" data-folder=\"{E(group.Key)}\">{E(group.Key)}<span>{group.Count()}</span></button>\n");
    }

    sb.Append("</nav>\n");
    foreach (var group in groups)
    {
        sb.Append($"<section data-folder=\"{E(group.Key)}\">\n  <h2>{E(group.Key)}<small>{group.Count()} presets</small></h2>\n  <div class=\"grid\">\n");
        foreach (var p in group)
        {
            int share = Parts(p) is [var single] ? (single.Name == p.Voice ? 100 : 0) : p.Mix;
            string speed = p.Speed.ToString("0.0#", CultureInfo.InvariantCulture);
            sb.Append($"""
                    <article class="card">
                      <h3>{E(p.Name)}</h3>
                      <p>{E(p.Description)}</p>
                      <div class="mix">{E(Spec(p))} · speed {speed}</div>
                      <div class="bar" title="{share}% {E(p.Voice)}"><i style="width:{share}%"></i></div>
                      <audio controls preload="none" src="samples/{Uri.EscapeDataString(p.Folder)}/{Uri.EscapeDataString(p.Name)}.wav"></audio>
                    </article>

                """);
        }

        sb.Append("  </div>\n</section>\n");
    }

    sb.Append("""
        </div>
        <script>
        // One folder or all; and one sample at a time.
        const buttons = document.querySelectorAll('.filters button');
        buttons.forEach(b => b.addEventListener('click', () => {
          buttons.forEach(x => x.setAttribute('aria-pressed', x === b ? 'true' : 'false'));
          document.querySelectorAll('section').forEach(s => s.hidden = b.dataset.folder !== '' && s.dataset.folder !== b.dataset.folder);
        }));
        document.addEventListener('play', e => document.querySelectorAll('audio').forEach(a => { if (a !== e.target) a.pause(); }), true);
        </script>
        </body>
        </html>

        """);
    return sb.ToString();
}

record Preset(string Folder, string Name, string Voice, string Voice2, int Mix, double Speed, string Description);
