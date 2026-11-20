using System.Text;
using NeonSidekick.Computers;
using NeonSidekick.Mcp;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="ConnectionsFileEdit.SetEntryString"/> (2026-11-20, plan idempotent-mixing-ripple): a server's <c>ask</c> written into
/// <c>mcp.json</c> from <c>/mcp</c> › Servers, replaced or inserted in the entry's own layout, every other byte kept, steps matched as the MCP
/// reader binds them (ordinal).
/// </summary>
public sealed class McpAskPickerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly IDisposable _cache = ConfigCache.Scope();

    public McpAskPickerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _cache.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string json, Encoding? encoding = null)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private static string? Set(string path, string name, string value) =>
        ConnectionsFileEdit.SetEntryString(path, McpConfigFile.ServersKey, name, McpConfigFile.AskKey, value);

    private static McpAskMode AskOf(string path, string name) =>
        McpConfigFile.Load(path, seal: false).Servers.Single(s => s.Key == name).Value.AskMode;

    [Fact]
    public void AnAskThere_IsReplaced_AndNothingElse()
    {
        string path = Write("""{ "mcpServers": { "docker": { "command": "docker", "ask": "writes" } } }""");

        Assert.Null(Set(path, "docker", "always"));

        Assert.Equal("""{ "mcpServers": { "docker": { "command": "docker", "ask": "always" } } }""", File.ReadAllText(path));
        Assert.Equal(McpAskMode.Always, AskOf(path, "docker"));
    }

    [Fact]
    public void AMultiLineEntry_TakesTheKeyOnItsOwnLine_IndentedAsTheLastPropertysName_AfterItsArray()
    {
        const string before = """
            {
              // the work servers
              "mcpServers": {
                "MCP_DOCKER": {
                  "command": "docker",
                  "args": [
                    "mcp",
                    "gateway"
                  ]
                }
              }
            }
            """;
        string path = Write(before.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.Null(Set(path, "MCP_DOCKER", "always"));

        string expected = before.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("      ]\n", "      ],\n      \"ask\": \"always\"\n", StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal(McpAskMode.Always, AskOf(path, "MCP_DOCKER"));
    }

    [Fact]
    public void ACrlfFile_KeepsItsLineBreaks()
    {
        string path = Write("{\r\n  \"mcpServers\": {\r\n    \"a\": {\r\n      \"command\": \"x\"\r\n    }\r\n  }\r\n}\r\n");

        Assert.Null(Set(path, "a", "never"));

        Assert.Equal("{\r\n  \"mcpServers\": {\r\n    \"a\": {\r\n      \"command\": \"x\",\r\n      \"ask\": \"never\"\r\n    }\r\n  }\r\n}\r\n", File.ReadAllText(path));
        Assert.Equal(McpAskMode.Never, AskOf(path, "a"));
    }

    [Fact]
    public void ATrailingComma_IsKept_AndNotDoubled()
    {
        string path = Write("{ \"mcpServers\": { \"a\": {\n  \"command\": \"x\",\n} } }");

        Assert.Null(Set(path, "a", "always"));

        Assert.Equal("{ \"mcpServers\": { \"a\": {\n  \"command\": \"x\",\n  \"ask\": \"always\"\n} } }", File.ReadAllText(path));
        Assert.Equal(McpAskMode.Always, AskOf(path, "a"));
    }

    [Fact]
    public void ACommentAfterTheLastValue_StaysWhereItIs()
    {
        string path = Write("{ \"mcpServers\": { \"a\": {\n  \"command\": \"x\" // the server\n} } }");

        Assert.Null(Set(path, "a", "always"));

        Assert.Equal("{ \"mcpServers\": { \"a\": {\n  \"command\": \"x\",\n  \"ask\": \"always\" // the server\n} } }", File.ReadAllText(path));
        Assert.Equal(McpAskMode.Always, AskOf(path, "a"));
    }

    [Fact]
    public void AnEntryOnOneLine_TakesTheKeyInline_AndAnEmptyOneInsideItsBraces()
    {
        string inline = Write("""{ "mcpServers": { "a": { "command": "x" } } }""");
        string empty = Write("""{ "mcpServers": { "a": {} } }""");

        Assert.Null(Set(inline, "a", "always"));
        Assert.Null(Set(empty, "a", "always"));

        Assert.Equal("""{ "mcpServers": { "a": { "command": "x", "ask": "always" } } }""", File.ReadAllText(inline));
        Assert.Equal("""{ "mcpServers": { "a": { "ask": "always" } } }""", File.ReadAllText(empty));
    }

    [Fact]
    public void ANonStringAsk_IsReplaced_AndAStrayCapitalAskIsLeftBesideTheNewKey()
    {
        string number = Write("""{ "mcpServers": { "a": { "command": "x", "ask": 5 } } }""");
        string capital = Write("""{ "mcpServers": { "a": { "command": "x", "Ask": "never" } } }""");

        Assert.Null(Set(number, "a", "always"));
        Assert.Null(Set(capital, "a", "always"));

        Assert.Equal("""{ "mcpServers": { "a": { "command": "x", "ask": "always" } } }""", File.ReadAllText(number));
        Assert.Equal("""{ "mcpServers": { "a": { "command": "x", "Ask": "never", "ask": "always" } } }""", File.ReadAllText(capital));
        Assert.Equal(McpAskMode.Always, AskOf(capital, "a"));   // the reader binds "ask" alone
    }

    [Fact]
    public void AMissingEntry_AndAStepGivenTwice_AreRefused_TheFileUnchanged()
    {
        string missing = Write("""{ "mcpServers": { "a": { "command": "x" } } }""");
        string twiceName = Write("""{ "mcpServers": { "a": { "command": "x" }, "a": { "command": "y" } } }""");
        string twiceKey = Write("""{ "mcpServers": { "a": { "command": "x", "ask": "never", "ask": "writes" } } }""");
        string notObject = Write("""{ "mcpServers": { "a": "x" } }""");

        Assert.Equal(ConnectionsFileEdit.EntryNotInFile, Set(missing, "b", "always"));
        Assert.Equal(ConnectionsFileEdit.EntryNotInFile, Set(missing, "A", "always"));   // ordinal, as the reader
        Assert.Equal(ConnectionsFileEdit.EntryNotInFile, Set(missing, " a", "always"));   // untrimmed
        Assert.Equal(ConnectionsFileEdit.KeyRepeated, Set(twiceName, "a", "always"));
        Assert.Equal(ConnectionsFileEdit.KeyRepeated, Set(twiceKey, "a", "always"));
        Assert.Equal(ConnectionsFileEdit.EntryNotInFile, Set(notObject, "a", "always"));
        Assert.Equal("""{ "mcpServers": { "a": { "command": "x" } } }""", File.ReadAllText(missing));
    }

    [Fact]
    public void NamesThatDifferInCaseOnly_AreTwoServers_AndEachIsWritten()
    {
        string path = Write("""{ "mcpServers": { "MCP_DOCKER": { "command": "x" }, "mcp_docker": { "command": "y" } } }""");

        Assert.Null(Set(path, "mcp_docker", "never"));

        Assert.Equal("""{ "mcpServers": { "MCP_DOCKER": { "command": "x" }, "mcp_docker": { "command": "y", "ask": "never" } } }""", File.ReadAllText(path));
        Assert.Equal(McpAskMode.Writes, AskOf(path, "MCP_DOCKER"));
        Assert.Equal(McpAskMode.Never, AskOf(path, "mcp_docker"));
    }

    [Fact]
    public void AUtf16File_StaysUtf16()
    {
        string path = Write("""{ "mcpServers": { "a": { "command": "x" } } }""", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        Assert.Null(Set(path, "a", "always"));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("""{ "mcpServers": { "a": { "command": "x", "ask": "always" } } }""", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }
}
