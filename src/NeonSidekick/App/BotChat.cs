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
/// <c>LLM URL</c> and <c>LLM model</c> are never read here. The one exception to "no tools" (2026-09-25, the
/// user's ask): with the Botchat tab's <c>Botchat images enabled</c> on and ComfyUI offered, pictures — the
/// bots' own <c>generate_image</c> (<see cref="ImageRule"/>) or the app's picture of each reply
/// (<see cref="ImagePromptInstruction"/>), as <c>Botchat image mode</c> says.</para>
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
    /// profile has no <c>persona.md</c>) through <see cref="Assistant.SystemPrompt"/> with every tool off, then
    /// <see cref="Rules"/>. Built afresh for every turn from that speaker's persona alone. With <paramref name="skills"/>
    /// (<c>Botchat skills enabled</c>, 2026-09-27: the main chat's catalog — the starting profile's, the global and the
    /// external skills, never the speaker's own profile's) the load-only skills block
    /// (<see cref="Skills.SkillsPrompt.LoadOnlySection"/>) goes between the two; null or empty, the prompt is as before.
    /// <paramref name="preloaded"/> (<see cref="PreloadedSkillsSection"/>, 2026-09-27, <c>Botchat skill mode</c>
    /// <c>prompt-writer-and-bots</c>) follows that block, before the rules. Pure.
    /// </summary>
    public static string SystemPrompt(string? persona, string speaker, IReadOnlyList<string> others, string topic, bool speechOutput, string? voiceDirective, bool markdown, string? pronouns = null, bool images = false, IReadOnlyList<Skills.Skill>? skills = null, string? preloaded = null) =>
        Assistant.SystemPrompt(speechOutput, memories: null, persona: string.IsNullOrWhiteSpace(persona) ? null : persona, voiceDirective: voiceDirective,
            tools: false, files: false, timers: false, markdown: markdown)
        + (skills is { Count: > 0 } ? "\n\n" + Skills.SkillsPrompt.LoadOnlySection(skills) : "")
        + (string.IsNullOrEmpty(preloaded) ? "" : "\n\n" + preloaded)
        + "\n\n" + Rules(speaker, others, topic, pronouns, images);

    // ── Preloaded skills (2026-09-27) ───────────────────────────────────────

    /// <summary>
    /// The skills <c>/botchat</c> loads itself (2026-09-27, the user's report: told to load a skill, the models mostly did not):
    /// those <paramref name="setting"/> (<c>Botchat preloaded skills</c>) names that are in <paramref name="catalog"/>, then any
    /// catalog skill whose name <paramref name="topic"/> spells out as a whole word — any case, a hyphen part of the name, so
    /// <c>pony</c> is not <c>pony-prompts</c> — each once, in the catalog's order. Pure.
    /// </summary>
    public static IReadOnlyList<Skills.Skill> PreloadedSkills(IReadOnlyList<Skills.Skill> catalog, IReadOnlyList<string>? setting, string topic)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(topic);
        var named = (setting ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog.Where(s => named.Contains(s.Name) || NamedIn(topic, s.Name)).ToList();
    }

    /// <summary>
    /// <paramref name="catalog"/> without the skills <paramref name="preloaded"/> names (2026-09-30, the user's question: both
    /// <c>Botchat preloaded skills</c> and <c>Botchat skills enabled</c> on): a skill whose content a prompt already carries is
    /// not listed there to load again. Null for no catalog or none left. Pure.
    /// </summary>
    public static IReadOnlyList<Skills.Skill>? WithoutPreloaded(IReadOnlyList<Skills.Skill>? catalog, IReadOnlyList<string> preloaded)
    {
        ArgumentNullException.ThrowIfNull(preloaded);
        return catalog?.Where(s => !preloaded.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } kept ? kept : null;
    }

    private static bool NamedIn(string topic, string name) =>
        name.Length > 0 && Regex.IsMatch(topic, @"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The preloaded skills as prompt text (2026-09-27): a line saying they are loaded and to be followed, then each skill's
    /// content as <c>load_skill</c> returns it. Empty for none. Pinned: it is prompt text.
    /// </summary>
    public static string PreloadedSkillsSection(IReadOnlyList<string> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return contents.Count == 0 ? "" : PreloadedSkillsLead + "\n\n" + string.Join("\n\n", contents);
    }

    /// <summary>
    /// How much of a preloaded skill's bundled text files goes in with it (2026-09-30, the user's ask: all the files, not the
    /// SKILL.md alone): one <c>load_skill</c> file's worth for all of them together, so a skill of fifty files cannot fill a
    /// local model's context in every bot's prompt. A file past it is left out; the skill's file list still names it.
    /// </summary>
    public const int MaxPreloadedFileChars = Skills.SkillCatalog.MaxResourceChars;

    /// <summary>The <c>--log</c> line for the files a preloaded skill left out past <see cref="MaxPreloadedFileChars"/> (2026-09-30).</summary>
    public static string PreloadedFilesLeftOutLogLine(string skill, IReadOnlyList<string> files) =>
        $"Botchat preloaded skill '{skill}' left out {string.Join(", ", files)}: its files are capped at {MaxPreloadedFileChars.ToString("N0", CultureInfo.InvariantCulture)} characters together.";

    /// <summary>The first line of <see cref="PreloadedSkillsSection"/>. Pinned: it is prompt text.</summary>
    public const string PreloadedSkillsLead = "These skills are loaded for you already; follow their instructions:";

    /// <summary>The line the chat shows when its preloaded skills are first read, or change (2026-09-27). Pinned.</summary>
    public static string PreloadedNotice(IReadOnlyList<string> names, BotSkillMode mode)
    {
        ArgumentNullException.ThrowIfNull(names);
        string whom = mode == BotSkillMode.PromptWriterOnly ? "for the picture prompts" : "for the picture prompts and the bots";
        return $"(botchat: skills loaded {whom}: {string.Join(", ", names)})";
    }

    /// <summary>
    /// The group-chat rules after the persona (2026-09-24): who else is in the room, speak only as yourself,
    /// no name prefix, short replies, keep it going — small local models otherwise drift into agreeing and
    /// saying goodbye. <paramref name="pronouns"/> (<see cref="PronounsLine"/>, 2026-09-25) follows the first
    /// sentence when given; <paramref name="images"/> (the bots offered <c>generate_image</c>, 2026-09-25) closes
    /// it with <see cref="ImageRule"/>. Pinned: it is prompt text.
    /// </summary>
    public static string Rules(string speaker, IReadOnlyList<string> others, string topic, string? pronouns = null, bool images = false)
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

        return text.ToString();
    }

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
    /// The workflows a fresh <c>/botchat</c> picture may use (2026-09-25; <c>Botchat txt2img workflow</c>'s list since
    /// 2026-09-27): those of <paramref name="workflows"/> (the offered ones) that take a prompt and no input picture — text →
    /// image — in the order given. Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> Txt2ImgWorkflows(IReadOnlyList<ComfyWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return workflows.Where(w => w.ImageCount == 0 && w.TakesPrompt).ToList();
    }

    /// <summary>
    /// The workflows a <c>/botchat</c> rework may use (2026-09-27, <c>Botchat img2img workflow</c>'s list): those of
    /// <paramref name="workflows"/> that take a prompt and exactly one input picture — image → image — in the order given. Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> Img2ImgWorkflows(IReadOnlyList<ComfyWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return workflows.Where(w => w.ImageCount == 1 && w.TakesPrompt).ToList();
    }

    /// <summary>
    /// The workflow of a fresh <c>/botchat</c> picture (<c>Botchat txt2img workflow</c>): the one <paramref name="setting"/> names
    /// (ignoring case) when it is among <see cref="Txt2ImgWorkflows"/> of <paramref name="installed"/> — every installed workflow,
    /// offered or not (later on 2026-09-27, the user's call); null — none — when blank or not among them (2026-09-27, the
    /// user's call: blank was the first of them until then). Pure.
    /// </summary>
    public static ComfyWorkflow? Txt2ImgWorkflow(IReadOnlyList<ComfyWorkflow> installed, string? setting) => Named(Txt2ImgWorkflows(installed), setting);

    /// <summary>The workflow of a <c>/botchat</c> rework (<c>Botchat img2img workflow</c>, 2026-09-27): as <see cref="Txt2ImgWorkflow"/>, among <see cref="Img2ImgWorkflows"/>. Pure.</summary>
    public static ComfyWorkflow? Img2ImgWorkflow(IReadOnlyList<ComfyWorkflow> installed, string? setting) => Named(Img2ImgWorkflows(installed), setting);

    private static ComfyWorkflow? Named(IReadOnlyList<ComfyWorkflow> usable, string? setting)
    {
        string name = setting?.Trim() ?? "";
        return name.Length == 0 ? null : usable.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The workflows the bots' own <c>generate_image</c> sees (2026-09-27, the user's ask: <c>autonomous</c> limited as <c>automatic</c>
    /// is): <paramref name="fresh"/> and, while there is a picture to rework, <paramref name="rework"/>, of those still installed
    /// (offered or not, later on 2026-09-27). Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> BotWorkflows(IReadOnlyList<ComfyWorkflow> installed, ComfyWorkflow? fresh, ComfyWorkflow? rework, bool reworkable)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return installed.Where(w => (fresh is not null && string.Equals(w.Name, fresh.Name, StringComparison.OrdinalIgnoreCase))
            || (reworkable && rework is not null && string.Equals(w.Name, rework.Name, StringComparison.OrdinalIgnoreCase))).ToList();
    }

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

    /// <summary>
    /// The image-prompt system message with a rework on offer (2026-09-27, the user's ask: after the first picture the prompt
    /// writer chooses a fresh picture or a rework): <paramref name="fresh"/> null means the rework is the only way; without
    /// <paramref name="rework"/> or a candidate it is <see cref="ImagePromptInstruction(ComfyWorkflow)"/> (or
    /// <see cref="PromisedPictureInstruction(ComfyWorkflow)"/>) as before. <paramref name="promised"/> is the promised-picture
    /// request's form, <see cref="NoPictureAnswer"/> kept. Pinned: it is prompt text.
    /// </summary>
    public static string PictureInstruction(bool promised, ComfyWorkflow? fresh, ComfyWorkflow? rework, IReadOnlyList<ReworkPicture> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (rework is null || candidates.Count == 0)
        {
            var only = fresh ?? throw new ArgumentException("Neither a fresh workflow nor a picture to rework.", nameof(fresh));
            return promised ? PromisedPictureInstruction(only) : ImagePromptInstruction(only);
        }

        var text = new StringBuilder("You write prompts for an image generator. Given one line of a group chat, ");
        if (promised)
        {
            text.Append("decide whether its speaker says they are drawing, painting, sketching, generating, making, sharing or showing " + PictureWords + " right now. ")
                .Append(CultureInfo.InvariantCulture, $"If they do not, answer exactly {NoPictureAnswer}. If they do, ");
        }

        const string NoChat = "never the chat itself, no speech bubbles, no screens of text. ";
        const string Keep = "keep what it shows where the line does not change it, and change what the line calls for";
        if (fresh is not null)
        {
            text.Append(promised ? "write a single prompt for the picture they describe — what it shows, its mood — " : "write a single prompt for a picture that illustrates it: the scene, the things or ideas it talks about, its mood — ")
                .Append(NoChat).Append("Write it in this style: ");
            AppendStyle(text, fresh, "Tips for this workflow: ");
            text.Append("\n\nYou may instead rework ").Append(WhichPicture(candidates))
                .Append(" rather than make a new one, when the line talks about it, answers it or builds on it: ").Append(Keep).Append(". ");
        }
        else
        {
            text.Append("write a single prompt that reworks ").Append(WhichPicture(candidates))
                .Append(promised ? " into the picture they describe: " : " so that it illustrates the line: ").Append(Keep).Append(" — ").Append(NoChat);
        }

        if (candidates.Count > 1)
        {
            text.Append("The pictures, oldest first:");
            foreach (var picture in candidates)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n{picture.Number}. {DescribePicture(picture)}");
            }

            text.Append("\n");
        }

        text.Append(fresh is not null ? "To rework it, put " : "Put ").Append(ReworkLine(candidates))
            .Append(" alone on the first line of your answer and the prompt after it. Write the rework's prompt in this style: ");
        AppendStyle(text, rework, "Tips for the rework workflow: ");
        text.Append("\n\nAnswer with ").Append(fresh is not null ? "the prompt alone, or the " + ReworkAnswer + " line and then the prompt" : "the " + ReworkAnswer + " line and then the prompt")
            .Append(promised ? ", or " + NoPictureAnswer : "").Append(": no preamble, no explanation, no quotes.");
        return text.ToString();
    }

    /// <summary>
    /// An image-prompt answer read (2026-09-27): a first line <see cref="ReworkAnswer"/> (a number after it, stray marks around
    /// it) takes that candidate — a number that names none takes the latest — and the rest is the prompt; no such line is a fresh
    /// picture when <paramref name="fresh"/>, else a rework of the latest. No candidate, no rework. The prompt is
    /// <see cref="CleanImagePrompt"/>'s. Pure.
    ///
    /// <para>The word is <c>REWORK</c> in capitals, as the request spells it, or in any case only with a number, a <c>#</c>, a mark
    /// or the line's end after it (2026-09-28, code review): with the case ignored, a fresh prompt such as <c>Rework of an old
    /// castle at dusk</c> reworked the latest picture and lost its first word. <c>**Rework #1:**</c> is still read.</para>
    /// </summary>
    public static (string Prompt, ReworkPicture? Rework) ParseImagePrompt(string? text, IReadOnlyList<ReworkPicture> candidates, bool fresh)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        string cleaned = CleanImagePrompt(text);
        int end = cleaned.IndexOf('\n', StringComparison.Ordinal);
        string first = end < 0 ? cleaned : cleaned[..end];
        var match = ReworkFirstLine().Match(first);
        if (!match.Success)
        {
            return (cleaned, fresh ? null : candidates.LastOrDefault());
        }

        string rest = (match.Groups["rest"].Value + (end < 0 ? "" : cleaned[end..])).Trim();
        var chosen = int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= candidates.Count
            ? candidates[n - 1]
            : candidates.LastOrDefault();
        return (CleanImagePrompt(rest), chosen);
    }

    [GeneratedRegex(@"^\W*(?:REWORK\b|(?i:rework)(?=\s*(?:[#:*.\-–—\d]|$)))[\s#:*.\-–—]*(?<n>\d+)?[\s:*.\-–—]*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReworkFirstLine();

    /// <summary>
    /// The line a bot's turn text ends with while <c>generate_image</c> may rework (2026-09-27, <c>autonomous</c>): the pictures
    /// it may start from, whose and their paths, and how to call it. The stored line stays as it was. Pinned: it is prompt text.
    /// </summary>
    public static string ReworkCaption(string workflow, IReadOnlyList<ReworkPicture> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        string how = $"call generate_image with workflow \"{workflow}\", its path as image, and a prompt for the reworked picture";
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
    /// answering as the instruction above says. Since 2026-09-30 (code review) it says a preloaded skill needs no load: the skill
    /// the topic names is the one preloaded, and the directive alone sent the writer to load it again. Pinned: it is prompt text.
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
        + "and follow it; a skill already loaded for you above needs no " + LoadSkillTool.ToolName + ". Then answer exactly as instructed above; never mention the skill in the answer.";

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

    /// <summary>Once per chat, when pictures are on but no picture can be made (2026-09-25; since 2026-09-27: no txt2img workflow set, nor an img2img one with a picture to rework). Pinned.</summary>
    public const string NoWorkflowNotice = "(botchat: no Botchat txt2img workflow is set — or it is no longer installed — so there are no pictures of the replies)";

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

    /// <summary>The line the turn text ends with when the camera's picture rides along (2026-10-02). Pinned: it is prompt text.</summary>
    public const string CameraCaption = "(Attached last: the user's camera, just now — what they are doing as you speak. React to it only when it adds something.)";

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
