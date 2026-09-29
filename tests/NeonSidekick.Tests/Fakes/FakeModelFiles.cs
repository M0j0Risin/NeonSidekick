using System.IO.Compression;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Writes files that pass <see cref="ModelStore.LooksLikeGgml"/> without being models, and Vosk
/// model directories (or archives) that pass <see cref="ModelStore.IsCompleteModelDirectory"/>
/// without being models, so voice sessions over fakes need no download.
/// </summary>
public static class FakeModelFiles
{
    /// <summary>The files <see cref="ModelStore.VoskRequiredFiles"/> names, with token contents, under <paramref name="directory"/>; <paramref name="omit"/> leaves some out.</summary>
    public static string WriteVoskModel(string directory, params string[] omit)
    {
        foreach (var relative in ModelStore.VoskRequiredFiles)
        {
            if (Array.IndexOf(omit, relative) >= 0)
            {
                continue;
            }

            string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fake " + relative);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The Vosk model under the app's models directory, as <see cref="ModelStore.Vosk"/> expects it.</summary>
    public static string WriteVoskModelUnder(string modelsDirectory, string name = ModelStore.VoskModelDirectoryName) =>
        WriteVoskModel(Path.Combine(modelsDirectory, name));

    /// <summary>
    /// A zip archive shaped like the one Vosk publishes: the model files under one top-level
    /// folder (<paramref name="topFolder"/>; empty puts them at the root), plus a README.
    /// <paramref name="omit"/> leaves required files out.
    /// </summary>
    public static byte[] VoskZip(string topFolder = ModelStore.VoskModelDirectoryName, params string[] omit)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            string prefix = topFolder.Length == 0 ? "" : topFolder + "/";
            AddEntry(zip, prefix + "README", "fake vosk model");
            foreach (var relative in ModelStore.VoskRequiredFiles)
            {
                if (Array.IndexOf(omit, relative) < 0)
                {
                    AddEntry(zip, prefix + relative, "fake " + relative);
                }
            }
        }

        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    /// <summary>The ggml magic followed by a little padding.</summary>
    public static byte[] GgmlBytes(int padding = 64)
    {
        var bytes = new byte[4 + padding];
        bytes[0] = 0x6C;
        bytes[1] = 0x6D;
        bytes[2] = 0x67;
        bytes[3] = 0x67;
        return bytes;
    }

    /// <summary>The first bytes of a real kokoro.onnx export (<c>ir_version</c> 9, <c>producer_name</c> "pytorch") followed by zeros — passes <see cref="ModelStore.LooksLikeOnnx"/>, is not a model.</summary>
    public static byte[] OnnxBytes(int padding = 64)
    {
        var bytes = new byte[11 + padding];
        new byte[] { 0x08, 0x09, 0x12, 0x07, (byte)'p', (byte)'y', (byte)'t', (byte)'o', (byte)'r', (byte)'c', (byte)'h' }.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>The GGUF magic followed by <paramref name="length"/> − 4 bytes of a counting pattern (so a truncated or reordered copy never matches the whole).</summary>
    public static byte[] GgufBytes(int length = 4096)
    {
        var bytes = new byte[Math.Max(4, length)];
        for (int i = 4; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 31 + 7);
        }

        bytes[0] = (byte)'G';
        bytes[1] = (byte)'G';
        bytes[2] = (byte)'U';
        bytes[3] = (byte)'F';
        return bytes;
    }

    /// <summary>A zip archive holding <paramref name="entries"/> (relative name → text) at its root.</summary>
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                AddEntry(zip, name, content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>The lowercase hex SHA-256 of <paramref name="bytes"/>, as a pinned spec carries it.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>Writes <see cref="OnnxBytes"/> at <paramref name="path"/>.</summary>
    public static string WriteOnnx(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, OnnxBytes());
        return path;
    }

    public static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, GgmlBytes());
        return path;
    }

    /// <summary>Both files a ready voice session needs, under <paramref name="modelsDirectory"/>, for the named Whisper model.</summary>
    public static void WriteBoth(string modelsDirectory, string whisperModel = "ggml-base.en.bin")
    {
        Write(Path.Combine(modelsDirectory, whisperModel));
        Write(Path.Combine(modelsDirectory, ModelStore.SileroFileName));
    }
}
