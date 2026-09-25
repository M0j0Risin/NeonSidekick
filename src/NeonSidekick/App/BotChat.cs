using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// <c>/botchat</c> (2026-09-24, the user's ask: "an infinite conversation between all the profiles"):
/// the pure half — who takes part, who speaks next, what each speaker is shown and told. The screen
/// (<c>ChatScreen.HandleBotChatAsync</c>) owns the loop, the keys, the speech and the session.
///
/// <para>The user's calls, the same day: the profiles named on the command, or every profile when none
/// is; a random speaker each turn, never the one who spoke last; no tools, only talk; each reply in the
/// speaker's own persona (its <c>persona.md</c>, rebuilt every turn, never another bot's) and voice; and
/// every turn on the starting profile's LLM server and model, one after another — a profile's own
/// <c>LLM URL</c> and <c>LLM model</c> are never read here.</para>
///
/// <para>Who speaks next (2026-09-25, the user's report: a bot asked another by name and a third answered):
/// a bot named in the last line — the user's lines since the last reply first, then that reply — answers,
/// one of them at random when several are named (<see cref="Addressed"/>); with no name, anyone but the last
/// speaker (<see cref="NextSpeaker"/>).</para>
///
/// <para>The conversation is one shared list of <see cref="BotChatLine"/>s. Each speaker sees it through
/// <see cref="BuildView"/>: its own lines as the assistant's, everyone else's — the user's interjections
/// too — as user messages written <c>Name: text</c>, consecutive ones merged (local chat templates want
/// the roles to alternate), the oldest dropped past <see cref="MaxLines"/> so an endless chat fits a window.</para>
/// </summary>
public static class BotChat
{
    /// <summary>How many of the latest lines a speaker is shown: <see cref="ConversationHistory.MaxTurns"/>, the main chat's own memory.</summary>
    public const int MaxLines = ConversationHistory.MaxTurns;

    /// <summary>The word that ends the names: what follows it is the topic, whatever its first word (later on 2026-09-24). Pinned.</summary>
    public const string TopicSeparator = "--";

    /// <summary>What the user's interjections are signed with in the bots' views. Pinned.</summary>
    public const string UserName = "User";

    /// <summary>
    /// The command's argument split into the participants and the topic: the leading words that name a
    /// profile in <paramref name="profiles"/> (ignoring case, each once) are the cast, the rest of the line
    /// the topic. The starter always takes part and always comes first; no name at all means every profile.
    /// A word that is no profile ends the names, so <c>/botchat ada max the best pizza</c> is two names and a
    /// topic, and a topic whose first word happens to be a profile's name takes that profile along — unless
    /// <see cref="TopicSeparator"/> comes first (later on 2026-09-24, the user's pick, <c>/imagine</c>'s own
    /// marker): the word <c>--</c> ends the names, and whatever follows it is the topic, whatever its first word,
    /// so <c>/botchat ada -- max speed of light</c> is ada and a topic, and <c>/botchat -- max speed</c> everyone.
    /// Only a whole word counts: <c>max--x</c> is no separator. Pure.
    /// </summary>
    public static (IReadOnlyList<string> Names, string Topic) ParseArgs(string args, IReadOnlyList<string> profiles, string starter)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(starter);
        var named = new List<string>();
        string rest = args.Trim();
        while (rest.Length > 0)
        {
            int split = rest.IndexOfAny([' ', '\t']);
            string word = split < 0 ? rest : rest[..split];
            if (word == TopicSeparator)
            {
                rest = split < 0 ? "" : rest[(split + 1)..].Trim();
                break;
            }

            string? match = profiles.FirstOrDefault(name => Profiles.NameEquals(name, word));
            if (match is null)
            {
                break;
            }

            if (!named.Any(name => Profiles.NameEquals(name, match)))
            {
                named.Add(match);
            }

            rest = split < 0 ? "" : rest[(split + 1)..].TrimStart();
        }

        var cast = new List<string> { starter };
        foreach (var name in named.Count == 0 ? profiles : named)
        {
            if (!cast.Any(taken => Profiles.NameEquals(taken, name)))
            {
                cast.Add(name);
            }
        }

        return (cast, rest);
    }

    /// <summary>
    /// The next speaker's index: a random one of <paramref name="count"/> other than <paramref name="last"/>
    /// (the user's call, 2026-09-24: never the same bot twice running); with two it simply alternates.
    /// <paramref name="last"/> null — nobody has spoken — is the first pick, which the screen makes the starter.
    /// With <paramref name="mentioned"/> (2026-09-25, <see cref="Addressed"/>) holding anyone but the last
    /// speaker, the pick is one of those instead; a mention of the last speaker alone changes nothing.
    /// </summary>
    public static int NextSpeaker(int count, int? last, Random random, IReadOnlyCollection<int>? mentioned = null)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        var named = mentioned?.Where(i => i >= 0 && i < count && i != last).Distinct().Order().ToList();
        if (named is { Count: > 0 })
        {
            return named[random.Next(named.Count)];
        }

        if (last is not { } previous)
        {
            return random.Next(count);
        }

        int pick = random.Next(count - 1);
        return pick >= previous ? pick + 1 : pick;
    }

    /// <summary>
    /// The indices of the <paramref name="names"/> that <paramref name="text"/> names (2026-09-25): whole words,
    /// ignoring case — no letter, digit, <c>_</c> or <c>-</c> either side — so <c>Ada,</c>, <c>Ada's</c> and
    /// <c>@ada</c> name ada and <c>Adam</c> does not. The names are the profiles', what the bots are told to call
    /// each other; a profile named with a common word (<c>default</c>) can be named by chance. Pure.
    /// </summary>
    public static IReadOnlyList<int> Mentioned(string text, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        var found = new List<int>();
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].Length > 0
                && Regex.IsMatch(text, @"(?<![\p{L}\p{N}_-])" + Regex.Escape(names[i]) + @"(?![\p{L}\p{N}_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>
    /// Who the chat's last word addresses (2026-09-25), for <see cref="NextSpeaker"/>: the bots the user's lines
    /// since the last reply name ("ada, what do you think?" is ada's to answer), or, when those name nobody, the
    /// bots that reply names. The speaker of that reply is left in; <see cref="NextSpeaker"/> skips it. Pure.
    /// </summary>
    public static IReadOnlyList<int> Addressed(IReadOnlyList<BotChatLine> lines, IReadOnlyList<string> cast)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(cast);
        int reply = lines.Count - 1;
        while (reply >= 0 && lines[reply].IsUser)
        {
            reply--;
        }

        var byUser = lines.Skip(reply + 1).SelectMany(line => Mentioned(line.Text, cast)).Distinct().ToList();
        var bySpeaker = reply >= 0 ? Mentioned(lines[reply].Text, cast) : [];
        bool SpeakerAside(int i) => reply >= 0 && Profiles.NameEquals(cast[i], lines[reply].Speaker);
        return byUser.Any(i => !SpeakerAside(i)) ? byUser : bySpeaker;
    }

    /// <summary>
    /// What <paramref name="speaker"/> is shown: the history before its turn (<c>Prior</c>, for
    /// <see cref="ConversationHistory.Replace"/>) and the turn's own user message (<c>TurnText</c>, for
    /// <see cref="Assistant.RunTurnAsync(string, IReadOnlyList{ImageAttachment}, CancellationToken)"/>).
    /// Only the latest <paramref name="maxLines"/> lines are kept. The view always opens on a user message —
    /// <see cref="OpeningText"/> when the kept lines start with the speaker's own — and always ends on
    /// one: the lines since the speaker last spoke, merged, or the opening itself on the very first turn. Pure.
    /// </summary>
    public static (IReadOnlyList<ChatMessage> Prior, string TurnText) BuildView(string speaker, IReadOnlyList<BotChatLine> lines, IReadOnlyList<string> others, string topic, int maxLines = MaxLines)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(lines);
        var messages = new List<(ChatRole Role, StringBuilder Text)>();
        foreach (var line in lines.Skip(Math.Max(0, lines.Count - Math.Max(1, maxLines))))
        {
            bool own = !line.IsUser && Profiles.NameEquals(line.Speaker, speaker);
            var role = own ? ChatRole.Assistant : ChatRole.User;
            string text = own ? line.Text : Signed(line);
            if (messages.Count > 0 && messages[^1].Role == role)
            {
                messages[^1].Text.Append("\n\n").Append(text);
            }
            else
            {
                messages.Add((role, new StringBuilder(text)));
            }
        }

        string opening = OpeningText(others, topic);
        if (messages.Count == 0 || messages[0].Role == ChatRole.Assistant)
        {
            messages.Insert(0, (ChatRole.User, new StringBuilder(opening)));
        }

        if (messages[^1].Role == ChatRole.Assistant)
        {
            // Only reachable if the speaker spoke last, which NextSpeaker never allows: ask it to go on.
            messages.Add((ChatRole.User, new StringBuilder(ContinueText)));
        }

        var prior = messages.Take(messages.Count - 1).Select(m => new ChatMessage(m.Role, m.Text.ToString())).ToList();
        return (prior, messages[^1].Text.ToString());
    }

    /// <summary>
    /// What the chat's session row keeps (2026-09-24): the starter's view with the lines it has not answered
    /// yet in as well — <see cref="BuildView"/> holds them back as the turn's text — so <c>/sessions</c> brings
    /// the chat back as a conversation with the starter that answers the last line. Pure.
    /// </summary>
    public static IReadOnlyList<ChatMessage> StoredHistory(string starter, IReadOnlyList<BotChatLine> lines, IReadOnlyList<string> cast, string topic)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(cast);
        var others = cast.Where(name => !Profiles.NameEquals(name, starter)).ToList();
        var (prior, text) = BuildView(starter, lines, others, topic);
        bool starterLast = lines.Count > 0 && !lines[^1].IsUser && Profiles.NameEquals(lines[^1].Speaker, starter);
        return starterLast ? prior : [.. prior, new ChatMessage(ChatRole.User, text)];
    }

    /// <summary>A line as the other speakers see it: <c>Name: text</c>. Pinned.</summary>
    public static string Signed(BotChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return (line.IsUser ? UserName : line.Speaker) + ": " + line.Text;
    }

    /// <summary>
    /// The system prompt for one speaker's turn: its persona (<see cref="Assistant.DefaultPersona"/> when its
    /// profile has no <c>persona.md</c>) through <see cref="Assistant.SystemPrompt"/> with every tool off, then
    /// <see cref="Rules"/>. Built afresh for every turn from that speaker's persona alone. Pure.
    /// </summary>
    public static string SystemPrompt(string? persona, string speaker, IReadOnlyList<string> others, string topic, bool speechOutput, string? voiceDirective, bool markdown, string? pronouns = null) =>
        Assistant.SystemPrompt(speechOutput, memories: null, persona: string.IsNullOrWhiteSpace(persona) ? null : persona, voiceDirective: voiceDirective,
            tools: false, files: false, timers: false, markdown: markdown)
        + "\n\n" + Rules(speaker, others, topic, pronouns);

    /// <summary>
    /// The group-chat rules after the persona (2026-09-24): who else is in the room, speak only as yourself,
    /// no name prefix, short replies, keep it going — small local models otherwise drift into agreeing and
    /// saying goodbye. <paramref name="pronouns"/> (<see cref="PronounsLine"/>, 2026-09-25) follows the first
    /// sentence when given. Pinned: it is prompt text.
    /// </summary>
    public static string Rules(string speaker, IReadOnlyList<string> others, string topic, string? pronouns = null)
    {
        ArgumentNullException.ThrowIfNull(others);
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"You are {speaker}, in a group chat with {JoinNames(others)} and the user, who may join in at any time. ");
        if (!string.IsNullOrWhiteSpace(pronouns))
        {
            text.Append(pronouns).Append(' ');
        }

        text.Append("The others' lines reach you as \"Name: text\". Speak only as yourself, in your own voice and personality; never write lines for anyone else and never start your reply with your name. ");
        text.Append("Keep each reply short: a few sentences. Keep the conversation going: react to what was just said, disagree when you see it differently, ask questions, and bring up something new when a thread runs dry. Never say goodbye or try to end the chat.");
        if (topic.Length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" The topic: {topic}");
        }

        return text.ToString();
    }

    /// <summary>
    /// A bot's gender from its profile's first TTS voice (2026-09-25, the user's ask: the bots were guessing each
    /// other's pronouns): a Kokoro voice name carries it in its second letter — <c>am_</c>, <c>bm_</c>, <c>jm_</c>…
    /// male, <c>af_</c>, <c>bf_</c>… female. Anything else, a blank voice or another server's name, is female: the
    /// user's call. The blended second voice (<c>TTS voice 2</c>) never counts. Pure.
    /// </summary>
    public static BotGender GenderOf(string? voice)
    {
        string name = (voice ?? "").Trim();
        return name.Length >= 3 && char.IsAsciiLetter(name[0]) && char.ToLowerInvariant(name[1]) == 'm' && name[2] == '_'
            ? BotGender.Male
            : BotGender.Female;
    }

    /// <summary>
    /// The pronouns sentence for one speaker's prompt (2026-09-25): every other bot with its own —
    /// <c>Use these pronouns for the others: ada is a woman (she/her); max is a man (he/him).</c> The speaker's own
    /// is never in it; its persona speaks for itself. Pinned: it is prompt text.
    /// </summary>
    public static string PronounsLine(IReadOnlyList<(string Name, BotGender Gender)> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        return "Use these pronouns for the others: "
            + string.Join("; ", others.Select(o => o.Name + (o.Gender == BotGender.Male ? " is a man (he/him)" : " is a woman (she/her)")))
            + ".";
    }

    /// <summary>The first user message: the chat's opening, the topic when there is one. Pinned.</summary>
    public static string OpeningText(IReadOnlyList<string> others, string topic)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(topic);
        return topic.Length > 0
            ? $"(The group chat with {JoinNames(others)} begins. Open it on this topic: {topic})"
            : $"(The group chat with {JoinNames(others)} begins. Open it on any subject you like.)";
    }

    /// <summary>The user message a view gets should it end on the speaker's own line. Pinned.</summary>
    public const string ContinueText = "(Go on.)";

    /// <summary><c>ada</c>, <c>ada and max</c>, <c>ada, max and neon</c>.</summary>
    public static string JoinNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    // ── The screen's words (pinned) ──────────────────────────────────────────

    public const string UsageError = "Usage: /botchat [profile ...] [[--] topic]: names two or more profiles (this one always joins), or none for every profile; after --, the rest is the topic whatever its first word; ESC or Ctrl+C stops it.";

    public const string TooFewError = "/botchat needs at least two profiles: add one with /profile add <name>.";

    /// <summary>The argument list's note beside a profile name.</summary>
    public const string ProfileNote = "add this profile to the chat";

    /// <summary>The line above the chat: the cast and how to stop it.</summary>
    public static string StartNotice(IReadOnlyList<string> names, string topic) =>
        topic.Length > 0
            ? $"(botchat: {JoinNames(names)} on \"{topic}\"; type to join in, ESC or Ctrl+C stops it)"
            : $"(botchat: {JoinNames(names)}; type to join in, ESC or Ctrl+C stops it)";

    /// <summary>The line when the chat ends: how many replies it ran to.</summary>
    public static string StoppedNotice(int replies) => $"(botchat stopped after {UsageText.Plural(replies, "reply", "replies")})";

    /// <summary>A profile whose <c>profile.json</c> could not be read sits the chat out.</summary>
    public static string SkippedNotice(string name, string reason) => $"(botchat: {name} sits this one out: {reason})";

    /// <summary>The session's title (2026-09-24): <c>Botchat: neon, ada and max</c>.</summary>
    public static string SessionTitle(IReadOnlyList<string> names) => "Botchat: " + JoinNames(names);

    /// <summary>The <c>--log</c> line per turn.</summary>
    public static string TurnLogLine(int n, string speaker) => string.Create(CultureInfo.InvariantCulture, $"Botchat turn {n}: {speaker}");
}

/// <summary>One line of the shared botchat transcript: a bot's reply (<paramref name="Speaker"/> its profile) or the user's interjection.</summary>
public sealed record BotChatLine(string Speaker, string Text, bool IsUser = false);

/// <summary>
/// One bot of the chat: its profile's name, its persona (null = the default) and <c>vocalia.md</c> directive,
/// its voice (the <see cref="NeonSidekick.Speech.VoiceMix.Spec"/> of its TTS voices) and speed, and which of the palette's
/// speaker colours its name takes (<see cref="BotChat"/>'s screen picks the colour, so a theme change follows), and
/// its <see cref="BotGender"/> from its first voice (<see cref="BotChat.GenderOf"/>, 2026-09-25).
/// </summary>
public sealed record BotParticipant(string Name, string? Persona, string? VoiceDirective, string Voice, double Speed, int ColorIndex, BotGender Gender = BotGender.Female);

/// <summary>A bot's gender, for the pronouns the others use (2026-09-25): from its first voice, female unless the voice says male.</summary>
public enum BotGender
{
    Female,
    Male,
}
