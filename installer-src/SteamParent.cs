using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// Starting a program with steam.exe as its parent process, the way Steam itself starts games.
// Why: when the launcher is opened from Explorer (the desktop shortcut), a game started from it is turned away by the game's own Steam check, which hands
// the launch to Steam, and Steam then starts the REGULAR game. Started with steam.exe as the parent, the co-op copy is accepted like a game Steam started.
static partial class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION info, int len, out int ret);

    // The pid of the process that started the given one (0 when it cannot be read).
    static int ParentPidOf(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);       // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return 0;
        try { var info = new PROCESS_BASIC_INFORMATION(); int ret; return NtQueryInformationProcess(h, 0, ref info, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out ret) == 0 ? (int)info.InheritedFromUniqueProcessId : 0; }
        finally { CloseHandle(h); }
    }

    // Starts exe with steam.exe as its parent. extraEnv is added to the environment the program gets. Returns the new process id, or 0 when it could not be done
    // (Steam not running, no permission, ...): the caller then starts the program the ordinary way.
    static int StartUnderSteam(string exe, string args, string workDir, IDictionary<string, string> extraEnv)
    {
        IntPtr steamHandle = IntPtr.Zero, list = IntPtr.Zero, parentBox = IntPtr.Zero, envPtr = IntPtr.Zero;
        try
        {
            // the steam.exe that is signed in (Steam writes its pid), else the oldest one
            var all = Process.GetProcessesByName("steam");
            int active = ActiveSteamPid();
            var steam = all.FirstOrDefault(p => p.Id == active) ?? all.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
            if (steam == null) return 0;
            steamHandle = OpenProcess(0x0080, false, steam.Id);        // PROCESS_CREATE_PROCESS
            if (steamHandle == IntPtr.Zero) return 0;
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) return 0;
            parentBox = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(parentBox, steamHandle);
            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020000, parentBox, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) return 0;   // PROC_THREAD_ATTRIBUTE_PARENT_PROCESS

            var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string)e.Value;
            if (extraEnv != null) foreach (var kv in extraEnv) vars[kv.Key] = kv.Value;
            var block = new StringBuilder();
            foreach (var kv in vars) block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            block.Append('\0');
            envPtr = Marshal.StringToHGlobalUni(block.ToString());

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            si.lpAttributeList = list;
            PROCESS_INFORMATION pi;
            string cmd = "\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args);
            if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400, envPtr, workDir, ref si, out pi)) return 0;   // EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        catch (Exception e) { Log("could not start under steam.exe: " + e.Message); return 0; }
        finally
        {
            if (list != IntPtr.Zero) { try { DeleteProcThreadAttributeList(list); } catch { } Marshal.FreeHGlobal(list); }
            if (parentBox != IntPtr.Zero) Marshal.FreeHGlobal(parentBox);
            if (envPtr != IntPtr.Zero) Marshal.FreeHGlobal(envPtr);
            if (steamHandle != IntPtr.Zero) CloseHandle(steamHandle);
        }
    }
}
