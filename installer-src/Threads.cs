using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Background threads that can never take the launcher down. An exception on a thread of its own ends a .NET program with the Windows
// "has stopped working" dialog; every worker thread of the launcher (normal and audit mode) runs through Bg(), which catches it, writes it
// to launcher.log and counts it, and the audit turns any such count into a FAIL (THREAD-SAFE) instead of a crash.
static partial class Program
{
    static int threadFaults;
    static readonly List<string> threadFaultText = new List<string>();

    static Thread Bg(string what, ThreadStart body)
    {
        return new Thread(() =>
        {
            try { body(); }
            catch (Exception e)
            {
                lock (threadFaultText) { threadFaults++; threadFaultText.Add(what + ": " + e.GetType().Name + ": " + e.Message); }
                try { Log("background work '" + what + "' stopped: " + e); } catch { }
            }
        }) { IsBackground = true };
    }

    // ThreadPool work the same way
    static void BgPool(string what, Action body) { ThreadPool.QueueUserWorkItem(_ => { var t = Bg(what, () => body()); t.Start(); t.Join(); }); }

    // A copy of ping.exe started as a fake game. A file copied a moment ago can be held by a virus scanner (CreateProcess then fails with
    // "used by another process"): the copy is written under another name and moved into place, and the start is tried again for up to 5 s.
    static Process StartFake(string exePath, string args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(exePath));
        if (!File.Exists(exePath))
        {
            string part = exePath + ".part";
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), part, true);
            File.Move(part, exePath);
        }
        Exception last = null;
        for (int i = 0; i < 20; i++)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(exePath) });
                Thread.Sleep(500);
                return p;
            }
            catch (Win32Exception e) { last = e; Thread.Sleep(250); }
        }
        throw new IOException("the fake game " + exePath + " could not be started: " + (last != null ? last.Message + " (" + ((Win32Exception)last).NativeErrorCode + ")" : "?"), last);
    }

    // ---- THREAD-SAFE (audit)
    static void AuditThreads()
    {
        // a thread that throws is caught, logged and counted (it must not end the launcher)
        int before; lock (threadFaultText) before = threadFaults;
        var t = Bg("audit: a thread that throws on purpose", () => { throw new InvalidOperationException("on purpose"); });
        t.Start(); bool joined = t.Join(5000);
        bool caught; lock (threadFaultText) { caught = threadFaults == before + 1; if (caught) { threadFaults--; threadFaultText.RemoveAt(threadFaultText.Count - 1); } }
        var done = new ManualResetEvent(false); int ran = 0;
        BgPool("audit: pool work that throws on purpose", () => { ran = 1; done.Set(); throw new InvalidOperationException("on purpose"); });
        bool poolRan = done.WaitOne(5000); Thread.Sleep(200);
        bool poolCaught; lock (threadFaultText) { poolCaught = threadFaults == before + 1; if (poolCaught) { threadFaults--; threadFaultText.RemoveAt(threadFaultText.Count - 1); } }
        // a fake game can be started right after its file was copied (the start that crashed the audit once)
        string dir = Path.Combine(Path.GetTempPath(), "growhome-coop-fake-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        bool fakeOk = false; string fd = "";
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Process p = null;
                try { p = StartFake(Path.Combine(dir, "f" + i, "GrowHome.exe"), "-n 3 127.0.0.1"); fakeOk = p != null; }
                catch (Exception e) { fakeOk = false; fd = e.Message; break; }
                finally { try { if (p != null && !p.HasExited) p.Kill(); if (p != null) p.WaitForExit(3000); } catch { } }
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        uint mode = 0; try { mode = GetErrorMode(); } catch { }
        bool quiet = (mode & (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX)) == (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
        // the window's thread: an exception in a posted action is caught by the launcher's own handler (logged, counted), never shown as the WinForms
        // "Unhandled exception" dialog; a frame that cannot be drawn is skipped. Only raised when the handler is on, so a broken build cannot show the dialog here.
        bool uiCaught = false, paintSkipped = false; string ud = "";
        if (!uiHandlerOn) ud = "the window's crash handler is not set";
        else
        {
            int u0; lock (auditLines) u0 = uiFaults;
            uiFaultTest = true;
            try
            {
                using (var w = new GameWindow(true))
                {
                    var h = w.Handle;
                    w.BeginInvoke(new Action(() => { throw new InvalidOperationException("audit: a posted action that throws on purpose"); }));
                    for (int i = 0; i < 100; i++) { System.Windows.Forms.Application.DoEvents(); lock (auditLines) if (uiFaults > u0) break; Thread.Sleep(20); }
                    lock (auditLines) uiCaught = uiFaults == u0 + 1;
                    w.TestPaintFault = () => { throw new System.Runtime.InteropServices.ExternalException("audit: a generic GDI+ error on purpose"); };
                    using (var bmp = new System.Drawing.Bitmap(320, 180)) using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        bool drew = w.SafeDraw(g, bmp.Size);
                        w.TestPaintFault = null;
                        paintSkipped = !drew && w.PaintFaults == 1 && w.SafeDraw(g, bmp.Size);
                    }
                }
            }
            catch (Exception e) { ud = "it escaped: " + e.GetType().Name + ": " + e.Message; }
            finally { uiFaultTest = false; lock (auditLines) uiFaults = u0; }
        }
        int faults; string faultText; lock (threadFaultText) { faults = threadFaults; faultText = string.Join(" | ", threadFaultText.ToArray()); }
        Check("THREAD-SAFE", joined && caught && ran == 1 && poolRan && poolCaught && fakeOk && quiet && crashHandlerOn && uiCaught && paintSkipped && faults == 0,
            "no background thread can crash the launcher: a throwing thread or pool job is caught and logged, an unhandled exception exits with a code (no Windows dialog: error mode set), an exception on the window's thread goes to the launcher's handler (no WinForms dialog) and a bad frame is skipped, a fake game starts right after its copy, and no thread of this run faulted",
            "thread caught=" + caught + ", pool caught=" + poolCaught + ", fake starts=" + fakeOk + (fd.Length > 0 ? " (" + fd + ")" : "") + ", error mode 0x" + mode.ToString("X") + ", crash handler=" + crashHandlerOn + ", window thread caught=" + uiCaught + ", bad frame skipped=" + paintSkipped + (ud.Length > 0 ? " (" + ud + ")" : "") + ", faults this run " + faults + (faults > 0 ? ": " + faultText : ""));
    }
}
