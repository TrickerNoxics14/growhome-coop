using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Self-checks of the launcher, run by audit\run-audit.ps1:   GrowHomeCoop.exe --audit [--use-game <game folder>] [--audit-out <file>]
// They exercise the real update and patch code against a throw-away folder and a throw-away web server on this PC; nothing real is touched.
// Output, one line per check:   CHECK <ID> <PASS|FAIL> <what> - <detail>
static partial class Program
{
    static readonly List<string> auditLines = new List<string>();
    static int auditFails;

    static void Check(string id, bool ok, string what, string detail)
    {
        lock (auditLines)
        {
            if (!ok) auditFails++;
            auditLines.Add("CHECK " + id + " " + (ok ? "PASS" : "FAIL") + " " + what + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
        }
    }

    static void WriteAuditOut()
    {
        string text;
        lock (auditLines) text = string.Join("\r\n", auditLines.ToArray()) + "\r\nRESULT fail=" + auditFails + "\r\nDONE\r\n";
        File.WriteAllText(auditOut ?? "audit-exe-result.txt", text);
        Console.WriteLine(text);
    }

    static int Audit()
    {
        string realGame = gameDir;
        string tmp = Path.Combine(Path.GetTempPath(), "growhome-coop-audit-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try { AuditUpdater(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the updater checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditSteamConfig(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the Steam setting checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditBorrower(); } catch (Exception e) { Check("AUDIT-ERROR", false, "the borrower checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditRunningGames(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the running-game checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditVerify(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the launch-verify checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditInstall(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the install checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditGui(tmp, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(auditOut ?? "audit-exe-result.txt")), "window")); } catch (Exception e) { Check("AUDIT-ERROR", false, "the window checks ran to the end", e.GetType().Name + ": " + e.Message + " " + (e.StackTrace ?? "").Split((char)10)[0].Trim()); }
        try { AuditUpdateButton(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the update button checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditDirectMarker(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the direct-start marker check ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditStartUnderSteam(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the start-under-steam check ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditStartMode(); } catch (Exception e) { Check("AUDIT-ERROR", false, "the start-mode checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditSafety(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the start-safety checks ran to the end", e.GetType().Name + ": " + e.Message + " " + (e.StackTrace ?? "").Split((char)10)[0].Trim()); }
        try { AuditHarden(tmp); } catch (Exception e) { Check("AUDIT-ERROR", false, "the hardening checks (steam_appid, co-op exe only, hang dump, mod pin, stale Steam) ran to the end", e.GetType().Name + ": " + e.Message + " " + (e.StackTrace ?? "").Split((char)10)[0].Trim()); }
        try { AuditPatch(tmp, realGame); } catch (Exception e) { Check("AUDIT-ERROR", false, "the patch checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { AuditThreads(); } catch (Exception e) { Check("AUDIT-ERROR", false, "the thread-safety checks ran to the end", e.GetType().Name + ": " + e.Message); }
        try { Directory.Delete(tmp, true); } catch { }
        WriteAuditOut();
        return auditFails == 0 ? 0 : 1;
    }

    static bool SameBytes(string file, byte[] expected)
    {
        if (!File.Exists(file)) return false;
        byte[] data = File.ReadAllBytes(file);
        return data.Length == expected.Length && data.SequenceEqual(expected);
    }

    static string auditPrivateKey, auditOtherKey;     // throw-away keys: the audit signs with one and trusts it; the real private key is never here

    // A manifest as the author would publish it: signed with the audit's own key (signature = null leaves it unsigned, "wrong" signs with another key).
    static string Manifest(int version, string dll, string sha, string signature = "good")
    {
        string sig = signature == "good" ? SignManifestForTest(auditPrivateKey, version, sha, dll) : signature == "wrong" ? SignManifestForTest(auditOtherKey, version, sha, dll) : signature;
        return "{\"version\": " + version + ", \"dll\": \"" + dll + "\", \"sha256\": \"" + sha + "\", \"notes\": \"audit\"" + (sig != null ? ", \"signature\": \"" + sig + "\"" : "") + "}";
    }

    static byte[] Bytes(string s) { return Encoding.ASCII.GetBytes(s); }

    static void AuditUpdater(string tmp)
    {
        installDir = tmp; gameDir = Path.Combine(tmp, "game");
        Directory.CreateDirectory(Path.GetDirectoryName(TargetDll));
        byte[] v1 = Bytes("mod version one");
        File.WriteAllBytes(TargetDll, v1);
        File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), "1");
        if (Process.GetProcessesByName("GrowHome").Length > 0) { Check("UPDATE-STAGED-APPLY", false, "Grow Home is closed while the updater is checked", "close the game and run the audit again"); return; }

        string realPublicKey = UpdatePublicKeyXml;
        using (var k1 = new System.Security.Cryptography.RSACryptoServiceProvider(2048)) { auditPrivateKey = k1.ToXmlString(true); UpdatePublicKeyXml = k1.ToXmlString(false); }
        using (var k2 = new System.Security.Cryptography.RSACryptoServiceProvider(2048)) auditOtherKey = k2.ToXmlString(true);
        var server = new MiniServer();
        try
        {
            string manifestPath = "/version.json";
            ManifestUrl = server.Url(manifestPath);
            var clock = Stopwatch.StartNew();

            // 1. a bad network never holds the game up and never touches the installed mod
            server.Hang(manifestPath);
            clock.Restart(); CheckForUpdate(false); long hangMs = clock.ElapsedMilliseconds;
            Check("UPDATE-OFFLINE-FAST", hangMs < 6000, "an update server that never answers delays the start by at most 6 s", hangMs + " ms");
            Check("UPDATE-OFFLINE-FAST", SameBytes(TargetDll, v1) && !File.Exists(StagedDll), "...and nothing is changed", "");
            var dead = new TcpListener(IPAddress.Loopback, 0); dead.Start(); int deadPort = ((IPEndPoint)dead.LocalEndpoint).Port; dead.Stop();
            ManifestUrl = "http://127.0.0.1:" + deadPort + manifestPath;
            clock.Restart(); CheckForUpdate(false); long refusedMs = clock.ElapsedMilliseconds;
            Check("UPDATE-OFFLINE-FAST", refusedMs < 4500, "a refused connection returns within a few seconds (Windows itself takes 2-3 s to report it)", refusedMs + " ms");
            clock.Restart(); ApplyStaged(); long applyMs = clock.ElapsedMilliseconds;
            Check("UPDATE-OFFLINE-FAST", applyMs < 200, "starting with nothing staged costs no time", applyMs + " ms");
            ManifestUrl = server.Url(manifestPath);

            // 2. a good update is staged first and installed at the next start
            byte[] v2 = Bytes("mod version two, brand new");
            server.Serve("/mod2.dll", v2); server.Serve(manifestPath, Manifest(2, server.Url("/mod2.dll"), Sha256(v2)));
            CheckForUpdate(false);
            Check("UPDATE-STAGED-APPLY", File.Exists(StagedDll) && File.Exists(StagedInfo) && SameBytes(TargetDll, v1) && InstalledVersion() == 1, "a newer version is downloaded and staged, not installed yet", "staged=" + File.Exists(StagedDll) + " installed version " + InstalledVersion());
            ApplyStaged();
            Check("UPDATE-STAGED-APPLY", SameBytes(TargetDll, v2) && InstalledVersion() == 2, "the staged version is installed at the next start", "installed version " + InstalledVersion());
            Check("UPDATE-STAGED-APPLY", SameBytes(TargetDll + ".bak", v1), "the previous mod is kept as .bak", "");
            Check("UPDATE-STAGED-APPLY", !File.Exists(StagedDll) && !File.Exists(StagedInfo), "the staged files are cleaned up", "");

            // 3. a download that does not match its fingerprint is never used
            byte[] v3good = Bytes("mod version three"), v3evil = Bytes("something else entirely");
            server.Serve("/mod3.dll", v3evil); server.Serve(manifestPath, Manifest(3, server.Url("/mod3.dll"), Sha256(v3good)));
            CheckForUpdate(false);
            Check("UPDATE-TAMPER-REJECT", !File.Exists(StagedDll) && SameBytes(TargetDll, v2), "a download that does not match its fingerprint is not staged", "");
            CheckForUpdate(true);
            Check("UPDATE-TAMPER-REJECT", SameBytes(TargetDll, v2) && InstalledVersion() == 2, "...and not installed either", "installed version " + InstalledVersion());

            // 4. the same or an older version does nothing
            foreach (int version in new[] { 2, 1, 0 })
            {
                byte[] other = Bytes("different bytes " + version);
                server.Serve("/modx.dll", other); server.Serve(manifestPath, Manifest(version, server.Url("/modx.dll"), Sha256(other)));
                CheckForUpdate(false);
                Check("UPDATE-NO-DOWNGRADE", !File.Exists(StagedDll) && SameBytes(TargetDll, v2), "a manifest with version " + version + " (installed is 2) changes nothing", "");
            }

            // 5. broken manifests are ignored without a crash
            var broken = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (name, json) => broken.Add(new KeyValuePair<string, string>(name, json));
            add("not json at all", "<html>502 bad gateway</html>");
            add("no fingerprint", "{\"version\": 9, \"dll\": \"" + server.Url("/mod2.dll") + "\"}");
            add("no download address", "{\"version\": 9, \"sha256\": \"" + Sha256(v2) + "\"}");
            add("plain http download from the internet", Manifest(9, "http://evil.example/mod.dll", Sha256(v2)));
            add("file:// download", Manifest(9, "file:///C:/Windows/System32/notepad.exe", Sha256(v2)));
            add("ftp download", Manifest(9, "ftp://evil.example/mod.dll", Sha256(v2)));
            add("version that is not a number", "{\"version\": \"abc\", \"dll\": \"" + server.Url("/mod2.dll") + "\", \"sha256\": \"" + Sha256(v2) + "\"}");
            add("version far too large", Manifest(9, server.Url("/mod2.dll"), Sha256(v2)).Replace("\"version\": 9", "\"version\": 99999999999999"));
            add("empty", "");
            foreach (var kv in broken)
            {
                server.Serve(manifestPath, kv.Value);
                bool threw = false;
                try { CheckForUpdate(false); } catch (Exception) { threw = true; }
                Check("UPDATE-BAD-MANIFEST", !threw && !File.Exists(StagedDll) && SameBytes(TargetDll, v2), "a manifest with " + kv.Key + " is ignored", threw ? "an exception escaped" : "");
            }
            bool prod = auditMode; auditMode = false;     // outside the audit only https is allowed, not even this PC over http
            bool httpsOk = UrlAllowed("https://raw.githubusercontent.com/x/y/main/mod.dll"), loopbackOk = UrlAllowed("http://127.0.0.1:80/mod.dll"), httpOk = UrlAllowed("http://example.com/mod.dll");
            auditMode = prod;
            Check("UPDATE-BAD-MANIFEST", httpsOk && !loopbackOk && !httpOk, "outside the audit only https download addresses are accepted", "https=" + httpsOk + " loopback http=" + loopbackOk + " http=" + httpOk);

            // 6. a staged file that changed after it was downloaded, or is not newer, is refused
            byte[] v4 = Bytes("mod version four");
            server.Serve("/mod4.dll", v4); server.Serve(manifestPath, Manifest(4, server.Url("/mod4.dll"), Sha256(v4)));
            CheckForUpdate(false);
            bool staged = File.Exists(StagedDll);
            File.AppendAllText(StagedDll, "tampered");
            ApplyStaged();
            Check("UPDATE-STAGE-TAMPER", staged && SameBytes(TargetDll, v2) && !File.Exists(StagedDll), "a staged file changed after its download is refused and removed", "staged=" + staged);
            byte[] v2b = Bytes("not newer than what is installed");
            File.WriteAllBytes(StagedDll, v2b); File.WriteAllText(StagedInfo, "2\n" + Sha256(v2b));
            ApplyStaged();
            Check("UPDATE-STAGE-TAMPER", SameBytes(TargetDll, v2) && !File.Exists(StagedDll), "a staged file that is not newer is refused and removed", "");

            // 7. a very slow network: the short check gives up harmlessly, the background check is patient, and checking can be switched off
            byte[] v5 = Bytes("mod version five");
            server.Serve("/mod5.dll", v5); server.Serve(manifestPath, Manifest(5, server.Url("/mod5.dll"), Sha256(v5)));
            server.Delay(manifestPath, 6000);
            int shortTry = CheckForUpdate(false);
            Check("UPDATE-BACKGROUND", shortTry == 0 && !File.Exists(StagedDll) && SameBytes(TargetDll, v2), "a very slow server makes the short check give up without harm", "");
            clock.Restart(); int patient = CheckForUpdate(false, true); long patientMs = clock.ElapsedMilliseconds;
            Check("UPDATE-BACKGROUND", patient == 5 && File.Exists(StagedDll) && patientMs >= 5000, "the background check is patient and gets the update from the slow server", "version " + patient + " staged after " + patientMs + " ms");
            File.Delete(StagedDll); File.Delete(StagedInfo); server.Delay(manifestPath, 0);
            string off = Path.Combine(installDir, "coop-noupdate.txt");
            File.WriteAllText(off, "off");
            int requestsBefore = server.Requests; int skipped = CheckForUpdate(false, true);
            Check("UPDATE-BACKGROUND", skipped == 0 && server.Requests == requestsBefore && !File.Exists(StagedDll), "with coop-noupdate.txt not a single request is made", (server.Requests - requestsBefore) + " requests");
            File.Delete(off);
            WriteUpdateReady(5);
            string note = Path.Combine(gameDir, "coop-update-ready.txt");
            Check("UPDATE-BACKGROUND", File.Exists(note) && File.ReadAllText(note).Trim() == "5", "the launcher leaves the game a note about the downloaded update", "");

            // 8. only the author can update: signatures, allowed hosts, redirects, size
            byte[] v6 = Bytes("mod version six");
            string v6url = server.Url("/mod6.dll"), v6sha = Sha256(v6);
            server.Serve("/mod6.dll", v6);
            Action<string, string> tryManifest = (name, json) => { };
            Func<string, int> staged6 = json => { server.Serve(manifestPath, json); File.Delete(StagedDll); File.Delete(StagedInfo); return CheckForUpdate(false); };
            Check("UPDATE-SIGNED", staged6(Manifest(6, v6url, v6sha)) == 6 && File.Exists(StagedDll), "an update signed by the author is accepted", "");
            File.Delete(StagedDll); File.Delete(StagedInfo);
            Check("UPDATE-SIGNED", staged6(Manifest(6, v6url, v6sha, null)) == 0 && !File.Exists(StagedDll), "an update with no signature is refused", "");
            Check("UPDATE-SIGNED", staged6(Manifest(6, v6url, v6sha, "wrong")) == 0 && !File.Exists(StagedDll), "an update signed with somebody else's key is refused", "");
            Check("UPDATE-SIGNED", staged6(Manifest(6, v6url, v6sha, "not base64 at all !!")) == 0 && !File.Exists(StagedDll), "a damaged signature is refused", "");
            string goodSig = SignManifestForTest(auditPrivateKey, 6, v6sha, v6url);
            byte[] otherFile = Bytes("a different file entirely"); server.Serve("/modx6.dll", otherFile);
            Check("UPDATE-SIGNED", staged6(Manifest(6, v6url, Sha256(otherFile), goodSig)) == 0 && !File.Exists(StagedDll), "a signature does not carry over to a different file fingerprint", "");
            Check("UPDATE-SIGNED", staged6(Manifest(6, server.Url("/modx6.dll"), v6sha, goodSig)) == 0 && !File.Exists(StagedDll), "a signature does not carry over to a different download address", "");
            Check("UPDATE-SIGNED", staged6(Manifest(7, v6url, v6sha, goodSig)) == 0 && !File.Exists(StagedDll), "an old signed manifest cannot be replayed with a higher version number", "");
            UpdatePublicKeyXml = realPublicKey;
            Check("UPDATE-SIGNED", !VerifyManifest(6, v6sha, v6url, goodSig), "the key built into the launcher does not accept a signature it did not make", "");
            using (var probe = new System.Security.Cryptography.RSACryptoServiceProvider()) { probe.FromXmlString(realPublicKey); Check("UPDATE-SIGNED", probe.KeySize >= 3072 && !realPublicKey.Contains("<D>") && !realPublicKey.Contains("<P>"), "the built-in key is a 3072-bit public key with no private part", probe.KeySize + " bits"); }
            using (var k = new System.Security.Cryptography.RSACryptoServiceProvider(2048)) { k.FromXmlString(auditPrivateKey); UpdatePublicKeyXml = k.ToXmlString(false); }

            bool hostsOk = UrlAllowed("https://raw.githubusercontent.com/TrickerNoxics14/growhome-coop/main/CoopMod.dll") && UrlAllowed("https://github.com/TrickerNoxics14/growhome-coop/releases/download/v2/x.dll");
            bool prod2 = auditMode; auditMode = false;
            bool evilBlocked = !UrlAllowed("https://evil.example/mod.dll") && !UrlAllowed("https://raw.githubusercontent.com.evil.example/mod.dll") && !UrlAllowed("https://user:pw@github.com/mod.dll")
                && !UrlAllowed("http://github.com/mod.dll") && !UrlAllowed("https://127.0.0.1/mod.dll") && !UrlAllowed("file:///C:/Windows/notepad.exe") && !UrlAllowed("ftp://github.com/x") && !UrlAllowed("not a url") && !UrlAllowed("http://127.0.0.1:80/x");
            auditMode = prod2;
            Check("UPDATE-SIGNED", hostsOk && evilBlocked, "downloads are only allowed from GitHub over https (look-alike hosts, passwords in the address, plain http and local files are not)", "github ok=" + hostsOk + ", others blocked=" + evilBlocked);

            server.Redirect("/mod6r.dll", "http://evil.example/mod.dll");
            string redirectedManifest = Manifest(6, server.Url("/mod6r.dll"), v6sha);
            Check("UPDATE-SIGNED", staged6(redirectedManifest) == 0 && !File.Exists(StagedDll), "a download that is redirected somewhere else is refused", "");
            byte[] huge = new byte[9 * 1024 * 1024]; server.Serve("/mod6big.dll", huge);
            Check("UPDATE-SIGNED", staged6(Manifest(6, server.Url("/mod6big.dll"), Sha256(huge))) == 0 && !File.Exists(StagedDll), "a download far bigger than a mod is refused", "");
            server.Serve("/mod6.dll", v6);
        }
        finally { server.Stop(); UpdatePublicKeyXml = realPublicKey; }
    }

    static bool StartsWithInit(string assemblyPath)
    {
        var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(assemblyPath);
        var awake = asm.MainModule.GetType("SteamManager").Methods.First(m => m.Name == "Awake");
        var first = awake.Body.Instructions[0];
        return first.OpCode == Mono.Cecil.Cil.OpCodes.Call && first.Operand is Mono.Cecil.MethodReference && ((Mono.Cecil.MethodReference)first.Operand).FullName.Contains("CoopMod.Bootstrap::Init");
    }

    static int AwakeLength(string assemblyPath)
    {
        var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(assemblyPath);
        return asm.MainModule.GetType("SteamManager").Methods.First(m => m.Name == "Awake").Body.Instructions.Count;
    }

    static void AuditPatch(string tmp, string realGame)
    {
        string managed = Path.Combine(realGame, "GrowHome_Data", "Managed");
        string orig = Path.Combine(managed, "Assembly-CSharp.dll.orig"), live = Path.Combine(managed, "Assembly-CSharp.dll"), mod = Path.Combine(managed, "CoopMod.dll");
        if (!File.Exists(orig) || !File.Exists(live)) { Check("PATCH-CORRECT", false, "the installed game has its original assembly kept as .orig", "missing in " + managed); return; }
        string origSha = Sha256(orig);
        Check("PATCH-CORRECT", origSha == ExpectedAssemblySha, "the kept original is the supported Steam build 857617", origSha.Substring(0, 12));
        string patched = Path.Combine(tmp, "Assembly-CSharp.patched.dll");
        PatchWithCecil(managed, patched, orig);
        Check("PATCH-CORRECT", StartsWithInit(patched), "patching puts CoopMod.Bootstrap.Init() first in SteamManager.Awake", "");
        Check("PATCH-CORRECT", AwakeLength(patched) == AwakeLength(orig) + 1, "patching adds exactly one instruction", AwakeLength(orig) + " -> " + AwakeLength(patched));
        Check("INSTALL-PATCHED", StartsWithInit(live), "the installed game calls the mod at start-up", "");
        Check("INSTALL-PATCHED", File.Exists(mod) && new FileInfo(mod).Length > 1000, "the installed mod file exists", File.Exists(mod) ? Sha256(mod).Substring(0, 12) : "missing");
    }

    // A tiny web server on this PC for the updater checks.
    sealed class MiniServer
    {
        readonly TcpListener listener;
        readonly Dictionary<string, byte[]> routes = new Dictionary<string, byte[]>();
        readonly HashSet<string> hangs = new HashSet<string>();
        readonly Dictionary<string, int> delays = new Dictionary<string, int>();
        public int Requests;
        volatile bool stopped;
        public readonly int Port;

        public MiniServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Bg("audit web server", Loop).Start();
        }

        public string Url(string path) { return "http://127.0.0.1:" + Port + path; }
        public void Serve(string path, string text) { Serve(path, Encoding.UTF8.GetBytes(text)); }
        public void Serve(string path, byte[] body) { lock (routes) { routes[path] = body; hangs.Remove(path); } }
        public void Hang(string path) { lock (routes) hangs.Add(path); }
        readonly Dictionary<string, string> redirects = new Dictionary<string, string>();
        public void Redirect(string path, string to) { lock (routes) redirects[path] = to; }
        public void Delay(string path, int ms) { lock (routes) delays[path] = ms; }
        public void Stop() { stopped = true; try { listener.Stop(); } catch { } }

        void Loop()
        {
            while (!stopped)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } catch { } });
            }
        }

        void Handle(TcpClient c)
        {
            try
            {
                using (c)
                {
                    var s = c.GetStream(); s.ReadTimeout = 5000;
                    var head = new StringBuilder(); int b;
                    while (head.Length < 8192 && (b = s.ReadByte()) >= 0) { head.Append((char)b); if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break; }
                    string[] first = head.ToString().Split('\n')[0].Split(' ');
                    string path = first.Length > 1 ? first[1].Split('?')[0] : "/";
                    byte[] body; bool hang; int delay;
                    Interlocked.Increment(ref Requests);
                    lock (routes) { hang = hangs.Contains(path); routes.TryGetValue(path, out body); delays.TryGetValue(path, out delay); }
                    if (delay > 0) Thread.Sleep(delay);
                    if (hang) { Thread.Sleep(30000); return; }
                    string moved; lock (routes) redirects.TryGetValue(path, out moved);
                    if (moved != null) { byte[] r = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + moved + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); s.Write(r, 0, r.Length); return; }
                    byte[] headBytes = Encoding.ASCII.GetBytes((body == null ? "HTTP/1.1 404 Not Found" : "HTTP/1.1 200 OK") + "\r\nContent-Length: " + (body == null ? 0 : body.Length) + "\r\nConnection: close\r\n\r\n");
                    s.Write(headBytes, 0, headBytes.Length);
                    if (body != null) s.Write(body, 0, body.Length);
                }
            }
            catch { }
        }
    }
}
