using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Help;

/// <summary>A command-line flag <c>neon_help</c> knows: the flag, its other spellings, and the setting it overrides for the launch, if any.</summary>
internal sealed record HelpFlag(string Flag, IReadOnlyList<string> Aliases, SettingsField? Overrides);

/// <summary>
/// The command line's flags and the <c>NEONSIDEKICK_*</c> variables, for <c>neon_help</c> (2026-10-25, the neon_help plan's Step 4). Each
/// flag's words are read from the usage text's line that starts with it (<see cref="SidekickOptions.Usage"/>, this system's own), so the rows
/// hold only the flag, its aliases and the setting it overrides (held to <c>SidekickApp.OverriddenBy</c> by a test). A variable that overrides a
/// setting is answered from <see cref="EnvironmentOverrides.Fields"/> with that setting's sentence and place; the rest from the hand-written
/// lines here. Off Windows the services, tasks and monitor checks have no rows (the usage text itself is unchanged).
/// </summary>
internal static partial class HelpStartup
{
    /// <summary>The order every setting is read in. Pinned.</summary>
    public const string LayeringLine = "Layering: a flag outranks its `NEONSIDEKICK_` variable, which outranks the saved profile, which outranks the default.";

    /// <summary>The list's heading (<c>kind: startup</c>, no query). Pinned.</summary>
    public const string FlagsHeading = "Command-line flags (ask about one, `--url`):";

    /// <summary>The variables' part of the list. Pinned.</summary>
    public const string VariablesHeading = "Environment variables (ask about one, `NEONSIDEKICK_LLM_URL`):";

    /// <summary>The flag answer's heading. Pinned.</summary>
    public static string FlagHeading(string syntax, string text) => $"Flag `{syntax}`: {text}";

    /// <summary>The variable answer's heading. Pinned.</summary>
    public static string VariableHeading(string variable, string text) => $"Variable `{variable}`: {text}";

    /// <summary>Every flag the usage header lists, in its order, the overriding ones with their setting.</summary>
    public static readonly IReadOnlyList<HelpFlag> OptionRows =
    [
        new("--headless", [], null),
        new("--smoke", [], null),
        new("--audio-check", [], null),
        new("--voice-check", [], null),
        new("--sql-check", [], null),
        new("--oracle-check", [], null),
        new("--mysql-check", [], null),
        new("--sqlite-check", [], null),
        new("--postgres-check", [], null),
        new("--unc-check", [], null),
        new("--eventlog-check", [], null),
        new("--network-check", [], null),
        new(ServicesCheck, [], null),
        new(TasksCheck, [], null),
        new("--ad-check", [], null),
        new("--docker-check", [], null),
        new("--camera-check", [], null),
        new(MonitorCheck, [], null),
        new(SidekickOptions.UrlFlag, [], SettingsField.LlmUrl),
        new(SidekickOptions.ModelFlag, [], SettingsField.LlmModel),
        new(SidekickOptions.CwdFlag, [], SettingsField.WorkingDirectory),
        new("--profile", [], null),
        new(SidekickOptions.YoloFlag, [], SettingsField.ShellCommandPolicy),
        new(SidekickOptions.NoPoliceFlag, [], SettingsField.ShellPolice),
        new("--log", [], null),
        new("--version", [], null),
        new("--help", ["-h", "-?"], null),
    ];

    private const string ServicesCheck = "--services-check";
    private const string TasksCheck = "--tasks-check";
    private const string MonitorCheck = "--monitor-check";

    /// <summary>The rows a system has: off Windows the services, tasks and monitor checks are left out.</summary>
    public static IReadOnlyList<HelpFlag> Flags(bool windows) =>
        windows ? OptionRows : OptionRows.Where(r => r.Flag is not (ServicesCheck or TasksCheck or MonitorCheck)).ToList();

    [GeneratedRegex(@"^\s*(?<syntax>\S.*?)\s{2,}(?<text>\S.*)$")]
    private static partial Regex UsageLine();

    /// <summary>A flag's usage line split into its syntax (<c>--url &lt;url&gt;</c>) and its words; null when the text has no line for it.</summary>
    public static (string Syntax, string Text)? LineOf(HelpFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        foreach (string line in SidekickOptions.Usage.Split('\n'))
        {
            var m = UsageLine().Match(line);
            if (!m.Success)
            {
                continue;
            }

            string syntax = m.Groups["syntax"].Value;
            string head = syntax.Split(' ')[0].TrimEnd(',');
            if (string.Equals(head, flag.Flag, StringComparison.Ordinal) || flag.Aliases.Contains(head, StringComparer.Ordinal))
            {
                return (syntax, m.Groups["text"].Value.Trim());
            }
        }

        return null;
    }

    /// <summary>One flag in full: its line, its aliases, the setting it overrides, the layering.</summary>
    public static string Flag(HelpFlag flag, bool windows)
    {
        var line = LineOf(flag);
        var sb = new StringBuilder(FlagHeading(line?.Syntax ?? flag.Flag, line?.Text ?? ""));
        if (flag.Aliases.Count > 0)
        {
            sb.Append("\n  Also written: ").Append(string.Join(", ", flag.Aliases.Select(a => $"`{a}`")));
        }

        if (flag.Overrides is { } field && HelpLocation.Of(field, windows) is { } row)
        {
            sb.Append("\n  Overrides: ").Append(row.Label).Append(" at ").Append(row.Path).Append(", for this launch");
        }

        return sb.Append("\n  ").Append(LayeringLine).ToString();
    }

    /// <summary>
    /// What each variable no setting row's override names says (2026-10-25: those <c>SidekickApp.OverriddenBy</c> never names, decided by
    /// <see cref="EnvironmentOverrides.Fields"/>): the home and the profile, and four that set a setting the pane shows no override mark for.
    /// </summary>
    public static string? HandLine(string variable, bool mac) => variable switch
    {
        EnvironmentOverrides.HomeVariable => $"the home folder: the settings, profiles, models, the llama.cpp runtime, skills and the connection files. Default `{HelpFiles.HomeDefault(mac)}`.",
        EnvironmentOverrides.ProfileVariable => "the profile a launch opens: `neon` unless `--profile`, this variable or a shortcut names another (`--profile` wins); an unknown name exits with code 2, and a switch made in the app lasts for the run.",
        EnvironmentOverrides.HomeAssistantUrlVariable => "sets *Home Assistant URL* for this launch.",
        EnvironmentOverrides.HomeAssistantTokenVariable => "sets *Home Assistant API key* for this launch; it is never logged.",
        EnvironmentOverrides.EmbeddedBackendVariable => mac ? "sets *Embedded backend* for this launch: `auto` or `metal`." : "sets *Embedded backend* for this launch: `auto`, `cuda`, `vulkan` or `cpu`.",
        EnvironmentOverrides.EmbeddedContextVariable => "sets *Embedded context length* for this launch: 0 (fit) or 512–262144 tokens.",
        _ => null,
    };

    /// <summary>The setting a hand-written variable sets, for its place.</summary>
    private static SettingsField? HandField(string variable) => variable switch
    {
        EnvironmentOverrides.HomeAssistantUrlVariable => SettingsField.HomeAssistantUrl,
        EnvironmentOverrides.HomeAssistantTokenVariable => SettingsField.HomeAssistantToken,
        EnvironmentOverrides.EmbeddedBackendVariable => SettingsField.EmbeddedBackend,
        EnvironmentOverrides.EmbeddedContextVariable => SettingsField.EmbeddedContextSize,
        _ => null,
    };

    /// <summary>One variable in full: what it sets, the setting's place and sentence, the layering.</summary>
    public static string Variable(string variable, bool windows, bool mac)
    {
        if (EnvironmentOverrides.Fields.TryGetValue(variable, out var field) && HelpLocation.Of(field, windows) is { } row)
        {
            return VariableHeading(variable, $"overrides *{row.Label}* for this launch.") +
                "\n  Setting: " + row.Label + " at " + row.Path + ": " + HelpText.FirstSentence(HelpSettings.Describe(field)) +
                "\n  " + LayeringLine;
        }

        var sb = new StringBuilder(VariableHeading(variable, HandLine(variable, mac) ?? ""));
        if (HandField(variable) is { } hand && HelpLocation.Of(hand, windows) is { } handRow)
        {
            sb.Append("\n  Setting: ").Append(handRow.Label).Append(" at ").Append(handRow.Path);
        }

        return sb.ToString();
    }

    /// <summary>Every flag with its arguments, every variable's name, the layering (<c>kind: startup</c>, no query).</summary>
    public static string List(bool windows)
    {
        var sb = new StringBuilder(FlagsHeading);
        foreach (var flag in Flags(windows))
        {
            sb.Append("\n- `").Append(LineOf(flag)?.Syntax ?? flag.Flag).Append('`');
        }

        sb.Append("\n\n").Append(VariablesHeading).Append('\n').Append(string.Join(", ", EnvironmentOverrides.AllVariables.Select(v => $"`{v}`")));
        return sb.Append("\n\n").Append(LayeringLine).ToString();
    }

    /// <summary>
    /// The flags and variables a query's tokens name (2026-10-25, step 11), each once, at most <see cref="HelpText.MaxMatches"/>: a flag or an
    /// alias; a variable by its full name, or by the part after the prefix when it holds an underscore (<c>HA_TOKEN</c>), or a part without one
    /// (<c>HOME</c>) beside "variable" or "env", or under <c>kind: startup</c>.
    /// </summary>
    public static List<string> Find(IReadOnlyList<HelpToken> tokens, bool windows, bool mac, bool kindStartup)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (Flags(windows).FirstOrDefault(f => string.Equals(f.Flag, token.Text, StringComparison.OrdinalIgnoreCase) || f.Aliases.Contains(token.Text, StringComparer.Ordinal)) is { } flag)
            {
                if (seen.Add(flag.Flag))
                {
                    found.Add(Flag(flag, windows));
                }
            }
            else if (VariableOf(token, kindStartup) is { } variable && seen.Add(variable))
            {
                found.Add(Variable(variable, windows, mac));
            }

            if (found.Count == HelpText.MaxMatches)
            {
                break;
            }
        }

        return found;
    }

    private static string? VariableOf(HelpToken token, bool kindStartup)
    {
        foreach (string variable in EnvironmentOverrides.AllVariables)
        {
            if (string.Equals(variable, token.Text, StringComparison.OrdinalIgnoreCase))
            {
                return variable;
            }

            string part = variable[EnvironmentOverrides.Prefix.Length..];
            if (string.Equals(part, token.Text, StringComparison.OrdinalIgnoreCase)
                && (part.Contains('_', StringComparison.Ordinal) || kindStartup || token.Previous is "variable" or "env" or "environment" || token.Next is "variable" or "env"))
            {
                return variable;
            }
        }

        return null;
    }

    /// <summary>The flags and variables for the free-word search.</summary>
    public static IEnumerable<HelpItem> Items(bool windows, bool mac)
    {
        foreach (var flag in Flags(windows))
        {
            var line = LineOf(flag);
            yield return new HelpItem(flag.Flag, line?.Text ?? "", () => Flag(flag, windows));
        }

        foreach (string variable in EnvironmentOverrides.AllVariables)
        {
            string text = EnvironmentOverrides.Fields.TryGetValue(variable, out var field) ? SettingsMenu.FieldName(field) : HandLine(variable, mac) ?? "";
            yield return new HelpItem(variable, text, () => Variable(variable, windows, mac));
        }
    }
}
