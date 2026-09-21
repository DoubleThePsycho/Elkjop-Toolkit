using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Support_Tool.Services;

// Native-only (Toolhelp32Snapshot, no PowerShell spawn - this codebase deliberately avoids an
// extra visible/background helper process where a native API will do, see
// AppxUninstallExecutor's own console-hiding remarks) process-lineage check used by
// ProgramUninstallExecutor to detect a still-running DETACHED CHILD of the uninstaller it
// launched - exit code 0 plus the direct process having exited is not by itself proof a real
// uninstall interaction has finished, since a bootstrapper commonly hands off to a child
// installer/UI process and exits immediately.
//
// Matches by PARENT PROCESS ID (walked transitively), never by executable name - matching by
// name alone could accidentally pick up a completely unrelated process that merely happens to
// share a name with something this uninstaller launched.
internal static class ProcessLineage
{
    // True if any process CURRENTLY running on the machine descends (directly or transitively)
    // from rootPid AND started at or after rootStartTimeUtc. The start-time floor guards against
    // Windows PID reuse: rootPid could since have been reassigned to a totally unrelated process,
    // and a candidate that started before the uninstaller we launched physically cannot be one of
    // its descendants. Every failure mode here is deliberately biased toward "yes, assume a
    // descendant might still be alive" - this class exists specifically to keep
    // ProgramUninstallExecutor from reporting a false Succeeded, so an inability to positively
    // rule a candidate out must never read as ruling it out.
    public static bool HasLiveDescendant(int rootPid, DateTime rootStartTimeUtc)
    {
        List<(int Pid, int ParentPid)> processes;
        try
        {
            processes = SnapshotProcesses();
        }
        catch
        {
            // Couldn't even take the snapshot - can't prove there's no live descendant.
            return true;
        }

        // Structural traversal over ONE atomic snapshot - every parent/child link here was
        // observed at the same instant, so walking it is safe/bounded (a `visited` set also
        // guards against any theoretically-corrupt cyclic data) regardless of how any individual
        // pid's liveness later resolves; genuineness (start-time) is checked per-candidate
        // afterward, not during this walk.
        var byParent = processes
            .GroupBy(p => p.ParentPid)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Pid).ToList());

        var descendants = new HashSet<int>();
        var visited = new HashSet<int> { rootPid };
        var frontier = new Queue<int>();
        frontier.Enqueue(rootPid);

        while (frontier.Count > 0)
        {
            var pid = frontier.Dequeue();
            if (!byParent.TryGetValue(pid, out var children))
            {
                continue;
            }

            foreach (var childPid in children)
            {
                if (!visited.Add(childPid))
                {
                    continue;
                }

                descendants.Add(childPid);
                frontier.Enqueue(childPid);
            }
        }

        foreach (var pid in descendants)
        {
            if (IsGenuineLiveDescendant(pid, rootStartTimeUtc))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGenuineLiveDescendant(int pid, DateTime rootStartTimeUtc)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // No such process anymore - it already exited between the snapshot and this check,
            // not currently alive.
            return false;
        }
        catch
        {
            // Exists (per the snapshot) but couldn't even be opened - can't rule it out.
            return true;
        }

        try
        {
            using (process)
            {
                if (process.HasExited)
                {
                    return false;
                }

                return process.StartTime.ToUniversalTime() >= rootStartTimeUtc;
            }
        }
        catch
        {
            // Exists and is presumably still running, but its start time couldn't be read (e.g.
            // access denied to a protected process) - can't rule it out.
            return true;
        }
    }

    private static List<(int Pid, int ParentPid)> SnapshotProcesses()
    {
        var result = new List<(int Pid, int ParentPid)>();

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            throw new InvalidOperationException("Could not create a process snapshot.");
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                throw new InvalidOperationException("Could not enumerate the process snapshot.");
            }

            do
            {
                result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
