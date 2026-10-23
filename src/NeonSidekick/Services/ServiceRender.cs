using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Services;

/// <summary>A service list's filters, as a call or <c>/services</c> gives them.</summary>
public sealed record ServiceFilter(string? State = null, string? Start = null, string? Words = null, bool Drivers = false)
{
    /// <summary>Whether <paramref name="entry"/> passes the state and words filters (the start filter needs the config: <see cref="ServiceStartOf"/>).</summary>
    public bool Passes(ServiceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (State is { } state)
        {
            bool ok = state switch
            {
                "running" => entry.State == ServiceRunState.Running,
                "stopped" => entry.State == ServiceRunState.Stopped,
                "failed" => ServiceRender.Failed(entry),
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
        }

        return Words is not { Length: > 0 } words
            || entry.Name.Contains(words, StringComparison.OrdinalIgnoreCase) || entry.DisplayName.Contains(words, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a start type passes the start filter.</summary>
    public bool PassesStart(ServiceStartType start, bool delayed) => Start switch
    {
        null => true,
        "delayed" => start == ServiceStartType.Automatic && delayed,
        "automatic" => start == ServiceStartType.Automatic && !delayed,
        _ => string.Equals(ServiceText.StartWord(start, delayed), Start, StringComparison.Ordinal),
    };

    public static readonly IReadOnlyList<string> States = ["running", "stopped", "failed"];

    public static readonly IReadOnlyList<string> Starts = ["automatic", "delayed", "manual", "disabled", "boot", "system"];
}

/// <summary>A start type read per service for the list's <c>start</c> filter.</summary>
public delegate (ServiceStartType Start, bool Delayed) ServiceStartOf(string name);

/// <summary>
/// The services tools' answers (2026-10-15, the admin-tools plan, <c>EventLogRender</c>'s shape): a header line, a line per service, a page's
/// next call. What a stranger wrote — a display name, a description — is scrubbed and fenced; under <c>redacted</c> detail the image path and
/// the recovery command go through <see cref="ToolRedaction.Text"/> first (fenced too). An answer is held to <see cref="MaxChars"/> and says
/// what was left out; a single value to <see cref="UntrustedText.MaxValueChars"/>. Pure.
/// </summary>
public static class ServiceRender
{
    public const int MaxChars = 16_000;

    /// <summary>A stopped service that ended in an error: its exit code is not 0 or "never started" (1077).</summary>
    public static bool Failed(ServiceEntry entry) => entry.State == ServiceRunState.Stopped && entry.Win32ExitCode is not 0 and not 1077;

    /// <summary>
    /// The list: the header (what was shown of what, the filters), a line per service, the next page's call. Since 2026-10-23 (the admin
    /// review's S9, E4, A7): the header counts the lines the character budget let through, not the page; the next call restates every filter
    /// and the limit (it kept only the offset and the computer, so the copied call paged another list); and a start filter's read that
    /// stopped once the page was full (<paramref name="complete"/> false) says "at least" rather than a count it never finished.
    /// </summary>
    public static string List(string where, IReadOnlyList<ServiceEntry> matched, int total, int offset, int limit, ServiceFilter filter, string? computerArgument, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(matched);
        var page = matched.Skip(offset).Take(limit).ToList();
        var lines = new StringBuilder();
        int shown = 0;
        foreach (var entry in page)
        {
            string line = "\n  " + Line(entry);
            if (lines.Length + line.Length > MaxChars - 1200)
            {
                break;
            }

            lines.Append(line);
            shown++;
        }

        string count = (complete ? "" : "at least ") + matched.Count.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder($"Services on {where}: {shown.ToString(CultureInfo.InvariantCulture)} of {count} matching ({total.ToString(CultureInfo.InvariantCulture)} in all){Filters(filter)}");
        text.Append(lines);
        int left = matched.Count - offset - shown;
        if (left > 0 || !complete)
        {
            text.Append('\n').Append(complete ? left.ToString(CultureInfo.InvariantCulture) + " more services" : "more services may match")
                .Append("; narrow with filter or state, or next: ").Append(NextCall(offset + shown, limit, filter, computerArgument));
        }

        if (shown > 0)
        {
            text.Append('\n').Append(UntrustedText.QuoteNote);
        }

        return text.ToString();
    }

    /// <summary>The next page's call, every filter restated (E4).</summary>
    public static string NextCall(int offset, int limit, ServiceFilter filter, string? computerArgument)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var parts = new List<string> { "offset " + offset.ToString(CultureInfo.InvariantCulture) };
        if (filter.State is { } state)
        {
            parts.Add("state " + state);
        }

        if (filter.Start is { } start)
        {
            parts.Add("start " + start);
        }

        if (filter.Words is { Length: > 0 } words)
        {
            parts.Add("filter \"" + UntrustedText.OneLine(words).Replace("\"", "'", StringComparison.Ordinal) + "\"");
        }

        if (filter.Drivers)
        {
            parts.Add("drivers true");
        }

        parts.Add("limit " + limit.ToString(CultureInfo.InvariantCulture));
        if (computerArgument is not null)
        {
            parts.Add("computer " + UntrustedText.OneLine(computerArgument));
        }

        return "service_list(" + string.Join(", ", parts) + ")";
    }

    /// <summary>One service's line: its name, its display name fenced, its state, its process, a failure's exit code.</summary>
    public static string Line(ServiceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string state = ServiceText.StateWord(entry.State) + (entry.ProcessId > 0 ? " pid " + entry.ProcessId.ToString(CultureInfo.InvariantCulture) : "")
            + (Failed(entry) ? " exit " + ExitCode(entry.Win32ExitCode, entry.ServiceExitCode) : "") + (entry.IsDriver ? " (driver)" : "");
        return Value(entry.Name, 256) + "  " + UntrustedText.Fenced(UntrustedText.Cut(entry.DisplayName, 200)) + "  " + state;
    }

    /// <summary>
    /// A value the computer gave, as one fenced line (2026-10-23, the admin review's B11: key names, dependencies, dependents, the account and
    /// the load-order group were scrubbed but not fenced, so a planted name read as the app's own words).
    /// </summary>
    private static string Value(string text, int max = 200) => UntrustedText.Fenced(UntrustedText.Cut(UntrustedText.OneLine(text), max));

    /// <summary>
    /// A dependency as it reads: a service by its name, or a load-order group (<c>+</c> first, SC_GROUP_IDENTIFIER) as "group …"
    /// (2026-10-23, the admin review's S16: shown as a service, and counted among the hidden ones under a narrowed list).
    /// </summary>
    private static bool IsGroup(string dependency) => dependency.StartsWith('+');

    /// <summary>
    /// One service whole: status, configuration, recovery, dependencies and dependents. <paramref name="visible"/> (the computer's
    /// <c>services</c> list; null, all) names the related services it shows and counts the rest (2026-10-20, a review: a narrowed list's hidden
    /// services were named here).
    /// </summary>
    public static string Detail(string where, ServiceDetail detail, bool redacted, Func<string, bool>? visible = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var c = detail.Config;
        var s = detail.Status;
        var text = new StringBuilder($"Service {Value(c.Name, 256)} on {where}: {ServiceText.StateWord(s.State)}" + (s.ProcessId > 0 ? $" (pid {s.ProcessId.ToString(CultureInfo.InvariantCulture)})" : ""));
        Add(text, "display name", UntrustedText.Fenced(UntrustedText.Cut(c.DisplayName)));
        if (c.Description is { } description)
        {
            // Redacted like the program line (2026-10-23, the admin review's C3: free text, where a secret is as likely as anywhere).
            Add(text, "description", UntrustedText.Fenced(UntrustedText.Cut(redacted ? ToolRedaction.Text(description) : description)));
        }

        Add(text, "start", ServiceText.StartWord(c.StartType, c.DelayedAutoStart));
        Add(text, "account", Value(c.Account));
        Add(text, "image", UntrustedText.Fenced(UntrustedText.Cut(redacted ? ToolRedaction.Text(c.ImagePath) : c.ImagePath)));
        Add(text, "type", TypeWords(c.ServiceType));
        if (c.LoadOrderGroup is { } group)
        {
            Add(text, "group", Value(group));
        }

        if (c.Dependencies.Count > 0)
        {
            var services = c.Dependencies.Where(d => !IsGroup(d)).ToList();
            var shown = services.Where(d => visible is null || visible(d)).Select(d => Value(d))
                .Concat(c.Dependencies.Where(IsGroup).Select(d => "group " + Value(d[1..])));
            Add(text, "depends on", Related(shown, services.Count(d => visible is not null && !visible(d))));
        }

        if (!detail.DependentsKnown)
        {
            Add(text, "needed by", ServiceText.DependentsUnknown);
        }
        else if (detail.Dependents.Count > 0)
        {
            // The visible ones past the first 100 counted too (2026-10-23, the admin review's I9: they were dropped with no word).
            var shown = detail.Dependents.Where(d => visible is null || visible(d.Name)).ToList();
            var named = shown.Take(100).Select(d => Value(d.Name) + " (" + ServiceText.StateWord(d.State) + ")");
            Add(text, "needed by", Related(shown.Count > 100 ? named.Append(ServiceText.MoreNotListed(shown.Count - 100)) : named, detail.Dependents.Count - shown.Count));
        }

        if (c.Failure is { } failure && (failure.Actions.Count > 0 || failure.Command is not null))
        {
            // Each action tied to its failure, the last for every later one; INFINITE (0xFFFFFFFF) as never (2026-10-23, the admin review's G13:
            // it read 49710:6:28:15, and the actions were a bare list).
            var words = failure.Actions.Select((a, i) => ServiceText.FailureOrdinal(i, failure.Actions.Count) + ": " + ServiceText.FailureAction(a)).ToList();
            if (failure.ResetPeriodSeconds == uint.MaxValue)
            {
                words.Add(ServiceText.FailureCountNeverResets);
            }
            else if (failure.ResetPeriodSeconds > 0)
            {
                words.Add("count reset after " + TimeSpan.FromSeconds(failure.ResetPeriodSeconds).ToString("g", CultureInfo.InvariantCulture));
            }

            Add(text, "recovery", string.Join("; ", words));
            if (failure.Command is { } command)
            {
                Add(text, "recovery command", UntrustedText.Fenced(UntrustedText.Cut(redacted ? ToolRedaction.Text(command) : command)));
            }
        }

        if (s.State == ServiceRunState.Stopped && s.Win32ExitCode is not 0 and not 1077)
        {
            Add(text, "last exit", ExitCode(s.Win32ExitCode, s.ServiceExitCode));
        }

        // The notes kept whole past a cut, the cut never inside a fence (2026-10-23, the admin review's C6).
        string tail = (redacted ? "\nsecret-like values hidden as " + ToolRedaction.Placeholder : "") + "\n" + UntrustedText.QuoteNote;
        return UntrustedText.Capped(text.ToString(), tail, MaxChars, "\n(cut at " + MaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)");
    }

    /// <summary>Related services' names, then the hidden ones counted.</summary>
    private static string Related(IEnumerable<string> shown, int hidden) =>
        string.Join(", ", hidden > 0 ? shown.Append(ServiceText.NotShown(hidden)) : shown);

    private static void Add(StringBuilder text, string label, string value) => text.Append("\n  ").Append(label).Append(": ").Append(value);

    /// <summary>The type in words: own process, shared process, user service, driver.</summary>
    public static string TypeWords(uint type) =>
        ServiceKinds.IsDriver(type) ? "driver"
        : (type & ServiceKinds.UserService) != 0 ? "per-user service"
        : (type & ServiceKinds.Win32ShareProcess) != 0 ? "shared process"
        : "own process";

    /// <summary>An exit code: the Win32 one, or a service-specific one under 1066.</summary>
    public static string ExitCode(uint win32, uint service) =>
        win32 == 1066 ? "service error " + service.ToString(CultureInfo.InvariantCulture) : win32.ToString(CultureInfo.InvariantCulture);

    private static string Filters(ServiceFilter filter)
    {
        var parts = new List<string>(4);
        if (filter.State is { } state)
        {
            parts.Add("state " + state);
        }

        if (filter.Start is { } start)
        {
            parts.Add("start " + start);
        }

        if (filter.Words is { Length: > 0 } words)
        {
            parts.Add("filter \"" + words + "\"");
        }

        if (filter.Drivers)
        {
            parts.Add("drivers too");
        }

        return parts.Count == 0 ? "" : ", " + string.Join(", ", parts);
    }
}
