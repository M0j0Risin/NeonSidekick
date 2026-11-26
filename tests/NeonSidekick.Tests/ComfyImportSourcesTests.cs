using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using NeonSidekick.Comfy;

namespace NeonSidekick.Tests;

/// <summary>
/// The import's other sources (2026-11-25, the workflow manager): the graph a ComfyUI PNG or WebP carries (<see cref="ComfyEmbeddedGraph"/>),
/// the server's recent runs (<see cref="ComfyHistory"/>), and the older saves' <c>NaN</c> and <c>is_changed</c>.
/// </summary>
public sealed class ComfyImportSourcesTests
{
    /// <summary>A small text → image graph as ComfyUI runs it (made-up names).</summary>
    internal const string Graph = """
        {"4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"model.safetensors"}},
         "6":{"class_type":"CLIPTextEncode","inputs":{"text":"a lighthouse at dusk","clip":["4",1]}},
         "7":{"class_type":"CLIPTextEncode","inputs":{"text":"blurry","clip":["4",1]}},
         "5":{"class_type":"EmptyLatentImage","inputs":{"width":832,"height":1216,"batch_size":1}},
         "3":{"class_type":"KSampler","inputs":{"seed":5,"steps":24,"cfg":6.5,"sampler_name":"euler","scheduler":"normal","denoise":1,"model":["4",0],"positive":["6",0],"negative":["7",0],"latent_image":["5",0]}},
         "8":{"class_type":"VAEDecode","inputs":{"samples":["3",0],"vae":["4",2]}},
         "9":{"class_type":"SaveImage","inputs":{"filename_prefix":"ComfyUI","images":["8",0]}}}
        """;

    // ── Pictures ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk;   // the CRC is never read
    }

    internal static byte[] Png(params byte[][] chunks) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", new byte[13]), .. chunks.SelectMany(c => c), .. Chunk("IEND", [])];

    internal static byte[] Text(string key, string value) => [.. Encoding.Latin1.GetBytes(key), 0, .. Encoding.Latin1.GetBytes(value)];

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    [Fact]
    public void APng_GivesItsPromptGraph_FromEachChunkKind()
    {
        byte[] json = Encoding.UTF8.GetBytes(Graph);
        Assert.Equal(Graph, ComfyEmbeddedGraph.Read(Png(Chunk("tEXt", Text("workflow", "{\"nodes\":[]}")), Chunk("tEXt", Text("prompt", Graph)))).Json);
        Assert.Equal(Graph, ComfyEmbeddedGraph.Read(Png(Chunk("zTXt", [.. Encoding.Latin1.GetBytes("prompt"), 0, 0, .. Zlib(json)]))).Json);
        Assert.Equal(Graph, ComfyEmbeddedGraph.Read(Png(Chunk("iTXt", [.. Encoding.Latin1.GetBytes("prompt"), 0, 0, 0, 0, 0, .. json]))).Json);
        Assert.Equal(Graph, ComfyEmbeddedGraph.Read(Png(Chunk("iTXt", [.. Encoding.Latin1.GetBytes("prompt"), 0, 1, 0, 0, 0, .. Zlib(json)]))).Json);
        Assert.Equal(Graph, ComfyEmbeddedGraph.Read(Png(Chunk("IDAT", [1, 2, 3]), Chunk("comf", Text("prompt", Graph)))).Json);   // an animated PNG's, after the image data
        Assert.True(ComfyEmbeddedGraph.IsPicture(Png()));
    }

    [Fact]
    public void APng_WithTheUiGraphAlone_OrNothing_OrBrokenChunks_SaysSo()
    {
        Assert.Equal((null, ComfyText.EmbeddedUiOnly), ComfyEmbeddedGraph.Read(Png(Chunk("tEXt", Text("workflow", "{\"nodes\":[]}")))));
        Assert.Equal((null, ComfyText.EmbeddedNone), ComfyEmbeddedGraph.Read(Png()));
        byte[] whole = Png(Chunk("tEXt", Text("prompt", Graph)));
        Assert.Equal((null, ComfyText.EmbeddedNone), ComfyEmbeddedGraph.Read(whole[..(whole.Length - 40)]));   // truncated inside the chunk
        var bomb = Zlib(new byte[ComfyEmbeddedGraph.MaxTextBytes + 1024]);
        Assert.Equal((null, ComfyText.EmbeddedNone), ComfyEmbeddedGraph.Read(Png(Chunk("zTXt", [.. Encoding.Latin1.GetBytes("prompt"), 0, 0, .. bomb]))));
        Assert.Equal((null, ComfyText.EmbeddedNone), ComfyEmbeddedGraph.Read(Png(Chunk("zTXt", [.. Encoding.Latin1.GetBytes("prompt"), 0, 0, 1, 2, 3]))));
        Assert.False(ComfyEmbeddedGraph.IsPicture(Encoding.UTF8.GetBytes(Graph)));
    }

    /// <summary>A WebP whose EXIF holds the tags as ComfyUI writes them: 0x0110 <c>prompt:…</c>, 0x010F <c>workflow:…</c>.</summary>
    private static byte[] Webp(bool bigEndian, bool exifHeader, params (ushort Tag, string Text)[] tags)
    {
        var values = tags.Select(t => Encoding.UTF8.GetBytes(t.Text + "\0")).ToList();
        int ifd = 8;
        int data = ifd + 2 + (tags.Length * 12) + 4;
        var tiff = new byte[data + values.Sum(v => v.Length)];
        void U16(int at, int v) { if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), (ushort)v); }
        void U32(int at, int v) { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at), (uint)v); }
        Encoding.ASCII.GetBytes(bigEndian ? "MM" : "II").CopyTo(tiff, 0);
        U16(2, 42);
        U32(4, ifd);
        U16(ifd, tags.Length);
        int at = data;
        for (int i = 0; i < tags.Length; i++)
        {
            int entry = ifd + 2 + (i * 12);
            U16(entry, tags[i].Tag);
            U16(entry + 2, 2);
            U32(entry + 4, values[i].Length);
            U32(entry + 8, at);
            values[i].CopyTo(tiff, at);
            at += values[i].Length;
        }

        byte[] exif = exifHeader ? [.. "Exif\0\0"u8, .. tiff] : tiff;
        static byte[] RiffChunk(string type, byte[] body)
        {
            var c = new byte[8 + body.Length + (body.Length & 1)];
            Encoding.ASCII.GetBytes(type).CopyTo(c, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(4), (uint)body.Length);
            body.CopyTo(c, 8);
            return c;
        }

        byte[] chunks = [.. RiffChunk("VP8 ", new byte[11]), .. RiffChunk("EXIF", exif)];
        var riff = new byte[12 + chunks.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(riff, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)(4 + chunks.Length));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(riff, 8);
        chunks.CopyTo(riff, 12);
        return riff;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void AWebp_GivesItsPromptGraph_InEitherByteOrder(bool bigEndian, bool exifHeader)
    {
        var webp = Webp(bigEndian, exifHeader, (0x010F, "workflow:{\"nodes\":[]}"), (0x0110, "prompt:" + Graph));

        Assert.True(ComfyEmbeddedGraph.IsPicture(webp));
        Assert.Equal(Graph, ComfyEmbeddedGraph.Read(webp).Json);
        Assert.Equal((null, ComfyText.EmbeddedUiOnly), ComfyEmbeddedGraph.Read(Webp(bigEndian, exifHeader, (0x010F, "workflow:{\"nodes\":[]}"))));
        Assert.Equal((null, ComfyText.EmbeddedNone), ComfyEmbeddedGraph.Read(webp[..40]));
    }

    // ── NaN and is_changed ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NumbersAsNull_ReadsTheBareWordsOnly()
    {
        Assert.Equal("""{"a":null,"b":[null,-1],"c":"NaN and Infinity","d":null,"e":"a\"NaN"}""", ComfyImport.NumbersAsNull("""{"a":NaN,"b":[Infinity,-1],"c":"NaN and Infinity","d":-Infinity,"e":"a\"NaN"}"""));
        Assert.Equal("{\"x\":1}", ComfyImport.NumbersAsNull("{\"x\":1}"));
    }

    [Fact]
    public void Placehold_TakesAGraphWithNaN_AndDropsIsChanged_AndHoldsThePrompt()
    {
        string old = Graph.Replace("\"cfg\":6.5", "\"cfg\":6.5,\"extra\":NaN", StringComparison.Ordinal)
            .Replace("\"class_type\":\"EmptyLatentImage\",", "\"class_type\":\"EmptyLatentImage\",\"is_changed\":[\"x\"],", StringComparison.Ordinal);
        var (result, problem) = ComfyImport.Placehold(old);

        Assert.Null(problem);
        Assert.DoesNotContain("is_changed", result!.GraphJson);
        Assert.Equal("a lighthouse at dusk", result.Prompt);
        Assert.Equal("blurry", result.Negative);
    }

    // ── /history ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static JsonObject Entry(long number, string graph, long? createTime = null, string client = "user-1", string status = "success", long? startMessage = null, string? output = null)
    {
        var extra = new JsonObject { ["client_id"] = client };
        if (createTime is { } t)
        {
            extra["create_time"] = t;
        }

        var messages = new JsonArray();
        if (startMessage is { } m)
        {
            messages.Add(new JsonArray("execution_start", new JsonObject { ["timestamp"] = m }));
        }

        var outputs = new JsonObject();
        if (output is not null)
        {
            outputs["9"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["filename"] = output, ["subfolder"] = "", ["type"] = "output" }) };
        }

        return new JsonObject
        {
            ["prompt"] = new JsonArray(number, "id", JsonNode.Parse(graph), extra, new JsonArray("9")),
            ["outputs"] = outputs,
            ["status"] = new JsonObject { ["status_str"] = status, ["completed"] = status == "success", ["messages"] = messages },
        };
    }

    [Fact]
    public void History_NewestFirst_FailedOut_OneRowPerWorkflow_TheAppsOwnMarked()
    {
        string other = Graph.Replace("model.safetensors", "other.safetensors", StringComparison.Ordinal);
        string reseeded = Graph.Replace("\"seed\":5", "\"seed\":99", StringComparison.Ordinal).Replace("a lighthouse", "a harbor", StringComparison.Ordinal).Replace("\"filename_prefix\":\"ComfyUI\"", "\"filename_prefix\":\"out\"", StringComparison.Ordinal);
        string own = Graph.Replace("\"filename_prefix\":\"ComfyUI\"", "\"filename_prefix\":\"neon/portrait\"", StringComparison.Ordinal).Replace("\"sampler_name\":\"euler\"", "\"sampler_name\":\"dpmpp_2m\"", StringComparison.Ordinal);   // steps alone would be a placeholder: the same workflow
        var root = new JsonObject
        {
            ["a"] = Entry(1, Graph, createTime: 1_000_000, output: "ComfyUI_00001_.png"),
            ["b"] = Entry(2, reseeded, createTime: 3_000_000, output: "out_00002_.png"),
            ["c"] = Entry(3, other, startMessage: 2_000_000),
            ["d"] = Entry(4, other, createTime: 4_000_000, status: "error"),
            ["e"] = Entry(5, own, createTime: 5_000_000, client: ComfyHistory.OwnClientPrefix + "abc"),
            ["f"] = Entry(6, "{\"nodes\":[],\"links\":[]}", createTime: 500),
            ["odd"] = 5,
            ["short"] = new JsonObject { ["prompt"] = new JsonArray(1, "x") },
        };

        var runs = ComfyHistory.Parse(root.ToJsonString());

        Assert.Equal(["e", "b", "c", "f"], runs.Select(r => r.Id));
        Assert.True(runs[0].Own);
        Assert.Equal(2, runs[1].Repeats);                      // a and b: one workflow, the newest kept
        Assert.Equal("out_00002_.png", runs[1].OutputFile);
        Assert.Equal("a harbor at dusk", runs[1].Import!.Prompt);
        Assert.Equal(2_000_000, runs[2].StartedMs);            // the execution_start message's time
        Assert.Equal("other.safetensors", runs[2].Model);
        Assert.Null(runs[3].Import);
        Assert.Equal(ComfyWorkflow.UiFormatProblem, runs[3].Problem);
        Assert.Empty(ComfyHistory.Parse("not json"));
        Assert.Empty(ComfyHistory.Parse("[]"));
    }

    [Fact]
    public void History_MarksAnOlderOwnRun_ByItsNeonPrefix()
    {
        string own = Graph.Replace("\"filename_prefix\":\"ComfyUI\"", "\"filename_prefix\":\"neon/portrait\"", StringComparison.Ordinal);
        var root = new JsonObject { ["a"] = Entry(1, own, createTime: 1, client: "0123abcd") };

        Assert.True(Assert.Single(ComfyHistory.Parse(root.ToJsonString())).Own);
    }
}
