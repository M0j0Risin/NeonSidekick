using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// Command-line flags, parsed by hand.
///
/// <para>Hand-rolled rather than Spectre.Console.Cli or System.CommandLine: both bind options by
/// reflection over attributes, which is exactly the surface NativeAOT trims. Six flags do not
/// justify a framework.</para>
/// </summary>
/// <param name="Smoke">Render the banner, verify native dependencies, exit 0/1. Run by
/// <c>build.ps1</c> against the <em>published</em> binary.</param>
/// <param name="Headless">A stdin/stdout REPL with no TUI, for piped and scripted use.</param>
/// <param name="AudioCheck">Play a tone through the speech output path and report whether it drained; exit 0/1. Needs a device, so not in the build gate.</param>
/// <param name="VoiceCheck">Record a few seconds from the microphone, transcribe them, exit 0/1. Needs a microphone and a speaker, so not in the build gate.</param>
/// <param name="ShowHelp"><c>-h</c> / <c>--help</c>.</param>
/// <param name="ShowVersion"><c>--version</c>.</param>
/// <param name="Url"><c>--url</c>: the LLM base URL for this launch; outranks the variable and the file.</param>
/// <param name="Model"><c>--model</c>: the model id for this launch; outranks the variable and the file.</param>
/// <param name="WorkingDirectory"><c>--cwd</c>: the working directory for this launch, made full against the
/// launch directory (the point of the flag: start the app from a project folder); outranks the saved setting.</param>
/// <param name="LogPath"><c>--log</c>: a file every diagnostic line (Trace and up) is appended to.
/// The TUI shows only warnings and errors; the Debug and Info lines (what the wake recogniser
/// heard, why a hit was ignored) are how a voice problem is diagnosed in the field.</param>
/// <param name="Profile"><c>--profile</c>: the profile this launch loads (2026-09-26, the user's ask: a scripted
/// headless run must not follow whichever profile was last clicked into). Outranks <c>NEONSIDEKICK_PROFILE</c> and
/// the pointer in <c>settings.json</c>, and never rewrites that pointer. Not part of <see cref="ApplyTo"/>: it
/// picks which <c>profile.json</c> is read, not a value in it; <c>Program.cs</c> hands it to <see cref="AppSettings"/>
/// through <see cref="LaunchProfile"/>, which gives a headless run <c>default</c> when neither is set.</param>
/// <param name="Yolo"><c>--yolo</c>: every shell command and script runs without asking, this launch only
/// (2026-09-26, the user's ask: a scripted run named the policy only through <c>NEONSIDEKICK_COMMAND_POLICY</c>, awkward
/// to set for one command in PowerShell or cmd). <see cref="ApplyTo"/> sets <c>Shell command policy</c> to <c>yolo</c>, so it
/// outranks the variable and the saved setting and is never saved; every mode, the screen included (no approval pane
/// this launch). The path police is a setting of its own and still applies (the user's call) unless <see cref="NoPolice"/>.</param>
/// <param name="NoPolice"><c>--no-police</c>: <c>Shell police outside paths</c> off for this launch (2026-09-26, the user's
/// ask: the police could only be turned off by saving the profile). Outranks <c>NEONSIDEKICK_SHELL_POLICE</c> and the saved
/// setting and is never saved; every mode. Never implied by <see cref="Yolo"/>: the two together leave no guard at all, so
/// each has to be asked for.</param>
/// <param name="Error">Set when an argument was not understood; the caller prints it with
/// <see cref="Usage"/> and exits 2.</param>
public sealed record SidekickOptions(
    bool Smoke,
    bool Headless,
    bool AudioCheck,
    bool VoiceCheck,
    bool ShowHelp,
    bool ShowVersion,
    string? Url,
    string? Model,
    string? WorkingDirectory,
    string? LogPath,
    string? Profile,
    bool Yolo,
    bool NoPolice,
    string? Error)
{
    public const string UrlFlag = "--url";
    public const string ModelFlag = "--model";
    public const string CwdFlag = "--cwd";
    public const string LogFlag = "--log";
    public const string ProfileFlag = "--profile";
    public const string YoloFlag = "--yolo";
    public const string NoPoliceFlag = "--no-police";
    public const string OracleCheckFlag = "--oracle-check";
    public const string MySqlCheckFlag = "--mysql-check";
    public const string UncCheckFlag = "--unc-check";

    /// <summary><c>--unc-check &lt;share&gt;</c> (2026-09-30): run <see cref="UncCheck"/> over that share of <c>unc.json</c> and exit 0/1 — the UNC tools' proof, reads only.</summary>
    public string? UncCheck { get; init; }

    /// <summary><c>--mysql-check &lt;connection&gt;</c> (2026-09-30): run <see cref="MySqlCheck"/> over that connection of <c>mysql.json</c> and exit 0/1, <see cref="OracleCheck"/>'s twin.</summary>
    public string? MySqlCheck { get; init; }

    /// <summary>
    /// <c>--oracle-check &lt;connection&gt;</c> (2026-09-30): run <see cref="OracleCheck"/> over that connection of the loaded
    /// profile's <c>oracle.json</c> and exit 0/1 — the Oracle tools' proof on the published binary. A check mode: no screen, no
    /// turn; needs a server, so not in the build gate. A property rather than a positional member, so every existing
    /// construction stands.
    /// </summary>
    public string? OracleCheck { get; init; }

    /// <summary>The help text. Pinned wording; tests assert on it.</summary>
    public const string Usage =
        "Usage: NeonSidekick [--headless] [--smoke] [--audio-check] [--voice-check] [--oracle-check <connection>] [--mysql-check <connection>] [--unc-check <share>] [--url <url>] [--model <id>] [--cwd <path>] [--profile <name>] [--yolo] [--no-police] [--log <path>] [--version] [--help]\n" +
        "\n" +
        "  (no flags)     interactive TUI\n" +
        "  --headless     stdin/stdout REPL, no TUI (profile \"default\" unless --profile or NEONSIDEKICK_PROFILE names one)\n" +
        "  --smoke        render the banner, verify native dependencies, exit 0/1\n" +
        "  --audio-check  play a 440 Hz tone through the speech output path, exit 0/1\n" +
        "  --voice-check  record up to 5 s from the microphone, transcribe it, exit 0/1\n" +
        "  --oracle-check <connection>  prove the Oracle tools against that connection of oracle.json (reads only), exit 0/1\n" +
        "  --mysql-check <connection>   prove the MySQL tools against that connection of mysql.json (reads only), exit 0/1\n" +
        "  --unc-check <share>          prove the UNC tools against that share of unc.json (reads only), exit 0/1\n" +
        "  --url <url>    LLM base URL for this launch (outranks NEONSIDEKICK_LLM_URL and the saved setting)\n" +
        "  --model <id>   model id for this launch (outranks NEONSIDEKICK_LLM_MODEL and the saved setting)\n" +
        "  --cwd <path>   working directory for this launch (outranks the saved setting)\n" +
        "  --profile <name>  profile for this launch (outranks NEONSIDEKICK_PROFILE and settings.json, which it leaves alone)\n" +
        "  --yolo         run every shell command without asking, this launch only (outranks NEONSIDEKICK_COMMAND_POLICY; the path police still applies unless --no-police)\n" +
        "  --no-police    let shell commands name paths outside the working directory, this launch only (outranks NEONSIDEKICK_SHELL_POLICE)\n" +
        "  --log <path>   append every diagnostic line (Trace and up) to a file\n" +
        "  --version      print the version and exit\n" +
        "  -h, --help     this text\n" +
        "\n" +
        "Exit codes: 0 done, 2 bad argument or unknown profile, 3 headless run in which a shell command was refused\n" +
        "\n" +
        "Keys: ESC = cancel/back";

    /// <summary>The empty option set; every flag false, no values, no error.</summary>
    public static SidekickOptions None { get; } = new(false, false, false, false, false, false, null, null, null, null, null, false, false, null);

    /// <summary>Parses <paramref name="args"/>. Never throws; an unknown argument or a missing value sets <see cref="Error"/>.</summary>
    public static SidekickOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = None;
        for (int i = 0; i < args.Count; i++)
        {
            var raw = args[i];
            var arg = raw.Trim();
            string lower = arg.ToLowerInvariant();

            if (TryValueFlag(UrlFlag, args, ref i, arg, lower, out var url, out var error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { Url = url };
                continue;
            }

            if (TryValueFlag(ModelFlag, args, ref i, arg, lower, out var model, out error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { Model = model };
                continue;
            }

            if (TryValueFlag(CwdFlag, args, ref i, arg, lower, out var cwd, out error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { WorkingDirectory = cwd };
                continue;
            }

            if (TryValueFlag(LogFlag, args, ref i, arg, lower, out var logPath, out error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { LogPath = logPath };
                continue;
            }

            if (TryValueFlag(ProfileFlag, args, ref i, arg, lower, out var profile, out error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { Profile = profile };
                continue;
            }

            if (TryValueFlag(OracleCheckFlag, args, ref i, arg, lower, out var oracle, out error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { OracleCheck = oracle };
                continue;
            }

            if (TryValueFlag(MySqlCheckFlag, args, ref i, arg, lower, out var mysql, out error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { MySqlCheck = mysql };
                continue;
            }

            if (TryValueFlag(UncCheckFlag, args, ref i, arg, lower, out var unc, out error))
            {
                if (error is not null)
                {
                    return result with { Error = error };
                }

                result = result with { UncCheck = unc };
                continue;
            }

            switch (lower)
            {
                case "--smoke":
                    result = result with { Smoke = true };
                    break;
                case "--headless":
                    result = result with { Headless = true };
                    break;
                case "--audio-check":
                    result = result with { AudioCheck = true };
                    break;
                case "--voice-check":
                    result = result with { VoiceCheck = true };
                    break;
                case YoloFlag:
                    result = result with { Yolo = true };
                    break;
                case NoPoliceFlag:
                    result = result with { NoPolice = true };
                    break;
                case "--version":
                    result = result with { ShowVersion = true };
                    break;
                case "-h":
                case "--help":
                case "-?":
                case "/?":
                    result = result with { ShowHelp = true };
                    break;
                case "":
                    break;
                default:
                    return result with { Error = $"Unknown argument: {raw}" };
            }
        }

        return result;
    }

    /// <summary>The flags that carry a value this launch (a test seam since the status panel went, 2026-09-14).</summary>
    public IReadOnlyList<string> ActiveFlags()
    {
        var flags = new List<string>(3);
        if (Url is not null)
        {
            flags.Add(UrlFlag);
        }

        if (Model is not null)
        {
            flags.Add(ModelFlag);
        }

        if (WorkingDirectory is not null)
        {
            flags.Add(CwdFlag);
        }

        if (Profile is not null)
        {
            flags.Add(ProfileFlag);
        }

        if (Yolo)
        {
            flags.Add(YoloFlag);
        }

        if (NoPolice)
        {
            flags.Add(NoPoliceFlag);
        }

        return flags;
    }

    /// <summary>
    /// The profile this launch loads: <c>--profile</c>, else <paramref name="environmentProfile"/>
    /// (<c>NEONSIDEKICK_PROFILE</c>), else <c>default</c> for a headless run, else null — the pointer in
    /// <c>settings.json</c> decides. The headless default is 2026-09-26, the user's call: a scripted run must not follow
    /// whichever profile the TUI last switched to, even when nobody named one. It goes in as an override, so the pointer
    /// is left alone as it is for <c>--profile</c>; <c>default</c> always exists, so it can never end the launch with 2.
    /// </summary>
    public string? LaunchProfile(string? environmentProfile) =>
        Profile ?? environmentProfile ?? (Headless ? Profiles.DefaultName : null);

    /// <summary>The mode this launch runs: <c>interactive</c>, <c>headless</c>, <c>smoke</c>, <c>audio-check</c>, <c>voice-check</c>, <c>oracle-check</c>, <c>mysql-check</c>, <c>unc-check</c>.</summary>
    public string Mode =>
        Headless ? "headless"
        : Smoke ? "smoke"
        : AudioCheck ? "audio-check"
        : VoiceCheck ? "voice-check"
        : OracleCheck is not null ? "oracle-check"
        : MySqlCheck is not null ? "mysql-check"
        : UncCheck is not null ? "unc-check"
        : "interactive";

    /// <summary>Whether this launch runs one of the check modes (<c>--smoke</c>, <c>--audio-check</c>, <c>--voice-check</c>, <c>--oracle-check</c>, <c>--mysql-check</c>, <c>--unc-check</c>): no screen, no input reader.</summary>
    public bool IsCheck => Smoke || AudioCheck || VoiceCheck || OracleCheck is not null || MySqlCheck is not null || UncCheck is not null;

    /// <summary>
    /// The value flags as typed, for the log at startup: <c>--cwd D:\x --log C:\t.log</c>; null when
    /// none was given. The mode flags are <see cref="Mode"/>'s.
    /// </summary>
    public string? Describe()
    {
        var parts = new List<string>(4);
        if (Url is not null)
        {
            parts.Add(UrlFlag + " " + Url);
        }

        if (Model is not null)
        {
            parts.Add(ModelFlag + " " + Model);
        }

        if (WorkingDirectory is not null)
        {
            parts.Add(CwdFlag + " " + WorkingDirectory);
        }

        if (Profile is not null)
        {
            parts.Add(ProfileFlag + " " + Profile);
        }

        if (Yolo)
        {
            parts.Add(YoloFlag);
        }

        if (NoPolice)
        {
            parts.Add(NoPoliceFlag);
        }

        if (LogPath is not null)
        {
            parts.Add(LogFlag + " " + LogPath);
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// A copy of <paramref name="effective"/> with this launch's flags applied on top. Mirrors
    /// <see cref="EnvironmentOverrides.ApplyTo"/>, so the whole precedence is one expression:
    /// <c>options.ApplyTo(environment.ApplyTo(settings.Current))</c>.
    /// </summary>
    public AppSettingsData ApplyTo(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var result = AppSettings.Copy(effective);
        if (Url is not null)
        {
            result.LlmUrl = Url;
        }

        if (Model is not null)
        {
            result.LlmModel = Model;
        }

        if (WorkingDirectory is not null)
        {
            result.WorkingDirectory = Path.GetFullPath(WorkingDirectory);
        }

        if (Yolo)
        {
            result.ShellCommandPolicy = Shell.CommandPolicy.Name(Shell.CommandPolicyMode.Yolo);
        }

        if (NoPolice)
        {
            result.ShellPoliceOutsidePaths = false;
        }

        return result;
    }

    /// <summary>
    /// Matches <c>--flag value</c> and <c>--flag=value</c>. A missing value, or one that looks like
    /// another flag, is an error naming the flag.
    /// </summary>
    private static bool TryValueFlag(string flag, IReadOnlyList<string> args, ref int i, string arg, string lower, out string? value, out string? error)
    {
        value = null;
        error = null;

        if (lower.StartsWith(flag + "=", StringComparison.Ordinal))
        {
            value = arg[(flag.Length + 1)..].Trim();
            if (value.Length == 0)
            {
                error = $"{flag} needs a value";
            }

            return true;
        }

        if (lower != flag)
        {
            return false;
        }

        if (i + 1 >= args.Count || args[i + 1].Trim().StartsWith('-') || args[i + 1].Trim().Length == 0)
        {
            error = $"{flag} needs a value";
            return true;
        }

        i++;
        value = args[i].Trim();
        return true;
    }
}
