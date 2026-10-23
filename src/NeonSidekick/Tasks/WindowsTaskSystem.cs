using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using NeonSidekick.Sql;

namespace NeonSidekick.Tasks;

/// <summary>
/// The Task Scheduler in-process (2026-10-15, the admin-tools plan) over <see cref="TaskNative"/>: each <see cref="Run{T}"/> on a short MTA
/// thread of its own — <c>CoInitializeEx</c>, <c>CoCreateInstance(CLSID_TaskScheduler)</c>, <c>Connect</c>, the work, every interface
/// released, <c>CoUninitialize</c> — so no caller's apartment is chosen for it. A remote <c>runas</c>'s account and password go straight into
/// <c>Connect(server, user, domain, password)</c>, no impersonation. Remote runas was built to the documented API and never run here (no
/// second machine): <c>.notes/remote_admin_todo.md</c>. Every failure is a <see cref="TaskException"/> with its HRESULT. Excluded from
/// coverage: proven by the smoke's <c>tasks:com</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskSystem : ITaskSystem
{
    public T Run<T>(TaskTarget target, Func<ITaskSession, T> work)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(work);
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            int co = TaskNative.CoInitializeEx(0, TaskNative.CoinitMultithreaded);
            nint service = 0;
            try
            {
                if (co < 0 && co != TaskNative.RpcEChangedMode)
                {
                    throw new TaskException(co, $"CoInitializeEx failed (0x{co:X8})");
                }

                service = TaskNative.CreateService();
                string? user = null, domain = null;
                if (target.Login is { } login)
                {
                    (domain, user) = WindowsCredentials.SplitAccount(login.User) ?? (null, login.User);
                }

                TaskNative.Connect(service, target.Host, user, domain, target.Login?.Password);
                result = work(new Session(service, target.Host is null));
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                TaskNative.Release(service);
                if (co >= 0)
                {
                    TaskNative.CoUninitialize();
                }
            }
        })
        { IsBackground = true, Name = "Task Scheduler" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    private sealed class Session(nint service, bool local) : ITaskSession
    {
        private IReadOnlyList<string>? _self;

        public string TargetServer => TaskNative.GetString(service, TaskNative.ServiceTargetServer, "ITaskService.TargetServer");

        public int HighestVersion => TaskNative.GetInt(service, TaskNative.ServiceHighestVersion, "ITaskService.HighestVersion");

        /// <summary>
        /// The connected account's forms (2026-10-20, a review): ITaskService's ConnectedDomain\ConnectedUser, the bare name for a local account
        /// on this PC alone, and the SID —
        /// this PC's own token's on this PC, else the name looked up here (a remote computer's local account may not be: its SID is then left out,
        /// and a task naming the account by SID counts as another's, the safe side). Read once per session.
        /// </summary>
        public IReadOnlyList<string> Self => _self ??= ReadSelf();

        private IReadOnlyList<string> ReadSelf()
        {
            var forms = new List<string>(4);
            string user = TaskNative.GetString(service, TaskNative.ServiceConnectedUser, "ITaskService.ConnectedUser").Trim();
            string domain = TaskNative.GetString(service, TaskNative.ServiceConnectedDomain, "ITaskService.ConnectedDomain").Trim();
            if (user.Length == 0)
            {
                return forms;
            }

            string qualified = domain.Length > 0 ? domain + "\\" + user : user;
            forms.Add(qualified);
            if (local && (domain.Length == 0 || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)))
            {
                forms.Add(user);   // a bare name is this PC's own account here; elsewhere it may be another account of that name
            }
            try
            {
                // Disposed (2026-10-23, the admin review's O9: a token handle leaked per local task session).
                using var current = local ? WindowsIdentity.GetCurrent() : null;
                if (current?.User is { } own)
                {
                    forms.Add(own.Value);
                }
                else if (new NTAccount(qualified).Translate(typeof(SecurityIdentifier)) is SecurityIdentifier sid)
                {
                    forms.Add(sid.Value);
                }
            }
            catch (SystemException)
            {
                // No SID: a task that names this account by its SID counts as another's.
            }

            return forms;
        }

        public IReadOnlyList<TaskEntry> List(string folder, bool recursive, bool skipMicrosoft = false)
        {
            var entries = new List<TaskEntry>(128);
            nint root = TaskNative.GetByPath(service, TaskNative.ServiceGetFolder, folder, "ITaskService.GetFolder");
            try
            {
                Walk(root, recursive, skipMicrosoft, entries, 0);
            }
            finally
            {
                TaskNative.Release(root);
            }

            return entries;
        }

        private static void Walk(nint folder, bool recursive, bool skipMicrosoft, List<TaskEntry> entries, int depth)
        {
            nint tasks = TaskNative.GetByFlags(folder, TaskNative.FolderGetTasks, TaskNative.TaskEnumHidden, "ITaskFolder.GetTasks");
            try
            {
                int count = TaskNative.GetInt(tasks, TaskNative.CollectionCount, "IRegisteredTaskCollection.Count");
                for (int i = 1; i <= count; i++)
                {
                    // The item read inside the try (2026-10-23, the admin review's O3): a task deleted or unreadable between Count and
                    // Item failed the whole walk, or dropped the rest of a folder.
                    nint task = 0;
                    try
                    {
                        task = TaskNative.Item(tasks, i, "IRegisteredTaskCollection.Item");
                        entries.Add(Entry(task));
                    }
                    catch (TaskException)
                    {
                        // A task that will not be read (its file gone, no rights) is left out, as the snap-in does.
                    }
                    finally
                    {
                        if (task != 0)
                        {
                            TaskNative.Release(task);
                        }
                    }
                }
            }
            finally
            {
                TaskNative.Release(tasks);
            }

            if (!recursive || depth > 32)
            {
                return;
            }

            nint folders = TaskNative.GetByFlags(folder, TaskNative.FolderGetFolders, 0, "ITaskFolder.GetFolders");
            try
            {
                int count = TaskNative.GetInt(folders, TaskNative.CollectionCount, "ITaskFolderCollection.Count");
                for (int i = 1; i <= count; i++)
                {
                    nint child = TaskNative.Item(folders, i, "ITaskFolderCollection.Item");
                    try
                    {
                        if (skipMicrosoft && TaskAccess.IsMicrosoft(TaskNative.GetString(child, TaskNative.FolderPath, "ITaskFolder.Path") + "\\"))
                        {
                            continue;
                        }

                        Walk(child, recursive, skipMicrosoft, entries, depth + 1);
                    }
                    catch (TaskException)
                    {
                        // A folder with no read right is skipped.
                    }
                    finally
                    {
                        TaskNative.Release(child);
                    }
                }
            }
            finally
            {
                TaskNative.Release(folders);
            }
        }

        private static TaskEntry Entry(nint task) => new(
            TaskNative.GetString(task, TaskNative.TaskPath, "IRegisteredTask.Path"),
            TaskNative.GetString(task, TaskNative.TaskName, "IRegisteredTask.Name"),
            (TaskRunState)TaskNative.GetInt(task, TaskNative.TaskState, "IRegisteredTask.State"),
            TaskNative.GetBool(task, TaskNative.TaskGetEnabled, "IRegisteredTask.Enabled"),
            TaskNative.GetDate(task, TaskNative.TaskLastRunTime, "IRegisteredTask.LastRunTime"),
            TaskNative.GetInt(task, TaskNative.TaskLastTaskResult, "IRegisteredTask.LastTaskResult"),
            TaskNative.GetDate(task, TaskNative.TaskNextRunTime, "IRegisteredTask.NextRunTime"),
            TaskNative.GetInt(task, TaskNative.TaskMissedRuns, "IRegisteredTask.NumberOfMissedRuns"));

        public TaskDetail Detail(string path) => WithTask(path, task =>
        {
            var entry = Entry(task);
            string xml = TaskNative.GetString(task, TaskNative.TaskXml, "IRegisteredTask.Xml");
            var running = new List<TaskInstance>();
            nint instances = TaskNative.GetByFlags(task, TaskNative.TaskGetInstances, 0, "IRegisteredTask.GetInstances");
            try
            {
                int count = TaskNative.GetInt(instances, TaskNative.CollectionCount, "IRunningTaskCollection.Count");
                for (int i = 1; i <= count && i <= 64; i++)
                {
                    // An instance that ended between Count and Item is skipped (2026-10-23, the admin review's O4: task_info failed for a
                    // short-lived one).
                    nint instance;
                    try
                    {
                        instance = TaskNative.Item(instances, i, "IRunningTaskCollection.Item");
                    }
                    catch (TaskException)
                    {
                        continue;
                    }

                    try
                    {
                        running.Add(new TaskInstance(
                            TaskNative.GetString(instance, TaskNative.RunningInstanceGuid, "IRunningTask.InstanceGuid"),
                            TaskNative.GetString(instance, TaskNative.RunningCurrentAction, "IRunningTask.CurrentAction"),
                            TaskNative.GetInt(instance, TaskNative.RunningEnginePid, "IRunningTask.EnginePID"),
                            (TaskRunState)TaskNative.GetInt(instance, TaskNative.RunningState, "IRunningTask.State")));
                    }
                    catch (TaskException)
                    {
                        // It ended while being read.
                    }
                    finally
                    {
                        TaskNative.Release(instance);
                    }
                }
            }
            finally
            {
                TaskNative.Release(instances);
            }

            return new TaskDetail(entry, xml, running);
        });

        public TaskEntry Entry(string path) => WithTask(path, task => Entry(task));

        public void Run(string path) => WithTask(path, task =>
        {
            TaskNative.Run(task);
            return true;
        });

        public void End(string path) => WithTask(path, task =>
        {
            TaskNative.Stop(task);
            return true;
        });

        public void SetEnabled(string path, bool enabled) => WithTask(path, task =>
        {
            TaskNative.PutEnabled(task, enabled);
            return true;
        });

        /// <summary>The task at <paramref name="path"/> (its folder from the root, then GetTask), worked and released.</summary>
        private T WithTask<T>(string path, Func<nint, T> work)
        {
            nint root = TaskNative.GetByPath(service, TaskNative.ServiceGetFolder, "\\", "ITaskService.GetFolder");
            try
            {
                nint task = TaskNative.GetByPath(root, TaskNative.FolderGetTask, path, "ITaskFolder.GetTask");
                try
                {
                    return work(task);
                }
                finally
                {
                    TaskNative.Release(task);
                }
            }
            finally
            {
                TaskNative.Release(root);
            }
        }
    }
}
