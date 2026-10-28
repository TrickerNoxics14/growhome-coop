using System;
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
        public static bool IsSnapshot(byte[] m) { return m != null && m.Length > 0 && m[0] == MsgSnapshot; }

        // Guests play in the host's world. Point every save at a throwaway folder first so the guest's own save file is never read-modify-written.
        public static void EnterGuestMode()
        {
            if (GuestMode) return;
            GuestMode = true;
            try
            {
                string root = Path.Combine(SaveGuard.Root, "guest-session");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(Path.Combine(root, "Levels"));
                string profile = Path.Combine(SaveGuard.Root, "PlayerProfile.sav");
                if (File.Exists(profile)) File.Copy(profile, Path.Combine(root, "PlayerProfile.sav"), true);
                SaveSystem.m_playerSavePath = root;
                SaveSystem.m_levelFilesPath = Path.Combine(root, "Levels");
                CoopBehaviour.Log("guest mode: saves redirected to " + root + "; my own save is untouched");
                CoopUI.Toast("GUEST MODE: YOUR OWN SAVE IS SAFE");
            }
            catch (Exception e) { CoopBehaviour.Log("guest mode failed: " + e.GetType().Name + " " + e.Message); }
        }

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
        static ITransport waitingFor;        // a player asked for my world while I was not in the world yet; answer them once I am
        static ITransport requestTarget;     // who I last asked for their world (for retries)
        static float requestedAt = -1f; static int retries;   // my own request, waiting for the snapshot to arrive

        // Call every frame: applies a snapshot that arrived while BUD was inactive, answers a request that came too early, retries a request that got no answer.
        public static void Tick()
        {
            bool inWorld = InWorld();
            if (waitingFor != null && inWorld) { var w = waitingFor; waitingFor = null; Handle(w, new byte[] { MsgRequest }); }
            if (requestedAt > 0f && Time.time - requestedAt > 10f)
            {
                if (retries < 5 && requestTarget != null) { retries++; CoopBehaviour.Log("world: no answer yet, asking again (" + retries + ")"); Request(requestTarget); }
                else { requestedAt = -1f; Status = "peer never answered the world request"; }
            }
            if (pending == null) return;
            if (Time.time - pendingSince > 60f) { pending = null; Status = "world snapshot dropped (BUD never became active)"; CoopBehaviour.Log("world: pending snapshot dropped after 60 s"); return; }
            if (inWorld) Handle(null, pending);
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
            requestedAt = Time.time; requestTarget = net;
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
                else if (msg[0] == MsgSnapshot) Apply(msg);
            }
            catch (Exception e)
            {
                Status = "world sync error: " + e.GetType().Name;
                CoopBehaviour.Log("world: error " + e);
            }
        }

        static void SendSnapshot(ITransport net)
        {
            if (!InWorld()) { waitingFor = net; CoopBehaviour.Log("world: a player asked for my world but I am not in the world yet; will answer when I am"); return; }
            string dir = Path.Combine(SaveGuard.Root, "snapshot-out");
            Directory.CreateDirectory(dir);
            bool ok = SaveSystem.SaveLevel(dir, "snapshot.sav");
            byte[] data = File.ReadAllBytes(Path.Combine(dir, "snapshot.sav"));
            var payload = new byte[data.Length + 1];
            payload[0] = MsgSnapshot;
            Buffer.BlockCopy(data, 0, payload, 1, data.Length);
            net.SendReliable(payload);
            Status = "sent my world (" + data.Length + " bytes)";
            CoopBehaviour.Log("world: sent snapshot, SaveLevel ok=" + ok + ", " + data.Length + " bytes");
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
            if (!InWorld())
            {
                if (pending == null) CoopBehaviour.Log("world: snapshot arrived but BUD is not active yet; will apply when it is");
                pending = msg; pendingSince = Time.time;
                Status = "world snapshot waiting for BUD...";
                return;
            }
            pending = null;
            var data = new byte[msg.Length - 1];
            Buffer.BlockCopy(msg, 1, data, 0, data.Length);
            string path = Path.Combine(SaveSystem.m_levelFilesPath, Application.loadedLevelName + ".sav");
            File.WriteAllBytes(path, data);
            CoopBehaviour.Log("world: received snapshot " + data.Length + " bytes, loading into " + Application.loadedLevelName);
            requestedAt = -1f;
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
