using System.Runtime.InteropServices;
using Niskala.Interop.Com;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.TaskScheduler;
using Windows.Win32.System.Variant;

namespace Niskala.Interop.Tasks;

public sealed record ScheduledTaskXml(string Path, string Xml);

public sealed record TaskEnumeration(IReadOnlyList<ScheduledTaskXml> Tasks, int UnreadableItems, int Error);

public static class TaskSchedulerNative
{
    public const int NotFound = unchecked((int)0x80070002);
    public const int AlreadyExists = unchecked((int)0x800700B7);
    public const int AccessDenied = unchecked((int)0x80070005);

    private const int TaskEnumHidden = 1;
    private const int PathNotFound = unchecked((int)0x80070003);

    public static unsafe TaskEnumeration Enumerate()
    {
        using ComScope com = ComScope.Enter();
        ITaskService* service = null;
        ITaskFolder* root = null;
        try
        {
            HRESULT created = Windows.Win32.System.TaskScheduler.TaskScheduler.CreateInstance(out service);
            if (created.Failed)
            {
                return new TaskEnumeration([], 0, created.Value);
            }

            service->Connect(default, default, default, default);
            using (SysFreeStringSafeHandle rootPath = PInvoke.SysAllocString(@"\"))
            {
                service->GetFolder(rootPath, &root);
            }

            var tasks = new List<ScheduledTaskXml>();
            int unreadable = 0;
            Walk(root, tasks, ref unreadable);
            return new TaskEnumeration(tasks, unreadable, 0);
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return new TaskEnumeration([], 0, ex.HResult);
        }
        finally
        {
            if (root is not null)
            {
                root->Release();
            }

            if (service is not null)
            {
                service->Release();
            }
        }
    }

    public static unsafe int TryRead(string path, out string? xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        xml = null;
        using ComScope com = ComScope.Enter();
        ITaskService* service = null;
        ITaskFolder* folder = null;
        IRegisteredTask* task = null;
        try
        {
            (string parent, string name) = Split(path);
            int error = TryOpenFolder(parent, create: false, out service, out folder);
            if (error != 0)
            {
                return error;
            }

            using SysFreeStringSafeHandle taskName = PInvoke.SysAllocString(name);
            folder->GetTask(taskName, &task);
            xml = BstrText.Take(task->Xml);
            return 0;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return Normalized(ex.HResult);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    public static unsafe int TryDelete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using ComScope com = ComScope.Enter();
        ITaskService* service = null;
        ITaskFolder* folder = null;
        try
        {
            (string parent, string name) = Split(path);
            int error = TryOpenFolder(parent, create: false, out service, out folder);
            if (error != 0)
            {
                return error;
            }

            using SysFreeStringSafeHandle taskName = PInvoke.SysAllocString(name);
            folder->DeleteTask(taskName, 0);
            return 0;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return Normalized(ex.HResult);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    public static unsafe int TryRegister(string path, string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(xml);
        using ComScope com = ComScope.Enter();
        ITaskService* service = null;
        ITaskFolder* folder = null;
        ITaskDefinition* definition = null;
        IPrincipal* principal = null;
        IRegisteredTask* task = null;
        try
        {
            (string parent, string name) = Split(path);
            int error = TryOpenFolder(parent, create: true, out service, out folder);
            if (error != 0)
            {
                return error;
            }

            service->NewTask(0, &definition);
            using (SysFreeStringSafeHandle text = PInvoke.SysAllocString(xml))
            {
                definition->XmlText = (BSTR)text.DangerousGetHandle();
            }

            principal = definition->Principal;
            using SysFreeStringSafeHandle taskName = PInvoke.SysAllocString(name);
            folder->RegisterTaskDefinition(taskName, definition, (int)TASK_CREATION.TASK_CREATE, default, default, principal->LogonType, default, &task);
            return 0;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return Normalized(ex.HResult);
        }
        finally
        {
            Release(task);
            Release(principal);
            Release(definition);
            Release(folder);
            Release(service);
        }
    }

    public static unsafe int TryGetEnabled(string path, out bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        enabled = false;
        using ComScope com = ComScope.Enter();
        ITaskService* service = null;
        ITaskFolder* folder = null;
        IRegisteredTask* task = null;
        try
        {
            int error = TryOpenTask(path, out service, out folder, out task);
            if (error == 0)
            {
                enabled = task->Enabled;
            }

            return error;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return Normalized(ex.HResult);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    public static unsafe int TrySetEnabled(string path, bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using ComScope com = ComScope.Enter();
        ITaskService* service = null;
        ITaskFolder* folder = null;
        IRegisteredTask* task = null;
        try
        {
            int error = TryOpenTask(path, out service, out folder, out task);
            if (error == 0)
            {
                task->Enabled = enabled;
            }

            return error;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return Normalized(ex.HResult);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    private static unsafe int TryOpenTask(string path, out ITaskService* service, out ITaskFolder* folder, out IRegisteredTask* task)
    {
        task = null;
        (string parent, string name) = Split(path);
        int error = TryOpenFolder(parent, create: false, out service, out folder);
        if (error != 0)
        {
            return error;
        }

        IRegisteredTask* found = null;
        using SysFreeStringSafeHandle taskName = PInvoke.SysAllocString(name);
        folder->GetTask(taskName, &found);
        task = found;
        return 0;
    }

    private static unsafe int TryOpenFolder(string path, bool create, out ITaskService* service, out ITaskFolder* folder)
    {
        service = null;
        folder = null;
        HRESULT created = Windows.Win32.System.TaskScheduler.TaskScheduler.CreateInstance(out service);
        if (created.Failed)
        {
            return created.Value;
        }

        service->Connect(default, default, default, default);
        ITaskFolder* found = null;
        try
        {
            using SysFreeStringSafeHandle folderPath = PInvoke.SysAllocString(path);
            service->GetFolder(folderPath, &found);
            folder = found;
            return 0;
        }
        catch (Exception ex) when (create && IsFailure(ex) && Normalized(ex.HResult) == NotFound)
        {
            ITaskFolder* root = null;
            try
            {
                using SysFreeStringSafeHandle rootPath = PInvoke.SysAllocString(@"\");
                using SysFreeStringSafeHandle folderPath = PInvoke.SysAllocString(path);
                service->GetFolder(rootPath, &root);
                root->CreateFolder(folderPath, default, &found);
                folder = found;
                return 0;
            }
            finally
            {
                Release(root);
            }
        }
    }

    private static (string Parent, string Name) Split(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash <= 0 ? (@"\", path.TrimStart('\\')) : (path[..slash], path[(slash + 1)..]);
    }

    private static int Normalized(int error) => error == PathNotFound ? NotFound : error;

    private static bool IsFailure(Exception ex) => ex is COMException or IOException or UnauthorizedAccessException or ArgumentException;

    private static unsafe void Release<T>(T* pointer)
        where T : unmanaged
    {
        if (pointer is not null)
        {
            ((Windows.Win32.System.Com.IUnknown*)pointer)->Release();
        }
    }

    private static unsafe void Walk(ITaskFolder* folder, List<ScheduledTaskXml> tasks, ref int unreadable)
    {
        IRegisteredTaskCollection* registered = null;
        ITaskFolderCollection* children = null;
        try
        {
            folder->GetTasks(TaskEnumHidden, &registered);
            int taskCount = registered->Count;
            for (int i = 1; i <= taskCount; i++)
            {
                IRegisteredTask* task = null;
                try
                {
                    registered->get_Item(Index(i), &task);
                    tasks.Add(new ScheduledTaskXml(BstrText.Take(task->Path), BstrText.Take(task->Xml)));
                }
                catch (Exception ex) when (IsFailure(ex))
                {
                    unreadable++;
                }
                finally
                {
                    if (task is not null)
                    {
                        task->Release();
                    }
                }
            }

            folder->GetFolders(0, &children);
            int folderCount = children->Count;
            for (int i = 1; i <= folderCount; i++)
            {
                ITaskFolder* child = null;
                try
                {
                    children->get_Item(Index(i), &child);
                    Walk(child, tasks, ref unreadable);
                }
                catch (Exception ex) when (IsFailure(ex))
                {
                    unreadable++;
                }
                finally
                {
                    if (child is not null)
                    {
                        child->Release();
                    }
                }
            }
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            unreadable++;
        }
        finally
        {
            if (children is not null)
            {
                children->Release();
            }

            if (registered is not null)
            {
                registered->Release();
            }
        }
    }

    private static VARIANT Index(int value)
    {
        VARIANT index = default;
        index.vt = VARENUM.VT_I4;
        index.lVal = value;
        return index;
    }
}
