using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Llm;

/// <summary>
/// The shape of one turn as its events tell it: how many tools the model called on its own, how
/// many of those answered with an error, whether the turn went on to succeed after the last error,
/// and whether it wrote a skill. Fed one <see cref="TurnEvent"/> at a time by the screen's event
/// loop (<see cref="Observe"/>); read by <c>SkillLearner.ShouldLearn</c> once the turn is over.
/// Only the model's own calls count: an opening pair (<see cref="Assistant.IsOpeningCallId"/> — the
/// clock, the working directory, the memory) is the app's doing and says nothing about the task. An error is a result whose text starts with <c>Error</c> — the
/// app's one convention for a tool's refusal, an unknown tool, unparseable arguments and a throwing
/// tool alike (<see cref="Assistant.InvokeToolAsync"/>). Pure; never touches the console.
/// </summary>
public sealed class TurnTrace
{
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal);
    private readonly List<string> _loaded = [];
    private readonly List<string> _names = [];

    /// <summary>The prefix every tool error result carries, seeded and model calls alike.</summary>
    public const string ErrorPrefix = "Error";

    /// <summary>The model's own tool calls (seeded pairs excluded).</summary>
    public int ToolCalls { get; private set; }

    /// <summary>Of those, the calls that answered with an error.</summary>
    public int Errors { get; private set; }

    /// <summary>Whether a call succeeded after the last error — the turn found its way past it.</summary>
    public bool Recovered { get; private set; }

    /// <summary>Whether a <c>skill_editor</c> call of the model's own succeeded: the turn kept its own lesson.</summary>
    public bool WroteSkill { get; private set; }

    /// <summary>The skills the model loaded itself, in order, for the log line.</summary>
    public IReadOnlyList<string> LoadedSkills => _loaded;

    private readonly Dictionary<string, int> _errorsAfter = new(StringComparer.Ordinal);

    /// <summary>
    /// Each skill the model loaded, by name, and the error results that came after its first load (2026-10-02, the reflection
    /// audit: a turn's errors before the load say nothing about the skill). What the skill records' <c>used</c> event keeps.
    /// </summary>
    public IReadOnlyDictionary<string, int> ErrorsAfterLoad => _errorsAfter;

    /// <summary>The distinct names of the model's own calls in first-call order (seeded pairs excluded) — the session store's <c>tool_names</c> (2026-09-19).</summary>
    public IReadOnlyList<string> ToolNames => _names;

    /// <summary>Counts <paramref name="evt"/>; every other event is ignored.</summary>
    public void Observe(TurnEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case TurnEvent.ToolCall call:
                if (Assistant.IsOpeningCallId(call.CallId))
                {
                    _seeded.Add(call.CallId);
                }
                else
                {
                    ToolCalls++;
                    if (!_names.Contains(call.Name, StringComparer.Ordinal))
                    {
                        _names.Add(call.Name);
                    }
                }

                break;

            case TurnEvent.ToolResult result:
                if (_seeded.Contains(result.CallId))
                {
                    break;
                }

                bool error = IsError(result.Text);
                if (error)
                {
                    Errors++;
                    Recovered = false;
                    foreach (string loaded in _errorsAfter.Keys.ToList())
                    {
                        _errorsAfter[loaded]++;
                    }
                }
                else
                {
                    if (Errors > 0)
                    {
                        Recovered = true;
                    }

                    if (string.Equals(result.Name, SkillEditorTool.ToolName, StringComparison.Ordinal))
                    {
                        WroteSkill = true;
                    }
                    else if (string.Equals(result.Name, LoadSkillTool.ToolName, StringComparison.Ordinal) && LoadedName(result.Text) is { } name)
                    {
                        _loaded.Add(name);
                        _errorsAfter.TryAdd(name, 0);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Folds a finished turn's trace into this one, the running tally across the turns since the
    /// last reflection (the <c>Reflection window</c>'s trigger): the calls add up; a turn with
    /// errors brings its own count and its own verdict on recovery (its last error's); a clean turn
    /// with calls after an unrecovered error is the recovery; a written skill sticks; the loaded
    /// names follow in order.
    /// </summary>
    public void Absorb(TurnTrace turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ToolCalls += turn.ToolCalls;
        if (turn.Errors > 0)
        {
            Errors += turn.Errors;
            Recovered = turn.Recovered;
        }
        else if (turn.ToolCalls > 0 && Errors > 0)
        {
            Recovered = true;
        }

        WroteSkill |= turn.WroteSkill;
        _loaded.AddRange(turn._loaded);
        foreach (var (name, errors) in turn._errorsAfter)
        {
            _errorsAfter[name] = _errorsAfter.GetValueOrDefault(name) + errors;
        }

        foreach (string name in turn._names)
        {
            if (!_names.Contains(name, StringComparer.Ordinal))
            {
                _names.Add(name);
            }
        }

        Turns++;
    }

    /// <summary>How many turns <see cref="Absorb"/> folded in; zero for a single turn's own trace.</summary>
    public int Turns { get; private set; }

    /// <summary>Whether a tool result's text is an error by the app's convention (<c>Error:</c> — every tool sentence and <see cref="Assistant.ToolFailed"/> alike since 2026-09-18).</summary>
    public static bool IsError(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.StartsWith(ErrorPrefix, StringComparison.Ordinal);
    }

    /// <summary>The name a successful <c>load_skill</c> result carries in its opening tag (<c>&lt;skill_content name="…"&gt;</c>); null for any other shape.</summary>
    internal static string? LoadedName(string text)
    {
        const string open = "<skill_content name=\"";
        if (!text.StartsWith(open, StringComparison.Ordinal))
        {
            return null;
        }

        int end = text.IndexOf('"', open.Length);
        return end > open.Length ? text[open.Length..end] : null;
    }

    /// <summary>The figures on one line for the log: <c>7 tool calls, 1 error, recovered, loaded: docker-deploy</c>. Pinned.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4)
        {
            ToolCalls == 1 ? "1 tool call" : ToolCalls.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tool calls",
            Errors == 1 ? "1 error" : Errors.ToString(System.Globalization.CultureInfo.InvariantCulture) + " errors",
        };
        if (Recovered)
        {
            parts.Add("recovered");
        }

        if (WroteSkill)
        {
            parts.Add("wrote a skill");
        }

        if (_loaded.Count > 0)
        {
            parts.Add("loaded: " + string.Join(", ", _loaded));
        }

        if (Turns > 1)
        {
            parts.Add("over " + Turns.ToString(System.Globalization.CultureInfo.InvariantCulture) + " turns");
        }

        return string.Join(", ", parts);
    }
}
