using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;

namespace CoopMod
{
    // Keeps the test copy's saves out of the owner's real save folder and Steam Cloud.
    // Saves go to <exe dir>\coop-saves, seeded once (read-only) from the account's cloud save; cloud writes are switched off.
    public static class SaveGuard
    {
        public static string Root;
        static bool redirected, seeded;
        static Type steamManagerType;
        static Component steamManager;

        public static void Redirect(string root)
        {
            Root = root;
            try { string gs = Path.Combine(root, "guest-session"); if (Directory.Exists(gs)) Directory.Delete(gs, true); } catch (Exception) { }   // a guest session never outlives the game
            Directory.CreateDirectory(Path.Combine(root, "Levels"));
            SaveSystem.m_playerSavePath = root;
            SaveSystem.m_levelFilesPath = Path.Combine(root, "Levels");
            redirected = true;
        }

        // Call every frame early in the session: SteamManager re-enables cloud saves in OnEnable, so keep forcing it off.
        public static void Tick()
        {
            if (!redirected) return;
            try
            {
                if (steamManagerType == null) steamManagerType = Type.GetType("SteamManager, Assembly-CSharp");
                if (steamManagerType == null) return;
                if (steamManager == null) steamManager = UnityEngine.Object.FindObjectOfType(steamManagerType) as Component;
                if (steamManager == null) return;
                steamManagerType.GetField("m_cloudSavable").SetValue(steamManager, false);
                steamManagerType.GetField("m_cloudSaveEnabled").SetValue(steamManager, false);
                if (!seeded) SeedFromCloud();
            }
            catch (Exception e) { CoopBehaviour.Log("SaveGuard: " + e.GetType().Name + " " + e.Message); }
        }

        // A Steam Cloud name becomes a file path: refuse anything that could leave the save folder (.., rooted, drive letters, back-slashes, control characters).
        public static bool SafeCloudName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 200) return false;
            if (name[0] == '/' || name.IndexOf(':') >= 0 || name.IndexOf('\\') >= 0) return false;
            foreach (char c in name) if (c < 0x20) return false;
            foreach (string part in name.Split('/')) if (part.Length == 0 || part == "." || part == "..") return false;
            return true;
        }

        static void SeedFromCloud()
        {
            if (!SteamUser.GetSteamID().IsValid()) return; // Steam not up yet; try again next frame
            string marker = Path.Combine(Root, "seeded.txt");
            if (File.Exists(marker)) { seeded = true; return; }
            int n = SteamRemoteStorage.GetFileCount();
            for (int i = 0; i < n; i++)
            {
                int size;
                string name = SteamRemoteStorage.GetFileNameAndSize(i, out size);
                if (string.IsNullOrEmpty(name) || size <= 0) continue;
                if (!SafeCloudName(name)) { CoopBehaviour.Log("SaveGuard: refused a cloud file with an unsafe name"); continue; }
                var buf = new byte[size];
                int got = SteamRemoteStorage.FileRead(name, buf, size);
                if (got != size) { CoopBehaviour.Log("SaveGuard: short cloud read for " + name); continue; }
                string dest = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
                if (!Path.GetFullPath(dest).StartsWith(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllBytes(dest, buf);
                CoopBehaviour.Log("SaveGuard: copied cloud file " + name + " (" + size + " bytes) into coop-saves");
            }
            File.WriteAllText(marker, "seeded from cloud (read-only copy)");
            seeded = true;
        }
    }

    // World snapshots: the peer serialises its scene with the game's own save system and we load it.
    public static class WorldSync
    {
        const byte MsgRequest = 1, MsgSnapshot = 2;
        public static string Status = "";
        public static bool GuestMode;   // I joined somebody else's world: my own save must not be touched
        static string ownPlayerPath, ownLevelsPath;   // where my own saves were before guest mode, put back when I am on the start screen without a party
        public static bool Preloading, Preloaded;     // following the party leader in: their world is written as my (guest) save before the game loads it
        public static bool IsSnapshot(byte[] m) { return m != null && m.Length > 0 && (m[0] == MsgSnapshot || m[0] == WorldTransfer.MsgPart); }

        // ---- whose world this is ----
        // The leader numbers the world it plays (a new number whenever it leaves that world or starts a new game). A guest counts as being in the
        // leader's world only while the world it loaded carries the number the leader says it plays now: then, and only then, do its changes count.
        public static int MyEpoch = NewEpoch();
        public static int LoadedEpoch;      // (guest) the leader world I loaded, 0 = none
        public static int LeaderEpoch;      // (guest) the world the leader says it plays now
        static int pendingEpoch, incomingEpoch;
        static bool wasInGame, startedByFollow;
        public static bool HostWorldLoaded { get { return Authority.InLeaderWorld(GuestMode, LoadedEpoch, LeaderEpoch); } }
        static int NewEpoch() { int e = (int)((DateTime.UtcNow.Ticks ^ ((long)Environment.TickCount << 20)) & 0x7FFFFFFF); return e == 0 ? 1 : e; }
        public static void NewLeaderWorld(string why) { MyEpoch = NewEpoch(); SharedWorld.ForgetWorld(); CoopBehaviour.Log("world: my world has a new number (" + why + "); players count again once they have it"); }

        // Every frame, with or without other players: a game that starts or ends is another world (numbers renew, a guest's copy no longer counts).
        static System.Reflection.FieldInfo newGameField;
        public static void Transitions()
        {
            bool game = InGameOrLoading();
            if (game && !wasInGame)
            {
                if (GuestMode && !startedByFollow && LoadedEpoch != 0) { LoadedEpoch = 0; CoopBehaviour.Log("world: my game started without the leader's world being loaded for it; it is copied again"); }
                if (!GuestMode && StartedAsNewGame()) NewLeaderWorld("a new game started");
                startedByFollow = false;
            }
            if (!game && wasInGame) NewLeaderWorld("I left the world");
            wasInGame = game;
        }

        static bool StartedAsNewGame()
        {
            try
            {
                if (!Singleton<StartScreen>.Exists) return false;
                if (newGameField == null) newGameField = typeof(StartScreen).GetField("m_newGame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return newGameField != null && (bool)newGameField.GetValue(Singleton<StartScreen>.Instance);
            }
            catch (Exception) { return true; }     // cannot tell: a new number is the safe side
        }
        public static bool RequestOutstanding { get { return requestedAt > 0f; } }
        static readonly Dictionary<ITransport, WorldTransfer.Assembly> incoming = new Dictionary<ITransport, WorldTransfer.Assembly>();
        static int transferSeq = Environment.TickCount;

        // a world is on its way in (a part arrived in the last 10 s): asking again would only start it over
        public static bool Receiving { get { foreach (var a in incoming.Values) if (a.Have > 0 && Time.realtimeSinceStartup - a.LastPartAt < 10f) return true; return false; } }
        public static string Progress { get { foreach (var a in incoming.Values) if (a.Have > 0 && Time.realtimeSinceStartup - a.LastPartAt < 10f) return a.Have + "/" + a.Parts; return ""; } }

        // A copy of my own world before other players come in, so a bad session can always be undone (the newest 10 are kept).
        static float lastBackup = -999f;
        public static string BackupOwnWorld(string why, bool force, string into = null)
        {
            try
            {
                if (GuestMode || SaveGuard.Root == null || (!force && Time.realtimeSinceStartup - lastBackup < 300f)) return null;
                string level = Application.loadedLevelName, file = Path.Combine(SaveSystem.m_levelFilesPath, level + ".sav");
                if (!File.Exists(file)) return null;
                string dir = into ?? Path.Combine(SaveGuard.Root, "backups");
                Directory.CreateDirectory(dir);
                string to = Path.Combine(dir, level + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sav");
                File.Copy(file, to, true);
                lastBackup = Time.realtimeSinceStartup;
                var old = new List<string>(Directory.GetFiles(dir, level + "-*.sav"));
                old.Sort(StringComparer.Ordinal);
                while (old.Count > 10) { File.Delete(old[0]); old.RemoveAt(0); }
                CoopBehaviour.Log("world: backed up my world (" + why + ") to " + to);
                return to;
            }
            catch (Exception e) { CoopBehaviour.Log("world: backup failed " + e.GetType().Name + " " + e.Message); return null; }
        }

        // Guests play in the host's world. Point every save at a throwaway folder first so the guest's own save file is never read-modify-written.
        public static void EnterGuestMode()
        {
            if (GuestMode) return;
            // every step first, guest mode last: if anything fails the saves stay where they were and no world from anybody is accepted (GuestPathsActive)
            try
            {
                string root = Path.Combine(SaveGuard.Root, "guest-session");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(Path.Combine(root, "Levels"));
                string profile = Path.Combine(SaveGuard.Root, "PlayerProfile.sav");
                if (File.Exists(profile)) File.Copy(profile, Path.Combine(root, "PlayerProfile.sav"), true);
                ownPlayerPath = SaveSystem.m_playerSavePath; ownLevelsPath = SaveSystem.m_levelFilesPath;
                LoadedEpoch = 0;
                NewLeaderWorld("now a guest");
                SaveSystem.m_playerSavePath = root;
                SaveSystem.m_levelFilesPath = Path.Combine(root, "Levels");
                GuestMode = true;
                CoopBehaviour.Log("guest mode: saves redirected to " + root + "; my own save is untouched");
                CoopUI.Toast("GUEST MODE: YOUR OWN SAVE IS SAFE");
            }
            catch (Exception e) { CoopBehaviour.Log("guest mode failed: " + e.GetType().Name + " " + e.Message + "; no world will be loaded"); CoopUI.Toast("GUEST MODE FAILED: THE HOST'S WORLD IS NOT LOADED"); }
            CoopMenu.RefreshStartMenu();
        }

        // Both save folders really point into guest-session: the only state in which another player's world may be written to disk or loaded.
        public static bool GuestPathsActive()
        {
            try
            {
                if (!GuestMode || SaveGuard.Root == null || SaveSystem.m_playerSavePath == null || SaveSystem.m_levelFilesPath == null) return false;
                char[] seps = { '\\', '/' };
                string g = Path.GetFullPath(Path.Combine(SaveGuard.Root, "guest-session")).TrimEnd(seps);
                string p = Path.GetFullPath(SaveSystem.m_playerSavePath).TrimEnd(seps), l = Path.GetFullPath(SaveSystem.m_levelFilesPath).TrimEnd(seps);
                return (p.Equals(g, StringComparison.OrdinalIgnoreCase) || p.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    && l.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // The game reads a world file in its own start coroutine without any error handling, so a world from the network is read once here first
        // (the same header and blocks the game reads). False for anything damaged.
        public static bool LooksLikeWorld(byte[] world)
        {
            if (world == null || world.Length < 9) return false;
            // the game's reader allocates whatever length the data claims: every length is checked against the bytes really there first
            int outer = BitConverter.ToInt32(world, 5);
            if (outer < 0 || outer > world.Length - 9) return false;
            int version = SaveSystem.m_loadedDataVersion;
            try
            {
                var header = typeof(SaveSystem).GetMethod("ReadHeader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (header == null) return false;
                var s = new Serializer(false); s.Bytes = world;
                var body = (Serializer)header.Invoke(null, new object[] { s });
                byte[] blocks = body.Bytes;
                int at = 0;
                if (!BlockFits(blocks, ref at, 0)) return false;
                new SaveBlock(false).Serialize(body);
                return true;
            }
            catch (Exception e)
            {
                // damaged data from another PC is refused, not a fault of the mod: logged as a refusal (no stack, which the audit would take for a crash)
                var inner = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                CoopBehaviour.Log("world: a world from the network is damaged and was refused (" + inner.GetType().Name.Replace("Exception", "") + ")");
                return false;
            }
            finally { SaveSystem.m_loadedDataVersion = version; }
        }

        // One save block as SaveBlock.Serialize reads it (data length, type, child count, data, children), every length inside the buffer.
        static bool BlockFits(byte[] b, ref int at, int depth)
        {
            if (b == null || depth > 64 || at < 0 || b.Length - at < 12) return false;
            int len = BitConverter.ToInt32(b, at), children = BitConverter.ToInt32(b, at + 8);
            at += 12;
            if (len < 0 || len > b.Length - at) return false;
            at += len;
            if (children < 0 || children > (b.Length - at) / 12) return false;
            for (int i = 0; i < children; i++) if (!BlockFits(b, ref at, depth + 1)) return false;
            return true;
        }

        // a player who is dropped: never answer or wait for them again
        public static void Forget(ITransport link)
        {
            if (link == null) return;
            waiters.Remove(link); incoming.Remove(link);
            if (requestTarget == link) { requestTarget = null; requestedAt = -1f; retries = 0; }
        }

        // Back on the start screen after a party: my own saves again (only there, never inside somebody's world). The profile is read again from my own
        // folder, as the start screen does, so the guest copy is never written over mine.
        public static void LeaveGuestMode()
        {
            if (!GuestMode || InGameOrLoading() || StartingNow() || ownPlayerPath == null || ownLevelsPath == null) return;
            SaveSystem.m_playerSavePath = ownPlayerPath; SaveSystem.m_levelFilesPath = ownLevelsPath;
            GuestMode = false; pending = null; waiters.Clear(); requestedAt = -1f; Preloading = false; Preloaded = false; SkipAutoPull = false; lateCopiesUntil = -1f;
            LoadedEpoch = 0; LeaderEpoch = 0; incoming.Clear();
            NewLeaderWorld("my own save again");
            try { SaveSystem.LoadPlayerProfile(); } catch (Exception e) { CoopBehaviour.Log("guest mode: reading my own profile again failed " + e.GetType().Name); }
            CoopBehaviour.Log("guest mode ended: saves are mine again (" + ownPlayerPath + ")");
            CoopUI.Toast("BACK TO YOUR OWN SAVE");
            CoopMenu.RefreshStartMenu();
        }

        // The game itself says a game is running or a load is under way (stricter than InWorld, which also needs BUD active: BUD is off for a moment
        // while the start screen fades into the game, and my own saves must never come back then).
        public static bool InGameOrLoading()
        {
            return (Singleton<SceneProfile>.Exists && Singleton<SceneProfile>.Instance.m_playerIsInGame) || SaveSystem.loadingScene;
        }

        public static string OwnPlayerPathForTest { get { return ownPlayerPath; } }
        public static string OwnLevelsPathForTest { get { return ownLevelsPath; } }
        public static void BeginPreload() { Preloading = true; Preloaded = false; pending = null; SkipAutoPull = false; }
        public static bool SkipAutoPull;   // the leader's world was loaded at the start: the usual pull a few seconds into the game would only load it twice
        static float lateCopiesUntil = -1f;
        // A follow has started the game's own start (fade, then load): my saves must stay the guest ones until the game is in, whatever the party does.
        public static bool Starting;
        static float startingSince;
        public static void BeginStart() { Starting = true; startedByFollow = true; startingSince = Time.realtimeSinceStartup; }
        // only right after a follow: a pull needed much later (a real reconnect) is never skipped
        public static bool SkipAutoPullNow() { if (SkipAutoPull && Time.realtimeSinceStartup - startingSince > 120f) SkipAutoPull = false; return SkipAutoPull; }
        public static bool StartingNow()
        {
            if (Starting && (InGameOrLoading() || !Singleton<StartScreen>.Exists || Time.realtimeSinceStartup - startingSince > 60f)) Starting = false;
            return Starting;
        }
        public static void EndPreload() { Preloading = false; }

        // Only touch the world when a game is actually running: BUD exists, the game says the player is in game, and no load is in progress.
        public static bool InWorld()
        {
            if (GameObject.Find("Walkster") == null) return false;
            if (!Singleton<SceneProfile>.Exists || !Singleton<SceneProfile>.Instance.m_playerIsInGame) return false;
            return !SaveSystem.loadingScene;
        }
        public static bool SkinsMayBeReset;   // set after a world load, which re-skins every SkinSwapper
        static byte[] pending;
        static float pendingSince;
        static readonly List<ITransport> waiters = new List<ITransport>();   // players who asked for my world while I was not in the world yet; answered once I am
        static ITransport requestTarget;     // who I last asked for their world (for retries)
        static float requestedAt = -1f; static int retries;   // my own request, waiting for the snapshot to arrive

        // Call every frame: applies a snapshot that arrived while BUD was inactive, answers a request that came too early, retries a request that got no answer.
        public static void Tick()
        {
            bool inWorld = InWorld();
            if (waiters.Count > 0 && inWorld) { var all = new List<ITransport>(waiters); waiters.Clear(); foreach (var w in all) Handle(w, new byte[] { MsgRequest }); }
            if (requestedAt > 0f && !Preloading && !Receiving && Time.realtimeSinceStartup - requestedAt > 10f)   // while following the leader in, the follow asks by itself; never while a world is coming in
            {
                if (retries < 5 && requestTarget != null) { retries++; CoopBehaviour.Log("world: no answer yet, asking again (" + retries + ")"); Request(requestTarget); }
                else { requestedAt = -1f; Status = "peer never answered the world request"; }
            }
            if (pending == null) return;
            if (Time.realtimeSinceStartup - pendingSince > 60f) { pending = null; Status = "world snapshot dropped (BUD never became active)"; CoopBehaviour.Log("world: pending snapshot dropped after 60 s"); return; }
            if (inWorld) { incomingEpoch = pendingEpoch; Apply(pending); }
        }

        // Send my current world to the peer, who loads it (the opposite direction of Request).
        public static void Push(ITransport net)
        {
            try { SendSnapshot(net); }
            catch (Exception e) { Status = "push error: " + e.GetType().Name; CoopBehaviour.Log("world: push error " + e); }
        }

        public static void Request(ITransport net)
        {
            if (requestedAt < 0f) retries = 0;
            requestedAt = Time.realtimeSinceStartup; requestTarget = net;
            net.SendReliable(new byte[] { MsgRequest });
            Status = "asked peer for its world...";
            CoopBehaviour.Log("world: requested snapshot from peer");
        }

        public static void Handle(ITransport net, byte[] msg)
        {
            if (msg == null || msg.Length == 0) return;
            try
            {
                if (msg[0] == MsgRequest) SendSnapshot(net);
                else if (msg[0] == MsgSnapshot) { incomingEpoch = 0; Apply(msg); }   // an older sender: no world number, so it never counts as the leader's world
                else if (msg[0] == WorldTransfer.MsgPart)
                {
                    WorldTransfer.Assembly a;
                    if (!incoming.TryGetValue(net, out a)) { a = new WorldTransfer.Assembly(); incoming[net] = a; }
                    byte[] world;
                    try { world = a.Add(msg, Time.realtimeSinceStartup); }
                    catch (FormatException e) { incoming.Remove(net); Status = "the world that came in was damaged"; CoopBehaviour.Log("world: a damaged world part was refused (" + e.Message + ")"); return; }
                    if (world == null) { Status = "getting the leader's world " + a.Have + "/" + a.Parts; return; }
                    incoming.Remove(net);
                    CoopBehaviour.Log("world: all " + a.Parts + " parts of the world arrived (" + world.Length + " bytes)");
                    var whole = new byte[world.Length + 1];
                    whole[0] = MsgSnapshot;
                    Buffer.BlockCopy(world, 0, whole, 1, world.Length);
                    incomingEpoch = a.Epoch;
                    Apply(whole);
                }
            }
            catch (Exception e)
            {
                Status = "world sync error: " + e.GetType().Name;
                CoopBehaviour.Log("world: error " + e);
            }
        }

        static void SendSnapshot(ITransport net)
        {
            byte[] data;
            bool ok = true;
            string own = Path.Combine(SaveSystem.m_levelFilesPath ?? "", Application.loadedLevelName + ".sav");
            if (InWorld())
            {
                string dir = Path.Combine(SaveGuard.Root, "snapshot-out");
                Directory.CreateDirectory(dir);
                ok = SaveSystem.SaveLevel(dir, "snapshot.sav");
                data = File.ReadAllBytes(Path.Combine(dir, "snapshot.sav"));
            }
            else if (!GuestMode && !InGameOrLoading() && Singleton<StartScreen>.Exists && File.Exists(own))
            {
                // still on the start screen (or fading into the game): my save on disk is exactly the world the game is about to load
                data = File.ReadAllBytes(own);
                CoopBehaviour.Log("world: answering from the start screen with my saved world");
            }
            else { if (!waiters.Contains(net)) waiters.Add(net); CoopBehaviour.Log("world: a player asked for my world but I am not in the world yet; will answer when I am"); return; }
            SendWorld(net, data);
            Status = "sent my world (" + data.Length + " bytes)";
            CoopBehaviour.Log("world: sent snapshot, SaveLevel ok=" + ok + ", " + data.Length + " bytes");
        }

        // in packed parts: one Steam message holds about 1 MB, a grown world is more
        public static int SendWorld(ITransport net, byte[] data)
        {
            var parts = WorldTransfer.Split(data, ++transferSeq, MyEpoch);
            int bytes = 0;
            foreach (var p in parts) { net.SendReliable(p); bytes += p.Length; }
            CoopBehaviour.Log("world: sent as " + parts.Count + " parts, " + bytes + " bytes packed");
            return parts.Count;
        }

        // The snapshot contains the sender's own BUD; remember ours and put it back so a pull never moves the local player.
        class Pose { public Transform t; public Vector3 pos, scale; public Quaternion rot; public Rigidbody rb; public Vector3 vel, angVel; }

        static System.Collections.Generic.List<Pose> CaptureLocalPlayer()
        {
            var list = new System.Collections.Generic.List<Pose>();
            var root = GameObject.Find("Walkster");
            if (root == null) return list;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var p = new Pose { t = t, pos = t.position, rot = t.rotation, scale = t.localScale, rb = t.rigidbody };
                if (p.rb != null && !p.rb.isKinematic) { p.vel = p.rb.velocity; p.angVel = p.rb.angularVelocity; }
                list.Add(p);
            }
            return list;
        }

        static void RestoreLocalPlayer(System.Collections.Generic.List<Pose> poses)
        {
            foreach (var p in poses)
            {
                if (p.t == null) continue;
                p.t.position = p.pos; p.t.rotation = p.rot; p.t.localScale = p.scale;
                if (p.rb != null && !p.rb.isKinematic) { p.rb.velocity = p.vel; p.rb.angularVelocity = p.angVel; }
            }
        }

        static void Apply(byte[] msg)
        {
            if (!GuestPathsActive()) { CoopBehaviour.Log("world: a world arrived but my saves are not in the guest folder; not loaded"); Status = "world not loaded (guest mode is off)"; return; }
            if (!InGameOrLoading() && Preloaded && Singleton<StartScreen>.Exists) { CoopBehaviour.Log("world: another copy of the leader's world arrived before the game started; the first one is used"); return; }
            if (!InGameOrLoading() && Preloading && Singleton<StartScreen>.Exists)
            {
                // still on the start screen, following the leader in: their world becomes the (guest) save the game is about to load
                var world = new byte[msg.Length - 1];
                Buffer.BlockCopy(msg, 1, world, 0, world.Length);
                if (!LooksLikeWorld(world)) { Status = "the leader's world was damaged"; return; }
                File.WriteAllBytes(Path.Combine(SaveSystem.m_levelFilesPath, Application.loadedLevelName + ".sav"), world);
                pending = null; requestedAt = -1f; requestTarget = null; retries = 0; Preloaded = true; LoadedEpoch = incomingEpoch;
                lateCopiesUntil = Time.realtimeSinceStartup + 90f;
                Status = "the leader's world is ready (" + world.Length + " bytes)";
                CoopBehaviour.Log("world: the leader's world arrived on the start screen (" + world.Length + " bytes); the game loads it as my guest save");
                return;
            }
            if (requestTarget == null && Time.realtimeSinceStartup < lateCopiesUntil)
            {
                // a copy asked for while following the leader in, arriving after the one already loaded: loading it would only reload the same world
                CoopBehaviour.Log("world: a late copy of the leader's world arrived; the one loaded at the start is kept");
                return;
            }
            if (!InWorld())
            {
                if (pending == null) CoopBehaviour.Log("world: snapshot arrived but BUD is not active yet; will apply when it is");
                pending = msg; pendingSince = Time.realtimeSinceStartup; pendingEpoch = incomingEpoch;
                Status = "world snapshot waiting for BUD...";
                return;
            }
            pending = null;
            var data = new byte[msg.Length - 1];
            Buffer.BlockCopy(msg, 1, data, 0, data.Length);
            if (!LooksLikeWorld(data)) { Status = "a damaged world was not loaded"; requestedAt = -1f; return; }
            string path = Path.Combine(SaveSystem.m_levelFilesPath, Application.loadedLevelName + ".sav");
            File.WriteAllBytes(path, data);
            CoopBehaviour.Log("world: received snapshot " + data.Length + " bytes, loading into " + Application.loadedLevelName);
            requestedAt = -1f; requestTarget = null; retries = 0;
            lateCopiesUntil = Time.realtimeSinceStartup + 30f;   // copies asked for earlier that still arrive would only load the same world again
            var keep = CaptureLocalPlayer();
            var mySkin = SceneProfile.ActiveSkin;   // the skin choice is stored inside the world data; it must stay personal
            SaveSystem.LoadLevel();
            RestoreLocalPlayer(keep);
            SceneProfile.ActiveSkin = mySkin;       // also fires the skin-change event so every SkinSwapper updates
            AutoSync.Rebaseline();
            SharedWorld.Rebaseline();
            SkinsMayBeReset = true;
            try { SaveSystem.SaveLevel(); CoopBehaviour.Log("world: saved my own state after the load"); }
            catch (Exception e) { CoopBehaviour.Log("world: save after load failed " + e.GetType().Name); }
            if (GuestMode) LoadedEpoch = incomingEpoch;
            Status = "loaded peer's world (" + data.Length + " bytes)"; CoopUI.Toast("WORLD DATA SYNCED");
            CoopBehaviour.Log("world: snapshot loaded");
        }
    }

    // Plants: when my plants change (and stop changing for 2 s), push my world to the peer. After loading a snapshot, adopt the new plant state silently so it is not echoed back.
    public static class AutoSync
    {
        public static bool Enabled;   // opt-in: create coop-autosync.txt next to the exe
        static float nextCheck, lastChange, rebaselineUntil;
        static int lastSig;
        static bool dirty, initialized;

        public static void Rebaseline() { rebaselineUntil = Time.time + 6f; dirty = false; }

        static int Signature()
        {
            int s = 17;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch)))
            {
                var b = (PlantBranch)o;
                s = s * 31 + b.m_Elements.Count;
                s = s * 31 + b.m_Branches.Count;

            }
            s = s * 31 + UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)).Length;
            return s;
        }

        public static void Tick(ITransport net, bool connected)
        {
            if (net == null) return;
            if (!Enabled) return;
            if (!connected) { initialized = false; return; }
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 0.5f;
            if (GameObject.Find("Walkster") == null) return;
            int sig = Signature();
            if (!initialized) { initialized = true; rebaselineUntil = Time.time + 15f; }   // warm-up after the link comes up: scene objects are still settling
            if (Time.time < rebaselineUntil) { lastSig = sig; dirty = false; return; }
            if (sig != lastSig) { lastSig = sig; lastChange = Time.time; dirty = true; }
            else if (dirty && Time.time - lastChange > 2f)
            {
                dirty = false;
                CoopBehaviour.Log("auto: my plants changed, pushing world to peer");
                WorldSync.Push(net);
            }
        }
    }
}
