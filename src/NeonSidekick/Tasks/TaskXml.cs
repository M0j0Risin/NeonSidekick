using System.Xml;

namespace NeonSidekick.Tasks;

/// <summary>One action of a task: what it runs.</summary>
public sealed record TaskAction(string Kind, string? Command, string? Arguments, string? WorkingDirectory, string? ClassId);

/// <summary>One trigger of a task: its kind and the values that say when.</summary>
public sealed record TaskTrigger(string Kind, IReadOnlyDictionary<string, string> Values, bool Enabled);

/// <summary>Who a task runs as.</summary>
public sealed record TaskPrincipalInfo(string? UserId, string? GroupId, string? LogonType, string? RunLevel);

/// <summary>A task's definition read from its XML.</summary>
public sealed record TaskDefinition(
    string? Author,
    string? Description,
    string? RegistrationDate,
    TaskPrincipalInfo Principal,
    IReadOnlyList<TaskTrigger> Triggers,
    IReadOnlyList<TaskAction> Actions,
    bool Hidden,
    bool? Enabled,
    string? ExecutionTimeLimit);

/// <summary>A definition that cannot be read.</summary>
public sealed class TaskXmlException(string message) : Exception(message);

/// <summary>
/// A task's definition read from <c>IRegisteredTask::get_Xml</c> (2026-10-15, the admin-tools plan: the XML avoids the ITaskDefinition and
/// IExecAction vtables altogether). One <see cref="XmlReader"/> pass over local names (the task schema's namespace or none), DTDs prohibited,
/// no resolver, at most <see cref="MaxCharacters"/> — a bigger task is reported, not parsed. Fixtures under <c>tests/…/Fixtures/tasks</c>. Pure.
/// </summary>
public static class TaskXml
{
    /// <summary>The largest definition read: 1 MB.</summary>
    public const long MaxCharacters = 1024 * 1024;

    private static readonly HashSet<string> TriggerKinds = new(StringComparer.Ordinal)
    {
        "TimeTrigger", "CalendarTrigger", "BootTrigger", "LogonTrigger", "IdleTrigger", "EventTrigger", "RegistrationTrigger", "SessionStateChangeTrigger",
    };

    public static TaskDefinition Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (xml.Length > MaxCharacters)
        {
            throw new TaskXmlException(TaskText.XmlTooLarge);
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        string? author = null, description = null, date = null, userId = null, groupId = null, logonType = null, runLevel = null, timeLimit = null;
        bool hidden = false;
        bool? enabled = null;
        var triggers = new List<TaskTrigger>();
        var actions = new List<TaskAction>();
        var path = new List<string>(16);
        Dictionary<string, string>? trigger = null;
        string? triggerKind = null;
        bool triggerEnabled = true;
        int triggerDepth = -1;
        Dictionary<string, string>? action = null;
        string? actionKind = null;
        int actionDepth = -1;
        bool firstPrincipal = true;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        string name = reader.LocalName;
                        bool empty = reader.IsEmptyElement;
                        path.Add(name);
                        int depth = path.Count;
                        string parent = depth >= 2 ? path[depth - 2] : "";
                        if (parent == "Triggers" && TriggerKinds.Contains(name))
                        {
                            trigger = new Dictionary<string, string>(StringComparer.Ordinal);
                            triggerKind = name;
                            triggerEnabled = true;
                            triggerDepth = depth;
                        }
                        else if (parent == "Actions" && name is "Exec" or "ComHandler" or "SendEmail" or "ShowMessage")
                        {
                            action = new Dictionary<string, string>(StringComparer.Ordinal);
                            actionKind = name;
                            actionDepth = depth;
                        }
                        else if (trigger is not null && depth > triggerDepth && name.StartsWith("ScheduleBy", StringComparison.Ordinal))
                        {
                            trigger["Schedule"] = name;
                        }
                        else if (trigger is not null && depth > triggerDepth && name is "DaysOfWeek" or "Months" or "DaysOfMonth")
                        {
                            trigger[name] = "";
                        }
                        else if (trigger is not null && parent is "DaysOfWeek" or "Months")
                        {
                            trigger[parent] = trigger.TryGetValue(parent, out string? had) && had.Length > 0 ? had + " " + name : name;
                        }

                        if (empty)
                        {
                            Close(path, ref trigger, ref triggerKind, triggerEnabled, triggerDepth, triggers, ref action, ref actionKind, actionDepth, actions);
                        }

                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        string value = reader.Value.Trim();
                        if (path.Count == 0)
                        {
                            break;
                        }

                        string leaf = path[^1];
                        string under = path.Count >= 2 ? path[^2] : "";
                        if (trigger is not null && path.Count > triggerDepth)
                        {
                            if (leaf == "Enabled" && path.Count == triggerDepth + 1)
                            {
                                triggerEnabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                            }
                            else if (under == "DaysOfMonth" && leaf == "Day")
                            {
                                trigger["DaysOfMonth"] = trigger.TryGetValue("DaysOfMonth", out string? days) && days.Length > 0 ? days + " " + value : value;
                            }
                            else
                            {
                                trigger[leaf] = value;
                            }
                        }
                        else if (action is not null && path.Count > actionDepth)
                        {
                            action[leaf] = value;
                        }
                        else if (under == "RegistrationInfo")
                        {
                            switch (leaf)
                            {
                                case "Author": author = value; break;
                                case "Description": description = value; break;
                                case "Date": date = value; break;
                            }
                        }
                        else if (under == "Principal" && firstPrincipal)
                        {
                            switch (leaf)
                            {
                                case "UserId": userId = value; break;
                                case "GroupId": groupId = value; break;
                                case "LogonType": logonType = value; break;
                                case "RunLevel": runLevel = value; break;
                            }
                        }
                        else if (under == "Settings")
                        {
                            switch (leaf)
                            {
                                case "Hidden": hidden = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                                case "Enabled": enabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase); break;
                                case "ExecutionTimeLimit": timeLimit = value; break;
                            }
                        }

                        break;
                    case XmlNodeType.EndElement:
                        if (path.Count > 0 && path[^1] == "Principal")
                        {
                            firstPrincipal = false;
                        }

                        Close(path, ref trigger, ref triggerKind, triggerEnabled, triggerDepth, triggers, ref action, ref actionKind, actionDepth, actions);
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new TaskXmlException(TaskText.XmlUnreadable(ex.Message));
        }

        return new TaskDefinition(author, description, date, new TaskPrincipalInfo(userId, groupId, logonType, runLevel), triggers, actions, hidden, enabled, timeLimit);
    }

    /// <summary>An element ends: a trigger or an action finished at its own depth is kept, then the path pops.</summary>
    private static void Close(List<string> path, ref Dictionary<string, string>? trigger, ref string? triggerKind, bool triggerEnabled, int triggerDepth, List<TaskTrigger> triggers,
        ref Dictionary<string, string>? action, ref string? actionKind, int actionDepth, List<TaskAction> actions)
    {
        if (trigger is not null && path.Count == triggerDepth)
        {
            triggers.Add(new TaskTrigger(triggerKind!, trigger, triggerEnabled));
            trigger = null;
            triggerKind = null;
        }

        if (action is not null && path.Count == actionDepth)
        {
            actions.Add(new TaskAction(actionKind!, Value(action, "Command"), Value(action, "Arguments"), Value(action, "WorkingDirectory"), Value(action, "ClassId")));
            action = null;
            actionKind = null;
        }

        path.RemoveAt(path.Count - 1);
    }

    private static string? Value(Dictionary<string, string> values, string key) => values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;
}

/// <summary>
/// Whether a task's run is a privilege boundary (2026-10-15, the admin-tools plan, the user's call): its principal is SYSTEM, LocalService or
/// NetworkService (by SID or name), its run level is <c>HighestAvailable</c>, or it runs as a group. Since 2026-10-20 (a review: a task stored
/// with another account's password, or S4U, ran as that account unguarded) also any account but the session's own (<see cref="ITaskSession.Self"/>
/// — a domain admin's, a virtual <c>NT SERVICE\…</c> one), a service-account logon, and a principal with no account named (the registering
/// user, unknown here). Run, End and Enable of such a task are refused unless the computer's entry sets <c>"privileged": true</c>; Disable
/// runs nothing. Pure.
/// </summary>
public static class TaskPrincipal
{
    private static readonly HashSet<string> ServiceAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", "SYSTEM", "NT AUTHORITY\\SYSTEM", "LocalSystem",
        "S-1-5-19", "LOCAL SERVICE", "NT AUTHORITY\\LOCAL SERVICE", "LocalService",
        "S-1-5-20", "NETWORK SERVICE", "NT AUTHORITY\\NETWORK SERVICE", "NetworkService",
    };

    /// <summary>
    /// Whether <paramref name="principal"/> is privileged for a session connected as <paramref name="self"/> (its forms; null or empty, unknown:
    /// every account then counts as another's).
    /// </summary>
    public static bool IsPrivileged(TaskPrincipalInfo principal, IReadOnlyCollection<string>? self)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string? user = principal.UserId?.Trim() is { Length: > 0 } named ? named : null;
        return (user is not null && ServiceAccounts.Contains(user))
            || string.Equals(principal.RunLevel?.Trim(), "HighestAvailable", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(principal.GroupId)
            || string.Equals(principal.LogonType?.Trim(), "ServiceAccount", StringComparison.OrdinalIgnoreCase)
            || user is null
            || !IsSelf(user, self);
    }

    /// <summary>
    /// Whether <paramref name="user"/> names the session's own account: equal to one of <paramref name="self"/>'s forms (the name, domain\name
    /// and the SID), case ignored.
    /// </summary>
    public static bool IsSelf(string user, IReadOnlyCollection<string>? self)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (self is null || self.Count == 0)
        {
            return false;
        }

        string trimmed = user.Trim();
        return self.Any(s => string.Equals(s.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="xml"/>'s principal is privileged for <paramref name="self"/>; an unreadable definition counts as privileged (the
    /// safe reading).
    /// </summary>
    public static bool IsPrivileged(string xml, IReadOnlyCollection<string>? self)
    {
        try
        {
            return IsPrivileged(TaskXml.Parse(xml).Principal, self);
        }
        catch (TaskXmlException)
        {
            return true;
        }
    }
}
