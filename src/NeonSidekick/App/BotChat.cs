using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NeonSidekick.Comfy;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
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
/// <c>LLM URL</c> and <c>LLM model</c> are never read here. The first exception to "no tools" (2026-09-25, the
/// user's ask): pictures — the bots' own <c>generate_image</c> (<see cref="ImageRule"/>) or the app's picture of each reply
/// (<see cref="ImagePromptInstruction"/>), as <c>Botchat image mode</c> says — over the workflows <c>Botchat ComfyUI enabled</c> or
/// <c>Botchat ComfyUI limited workflows</c> give (<see cref="ComfyWorkflows"/>, 2026-10-04; <c>Botchat images enabled</c> and one
/// workflow of each kind until then). Then skills (2026-09-27) and, on 2026-10-04 (the
/// user's asks), the main chat's tools (<c>Botchat tools enabled</c>, or the <c>Botchat limited tools</c> alone) and memory
/// (<c>Botchat memory enabled</c>, on by default; <see cref="BotChatMemoryMode"/> says whose): each its own switch on the tab.</para>
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
public static partial class BotChat
{
    /// <summary>How many of the latest lines a speaker is shown: <see cref="ConversationHistory.DefaultMaxTurns"/>, the main chat's default memory.</summary>
    public const int MaxLines = ConversationHistory.DefaultMaxTurns;

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
    /// profile has no <c>persona.md</c>) through <see cref="Assistant.BotSystemPrompt"/>, then <see cref="Rules"/>. Built afresh
    /// for every turn from that speaker's persona alone. With <paramref name="tools"/> (2026-10-04, <c>Botchat tools enabled</c>
    /// or <c>Botchat limited tools</c>) the default rules carry the sentences of the tools offered, as the main chat's would; with
    /// <paramref name="memories"/> (<c>Botchat memory enabled</c>) the memory section with the list follows them
    /// (<paramref name="memorySave"/>: <c>save_memory</c> offered) and <see cref="Rules"/> gains <see cref="MemoryRule"/>. With
    /// <paramref name="skills"/> (<c>Botchat skills enabled</c>, 2026-09-27, or <c>Botchat limited skills</c> since 2026-10-04: the
    /// main chat's catalog — the starting profile's, the global and the external skills, never the speaker's own profile's) the
    /// load-only skills block (<see cref="Skills.SkillsPrompt.LoadOnlySection"/>) goes before the rules. With none of them the
    /// prompt is as before, byte for byte. The preloaded skills' block of 2026-09-27 went on 2026-10-04 (the user's call). Pure.
    /// </summary>
    public static string SystemPrompt(string? persona, string speaker, IReadOnlyList<string> others, string topic, bool speechOutput, string? voiceDirective, bool markdown, string? pronouns = null, bool images = false, IReadOnlyList<Skills.Skill>? skills = null, bool camera = false, TurnRules? tools = null, IReadOnlyList<string>? memories = null, bool memorySave = true) =>
        Assistant.BotSystemPrompt(speechOutput, persona, voiceDirective, markdown, tools, memories, memorySave)
        + (skills is { Count: > 0 } ? "\n\n" + Skills.SkillsPrompt.LoadOnlySection(skills) : "")
        + "\n\n" + Rules(speaker, others, topic, pronouns, images, camera, memory: memories is not null);

    // ── A bot's tools (2026-10-04) ──────────────────────────────────────────

    /// <summary>
    /// A bot's tool list, in order (2026-10-04): the main chat's tools it is offered (in the main chat's order), the memory tools,
    /// <c>generate_image</c>, then <c>load_skill</c>. Each name once, botchat's own tool winning (code review, 2026-10-04: the
    /// main chat's <c>generate_image</c> sat beside botchat's narrowed one, the general one first, and the bots drew with the
    /// profile's workflows — the bots' general tools now leave the ComfyUI group out, and this keeps any other name the main
    /// chat shares with a bot's own tool from doing the same); a name the general list repeats is kept once, the first. Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> TurnTools(IReadOnlyList<AIFunction> general, IReadOnlyList<AIFunction> memory, AIFunction? image, AIFunction? skill)
    {
        ArgumentNullException.ThrowIfNull(general);
        ArgumentNullException.ThrowIfNull(memory);
        var own = new List<AIFunction>(memory.Count + 2);
        own.AddRange(memory);
        if (image is not null)
        {
            own.Add(image);
        }

        if (skill is not null)
        {
            own.Add(skill);
        }

        var seen = new HashSet<string>(own.Select(tool => tool.Name), StringComparer.Ordinal);
        var tools = new List<AIFunction>(general.Count + own.Count);
        tools.AddRange(general.Where(tool => seen.Add(tool.Name)));
        tools.AddRange(own.DistinctBy(tool => tool.Name, StringComparer.Ordinal));
        return tools;
    }

    /// <summary>
    /// The round trips a bot's turn may take (2026-10-04, folding the picture's and the skill's caps of 2026-09-25 and 2026-09-27):
    /// one for its words, two for a picture, two for a skill and a file it bundles, one for a memory — so 3 with a picture or a
    /// skill alone and 5 with both, as before. Offered the main chat's tools, the main chat's cap (<paramref name="mainCap"/>,
    /// <c>LLM max tool iterations</c>), never fewer than those. Pure.
    /// </summary>
    public static int ToolIterations(int? mainCap, bool image, bool skill, bool memory)
    {
        int small = 1 + (image ? 2 : 0) + (skill ? 2 : 0) + (memory ? 1 : 0);
        return mainCap is { } cap ? Math.Max(cap, small) : small;
    }

    /// <summary>
    /// The tools a bot's calls written out as text are caught for (2026-10-04, <see cref="Assistant.TextToolCallNames"/>): the
    /// four botchat has always offered or now offers itself. A written call to one of the main chat's tools is never run — the
    /// main chat runs none either.
    /// </summary>
    public static readonly IReadOnlySet<string> CaughtWrittenCalls = new HashSet<string>(StringComparer.Ordinal)
    {
        GenerateImageTool.ToolName, LoadSkillTool.ToolName, SaveMemoryTool.ToolName, RecallMemoryTool.ToolName,
    };

    /// <summary>
    /// The group-chat rules after the persona (2026-09-24): who else is in the room, speak only as yourself,
    /// no name prefix, short replies, keep it going — small local models otherwise drift into agreeing and
    /// saying goodbye. <paramref name="pronouns"/> (<see cref="PronounsLine"/>, 2026-09-25) follows the first
    /// sentence when given; <paramref name="images"/> (the bots offered <c>generate_image</c>, 2026-09-25) closes
    /// it with <see cref="ImageRule"/>. Pinned: it is prompt text.
    /// </summary>
    public static string Rules(string speaker, IReadOnlyList<string> others, string topic, string? pronouns = null, bool images = false, bool camera = false, bool memory = false)
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

        if (images)
        {
            text.Append(' ').Append(ImageRule);
        }

        if (camera)
        {
            text.Append(' ').Append(CameraRule);
        }

        if (memory)
        {
            text.Append(' ').Append(MemoryRule);
        }

        return text.ToString();
    }

    /// <summary>
    /// The rules' last sentence while the bots remember (2026-10-04, <c>Botchat memory enabled</c>): the others' lines reach a bot
    /// as user messages, so without it a bot saves another bot's words about itself as facts about the user. Pinned: it is prompt text.
    /// </summary>
    public const string MemoryRule = "Only lines signed User: are the user's; never save what another speaker says about themselves as a memory.";

    /// <summary>
    /// The rules' last sentence while the bots see the user (<c>Botchat camera</c>, later on 2026-10-02, the user's report: the
    /// bots took the webcam picture, attached to the message of the others' lines, for one of the others'). It names the
    /// user as the chat signs them (<see cref="UserName"/>). Pinned: it is prompt text.
    /// </summary>
    public const string CameraRule = "Each of your turns also comes with a live photo from the user's webcam, taken just now and attached last. "
        + "It shows " + UserName + " — the human in this chat — not a picture from you or the others. "
        + "Mention what you see only when it adds something, and never say another participant sent it.";

    /// <summary>
    /// The rules' last sentence while the bots are offered <c>generate_image</c> (2026-09-25, <c>Botchat image mode</c>
    /// <c>autonomous</c>): a picture now and then, never instead of talking. Pinned: it is prompt text.
    /// </summary>
    public const string ImageRule = "You can draw a picture with generate_image when one would add something to the conversation — at most one per reply, and never instead of saying something. "
        + "If you say you are drawing, painting, sketching, generating, making, sharing or showing " + PictureWords + ", call generate_image in that same reply, with a prompt: "
        + "never describe one you have not made, and never write the call out as text.";

    /// <summary>
    /// Every word a model may use for what it draws (later on 2026-09-25, the user's report: the rule said only "picture", and a bot
    /// talking of an image or a photo did not take it as its own), shared by <see cref="ImageRule"/> and
    /// <see cref="PromisedPictureInstruction"/> so the two never drift apart.
    /// </summary>
    private const string PictureWords = "a picture, image, photo, drawing, painting, sketch, illustration, portrait, artwork or anything else to look at";

    // ── A picture a bot only talked about (2026-09-25) ──────────────────────

    /// <summary>
    /// What a bot's turn did with <c>generate_image</c> (2026-09-25): <c>Tried</c> — it called the tool; <c>Drew</c> — a call's result
    /// was no <c>Error:</c> (later the same day, the user's report: a call with no prompt answered "give prompt" and counted as a
    /// drawing, so the promised picture never came). A bot's history is built afresh each turn from text lines, so any call in
    /// it is the turn's own. Pure.
    /// </summary>
    public static (bool Tried, bool Drew) PictureAttempt(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var contents = messages.SelectMany(m => m.Contents).ToList();
        var calls = contents.OfType<FunctionCallContent>().Where(call => string.Equals(call.Name, GenerateImageTool.ToolName, StringComparison.Ordinal)).Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
        bool drew = contents.OfType<FunctionResultContent>().Any(result => calls.Contains(result.CallId)
            && !(result.Result?.ToString() ?? "").StartsWith("Error", StringComparison.Ordinal));
        return (calls.Count > 0, drew);
    }

    /// <summary>
    /// Whether a reply talks about a picture at all (2026-09-25, <c>Botchat image mode</c> <c>autonomous</c>): the cheap sieve
    /// before <see cref="PromisedPictureInstruction"/>'s side request, so a reply with no picture word costs nothing. Loose on
    /// purpose — the request decides. Pure.
    /// </summary>
    public static bool MentionsPicture(string? reply) => !string.IsNullOrEmpty(reply) && PictureWord().IsMatch(reply);

    [GeneratedRegex(@"\b(?:draw|drew|drawn|sketch|paint|doodle|pic|pics|picture|image|illustrat|render|portrait|photo|snapshot|selfie|snap|art\b|artwork|graphic|cartoon|comic|poster|wallpaper)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PictureWord();

    /// <summary>
    /// The answer of <see cref="PromisedPictureInstruction"/>'s request when the line promises no picture (2026-09-25). Pinned: it is prompt text.
    /// </summary>
    public const string NoPictureAnswer = "NONE";

    /// <summary>
    /// The system message of the promised-picture request (2026-09-25, the user's ask: under <c>autonomous</c> a bot often says it
    /// drew something without calling <c>generate_image</c>, and the reply should be true): decide whether the line says its speaker
    /// is drawing, sharing or showing a picture — <see cref="NoPictureAnswer"/> when not — and otherwise write the prompt for
    /// <em>that</em> picture, in <paramref name="workflow"/>'s family style and its sidecar's tips. Pinned: it is prompt text.
    /// </summary>
    public static string PromisedPictureInstruction(ComfyWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var text = new StringBuilder("You write prompts for an image generator. Given one line of a group chat, decide whether its speaker says they are drawing, painting, sketching, generating, making, sharing or showing " + PictureWords + " right now. ")
            .Append(CultureInfo.InvariantCulture, $"If they do not, answer exactly {NoPictureAnswer}. ")
            .Append("If they do, write a single prompt for the picture they describe — what it shows, its mood — never the chat itself, no speech bubbles, no screens of text. ")
            .Append("Write it in this style: ").Append(ComfyFamilies.StyleGuide(workflow.Family));
        if (!string.IsNullOrWhiteSpace(workflow.Tips))
        {
            text.Append("\n\nTips for this workflow: ").Append(workflow.Tips.Trim());
        }

        text.Append(CultureInfo.InvariantCulture, $"\n\nAnswer with the prompt alone, or {NoPictureAnswer}: no preamble, no explanation, no quotes.");
        return text.ToString();
    }

    /// <summary>Whether a cleaned promised-picture answer means no picture: empty, or <see cref="NoPictureAnswer"/> (a trailing full stop allowed). Pure.</summary>
    public static bool IsNoPicture(string prompt) =>
        prompt.Length == 0 || string.Equals(prompt.TrimEnd('.'), NoPictureAnswer, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>--log</c> line of a promised picture's prompt (2026-09-25).</summary>
    public static string PromisedPictureLogLine(string speaker, string workflow, string prompt) => $"Botchat picture {speaker} talked about but did not draw, on {workflow}: {prompt}";

    // ── The app's pictures (2026-09-25) ─────────────────────────────────────

    /// <summary>
    /// Whether the settings give <c>/botchat</c> any ComfyUI workflow at all (2026-10-04, in place of <c>Botchat images enabled</c>):
    /// <c>Botchat ComfyUI enabled</c> on, or a name in <c>Botchat ComfyUI limited workflows</c>. The settings alone, no catalog: what
    /// <see cref="BotPicturePacer.Applies"/> reads. Whether a picture can be made is <see cref="ComfyWorkflows"/>'. Pure.
    /// </summary>
    public static bool ComfyChosen(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.BotChatComfy || (effective.BotChatLimitedComfyWorkflows?.Any(name => !string.IsNullOrWhiteSpace(name)) ?? false);
    }

    /// <summary>
    /// The ComfyUI workflows of <c>/botchat</c> (2026-10-04, the user's ask: the bots never confused about which the user wants):
    /// with <c>Botchat ComfyUI enabled</c> the ones <c>ComfyUI workflows offered</c> offers the main chat, else those
    /// <c>Botchat ComfyUI limited workflows</c> names among every installed one (offered or not, the user's pick) — in
    /// <paramref name="installed"/>'s order either way, a name not installed skipped (<see cref="ComfyWorkflowCatalog.Offered"/>).
    /// <see cref="Txt2ImgWorkflows"/> and <see cref="Img2ImgWorkflows"/> sort them into fresh pictures and reworks; another kind
    /// (two pictures, no prompt) is never used. Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> ComfyWorkflows(IReadOnlyList<ComfyWorkflow> installed, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(effective);
        return ComfyWorkflowCatalog.Offered(installed, effective.BotChatComfy ? effective.ComfyWorkflowsOffered : effective.BotChatLimitedComfyWorkflows);
    }

    /// <summary>
    /// The workflows a fresh <c>/botchat</c> picture may use (2026-09-25; <c>Botchat txt2img workflow</c>'s list from 2026-09-27,
    /// the chat's set's since 2026-10-04, <see cref="ComfyWorkflows"/>): those of <paramref name="workflows"/> that take a prompt
    /// and no input picture — text → image — in the order given. Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> Txt2ImgWorkflows(IReadOnlyList<ComfyWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return workflows.Where(w => w.ImageCount == 0 && w.TakesPrompt).ToList();
    }

    /// <summary>
    /// The workflows a <c>/botchat</c> rework may use (2026-09-27, <c>Botchat img2img workflow</c>'s list; the set's since 2026-10-04): those of
    /// <paramref name="workflows"/> that take a prompt and exactly one input picture — image → image — in the order given. Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> Img2ImgWorkflows(IReadOnlyList<ComfyWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return workflows.Where(w => w.ImageCount == 1 && w.TakesPrompt).ToList();
    }

    /// <summary>
    /// The workflows the bots' own <c>generate_image</c> sees (2026-09-27, the user's ask: <c>autonomous</c> limited as <c>automatic</c>
    /// is): <paramref name="fresh"/> and, while there is a picture to rework, <paramref name="rework"/>, of those still installed —
    /// lists since 2026-10-04 (the chat's set may have several of a kind). Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> BotWorkflows(IReadOnlyList<ComfyWorkflow> installed, IReadOnlyList<ComfyWorkflow> fresh, IReadOnlyList<ComfyWorkflow> rework, bool reworkable)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(rework);
        return installed.Where(w => fresh.Any(f => SameName(f, w)) || (reworkable && rework.Any(r => SameName(r, w)))).ToList();
    }

    private static bool SameName(ComfyWorkflow a, ComfyWorkflow b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    // ── Reworking a picture (2026-09-27) ────────────────────────────────────

    /// <summary>The most pictures <c>chat-history</c> offers to rework (2026-09-27): the newest, numbered oldest first.</summary>
    public const int MaxReworkPictures = 8;

    /// <summary>
    /// The pictures a rework may start from (2026-09-27, <c>Botchat img2img mode</c>): every picture of the chat's log (a
    /// generation of several is several), the last one under <c>latest</c>, the last <see cref="MaxReworkPictures"/> under
    /// <c>chat-history</c>, numbered from 1 oldest first. A picture still rendering is not in the log yet. Pure.
    /// </summary>
    public static IReadOnlyList<ReworkPicture> ReworkCandidates(IReadOnlyList<BotPicture> log, BotImg2ImgMode mode)
    {
        ArgumentNullException.ThrowIfNull(log);
        var all = log.SelectMany(p => p.Images.Select(image => (p.Owner, p.Drawn, image.Path))).ToList();
        int keep = mode == BotImg2ImgMode.Latest ? 1 : MaxReworkPictures;
        return all.Skip(Math.Max(0, all.Count - keep)).Select((c, i) => new ReworkPicture(i + 1, c.Owner, c.Drawn, c.Path)).ToList();
    }

    /// <summary>What a rework candidate is, in prompt text: a bot's own drawing, or the app's picture of a reply. Pinned: it is prompt text.</summary>
    public static string DescribePicture(ReworkPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return picture.Drawn ? $"{picture.Owner}'s picture" : $"the picture of {picture.Owner}'s reply";
    }

    /// <summary>The first line of an image-prompt answer that reworks a picture (2026-09-27). Pinned: it is prompt text.</summary>
    public const string ReworkAnswer = "REWORK";

    /// <summary>The rework line as the instructions spell it: bare with one candidate, numbered with several. Pinned: it is prompt text.</summary>
    private static string ReworkLine(IReadOnlyList<ReworkPicture> candidates) =>
        candidates.Count == 1 ? ReworkAnswer : ReworkAnswer + " n (n the picture's number)";

    private static string WhichPicture(IReadOnlyList<ReworkPicture> candidates) =>
        candidates.Count == 1 ? "the chat's latest picture (" + DescribePicture(candidates[0]) + ")" : "one of the chat's pictures";

    private static void AppendStyle(StringBuilder text, ComfyWorkflow workflow, string tipsLabel)
    {
        text.Append(ComfyFamilies.StyleGuide(workflow.Family));
        if (!string.IsNullOrWhiteSpace(workflow.Tips))
        {
            text.Append("\n\n").Append(tipsLabel).Append(workflow.Tips.Trim());
        }
    }

    /// <summary>The first line of an image-prompt answer that names its workflow, when a kind has several (2026-10-04). Pinned: it is prompt text.</summary>
    public const string WorkflowAnswer = "WORKFLOW";

    /// <summary>The workflow line as the instructions spell it (2026-10-04). Pinned: it is prompt text.</summary>
    private const string WorkflowLine = WorkflowAnswer + " name (the workflow's name as listed)";

    /// <summary>
    /// Several workflows of one kind for the prompt writer to choose from (2026-10-04, the user's pick): <paramref name="lead"/>, then
    /// each by name with its family's style and its sidecar's tips, in the chat's set's order.
    /// </summary>
    private static void AppendStyles(StringBuilder text, IReadOnlyList<ComfyWorkflow> workflows, string lead)
    {
        text.Append(lead);
        foreach (var workflow in workflows)
        {
            text.Append("\n\n").Append(workflow.Name).Append(": ").Append(ComfyFamilies.StyleGuide(workflow.Family));
            if (!string.IsNullOrWhiteSpace(workflow.Tips))
            {
                text.Append("\nTips for ").Append(workflow.Name).Append(": ").Append(workflow.Tips.Trim());
            }
        }
    }

    /// <summary>
    /// The image-prompt system message with a rework on offer (2026-09-27, the user's ask: after the first picture the prompt
    /// writer chooses a fresh picture or a rework): <paramref name="fresh"/> empty means the rework is the only way; without
    /// <paramref name="rework"/> or a candidate, and with one fresh workflow, it is <see cref="ImagePromptInstruction(ComfyWorkflow)"/>
    /// (or <see cref="PromisedPictureInstruction(ComfyWorkflow)"/>) as before. Lists since 2026-10-04 (the user's pick: the chat's set
    /// may hold several of a kind, and the writer chooses): a kind with several lists them by name, each in its style, and asks for
    /// <see cref="WorkflowAnswer"/> and the name on a line of its own before the prompt (after the <see cref="ReworkAnswer"/> line in a
    /// rework); with one of each kind the text is what it was, byte for byte. <paramref name="promised"/> is the promised-picture
    /// request's form, <see cref="NoPictureAnswer"/> kept. Pinned: it is prompt text.
    /// </summary>
    public static string PictureInstruction(bool promised, IReadOnlyList<ComfyWorkflow> fresh, IReadOnlyList<ComfyWorkflow> rework, IReadOnlyList<ReworkPicture> candidates)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(rework);
        ArgumentNullException.ThrowIfNull(candidates);
        bool reworking = rework.Count > 0 && candidates.Count > 0;
        if (!reworking && fresh.Count == 0)
        {
            throw new ArgumentException("Neither a fresh workflow nor a picture to rework.", nameof(fresh));
        }

        if (!reworking && fresh.Count == 1)
        {
            return promised ? PromisedPictureInstruction(fresh[0]) : ImagePromptInstruction(fresh[0]);
        }

        var text = new StringBuilder("You write prompts for an image generator. Given one line of a group chat, ");
        if (promised)
        {
            text.Append("decide whether its speaker says they are drawing, painting, sketching, generating, making, sharing or showing " + PictureWords + " right now. ")
                .Append(CultureInfo.InvariantCulture, $"If they do not, answer exactly {NoPictureAnswer}. If they do, ");
        }

        const string NoChat = "never the chat itself, no speech bubbles, no screens of text. ";
        const string Keep = "keep what it shows where the line does not change it, and change what the line calls for";
        if (fresh.Count > 0)
        {
            text.Append(promised ? "write a single prompt for the picture they describe — what it shows, its mood — " : "write a single prompt for a picture that illustrates it: the scene, the things or ideas it talks about, its mood — ")
                .Append(NoChat);
            if (fresh.Count == 1)
            {
                text.Append("Write it in this style: ");
                AppendStyle(text, fresh[0], "Tips for this workflow: ");
            }
            else
            {
                AppendStyles(text, fresh, "Choose the workflow that suits the picture best and write the prompt in its style. The workflows:");
                text.Append("\n\nPut ").Append(WorkflowLine).Append(" alone on the first line of your answer and the prompt after it.");
            }

            if (reworking)
            {
                text.Append("\n\nYou may instead rework ").Append(WhichPicture(candidates))
                    .Append(" rather than make a new one, when the line talks about it, answers it or builds on it: ").Append(Keep).Append(". ");
            }
        }
        else
        {
            text.Append("write a single prompt that reworks ").Append(WhichPicture(candidates))
                .Append(promised ? " into the picture they describe: " : " so that it illustrates the line: ").Append(Keep).Append(" — ").Append(NoChat);
        }

        if (reworking)
        {
            if (candidates.Count > 1)
            {
                text.Append("The pictures, oldest first:");
                foreach (var picture in candidates)
                {
                    text.Append(CultureInfo.InvariantCulture, $"\n{picture.Number}. {DescribePicture(picture)}");
                }

                text.Append("\n");
            }

            text.Append(fresh.Count > 0 ? "To rework it, put " : "Put ").Append(ReworkLine(candidates));
            if (rework.Count == 1)
            {
                text.Append(" alone on the first line of your answer and the prompt after it. Write the rework's prompt in this style: ");
                AppendStyle(text, rework[0], "Tips for the rework workflow: ");
            }
            else
            {
                text.Append(" alone on the first line of your answer, ").Append(WorkflowLine).Append(" alone on the second and the prompt after them. ");
                AppendStyles(text, rework, "Choose the rework workflow that suits it best and write the rework's prompt in its style. The rework workflows:");
            }
        }

        var answers = new List<string>();
        if (fresh.Count > 0)
        {
            answers.Add(fresh.Count == 1 ? "the prompt alone" : "the " + WorkflowAnswer + " line and then the prompt");
        }

        if (reworking)
        {
            answers.Add(rework.Count == 1 ? "the " + ReworkAnswer + " line and then the prompt" : "the " + ReworkAnswer + " line, the " + WorkflowAnswer + " line and then the prompt");
        }

        if (promised)
        {
            answers.Add(NoPictureAnswer);
        }

        text.Append("\n\nAnswer with ").Append(string.Join(", or ", answers)).Append(": no preamble, no explanation, no quotes.");
        return text.ToString();
    }

    /// <summary>
    /// An image-prompt answer read (2026-09-27): a first line <see cref="ReworkAnswer"/> (a number after it, stray marks around
    /// it) takes that candidate — a number that names none takes the latest — and the rest is the prompt; no such line is a fresh
    /// picture when <paramref name="fresh"/> has one, else a rework of the latest. No candidate or no <paramref name="rework"/>
    /// workflow, no rework. The prompt is <see cref="CleanImagePrompt"/>'s. Pure.
    ///
    /// <para>The word is <c>REWORK</c> in capitals, as the request spells it, or in any case only with a number, a <c>#</c>, a mark
    /// or the line's end after it (2026-09-28, code review): with the case ignored, a fresh prompt such as <c>Rework of an old
    /// castle at dusk</c> reworked the latest picture and lost its first word. <c>**Rework #1:**</c> is still read.</para>
    ///
    /// <para>The workflow (2026-10-04, the writer's pick when a kind has several): a <see cref="WorkflowAnswer"/> line — first, or
    /// right after the rework line, or before it — names one of the chosen kind (any case, or the longest name the line holds as a
    /// whole word); a prompt on the same line after the name is read when no line follows it (<see cref="SplitWorkflowLine"/>); the
    /// same capitals-or-a-mark rule keeps a fresh prompt such as <c>Workflow of a busy kitchen</c> whole, and the line is read only
    /// while the set has several workflows, so a set of one parses as before. With one of the kind it is that one; null when the kind
    /// has several and none was named — the caller takes the first and says so in the log.</para>
    /// </summary>
    public static (string Prompt, ReworkPicture? Rework, ComfyWorkflow? Workflow) ParseImagePrompt(string? text, IReadOnlyList<ReworkPicture> candidates, IReadOnlyList<ComfyWorkflow> fresh, IReadOnlyList<ComfyWorkflow> rework)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(rework);
        bool reworkable = rework.Count > 0 && candidates.Count > 0;
        bool named = fresh.Count > 1 || (reworkable && rework.Count > 1);
        string body = CleanImagePrompt(text);
        ReworkPicture? chosen = null;
        bool reworkRead = false;
        string? workflowName = null;
        // Up to two header lines, either order: the rework line and the workflow line.
        for (int header = 0; header < 2; header++)
        {
            int end = body.IndexOf('\n', StringComparison.Ordinal);
            string first = end < 0 ? body : body[..end];
            string after = end < 0 ? "" : body[end..];
            if (reworkable && !reworkRead && ReworkFirstLine().Match(first) is { Success: true } match)
            {
                reworkRead = true;
                chosen = int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= candidates.Count
                    ? candidates[n - 1]
                    : candidates[^1];
                body = CleanImagePrompt((match.Groups["rest"].Value + after).Trim());
                continue;
            }

            if (named && workflowName is null && WorkflowFirstLine().Match(first) is { Success: true } line)
            {
                string rest;
                (workflowName, rest) = SplitWorkflowLine(line.Groups["name"].Value, reworkable ? [.. fresh, .. rework] : fresh);
                // Words after the name are the prompt only when nothing follows the line: with the prompt below it they are a
                // note on the choice ("flux-dev (the photographic one)"), never the picture's.
                body = CleanImagePrompt(after.Trim().Length > 0 ? after : rest);
                continue;
            }

            break;
        }

        if (!reworkRead && reworkable && fresh.Count == 0)
        {
            chosen = candidates[^1];
        }

        var kind = chosen is null ? fresh : rework;
        var workflow = kind.Count == 1 ? kind[0] : NamedWorkflow(kind, workflowName);
        return (body, chosen, workflow);
    }

    [GeneratedRegex(@"^\W*(?:REWORK\b|(?i:rework)(?=\s*(?:[#:*.\-–—\d]|$)))[\s#:*.\-–—]*(?<n>\d+)?[\s:*.\-–—]*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReworkFirstLine();

    [GeneratedRegex(@"^\W*(?:WORKFLOW\b|(?i:workflow)(?=\s*[#:*\-–—]))[\s#:*\-–—""'`]*(?<name>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowFirstLine();

    /// <summary>The marks around a workflow's name on its line: quotes, bold, a full stop.</summary>
    private static readonly char[] NameMarks = [' ', '\t', '"', '\'', '`', '*', '.'];

    /// <summary>The marks between a workflow's name and a prompt on the same line.</summary>
    private static readonly char[] PromptLead = [' ', '\t', '"', '\'', '`', '*', ':', ',', ';', '-', '–', '—'];

    /// <summary>
    /// A <see cref="WorkflowAnswer"/> line's text (after the word) split into the name and what follows it on the line (code
    /// review, 2026-10-04: <c>WORKFLOW: flux — a cat on a roof</c> kept the name and lost the prompt): the line's whole text when it
    /// is a workflow's name; else the longest name of <paramref name="workflows"/> the text starts with, a mark or a space after
    /// it, its joiners (a space, <c>-</c>, <c>_</c>) read as one another or left out (the second 2026-10-04 review: with flux and
    /// flux-dev installed, <c>WORKFLOW: flux dev</c> took flux with the prompt "dev"); else, at the first <c>: </c> or spaced dash, the words before it — an unknown or misspelt name, for
    /// <see cref="NamedWorkflow"/> to refuse — unless only the whole line holds a name (<c>I'd pick: flux-dev</c>); else the whole
    /// text is the name. Pure.
    /// </summary>
    private static (string Name, string Prompt) SplitWorkflowLine(string text, IReadOnlyList<ComfyWorkflow> workflows)
    {
        string line = text.Trim();
        string whole = line.TrimEnd(NameMarks);
        if (workflows.Any(w => string.Equals(w.Name, whole, StringComparison.OrdinalIgnoreCase)))
        {
            return (whole, "");
        }

        var lead = workflows
            .Select(w => (Workflow: w, Length: LeadLength(line, w.Name)))
            .Where(found => found.Length > 0)
            .OrderByDescending(found => found.Workflow.Name.Length)
            .FirstOrDefault();
        if (lead.Workflow is not null)
        {
            return (lead.Workflow.Name, PromptAfter(line[lead.Length..]));
        }

        var split = Separators
            .Select(separator => (At: line.IndexOf(separator, StringComparison.Ordinal), separator.Length))
            .Where(found => found.At > 0)
            .OrderBy(found => found.At)
            .FirstOrDefault();
        if (split.Length > 0)
        {
            string before = line[..split.At].TrimEnd(NameMarks);
            bool onlyWhole = !workflows.Any(w => HoldsWord(before, w.Name)) && workflows.Any(w => HoldsWord(whole, w.Name));
            if (!onlyWhole)
            {
                return (before, PromptAfter(line[(split.At + split.Length)..]));
            }
        }

        return (whole, "");
    }

    /// <summary>
    /// The prompt after a workflow's name on its line: the marks before it dropped, and nothing when no letter or digit is left
    /// (the third 2026-10-04 review: <c>WORKFLOW: flux dev.</c> with flux-dev installed drew the prompt ".").
    /// </summary>
    private static string PromptAfter(string rest)
    {
        string prompt = rest.TrimStart(PromptLead);
        return prompt.Any(char.IsLetterOrDigit) ? prompt : "";
    }

    /// <summary>What may stand between a workflow's name and a prompt on its line, when the name is not one installed.</summary>
    private static readonly string[] Separators = [": ", " — ", " – ", " - "];

    /// <summary>
    /// How much of <paramref name="line"/> a workflow's <paramref name="name"/> leads, any case, a joiner of the name (a space,
    /// <c>-</c>, <c>_</c>) met by any joiner on the line or by none (<c>flux dev</c> and <c>fluxdev</c> lead with flux-dev), with no
    /// name character after it; 0 when it does not. A joiner the name has not, between two of its letters, is no match.
    /// </summary>
    private static int LeadLength(string line, string name)
    {
        int i = 0, j = 0;
        while (j < name.Length)
        {
            bool lineJoins = i < line.Length && line[i] is ' ' or '-' or '_';
            bool nameJoins = name[j] is ' ' or '-' or '_';
            if (nameJoins)
            {
                i += lineJoins ? 1 : 0;
                j++;
            }
            else if (i < line.Length && char.ToUpperInvariant(line[i]) == char.ToUpperInvariant(name[j]))
            {
                i++;
                j++;
            }
            else
            {
                return 0;
            }
        }

        return i > 0 && (i == line.Length || !IsNameChar(line[i])) ? i : 0;
    }

    /// <summary>A character that may continue a workflow's name: a letter, a digit, <c>_</c> or <c>-</c>.</summary>
    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    /// <summary>
    /// The workflow of <paramref name="kind"/> a <see cref="WorkflowAnswer"/> line names: by its name (any case), else the longest
    /// name the line holds as a whole word — never inside a longer one (code review, 2026-10-04: <c>sdxl-lightning</c>, not
    /// installed, quietly took a workflow named <c>sd</c>); null with none, so the caller's warning is written.
    /// </summary>
    private static ComfyWorkflow? NamedWorkflow(IReadOnlyList<ComfyWorkflow> kind, string? name)
    {
        string wanted = name?.Trim() ?? "";
        if (wanted.Length == 0)
        {
            return null;
        }

        return kind.FirstOrDefault(w => string.Equals(w.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? kind.Where(w => HoldsWord(wanted, w.Name)).OrderByDescending(w => w.Name.Length).FirstOrDefault();
    }

    /// <summary>Whether <paramref name="text"/> holds <paramref name="word"/> (any case) with no name character either side of it.</summary>
    private static bool HoldsWord(string text, string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        for (int at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + word.Length;
            if ((at == 0 || !IsNameChar(text[at - 1])) && (end == text.Length || !IsNameChar(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The line a bot's turn text ends with while <c>generate_image</c> may rework (2026-09-27, <c>autonomous</c>): the pictures
    /// it may start from, whose and their paths, and how to call it — with any of <paramref name="workflows"/>, the set's image →
    /// image ones (several since 2026-10-04: <c>workflow "a" or "b"</c>). The stored line stays as it was. Pinned: it is prompt text.
    /// </summary>
    public static string ReworkCaption(IReadOnlyList<string> workflows, IReadOnlyList<ReworkPicture> candidates)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(candidates);
        string names = string.Join(" or ", workflows.Select(w => $"\"{w}\""));
        string how = $"call generate_image with workflow {names}, its path as image, and a prompt for the reworked picture";
        if (candidates.Count == 1)
        {
            return $"(You may rework the chat's latest picture, {DescribePicture(candidates[0])}, at \"{candidates[0].Path}\": {how}.)";
        }

        return $"(You may rework one of the chat's pictures — {how}. Oldest first: "
            + string.Join("; ", candidates.Select(c => $"{DescribePicture(c)}, \"{c.Path}\"")) + ".)";
    }

    /// <summary>
    /// The system message of the image-prompt request (2026-09-25): one prompt that pictures the reply, in the style of
    /// <paramref name="workflow"/>'s family (<see cref="ComfyFamilies.StyleGuide"/>) and its sidecar's tips, and nothing
    /// else — no preamble, no quotes. Pinned: it is prompt text.
    /// </summary>
    public static string ImagePromptInstruction(ComfyWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var text = new StringBuilder("You write prompts for an image generator. Given one line of a group chat, write a single prompt for a picture that illustrates it: ")
            .Append("the scene, the things or ideas it talks about, its mood — never the chat itself, no speech bubbles, no screens of text. ")
            .Append("Write it in this style: ").Append(ComfyFamilies.StyleGuide(workflow.Family));
        if (!string.IsNullOrWhiteSpace(workflow.Tips))
        {
            text.Append("\n\nTips for this workflow: ").Append(workflow.Tips.Trim());
        }

        text.Append("\n\nAnswer with the prompt alone: no preamble, no explanation, no quotes.");
        return text.ToString();
    }

    /// <summary>
    /// What the image-prompt request's system text gains while skills are offered (2026-09-27, the user's report: in
    /// <c>automatic</c> the first picture was prompted before any skill could be loaded, a topic saying which to load had no
    /// way to be obeyed): the load-only catalog (<see cref="Skills.SkillsPrompt.LoadOnlySection"/>) and a directive to load
    /// the skill the topic or the line asks for pictures — or one for writing image prompts — and a file it bundles, before
    /// answering as the instruction above says. Its "a skill already loaded for you above needs no load_skill" (2026-09-30) went with
    /// the preloaded skills on 2026-10-04. Pinned: it is prompt text.
    /// </summary>
    public static string ImagePromptSkills(IReadOnlyList<Skills.Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return Skills.SkillsPrompt.LoadOnlySection(skills) + "\n\n" + ImagePromptSkillsDirective;
    }

    /// <summary>The directive of <see cref="ImagePromptSkills"/>, the tool's name from its constant (2026-09-30, code review). Pinned: it is prompt text.</summary>
    public const string ImagePromptSkillsDirective =
        "Before you answer, load with " + LoadSkillTool.ToolName + " any skill the chat's topic or the line asks to be used for pictures or image prompts, "
        + "or one whose description covers writing image prompts for this workflow — and a file it bundles when the skill says to read one — "
        + "and follow it. Then answer exactly as instructed above; never mention the skill in the answer.";

    /// <summary>The user message of the image-prompt request (2026-09-25): whose line, the topic when there is one, and the line. Pinned: it is prompt text.</summary>
    public static string ImagePromptRequest(string speaker, string reply, string topic)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(topic);
        string about = topic.Length > 0 ? $" (the chat's topic: {topic})" : "";
        return $"{speaker}'s line{about}:\n\n{reply}";
    }

    /// <summary>
    /// The model's image prompt as ComfyUI gets it (2026-09-25): trimmed, a code fence around it dropped, a leading
    /// <c>Prompt:</c> label dropped, and one pair of wrapping quotes taken off. Empty means no picture. Pure.
    /// </summary>
    public static string CleanImagePrompt(string? text)
    {
        string prompt = (text ?? "").Trim();
        if (prompt.StartsWith("```", StringComparison.Ordinal))
        {
            int open = prompt.IndexOf('\n', StringComparison.Ordinal);
            prompt = open < 0 ? "" : prompt[(open + 1)..];
            int close = prompt.LastIndexOf("```", StringComparison.Ordinal);
            prompt = (close < 0 ? prompt : prompt[..close]).Trim();
        }

        if (prompt.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
        {
            prompt = prompt["prompt:".Length..].Trim();
        }

        if (prompt.Length >= 2 && ((prompt[0] == '"' && prompt[^1] == '"') || (prompt[0] == '\'' && prompt[^1] == '\'')))
        {
            prompt = prompt[1..^1].Trim();
        }

        return prompt;
    }

    /// <summary>The spinner while a held reply is written unseen (<c>Botchat image async</c> off, later on 2026-09-25).</summary>
    public static string ThinkingSpinner(string speaker) => $"{speaker} is thinking…";

    /// <summary>A held turn's reply as the screen will show it: its text deltas joined, trimmed (later on 2026-09-25). Pure.</summary>
    public static string ReplyText(IEnumerable<TurnEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return string.Concat(events.OfType<TurnEvent.TextDelta>().Select(delta => delta.Text)).Trim();
    }

    /// <summary>The spinner while the model writes a reply's image prompt (2026-09-25).</summary>
    public static string PromptSpinner(string speaker) => $"Imagining {speaker}'s picture…";

    /// <summary>The line over an app's picture drawn after later lines (<c>Botchat image async</c>, 2026-09-25): whose reply it pictures. Pinned.</summary>
    public static string PictureNotice(string speaker) => $"(botchat: {speaker}'s picture)";

    /// <summary>
    /// Once per chat, when pictures are on but no picture can be made (2026-09-25; since 2026-09-27: no txt2img workflow set, nor an
    /// img2img one with a picture to rework; since 2026-10-04: none in the chat's set, <see cref="ComfyWorkflows"/>). Pinned.
    /// </summary>
    public const string NoWorkflowNotice = "(botchat: Botchat ComfyUI enabled and Botchat ComfyUI limited workflows give no installed txt2img workflow, so there are no pictures of the replies)";

    /// <summary>The <c>--log</c> warning when the image-prompt writer named no workflow of a kind with several (2026-10-04): the first is used.</summary>
    public static string UnnamedWorkflowLogLine(string speaker, string workflow) =>
        $"The botchat image prompt for {speaker}'s reply named no workflow of the chat's set; using the first, {workflow}.";

    /// <summary>When the model wrote no image prompt for a reply (2026-09-25). Pinned.</summary>
    public const string NoPromptNotice = "(botchat: no image prompt came back for that reply; no picture)";

    /// <summary>The <c>--log</c> line of an image prompt (2026-09-25).</summary>
    public static string ImagePromptLogLine(string speaker, string workflow, string prompt) => $"Botchat picture of {speaker}'s reply on {workflow}: {prompt}";

    /// <summary>The <c>--log</c> line of a rework's prompt (2026-09-27).</summary>
    public static string ReworkLogLine(string speaker, string workflow, ReworkPicture picture, string prompt) =>
        $"Botchat picture of {speaker}'s reply on {workflow}, reworking {picture.Path}: {prompt}";

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

    public const string UsageError = "Usage: /botchat [profile ...] [[--] topic]: names two or more profiles (this one always joins), or none for every profile; after --, the rest is the topic whatever its first word; ESC stops the voice, then cuts the bot replying short, then ends the chat, and /botchat --resume [line] carries it on.";

    public const string TooFewError = "/botchat needs at least two profiles: add one with /profile add <name>.";

    /// <summary>The argument list's note beside a profile name.</summary>
    public const string ProfileNote = "add this profile to the chat";

    /// <summary>The line above the chat: the cast and how to stop it.</summary>
    public static string StartNotice(IReadOnlyList<string> names, string topic) =>
        topic.Length > 0
            ? $"(botchat: {JoinNames(names)} on \"{topic}\"; type to join in; ESC stops the voice, then the reply, then the chat)"
            : $"(botchat: {JoinNames(names)}; type to join in; ESC stops the voice, then the reply, then the chat)";

    /// <summary>The notice under a bot's reply the ESC ladder cut short (2026-09-25): the chat goes on with the next bot. Pinned.</summary>
    public static string CutShortNotice(string name) => $"(botchat: {name} cut short)";

    /// <summary>The line when the chat ends: how many replies it ran to.</summary>
    public static string StoppedNotice(int replies) => $"(botchat stopped after {UsageText.Plural(replies, "reply", "replies")})";

    /// <summary>A profile whose <c>profile.json</c> could not be read sits the chat out.</summary>
    public static string SkippedNotice(string name, string reason) => $"(botchat: {name} sits this one out: {reason})";

    /// <summary>The spinner while the bots' own LLMs are reached (<c>Botchat LLM mode</c> <c>multi</c>, 2026-09-25). Pinned.</summary>
    public const string LinkingSpinner = "Reaching the bots' LLMs…";

    /// <summary>A bot on its own LLM (<c>Botchat LLM mode</c> <c>multi</c>, 2026-09-25): where it talks, once per chat. Pinned.</summary>
    public static string LinkNotice(string name, Uri server, string model, string reasoning) =>
        $"(botchat: {name} on {server} model={model} reasoning={reasoning})";

    // ── Embedded bots (later on 2026-09-29) ────────────────────────────────

    /// <summary>
    /// The warning for a bot whose profile names another embedded model than the one running (<c>Botchat multi-embedded</c>
    /// <c>parent-server</c>, later on 2026-09-29, the user's ask): it talks through the running one. Pinned.
    /// </summary>
    public static string SharedEmbeddedWarning(string name, string wanted, string running) =>
        $"(botchat: {name} wanted {wanted}, but one embedded server runs {running}, so {name} uses it; Botchat multi-embedded multi-server gives it its own)";

    /// <summary>The first word that stops a multi-server botchat's extra embedded servers (later on 2026-09-29, the user's ask). A whole word, as <see cref="ResumeSwitch"/>. Pinned.</summary>
    public const string KillSwitch = "--kill";

    /// <summary>The argument list's note beside <see cref="KillSwitch"/>.</summary>
    public const string KillNote = "stop the extra embedded servers of multi-server botchats";

    /// <summary>Whether <paramref name="args"/> ask for the kill: <see cref="KillSwitch"/> as the first word, ignoring case; <c>After</c> is what follows (the usage error when not empty). Pure.</summary>
    public static (bool Kill, string After) ParseKill(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string rest = args.Trim();
        int split = rest.IndexOfAny([' ', '\t']);
        string word = split < 0 ? rest : rest[..split];
        return string.Equals(word, KillSwitch, StringComparison.OrdinalIgnoreCase)
            ? (true, split < 0 ? "" : rest[(split + 1)..].Trim())
            : (false, "");
    }

    /// <summary><c>/botchat --kill</c> with more words after it. Pinned.</summary>
    public const string KillUsageError = "Usage: /botchat --kill, alone: it stops the extra embedded servers a multi-server botchat left running.";

    /// <summary>The extra servers stopped, at a chat's end (Botchat multi-embedded kill on) or by <c>/botchat --kill</c>: how many, and their models. Pinned.</summary>
    public static string ExtrasStoppedNotice(IReadOnlyList<string> models) =>
        $"(botchat: stopped {UsageText.Plural(models.Count, "extra embedded server", "extra embedded servers")}: {JoinNames(models)})";

    /// <summary><c>/botchat --kill</c> with none running. Pinned.</summary>
    public const string NoExtrasNotice = "(botchat: no extra embedded server is running)";

    // ── Resuming (2026-09-25) ───────────────────────────────────────────────

    /// <summary>
    /// The first word that carries on the last chat of this run (2026-09-25, the user's ask: a stopped chat could only start over).
    /// A whole word of its own, so it never reads as a profile's name nor as <see cref="TopicSeparator"/>. Pinned.
    /// </summary>
    public const string ResumeSwitch = "--resume";

    /// <summary>The argument list's note beside <see cref="ResumeSwitch"/>.</summary>
    public const string ResumeNote = "carry on the last botchat of this run";

    /// <summary>
    /// Whether <paramref name="args"/> resume the last chat: <see cref="ResumeSwitch"/> as the first word, ignoring case; the rest
    /// of the line is <c>Line</c>, which joins the chat as the user's before the next reply (empty for none). Pure.
    /// </summary>
    public static (bool Resume, string Line) ParseResume(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string rest = args.Trim();
        int split = rest.IndexOfAny([' ', '\t']);
        string word = split < 0 ? rest : rest[..split];
        return string.Equals(word, ResumeSwitch, StringComparison.OrdinalIgnoreCase)
            ? (true, split < 0 ? "" : rest[(split + 1)..].Trim())
            : (false, "");
    }

    /// <summary>When there is no chat of this run to resume.</summary>
    public const string NothingToResumeError = "There is no /botchat to resume in this run: start one with /botchat.";

    /// <summary>When the chat to resume was started from another profile than the one loaded now.</summary>
    public static string ResumeOtherProfileError(string starter) =>
        $"The last /botchat was started from the profile '{starter}': load it again with /profile to resume it.";

    /// <summary>The line above a resumed chat: the cast, the replies so far, and how to stop it.</summary>
    public static string ResumeNotice(IReadOnlyList<string> names, string topic, int replies)
    {
        string about = topic.Length > 0 ? $" on \"{topic}\"" : "";
        return $"(botchat resumed: {JoinNames(names)}{about}, {UsageText.Plural(replies, "reply", "replies")} so far; type to join in; ESC stops the voice, then the reply, then the chat)";
    }

    /// <summary>The session's title (2026-09-24): <c>Botchat: neon, ada and max</c>.</summary>
    public static string SessionTitle(IReadOnlyList<string> names) => "Botchat: " + JoinNames(names);

    /// <summary>The <c>--log</c> line per turn.</summary>
    public static string TurnLogLine(int n, string speaker) => string.Create(CultureInfo.InvariantCulture, $"Botchat turn {n}: {speaker}");

    /// <summary>The most pictures one turn message carries with <c>Botchat vision enabled</c> (2026-09-27): the newest; a local vision server pays for each.</summary>
    public const int MaxVisionPictures = 4;

    /// <summary>The longer side of the camera's picture a bot is shown (<c>Botchat camera</c>, 2026-10-02): enough to see the user, light on a local server.</summary>
    public const int CameraMaxSide = 1024;

    /// <summary>How long a bot's turn waits for the camera's picture before it goes without (2026-10-02).</summary>
    public static readonly TimeSpan CameraWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The line the turn text ends with when the camera's picture rides along (2026-10-02): its place among the attached pictures
    /// (<paramref name="number"/> of <paramref name="count"/>, always the last) and whose it is — a photo of <see cref="UserName"/>,
    /// not from any bot (later that day, the user's report: "the user's camera" alone was read as another bot's picture). Pinned:
    /// it is prompt text.
    /// </summary>
    public static string CameraCaption(int number, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"(Picture {number} of {count}, attached last: a live photo of {UserName}, the human in this chat, from their webcam just now — not from any of the bots.)");

    /// <summary>
    /// What one bot's turn message carries (later on 2026-10-02): the chat's pictures since it last spoke when
    /// <paramref name="vision"/> is on (<see cref="PicturesFor"/>, one slot fewer when <paramref name="camera"/> is there, so the
    /// caption names exactly the pictures attached), then the camera's picture last, and the captions to add to the turn text
    /// (empty when nothing rides along). Pure.
    /// </summary>
    public static (IReadOnlyList<Files.ImageAttachment> Images, string Captions) TurnPictures(string speaker, IReadOnlyList<BotPicture>? log, int seen, bool vision, Files.ImageAttachment? camera)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        IReadOnlyList<Files.ImageAttachment> images = [];
        string captions = "";
        if (vision && log is { Count: > 0 })
        {
            var shown = PicturesFor(speaker, log, seen, camera is null ? MaxVisionPictures : MaxVisionPictures - 1);
            if (shown.Count > 0)
            {
                captions = "\n\n" + PicturesCaption(speaker, shown);
                images = shown.SelectMany(p => p.Images).ToList();
            }
        }

        if (camera is not null)
        {
            images = WithCamera(images, camera);
            captions += "\n\n" + CameraCaption(images.Count, images.Count);
        }

        return (images, captions);
    }

    /// <summary>
    /// The chat's pictures with the camera's after them (2026-10-02): the camera's last, the newest of the others kept so the
    /// message carries at most <paramref name="max"/>. Pure.
    /// </summary>
    public static IReadOnlyList<Files.ImageAttachment> WithCamera(IReadOnlyList<Files.ImageAttachment> seen, Files.ImageAttachment camera, int max = MaxVisionPictures)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(camera);
        return [.. seen.TakeLast(Math.Max(0, max - 1)), camera];
    }

    /// <summary>
    /// The pictures a bot is shown on its turn (<c>Botchat vision enabled</c>, 2026-09-27): those logged after
    /// <paramref name="seen"/> (the last <see cref="BotPicture.Seq"/> it was shown), less the ones it drew itself with
    /// <c>generate_image</c> — it saw those as it drew them — the app's picture of its own reply kept (it never saw that).
    /// The newest entries whose pictures come to at most <paramref name="max"/>, oldest first; one entry over the cap is
    /// cut to its newest pictures. Pure.
    /// </summary>
    public static IReadOnlyList<BotPicture> PicturesFor(string speaker, IReadOnlyList<BotPicture> log, int seen, int max = MaxVisionPictures)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(log);
        var kept = new List<BotPicture>();
        int room = max;
        foreach (var picture in log.Where(p => p.Seq > seen && p.Images.Count > 0 && !(p.Drawn && Profiles.NameEquals(p.Owner, speaker))).Reverse())
        {
            if (room <= 0)
            {
                break;
            }

            kept.Add(picture.Images.Count <= room ? picture : picture with { Images = picture.Images.TakeLast(room).ToList() });
            room -= kept[^1].Images.Count;
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>
    /// The line the turn text ends with when pictures ride along (2026-09-27): what they are and whose, in the order
    /// attached. Pinned: it is prompt text.
    /// </summary>
    public static string PicturesCaption(string speaker, IReadOnlyList<BotPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        var names = pictures.Select(p =>
        {
            bool mine = Profiles.NameEquals(p.Owner, speaker);
            string count = p.Images.Count == 1 ? "" : p.Images.Count.ToString(CultureInfo.InvariantCulture) + " ";
            string plural = p.Images.Count == 1 ? "picture" : "pictures";
            return p.Drawn ? $"{p.Owner}'s {count}{plural}"
                : $"the {count}{plural} of {(mine ? "your" : p.Owner + "'s")} reply";
        });
        return "(Attached, oldest first: the pictures shown in the chat since you last spoke — " + string.Join(", ", names) + ".)";
    }
}

/// <summary>
/// A picture shown in a <c>/botchat</c> (2026-09-27, <c>Botchat vision enabled</c>): its place in the chat's picture log,
/// the bot it belongs to — the one whose reply the app pictured, or the one that drew it (<paramref name="Drawn"/>, its own
/// <c>generate_image</c>) — and the pictures.
/// </summary>
public sealed record BotPicture(int Seq, string Owner, bool Drawn, IReadOnlyList<Files.ImageAttachment> Images);

/// <summary>
/// A picture a <c>/botchat</c> rework may start from (2026-09-27, <see cref="BotChat.ReworkCandidates"/>): its number in the
/// list the model is shown, whose it is (<see cref="BotPicture"/>'s owner and kind) and its path under the working directory.
/// </summary>
public sealed record ReworkPicture(int Number, string Owner, bool Drawn, string Path);

/// <summary>One line of the shared botchat transcript: a bot's reply (<paramref name="Speaker"/> its profile) or the user's interjection.</summary>
public sealed record BotChatLine(string Speaker, string Text, bool IsUser = false);

/// <summary>
/// A stopped <c>/botchat</c>, kept for <see cref="BotChat.ResumeSwitch"/> (2026-09-25): in memory, this run only. The profile it
/// was started from, the cast's names in order, the topic, the shared lines, who spoke last — a name, so it maps onto a cast
/// rebuilt without one of them — the replies so far and its session row, if any.
/// </summary>
public sealed record BotChatState(string Starter, IReadOnlyList<string> Cast, string Topic, IReadOnlyList<BotChatLine> Lines, string? LastSpeaker, int Replies, long? SessionId);

/// <summary>
/// One bot of the chat: its profile's name, its persona (null = the default) and <c>vocalia.md</c> directive,
/// its voice (the <see cref="NeonSidekick.Speech.VoiceMix.Spec"/> of its TTS voices) and speed, and which of the palette's
/// speaker colours its name takes (<see cref="BotChat"/>'s screen picks the colour, so a theme change follows), and
/// its <see cref="BotGender"/> from its first voice (<see cref="BotChat.GenderOf"/>, 2026-09-25), and — for every bot but the
/// starter — its saved <paramref name="Profile"/>, whose LLM settings <c>Botchat LLM mode</c> <c>multi</c> reads (later on 2026-09-25).
/// </summary>
public sealed record BotParticipant(string Name, string? Persona, string? VoiceDirective, string Voice, double Speed, int ColorIndex, BotGender Gender = BotGender.Female, NeonSidekick.Settings.AppSettingsData? Profile = null);

/// <summary>A bot's gender, for the pronouns the others use (2026-09-25): from its first voice, female unless the voice says male.</summary>
public enum BotGender
{
    Female,
    Male,
}
