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

    /// <summary>The list: the header (what was shown of what, the filters), a line per service, the next page's call.</summary>
    public static string List(string where, IReadOnlyList<ServiceEntry> matched, int total, int offset, int limit, ServiceFilter filter, string? computerArgument)
    {
        ArgumentNullException.ThrowIfNull(matched);
        var page = matched.Skip(offset).Take(limit).ToList();
        string filters = Filters(filter);
        var text = new StringBuilder($"Services on {where}: {page.Count.ToString(CultureInfo.InvariantCulture)} of {matched.Count.ToString(CultureInfo.InvariantCulture)} matching ({total.ToString(CultureInfo.InvariantCulture)} in all){filters}");
        int shown = 0;
        foreach (var entry in page)
        {
            string line = "\n  " + Line(entry);
            if (text.Length + line.Length > MaxChars - 600)
            {
                break;
            }

            text.Append(line);
            shown++;
        }

        int left = matched.Count - offset - shown;
        if (left > 0)
        {
            text.Append('\n').Append(left.ToString(CultureInfo.InvariantCulture)).Append(" more services; pass filter or state, or next: service_list(offset ")
                .Append((offset + shown).ToString(CultureInfo.InvariantCulture)).Append(computerArgument is null ? "" : ", computer " + computerArgument).Append(')');
        }

        if (shown > 0)
        {
            text.Append('\n').Append(UntrustedText.QuoteNote);
        }

        return text.ToString();
    }

    /// <summary>One service's line: its name, its display name fenced, its state, its process, a failure's exit code.</summary>
    public static string Line(ServiceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string state = ServiceText.StateWord(entry.State) + (entry.ProcessId > 0 ? " pid " + entry.ProcessId.ToString(CultureInfo.InvariantCulture) : "")
            + (Failed(entry) ? " exit " + ExitCode(entry.Win32ExitCode, entry.ServiceExitCode) : "") + (entry.IsDriver ? " (driver)" : "");
        return UntrustedText.Scrub(entry.Name) + "  " + UntrustedText.Fenced(UntrustedText.Cut(entry.DisplayName, 200)) + "  " + state;
    }

    /// <summary>One service whole: status, configuration, recovery, dependencies and dependents.</summary>
    public static string Detail(string where, ServiceDetail detail, bool redacted)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var c = detail.Config;
        var s = detail.Status;
        var text = new StringBuilder($"Service {UntrustedText.Scrub(c.Name)} on {where}: {ServiceText.StateWord(s.State)}" + (s.ProcessId > 0 ? $" (pid {s.ProcessId.ToString(CultureInfo.InvariantCulture)})" : ""));
        Add(text, "display name", UntrustedText.Fenced(UntrustedText.Cut(c.DisplayName)));
        if (c.Description is { } description)
        {
            Add(text, "description", UntrustedText.Fenced(UntrustedText.Cut(description)));
        }

        Add(text, "start", ServiceText.StartWord(c.StartType, c.DelayedAutoStart));
        Add(text, "account", UntrustedText.Scrub(c.Account));
        Add(text, "image", UntrustedText.Fenced(UntrustedText.Cut(redacted ? ToolRedaction.Text(c.ImagePath) : c.ImagePath)));
        Add(text, "type", TypeWords(c.ServiceType));
        if (c.LoadOrderGroup is { } group)
        {
            Add(text, "group", UntrustedText.Scrub(group));
        }

        if (c.Dependencies.Count > 0)
        {
            Add(text, "depends on", string.Join(", ", c.Dependencies.Select(UntrustedText.Scrub)));
        }

        if (!detail.DependentsKnown)
        {
            Add(text, "needed by", ServiceText.DependentsUnknown);
        }
        else if (detail.Dependents.Count > 0)
        {
            Add(text, "needed by", string.Join(", ", detail.Dependents.Take(100).Select(d => UntrustedText.Scrub(d.Name) + " (" + ServiceText.StateWord(d.State) + ")")));
        }

        if (c.Failure is { } failure && (failure.Actions.Count > 0 || failure.Command is not null))
        {
            var words = failure.Actions.Select(ServiceText.FailureAction).ToList();
            if (failure.ResetPeriodSeconds > 0)
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

        if (redacted)
        {
            text.Append("\nsecret-like values hidden as ").Append(ToolRedaction.Placeholder);
        }

        text.Append('\n').Append(UntrustedText.QuoteNote);
        return text.Length > MaxChars ? text.ToString(0, MaxChars - 40) + "\n(cut at " + MaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)" : text.ToString();
    }

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
