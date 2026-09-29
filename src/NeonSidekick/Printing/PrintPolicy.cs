using NeonSidekick.Settings;

namespace NeonSidekick.Printing;

/// <summary>What <see cref="PrintPolicy.Judge"/> decided for one <c>print_file</c> call.</summary>
public enum PrintVerdict
{
    /// <summary>It prints.</summary>
    Run,

    /// <summary>It prints only after the user's yes on the pane; nothing to ask (headless) is a no.</summary>
    Ask,

    /// <summary>It does not print: <c>Print action policy</c> is <c>off</c>.</summary>
    Refuse,
}

/// <summary>
/// Whether the model may print (2026-09-28, the user's call: paper is spent, so "ask" by default): the setting
/// <c>Print action policy</c> — <see cref="Off"/> (the model may list the printers, never print), <see cref="Ask"/> (the
/// default: every <c>print_file</c> waits for the user's yes on the pane, which names the file, the printer, the pages and the
/// copies) or <see cref="Allow"/> (it prints without asking) — <c>HaPolicy</c> in shape. <c>/print</c> is the user's own hand
/// and is never judged. Pure.
/// </summary>
public static class PrintPolicy
{
    public const string Off = "off";
    public const string Ask = "ask";
    public const string Allow = "allow";

    /// <summary>The policies in the picker's order.</summary>
    public static readonly string[] Names = [Off, Ask, Allow];

    public const string Default = Ask;

    /// <summary>The policy in force: the setting in lower case, anything unknown read as <see cref="Default"/>.</summary>
    public static string Resolve(string? policy)
    {
        string name = policy?.Trim().ToLowerInvariant() ?? "";
        return Names.Contains(name, StringComparer.Ordinal) ? name : Default;
    }

    /// <summary>A one-line description for the picker.</summary>
    public static string Describe(string policy) => Resolve(policy) switch
    {
        Off => "the model may list the printers but never print",
        Allow => "the model prints without asking",
        _ => "every print by the model asks first",
    };

    /// <summary>The verdict as <paramref name="effective"/> stands.</summary>
    public static PrintVerdict Judge(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Resolve(effective.PrintActionPolicy) switch
        {
            Off => PrintVerdict.Refuse,
            Allow => PrintVerdict.Run,
            _ => PrintVerdict.Ask,
        };
    }
}
