using System.Net;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Network;
using NeonSidekick.Services;
using NeonSidekick.Tasks;

namespace NeonSidekick.Tests.Fakes;

/// <summary>A pinger that answers from a script: each address its hops by TTL (a reply at the last), or a silence.</summary>
public sealed class FakePinger : IPinger
{
    public Dictionary<IPAddress, List<IPAddress?>> Routes { get; } = [];

    public HashSet<IPAddress> Silent { get; } = [];

    public List<(IPAddress Address, int Ttl)> Sent { get; } = [];

    public bool CanTrace { get; set; } = true;

    public Task<PingHop> PingAsync(IPAddress address, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add((address, ttl));
        }

        if (Silent.Contains(address))
        {
            return Task.FromResult(new PingHop(PingStatus.TimedOut, null, null));
        }

        if (!Routes.TryGetValue(address, out var route) || ttl > route.Count)
        {
            return Task.FromResult(new PingHop(PingStatus.Reply, address, TimeSpan.FromMilliseconds(5)));
        }

        var hop = route[ttl - 1];
        return Task.FromResult(hop is null ? new PingHop(PingStatus.TimedOut, null, null) : ttl == route.Count ? new PingHop(PingStatus.Reply, hop, TimeSpan.FromMilliseconds(ttl)) : new PingHop(PingStatus.TtlExpired, hop, TimeSpan.FromMilliseconds(ttl)));
    }
}

/// <summary>A Service Control Manager in memory: services with configurations, a record of every change, refusals by code.</summary>
public sealed class FakeServiceSystem : IServiceSystem
{
    public sealed class Service
    {
        public required ServiceEntry Entry { get; set; }

        public required ServiceConfiguration Config { get; set; }

        public List<string> DependsOnMe { get; } = [];
    }

    public Dictionary<string, Service> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Changes { get; } = [];

    public List<ServiceTarget> Targets { get; } = [];

    /// <summary>A code thrown by every call (a remote that refuses, a session that fails).</summary>
    public int? FailWith { get; set; }

    /// <summary>A code thrown by a change alone (access denied without elevation).</summary>
    public int? ChangeFailsWith { get; set; }

    public Service Add(string name, string display, ServiceRunState state = ServiceRunState.Running, ServiceStartType start = ServiceStartType.Automatic, string image = @"C:\Windows\System32\svchost.exe -k netsvcs", uint type = ServiceKinds.Win32OwnProcess, string? description = null, uint exit = 0)
    {
        var service = new Service
        {
            Entry = new ServiceEntry(name, display, state, state == ServiceRunState.Running ? 1234 : 0, type, exit, 0),
            Config = new ServiceConfiguration(name, display, description, type, start, false, image, "LocalSystem", null, [], new ServiceFailureActions(86400, null, null, [new ServiceFailureAction(1, 60000)])),
        };
        Services[name] = service;
        return service;
    }

    public T Run<T>(ServiceTarget target, Func<IServiceSession, T> work)
    {
        Targets.Add(target);
        if (FailWith is { } code)
        {
            throw new ServiceException(code, "fake failure " + code);
        }

        return work(new Session(this));
    }

    private sealed class Session(FakeServiceSystem owner) : IServiceSession
    {
        private Service Find(string name) => owner.Services.TryGetValue(name, out var s) ? s : throw new ServiceException(1060, "no such service");

        public IReadOnlyList<ServiceEntry> List(bool drivers) => owner.Services.Values.Select(s => s.Entry).Where(e => drivers || !e.IsDriver).ToList();

        public ServiceDetail Detail(string name)
        {
            var s = Find(name);
            var dependents = s.DependsOnMe.Select(d => owner.Services[d].Entry).ToList();
            return new ServiceDetail(s.Config, Status(name), dependents);
        }

        public ServiceStatus Status(string name)
        {
            var e = Find(name).Entry;
            return new ServiceStatus(e.State, e.ProcessId, e.Win32ExitCode, e.ServiceExitCode, 0, 0, 1);
        }

        public (ServiceStartType Start, bool Delayed) StartOf(string name) => (Find(name).Config.StartType, Find(name).Config.DelayedAutoStart);

        public IReadOnlyList<ServiceEntry> Dependents(string name, bool activeOnly) =>
            Find(name).DependsOnMe.Select(d => owner.Services[d].Entry).Where(e => !activeOnly || e.State != ServiceRunState.Stopped).ToList();

        public void Start(string name)
        {
            Change("start " + name);
            var s = Find(name);
            if (s.Entry.State == ServiceRunState.Running)
            {
                throw new ServiceException(1056, "already running");
            }

            s.Entry = s.Entry with { State = ServiceRunState.Running, ProcessId = 4321 };
        }

        public ServiceStatus Stop(string name)
        {
            Change("stop " + name);
            var s = Find(name);
            s.Entry = s.Entry with { State = ServiceRunState.Stopped, ProcessId = 0 };
            return Status(name);
        }

        public void SetStartType(string name, ServiceStartChoice choice)
        {
            Change("start_type " + name + " " + choice);
            var s = Find(name);
            s.Config = s.Config with
            {
                StartType = choice switch { ServiceStartChoice.Manual => ServiceStartType.Manual, ServiceStartChoice.Disabled => ServiceStartType.Disabled, _ => ServiceStartType.Automatic },
                DelayedAutoStart = choice == ServiceStartChoice.Delayed,
            };
        }

        private void Change(string what)
        {
            if (owner.ChangeFailsWith is { } code)
            {
                throw new ServiceException(code, "change refused");
            }

            owner.Changes.Add(what);
        }
    }
}

/// <summary>A Task Scheduler in memory: tasks by path with their XML, a record of every change.</summary>
public sealed class FakeTaskSystem : ITaskSystem
{
    public Dictionary<string, (TaskEntry Entry, string Xml)> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Changes { get; } = [];

    public int? FailWith { get; set; }

    public static string Xml(string command, string? user = "CORP\\ops", string? runLevel = null, string? group = null, string trigger = "<CalendarTrigger><StartBoundary>2026-01-01T03:00:00</StartBoundary><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>", string? description = "a task") =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        "<RegistrationInfo><Author>CORP\\admin</Author>" + (description is null ? "" : "<Description>" + description + "</Description>") + "</RegistrationInfo>" +
        "<Triggers>" + trigger + "</Triggers>" +
        "<Principals><Principal id=\"Author\">" + (user is null ? "" : "<UserId>" + user + "</UserId>") + (group is null ? "" : "<GroupId>" + group + "</GroupId>") + (runLevel is null ? "" : "<RunLevel>" + runLevel + "</RunLevel>") + "</Principal></Principals>" +
        "<Settings><Enabled>true</Enabled></Settings>" +
        "<Actions Context=\"Author\"><Exec><Command>" + command + "</Command></Exec></Actions></Task>";

    public void Add(string path, string xml, TaskRunState state = TaskRunState.Ready, int lastResult = 0)
    {
        string name = path[(path.LastIndexOf('\\') + 1)..];
        Tasks[path] = (new TaskEntry(path, name, state, state != TaskRunState.Disabled, new DateTime(2026, 10, 15, 3, 0, 0), lastResult, new DateTime(2026, 10, 16, 3, 0, 0), 0), xml);
    }

    public T Run<T>(TaskTarget target, Func<ITaskSession, T> work)
    {
        if (FailWith is { } code)
        {
            throw new TaskException(code, "fake failure");
        }

        return work(new Session(this));
    }

    private sealed class Session(FakeTaskSystem owner) : ITaskSession
    {
        public string TargetServer => "FAKEPC";

        public int HighestVersion => 0x10006;

        public IReadOnlyList<TaskEntry> List(string folder, bool recursive)
        {
            string prefix = folder.TrimEnd('\\') + "\\";
            return owner.Tasks.Values.Select(t => t.Entry).Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (recursive || e.Path.LastIndexOf('\\') == prefix.Length - 1)).ToList();
        }

        public TaskDetail Detail(string path) =>
            owner.Tasks.TryGetValue(path, out var t) ? new TaskDetail(t.Entry, t.Xml, []) : throw new TaskException(unchecked((int)0x80070002), "no such task");

        public void Run(string path) => Set(path, "run", e => e with { State = TaskRunState.Running });

        public void End(string path) => Set(path, "end", e => e with { State = TaskRunState.Ready });

        public void SetEnabled(string path, bool enabled) => Set(path, enabled ? "enable" : "disable", e => e with { Enabled = enabled, State = enabled ? TaskRunState.Ready : TaskRunState.Disabled });

        private void Set(string path, string what, Func<TaskEntry, TaskEntry> change)
        {
            var t = owner.Tasks.TryGetValue(path, out var found) ? found : throw new TaskException(unchecked((int)0x80070002), "no such task");
            owner.Changes.Add(what + " " + path);
            owner.Tasks[path] = (change(t.Entry), t.Xml);
        }
    }
}

/// <summary>
/// A directory in memory: entries by DN with text or binary values, and a search that understands the filters the app builds well enough for the
/// tests — an equality term on any attribute (case ignored), a presence term, <c>&gt;=</c>/<c>&lt;=</c> on numbers (and on GeneralizedTime as
/// text), AND, OR, NOT, the in-chain rule over <c>memberOf</c>, the UAC bit rule, a <c>*words*</c> substring, an <c>objectSid</c> of escaped
/// bytes and <c>distinguishedName</c> equality (2026-10-15, the AD gaps plan). <c>objectCategory</c> is judged by an entry's most specific class,
/// the last in its <c>objectClass</c> chain, as AD's is (a gMSA's chain holds computer, its category is its own); <c>canonicalName</c> is
/// constructed when asked for; one-level scope is the children alone. Each search's time limit is recorded and <see cref="OnSearch"/> runs before
/// it (a test advances its clock there).
/// </summary>
public sealed class FakeDirectorySystem : IDirectorySystem
{
    public Dictionary<string, Dictionary<string, List<byte[]>>> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryEntry Root { get; set; } = new("", new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase)
    {
        ["defaultNamingContext"] = [B("DC=lab,DC=example")],
        ["configurationNamingContext"] = [B("CN=Configuration,DC=lab,DC=example")],
        ["schemaNamingContext"] = [B("CN=Schema,CN=Configuration,DC=lab,DC=example")],
        ["dnsHostName"] = [B("dc1.lab.example")],
        ["domainFunctionality"] = [B("7")],
        ["forestFunctionality"] = [B("7")],
    });

    public List<string> Filters { get; } = [];

    /// <summary>Each search's time limit, in order (null: the default).</summary>
    public List<TimeSpan?> TimeLimits { get; } = [];

    /// <summary>Told each search's filter before it runs.</summary>
    public Action<string>? OnSearch { get; set; }

    public int? FailWith { get; set; }

    /// <summary>Puts <paramref name="member"/> in <paramref name="group"/>: the group's <c>member</c> and the member's <c>memberOf</c> kept in step.</summary>
    public void AddMember(string group, string member)
    {
        Values(group, "member").Add(B(member));
        Values(member, "memberOf").Add(B(group));
    }

    /// <summary>Makes <paramref name="manager"/> the manager of <paramref name="report"/>: <c>manager</c> and <c>directReports</c> in step.</summary>
    public void AddReport(string manager, string report)
    {
        Values(report, "manager").Add(B(manager));
        Values(manager, "directReports").Add(B(report));
    }

    private List<byte[]> Values(string dn, string attribute)
    {
        var map = Entries[dn];
        if (!map.TryGetValue(attribute, out var list))
        {
            map[attribute] = list = [];
        }

        return list;
    }

    public static byte[] B(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    public void Add(string dn, params (string Name, object Value)[] attributes)
    {
        var map = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in attributes)
        {
            if (!map.TryGetValue(name, out var list))
            {
                map[name] = list = [];
            }

            list.Add(value switch { byte[] bytes => bytes, string s => B(s), _ => B(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!) });
        }

        Entries[dn] = map;
    }

    public T Run<T>(DirectoryTarget target, Func<IDirectorySession, T> work)
    {
        if (FailWith is { } code)
        {
            throw new DirectoryException(code, "fake failure");
        }

        return work(new Session(this));
    }

    private sealed class Session(FakeDirectorySystem owner) : IDirectorySession
    {
        public DirectoryEntry RootDse() => owner.Root;

        public DirectoryResults Search(string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, bool paged, TimeSpan? timeLimit = null)
        {
            owner.OnSearch?.Invoke(filter);
            owner.Filters.Add(filter);
            owner.TimeLimits.Add(timeLimit);
            var found = new List<DirectoryEntry>();
            foreach (var (dn, map) in owner.Entries)
            {
                bool inScope = scope switch
                {
                    DirectoryScope.Base => string.Equals(dn, baseDn, StringComparison.OrdinalIgnoreCase),
                    DirectoryScope.OneLevel => string.Equals(AdValues.Parent(dn), baseDn, StringComparison.OrdinalIgnoreCase),
                    _ => string.Equals(dn, baseDn, StringComparison.OrdinalIgnoreCase) || dn.EndsWith("," + baseDn, StringComparison.OrdinalIgnoreCase),
                };
                if (!inScope || (scope != DirectoryScope.Base && !Filter.Matches(filter, dn, map, owner)))
                {
                    continue;
                }

                var picked = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
                foreach (string attribute in attributes)
                {
                    if (attribute.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase))
                    {
                        Ranged(map, attribute, picked);
                        continue;
                    }

                    if (map.TryGetValue(attribute, out var values))
                    {
                        picked[attribute] = values;
                    }
                    else if (string.Equals(attribute, "canonicalName", StringComparison.OrdinalIgnoreCase) && Domain(dn) is { Length: > 0 } domain)
                    {
                        // Constructed, as AD and Samba compute it: the domain's DNS name, then the path.
                        string path = AdValues.CanonicalPath(dn, domain);
                        picked[attribute] = [B(AdValues.DnsName(domain) + "/" + path)];
                    }
                }

                found.Add(new DirectoryEntry(dn, picked));
                if (found.Count >= sizeLimit)
                {
                    return new DirectoryResults(found, true);
                }
            }

            return new DirectoryResults(found, false);
        }

        /// <summary>The DN's domain part: its <c>DC=</c> names.</summary>
        private static string Domain(string dn)
        {
            int at = dn.IndexOf("DC=", StringComparison.OrdinalIgnoreCase);
            return at < 0 ? "" : dn[at..];
        }

        /// <summary>Range retrieval in pages of 2, so a test of four members walks two ranges.</summary>
        private static void Ranged(Dictionary<string, List<byte[]>> map, string attribute, Dictionary<string, IReadOnlyList<byte[]>> picked)
        {
            if (!map.TryGetValue("member", out var all))
            {
                return;
            }

            int from = int.Parse(attribute["member;range=".Length..attribute.IndexOf('-', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
            var page = all.Skip(from).Take(2).ToList();
            bool last = from + page.Count >= all.Count;
            picked["member;range=" + from + "-" + (last ? "*" : (from + page.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))] = page;
        }
    }

    /// <summary>A small LDAP filter reader for the app's own filters.</summary>
    private static class Filter
    {
        public static bool Matches(string filter, string dn, Dictionary<string, List<byte[]>> map, FakeDirectorySystem owner)
        {
            int at = 0;
            return Read(filter, ref at, dn, map, owner);
        }

        private static bool Read(string f, ref int at, string dn, Dictionary<string, List<byte[]>> map, FakeDirectorySystem owner)
        {
            if (f[at] != '(')
            {
                throw new FormatException("filter: " + f);
            }

            at++;
            char op = f[at];
            if (op is '&' or '|')
            {
                at++;
                var results = new List<bool>();
                while (f[at] == '(')
                {
                    results.Add(Read(f, ref at, dn, map, owner));
                }

                at++;
                return op == '&' ? results.All(r => r) : results.Any(r => r);
            }

            if (op == '!')
            {
                at++;
                bool inner = Read(f, ref at, dn, map, owner);
                at++;
                return !inner;
            }

            int close = at;
            int depth = 0;
            while (close < f.Length && !(f[close] == ')' && depth == 0))
            {
                close++;
            }

            string term = f[at..close];
            at = close + 1;
            return Term(term, dn, map, owner);
        }

        private static bool Term(string term, string dn, Dictionary<string, List<byte[]>> map, FakeDirectorySystem owner)
        {
            if (term.Contains(":1.2.840.113556.1.4.1941:=", StringComparison.Ordinal))
            {
                string group = Unescape(term[(term.IndexOf(":=", StringComparison.Ordinal) + 2)..]);
                return InChain(dn, group, owner, 0);
            }

            if (term.Contains(":1.2.840.113556.1.4.803:=", StringComparison.Ordinal))
            {
                string name = term[..term.IndexOf(':', StringComparison.Ordinal)];
                long bit = long.Parse(term[(term.IndexOf(":=", StringComparison.Ordinal) + 2)..], System.Globalization.CultureInfo.InvariantCulture);
                return Number(map, name) is { } n && (n & bit) != 0;
            }

            if (term.Contains(">=", StringComparison.Ordinal) || term.Contains("<=", StringComparison.Ordinal))
            {
                bool atLeast = term.Contains(">=", StringComparison.Ordinal);
                var parts = term.Split(atLeast ? ">=" : "<=");
                if (long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out long bound))
                {
                    return Number(map, parts[0]) is { } n && (atLeast ? n >= bound : n <= bound);
                }

                // GeneralizedTime: compared as text, which orders it.
                return map.TryGetValue(parts[0], out var texts) && texts.Count > 0
                    && (atLeast ? string.CompareOrdinal(System.Text.Encoding.UTF8.GetString(texts[0]), parts[1]) >= 0 : string.CompareOrdinal(System.Text.Encoding.UTF8.GetString(texts[0]), parts[1]) <= 0);
            }

            int eq = term.IndexOf('=', StringComparison.Ordinal);
            string attribute = term[..eq];
            string value = term[(eq + 1)..];
            if (string.Equals(attribute, "distinguishedName", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(dn, Unescape(value), StringComparison.OrdinalIgnoreCase);
            }
            if (!map.TryGetValue(attribute, out var values))
            {
                return string.Equals(attribute, "objectCategory", StringComparison.OrdinalIgnoreCase) && Category(map, value);
            }

            if (value == "*")
            {
                return true;
            }

            if (string.Equals(attribute, "objectSid", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = UnescapeBytes(value);
                return values.Any(v => v.AsSpan().SequenceEqual(bytes));
            }

            if (value.StartsWith('*') && value.EndsWith('*') && value.Length > 1)
            {
                string inner = Unescape(value[1..^1]);
                return values.Any(v => System.Text.Encoding.UTF8.GetString(v).Contains(inner, StringComparison.OrdinalIgnoreCase));
            }

            string wanted = Unescape(value);
            return values.Any(v => string.Equals(System.Text.Encoding.UTF8.GetString(v), wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The entry's category, by its most specific class (the last in its chain): a user and a contact are a person, as in AD.</summary>
        private static bool Category(Dictionary<string, List<byte[]>> map, string value) =>
            map.TryGetValue("objectClass", out var classes) && classes.Count > 0 && System.Text.Encoding.UTF8.GetString(classes[^1]) switch
            {
                "user" or "contact" => value == "person",
                var other => string.Equals(other, value, StringComparison.OrdinalIgnoreCase),
            };

        private static bool InChain(string dn, string group, FakeDirectorySystem owner, int depth)
        {
            if (depth > 10 || !owner.Entries.TryGetValue(dn, out var map) || !map.TryGetValue("memberOf", out var groups))
            {
                return false;
            }

            foreach (var g in groups.Select(v => System.Text.Encoding.UTF8.GetString(v)))
            {
                if (string.Equals(g, group, StringComparison.OrdinalIgnoreCase) || InChain(g, group, owner, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private static long? Number(Dictionary<string, List<byte[]>> map, string name) =>
            map.TryGetValue(name, out var values) && values.Count > 0 && long.TryParse(System.Text.Encoding.UTF8.GetString(values[0]), System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : null;

        private static string Unescape(string text) => System.Text.Encoding.UTF8.GetString(UnescapeBytes(text));

        private static byte[] UnescapeBytes(string text)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 2 < text.Length)
                {
                    bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(text[i].ToString()));
                }
            }

            return bytes.ToArray();
        }
    }
}
