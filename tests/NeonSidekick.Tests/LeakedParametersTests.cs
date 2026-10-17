using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

/// <summary>
/// The repair of a leaked <c>&lt;/parameter=name&gt;</c> (2026-10-17): the three shapes the logs hold (2026-10-12, 2026-10-14 and the
/// toki-pona session), and what is left alone.
/// </summary>
public class LeakedParametersTests : IDisposable
{
    private static readonly JsonElement Schema = new SkillEditorTool(() => new SkillRoots("p", "g", "e"), () => false, new SkillFileAccess(TimeProvider.System)).JsonSchema;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IDictionary<string, object?> Args(params (string Name, object? Value)[] values)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }

        return dictionary;
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void TheOctober12Shape_TakesTheNameOutOfTheAction()
    {
        var sent = Args(("action", Json("\"create\\n</parameter=name>\\ncomfy-image-refinement\"")), ("name", Json("\"\"")), ("description", "d"));
        var fixedUp = LeakedParameters.Repair(sent, Schema, out var repaired);
        Assert.NotSame(sent, fixedUp);
        Assert.Equal("create", fixedUp["action"]);
        Assert.Equal("comfy-image-refinement", fixedUp["name"]);
        Assert.Equal("d", fixedUp["description"]);
        Assert.Equal(["action → name"], repaired);
        Assert.Equal("Repaired a leaked </parameter=…> in skill_editor: action → name.", LeakedParameters.LogLine("skill_editor", repaired));
    }

    [Fact]
    public void TheOctober14Shape_TheNameAlreadyTheSame_OnlyCutsTheAction()
    {
        var sent = Args(("action", "create\n</parameter=name>\nnsfw-gen-image-qa"), ("name", "nsfw-gen-image-qa"));
        var fixedUp = LeakedParameters.Repair(sent, Schema, out var repaired);
        Assert.Equal("create", fixedUp["action"]);
        Assert.Equal("nsfw-gen-image-qa", fixedUp["name"]);
        Assert.Single(repaired);
    }

    [Fact]
    public void TheTokiPonaShape_FillsTheContent_AndAProperCloseIsDropped()
    {
        var sent = Args(("action", "write_file\n</parameter=content>\n# Toki Pona spelling\nj — like y\n</parameter>"), ("name", "toki-pona"), ("path", "references/pronunciation.txt"));
        var fixedUp = LeakedParameters.Repair(sent, Schema, out _);
        Assert.Equal("write_file", fixedUp["action"]);
        Assert.Equal("# Toki Pona spelling\nj — like y", fixedUp["content"]);
    }

    [Fact]
    public void ASecondLeak_InTheRest_IsRepairedToo()
    {
        var sent = Args(("action", "write_file\n</parameter=path>\nnotes.txt\n</parameter=content>\nhello"), ("name", "x"));
        var fixedUp = LeakedParameters.Repair(sent, Schema, out var repaired);
        Assert.Equal("write_file", fixedUp["action"]);
        Assert.Equal("notes.txt", fixedUp["path"]);
        Assert.Equal("hello", fixedUp["content"]);
        Assert.Equal(["action → path", "path → content"], repaired);
    }

    [Fact]
    public void WhatIsLeftAlone()
    {
        // A name the schema does not have, a different value already sent, a bare close, the key naming itself, a non-string value.
        foreach (var sent in new[]
        {
            Args(("action", "create\n</parameter=colour>\nred"), ("name", "x")),
            Args(("action", "create\n</parameter=name>\nother"), ("name", "mine")),
            Args(("action", "create"), ("instructions", "Use </parameter> in XML examples.")),
            Args(("action", "create\n</parameter=action>\nupdate")),
            Args(("action", "create"), ("replace_all", true)),
        })
        {
            Assert.Same(sent, LeakedParameters.Repair(sent, Schema, out var repaired));
            Assert.Empty(repaired);
        }
    }

    /// <summary>End to end through the shared invoke path: the call is repaired before the tool reads it, and the call itself is changed.</summary>
    [Fact]
    public async Task TheInvokePath_RepairsTheCall_BeforeTheToolRuns()
    {
        var roots = new SkillRoots(Path.Combine(_dir, "profile"), Path.Combine(_dir, "global"), Path.Combine(_dir, "external"));
        var tool = new SkillEditorTool(() => roots, () => false);
        var call = new FunctionCallContent("c1", SkillEditorTool.ToolName, Args(
            ("action", "create\n</parameter=name>\nhaiku"), ("name", ""), ("description", "Writes haiku."), ("instructions", "Three lines.")));
        var (text, _) = await Assistant.InvokeToolAsync([tool], call, CancellationToken.None);
        Assert.StartsWith("created skill 'haiku' (profile, ", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(roots.Profile, "haiku", "SKILL.md")));
        Assert.Equal("create", call.Arguments!["action"]);
        Assert.Equal("haiku", call.Arguments["name"]);
    }
}
