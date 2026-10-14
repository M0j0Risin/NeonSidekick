using NeonSidekick.Advisors;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The profile advisors' host in the tests (2026-10-10): profiles by name, each linked to its own <see cref="FakeChatClient"/>; a
/// problem in place of a link; what was linked and forgotten recorded.
/// </summary>
public sealed class FakeAdvisorHost : IAdvisorHost
{
    public Dictionary<string, AdvisorProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, FakeChatClient> Clients { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Problems { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Linked { get; } = [];

    public List<string> Forgotten { get; } = [];

    /// <summary>The system prompts each assistant was made with, in order.</summary>
    public List<string> SystemPrompts { get; } = [];

    public string? Warning { get; set; }

    /// <summary>What the links say of their server (2026-10-10, a Claude Code advisor): whether it keeps the conversation.</summary>
    public bool LinkSession { get; set; }

    public bool ThrowOnRead { get; set; }

    /// <summary>A profile <paramref name="name"/> with a client of its own, answering <paramref name="persona"/>'s way.</summary>
    public FakeChatClient Add(string name, string? persona = null, AppSettingsData? settings = null)
    {
        Profiles[name] = new AdvisorProfile(name, settings ?? new AppSettingsData { LlmModel = name + "-model" }, persona);
        var client = new FakeChatClient();
        Clients[name] = client;
        return client;
    }

    public AdvisorProfile? Read(string name)
    {
        if (ThrowOnRead)
        {
            throw new System.Text.Json.JsonException("bad file");
        }

        return Profiles.GetValueOrDefault(name);
    }

    public Task<(AdvisorLink? Link, string? Problem)> LinkAsync(AdvisorProfile profile, CancellationToken cancellationToken)
    {
        Linked.Add(profile.Name);
        if (Problems.TryGetValue(profile.Name, out var problem))
        {
            return Task.FromResult<(AdvisorLink?, string?)>((null, problem));
        }

        var client = Clients[profile.Name];
        var link = new AdvisorLink(history =>
        {
            SystemPrompts.Add(history.SystemPrompt);
            return new Assistant(client, history, LlmTimeouts.Default);
        }, profile.Settings.LlmModel, Warning, LinkSession);
        return Task.FromResult<(AdvisorLink?, string?)>((link, null));
    }

    public void Forget(string profile) => Forgotten.Add(profile);
}
