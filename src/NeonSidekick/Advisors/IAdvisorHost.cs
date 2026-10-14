using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Advisors;

/// <summary>An advisor's profile as a call reads it (2026-10-10): its saved settings and its persona (null for the default).</summary>
public sealed record AdvisorProfile(string Name, AppSettingsData Settings, string? Persona);

/// <summary>
/// An advisor's way to its model (2026-10-10): <see cref="Create"/> makes an assistant over it for a history, <see cref="Model"/> names
/// the model it answers on, and <see cref="Warning"/> says when that is not the one its profile named (<c>parent-server</c>).
/// <see cref="Session"/>: the server keeps the conversation (the Claude Code server), so the assistant names the advisor's thread in
/// <see cref="Assistant.ConversationId"/>. (A <c>Tools</c> flag, false for a Claude Code advisor answering text-only, went on 2026-10-13
/// when every advisor there got a process of its own.)
/// </summary>
public sealed record AdvisorLink(Func<ConversationHistory, Assistant> Create, string Model, string? Warning = null, bool Session = false);

/// <summary>
/// What <c>neon_advisor</c> needs of the app (2026-10-10): an advisor's profile read without loading it, and a link to its own
/// server and model, kept between calls. The app's is <c>App.AdvisorHost</c> over the session's links; the tests' a fake.
/// </summary>
public interface IAdvisorHost
{
    /// <summary>The profile <paramref name="name"/> of this home, read and not loaded; null when it does not exist. A corrupt file throws.</summary>
    AdvisorProfile? Read(string name);

    /// <summary>A link to <paramref name="profile"/>'s server and model, or the problem in a sentence; reused while nothing it rests on changed.</summary>
    Task<(AdvisorLink? Link, string? Problem)> LinkAsync(AdvisorProfile profile, CancellationToken cancellationToken);

    /// <summary>The link of <paramref name="profile"/> dropped (its turn failed): the next call links anew.</summary>
    void Forget(string profile);
}

/// <summary>
/// An advisor call on the transcript as it runs (2026-10-10, the shape of the Claude Code advisor tool's view, retired later that day).
/// <c>avatar</c> is the advisor profile's (<see cref="Emojis.AvatarRules.Normalize"/>, "" for none), for its question line and footer.
/// </summary>
public interface IAdvisorView
{
    void Began(AdvisorEntry advisor, string question, string avatar);

    void Tool(AdvisorEntry advisor, string name, string detail);

    void Answered(AdvisorEntry advisor, string answer, string model, TokenUsage usage, string? warning, string avatar);
}
