using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Steamworks;
using UnityEngine;

namespace CoopMod
{
    // Called from the patched SteamManager.Awake; Unity is fully started by then.
    public static class Bootstrap
    {
        static bool started;
        public static void Init()
        {
            if (started) return;
            started = true;
            var go = new GameObject("CoopMod");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<CoopBehaviour>();
        }
    }

    // Co-op for up to MaxPeers other players. Every player connects to every other player (full mesh) and shows a copy of each of them.
    // The player with the lowest SteamID64 is the "host": the others copy that player's world when they join.
    public class CoopBehaviour : MonoBehaviour
    {
        const float SendInterval = 0.05f; // 20 Hz
        const int MaxPeers = 7;
        const byte MsgVisibility = MessageGate.MsgVisibility;

        // network
        SteamHub hub; UdpLanHub lanHub; BroadcastTransport broadcast;
        readonly List<Peer> peers = new List<Peer>();
        readonly List<string> peerSpecs = new List<string>();
        ulong myId; int myNumber;
        float nextSteamTry, nextSend; uint sendSeq;
        bool pendJumpDown, pendJumpUp;
        Peer testPeer;                                  // F9 keyboard-driven test copy when there is no network

        // misc
        float lastHitchLog;
        static string logName = "coopmod.log";

        // Steam starts the game with its working directory set to the original install, so anchor files to the running exe's folder.
        static string Here(string file) { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, file); }
        public static void Log(string m) { File.AppendAllText(Here(logName), m + "\n"); }

        float nextUpdateNote;
        // The launcher leaves this file when it has downloaded a newer mod in the background. Returns the message to show (and removes the file), or null.
        public static string TakeUpdateNotice(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string v = File.ReadAllText(path).Trim();
                File.Delete(path);
                int n; if (!int.TryParse(v, out n) || n <= 0 || n > 100000) return null;
                return "MOD UPDATE " + n + " DOWNLOADED: RESTART THE GAME TO USE IT";
            }
            catch (Exception) { return null; }
        }

        static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        bool Online { get { return hub != null; } }
        bool AnyConnected { get { foreach (var p in peers) if (p.Connected) return true; return false; } }

        // Who am I playing with: coop-peers.txt (one SteamID64 per line, or "self"), else the old coop-peer.txt, else -coop-steam <id|self>.
        void LoadPeerSpecs()
        {
            try
            {
                foreach (string name in new[] { "coop-peers.txt", "coop-peer.txt" })
                {
                    string f = Here(name);
                    if (!File.Exists(f)) continue;
                    foreach (string line in File.ReadAllLines(f))
                    {
                        string v = line.Trim();
                        if (v.Length > 0 && !v.StartsWith("#")) peerSpecs.Add(v);
                    }
                    if (peerSpecs.Count > 0) return;
                }
            }
            catch (Exception e) { Log("peer list: " + e.GetType().Name + " " + e.Message); }
            string a = Arg("-coop-steam");
            if (a != null && !System.Text.RegularExpressions.Regex.IsMatch(a, "^[0-9]{17}$")) a = null;   // only a real SteamID64; the old "self" test value is ignored (use coop-peers.txt for that)
            if (a != null) peerSpecs.Add(a);
        }

        void Awake()
        {
            Application.runInBackground = true; // test windows must keep updating while unfocused
            SaveGuard.Redirect(Here("coop-saves")); // never touch the real save folder or Steam Cloud
            AutoSync.Enabled = File.Exists(Here("coop-autosync.txt"));
            SharedWorld.Enabled = !File.Exists(Here("coop-noshare.txt"));
            CoopUI.Hidden = File.Exists(Here("coop-ui-hidden.txt"));
            LoadPeerSpecs();
            try { string bf = Here("coop-bots.txt"); if (File.Exists(bf)) botsWanted = Math.Min(MaxPeers, int.Parse(File.ReadAllText(bf).Trim())); } catch (Exception) { }
            logName = "coopmod-steam.log";
            try { string lp = Here(logName); if (File.Exists(lp) && new FileInfo(lp).Length > 3 * 1024 * 1024) { string prev = Here("coopmod-steam.prev.log"); if (File.Exists(prev)) File.Delete(prev); File.Move(lp, prev); } } catch (Exception) { }   // the log never grows past a few MB
            StartScreen.m_skipIntros = !File.Exists(Here("coop-intro.txt"));   // straight to the start menu: the logo screens only cost time on every start (put coop-intro.txt next to the exe to see them)
            Log("CoopMod loaded; Unity " + Application.unityVersion + "; players to connect to: " + peerSpecs.Count);
            if (File.Exists(Here("coop-selftest.txt"))) { Log("self test requested (coop-selftest.txt)"); gameObject.AddComponent<SelfTest>(); }
        }

        void OnDestroy()
        {
            if (hub != null) hub.Close();
            if (lanHub != null) lanHub.Close();
        }

        // The game pauses itself whenever its window loses focus. In co-op that only covers the screen with the pause menu, so resume right away.
        bool hasFocus = true; float focusLostAt;
        void OnApplicationFocus(bool focus) { hasFocus = focus; if (!focus) focusLostAt = Time.realtimeSinceStartup; }

        void ResumeAfterFocusLoss()
        {
            if (hasFocus || !Singleton<PauseManager>.Exists || !PauseManager.Paused) return;
            if (Time.realtimeSinceStartup - focusLostAt < 0.3f) return;
            PauseManager.ResumeRequested = true;
        }

        void OnLevelWasLoaded(int level)
        {
            Log("Level loaded: " + level + " " + Application.loadedLevelName);
            foreach (var p in peers) p.ClearClone();
            if (testPeer != null) testPeer.ClearClone();
            skinRestored = false; worldSince = -1f;
        }

        void Update()
        {
            Perf.NewFrame();
            float frameDt = Time.unscaledDeltaTime;
            if (frameDt > 0.08f && Online && Time.time > 10f && Time.time - lastHitchLog > 1f)
            {
                lastHitchLog = Time.time;
                Log("hitch: frame took " + (frameDt * 1000f).ToString("F0") + " ms; my mod used " + Perf.LastFrameMs.ToString("F1") + " ms in the frame before (heaviest: " + Perf.LastHeavy + " " + Perf.LastHeavyMs.ToString("F1") + " ms)");
            }
            SaveGuard.Tick();
            if (Online && AnyConnected) ResumeAfterFocusLoss();
            if (Input.GetKeyDown(KeyCode.F7) && Online) PullWorld();
            if (Input.GetKeyDown(KeyCode.F6) && Online) PushWorld();
            if (Input.GetKeyDown(KeyCode.F5)) Census.Run();
            if (Input.GetKeyDown(KeyCode.F8)) DumpPlayer();
            if (Input.GetKeyDown(KeyCode.F9) && !Online) ToggleTestPeer();
            if (Input.GetKeyDown(KeyCode.F1)) ToggleUi();
            if (Online && Input.GetKeyDown(KeyCode.F2)) MakeInviteCode();
            if (Online && botsWanted > 0 && Time.time >= nextBotAdd && WorldSync.InWorld()) { nextBotAdd = Time.time + 2f; botsWanted--; AddBot(); }
            if (Online && Input.GetKeyDown(KeyCode.F10)) AddBot();
            if (Online && Input.GetKeyDown(KeyCode.F11)) RemoveBot();
            if (Online && Input.GetKeyDown(KeyCode.F3)) OpenJoinBox();
            if (!Online && Time.time >= nextSteamTry) TryStartSteam();
            if (Online) NetTick();
            if (Time.realtimeSinceStartup >= nextUpdateNote) { nextUpdateNote = Time.realtimeSinceStartup + 10f; string note = TakeUpdateNotice(Here("coop-update-ready.txt")); if (note != null) { CoopUI.Toast(note); Log("update notice shown: " + note); } }
        }

        // ---- who is the host, F7 -------------------------------------------------------------------------------------------------------

        // The lowest SteamID64 among everyone is the host. Returns null when I am the host.
        Peer HostPeer()
        {
            // In a lobby the host is whoever made the code (F2). Without a lobby (old Steam-ID lists) it is the lowest SteamID.
            if (lobby != null && lobby.InLobby)
            {
                ulong owner = lobby.OwnerId();
                if (owner == myId) return null;
                foreach (var p in peers) if (!p.IsBot && p.Id == owner) return p;
            }
            Peer best = null;
            foreach (var p in peers) if (!p.IsBot && p.Id < myId && (best == null || p.Id < best.Id)) best = p;
            return best;
        }

        // F7: a guest asks the host for the host's world again. The host's world is the main world, so the host never pulls.
        void PullWorld()
        {
            var host = HostPeer();
            if (host == null) { CoopUI.Toast("YOU ARE THE HOST: THIS IS THE MAIN WORLD"); return; }
            WorldSync.EnterGuestMode();
            WorldSync.Request(host.Link);
        }

        // F6: only the host may send a world, and only to the guests.
        void PushWorld()
        {
            if (HostPeer() != null) { CoopUI.Toast("ONLY THE HOST CAN SEND THE WORLD"); return; }
            WorldSync.Push(broadcast);
        }

        // A guest copies the host's world once, a few seconds after the link comes up and the guest is in the world.
        void AutoPull()
        {
            var host = HostPeer();
            if (host == null || host.AutoPulled || !host.Connected || Time.time - host.ConnectedAt < 3f) return;
            if (!WorldSync.InWorld()) return;
            host.AutoPulled = true;
            Log("auto: " + host.Label + " is the host, joining their world as a guest");
            WorldSync.EnterGuestMode();
            WorldSync.Request(host.Link);
        }

        // ---- bring-up ------------------------------------------------------------------------------------------------------------------

        readonly List<PeerLink> allLinks = new List<PeerLink>();
        Lobby lobby;

        void TryStartSteam()
        {
            nextSteamTry = Time.time + 2f;
            try
            {
                if (!SteamUser.GetSteamID().IsValid()) return;
                myId = SteamUser.GetSteamID().m_SteamID;
                var ids = new List<ulong>();
                foreach (string spec in peerSpecs)
                {
                    bool self = spec.Trim().ToLower() == "self";
                    ulong id = self ? myId : ulong.Parse(spec.Trim());
                    if (!self && id == myId) continue;                    // my own id in the list: ignore
                    if (ids.Contains(id) || ids.Count >= MaxPeers) continue;
                    ids.Add(id);
                }
                SharedWorld.MyId = myId;
                hub = new SteamHub(new List<ulong>());                    // peers are added below / by invite codes
                broadcast = new BroadcastTransport(allLinks);
                foreach (ulong id in ids) AddPeerInternal(id, id == myId);
                RenumberPeers();
                SetupLan();

                lobby = new Lobby { OnMember = AddMember };
                hub.AcceptUnknown = id => { if (lobby != null && lobby.IsMember(id)) { AddMember(id); return true; } return false; };
                Log("steam up; my SteamID64=" + myId + " (I am Player " + myNumber + "); other players: " + peers.Count + "; host is " + (HostPeer() == null ? "me" : HostPeer().Label));
            }
            catch (Exception e) { Log("steam init not ready: " + e.GetType().Name + " " + e.Message); }
        }

        void AddPeerInternal(ulong id, bool selfEcho)
        {
            var link = hub.AddPeer(id);
            var p = new Peer { Id = id, Link = link };
            if (selfEcho) p.SelfOffset = new Vector3(3f, 0f, 0f);
            peers.Add(p); allLinks.Add(link);
        }

        // F10 / F11: add or remove a scripted test player (runs in a circle around where it was added, jumps, own skin).
        int botSerial, botsWanted; float nextBotAdd;   // botsWanted comes from coop-bots.txt (a number): bots are added one by one once the player is in the world
        static readonly SceneProfile.CharacterSkin[] BotSkins = {
            SceneProfile.CharacterSkin.Desert, SceneProfile.CharacterSkin.Ninja, SceneProfile.CharacterSkin.Native,
            SceneProfile.CharacterSkin.WhyAye, SceneProfile.CharacterSkin.Facebook, SceneProfile.CharacterSkin.Oceanic, SceneProfile.CharacterSkin.Standard };

        void AddBot()
        {
            if (hub == null || peers.Count >= MaxPeers) return;
            var root = GameObject.Find("Walkster");
            var walker = root != null ? root.GetComponentInChildren<BipedWalkScript>() : null;
            if (walker == null) { Log("bot: not in the world yet"); return; }
            int idx = ++botSerial;
            // a skin no other bot and not I am wearing
            byte skin = (byte)BotSkins[0];
            foreach (var sk in BotSkins)
            {
                bool used = sk == SceneProfile.ActiveSkin;
                foreach (var q in peers) if (q.IsBot && q.BotSkin == (byte)sk) used = true;
                if (!used) { skin = (byte)sk; break; }
            }
            float ang = idx * 2.1f;
            Vector3 center = walker.body.position + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 3f;
            var link = new BotLink(idx, skin, center, 0.9f);
            var p = new Peer { Id = 9000000000000000000UL + (ulong)idx, Link = link, IsBot = true, BotIndex = idx, BotSkin = skin };
            peers.Add(p);
            RenumberPeers();
            Log("bot " + idx + " added with skin " + (SceneProfile.CharacterSkin)skin + " (" + peers.Count + " other players)");
        }

        void RemoveBot()
        {
            for (int i = peers.Count - 1; i >= 0; i--)
            {
                if (!peers[i].IsBot) continue;
                if (peers[i].Clone != null) Destroy(peers[i].Clone);
                Log("bot " + peers[i].BotIndex + " removed");
                peers.RemoveAt(i);
                RenumberPeers();
                return;
            }
        }

        // A player showed up (typed my code, or I typed theirs): connect to them.
        void AddMember(ulong id)
        {
            if (hub == null || id == 0 || id == myId) return;
            foreach (var p in peers) if (p.Id == id) return;
            if (peers.Count >= MaxPeers) return;
            AddPeerInternal(id, false);
            RenumberPeers();
            Log("player added: " + id + " (now " + peers.Count + " other players; I am Player " + myNumber + ")");
        }

        // Same numbering on every PC: rank of the SteamID among all players.
        void RenumberPeers()
        {
            var all = new List<ulong> { myId };
            foreach (var p in peers) if (!all.Contains(p.Id)) all.Add(p.Id);
            all.Sort();
            myNumber = all.IndexOf(myId) + 1;
            foreach (var p in peers) p.Number = all.IndexOf(p.Id) + 1;
            peers.Sort((a, b) => a.Number.CompareTo(b.Number));
        }

        // coop-lan.txt: lines "<steamid64> <ip>:<port>" (or just "<ip>:<port>" for the only other player). Optional fast path for PCs on one network.
        void SetupLan()
        {
            try
            {
                string f = Here("coop-lan.txt");
                if (!File.Exists(f)) return;
                foreach (string raw in File.ReadAllLines(f))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    string addr = parts[parts.Length - 1];
                    ulong id = parts.Length > 1 ? ulong.Parse(parts[0]) : (peers.Count == 1 ? peers[0].Id : 0UL);
                    string[] ap = addr.Split(':');
                    if (id == 0 || !hub.Links.ContainsKey(id)) continue;
                    if (lanHub == null) lanHub = new UdpLanHub(int.Parse(ap[1]));
                    hub.Links[id].SetLan(lanHub, IPAddress.Parse(ap[0]));
                    Log("lan fast path on for " + id + ": " + addr);
                }
            }
            catch (Exception e) { Log("lan fast path failed: " + e.GetType().Name + " " + e.Message); }
        }

        // ---- per frame ------------------------------------------------------------------------------------------------------------------

        void NetTick()
        {
            hub.Pump();
            if (lanHub != null) lanHub.Pump();
            if (peers.Count == 0) { SkinPersistTick(); return; }
            Perf.Begin(); WorldSync.Tick(); Perf.End("WorldSync.Tick");
            Perf.Begin(); SkinPersistTick(); Perf.End("SkinPersistTick");
            if (Input.GetKeyDown(KeyCode.F4)) BringNextRemote();

            foreach (var p in peers)
            {
                Perf.Begin(); ApplyVisibility(p); Perf.End("ApplyVisibility");
                Perf.Begin(); CloneDiag(p); Perf.End("CloneDiag");
                Perf.Begin(); SkinTick(p); Perf.End("SkinTick");
                SyncLog(p);
                if (p.WasConnected && Time.time - p.LastPacketTime > 6f) { p.WasConnected = false; if (!p.IsBot) CoopUI.Toast("SIGNAL LOST: " + p.Label); }
                if (Time.time >= p.NextPing)
                {
                    p.NextPing = Time.time + 1f;
                    var pg = new byte[5]; pg[0] = 0xC1;
                    Buffer.BlockCopy(BitConverter.GetBytes(Time.realtimeSinceStartup), 0, pg, 1, 4);
                    p.Link.Send(pg);
                }
                byte[] rel;
                while ((rel = p.Link.ReceiveReliable()) != null)
                {
                    switch (MessageGate.Classify(rel, HostPeer() == p))
                    {
                        case Route.Visibility: ReceiveVisibility(p, rel); break;
                        case Route.Shared: SharedWorld.Handle(rel); break;
                        case Route.Oversized: Log("ignored an oversized message from " + p.Label); break;
                        case Route.SnapshotNotFromHost: Log("ignored world data from " + p.Label + " (not the host)"); break;
                        case Route.World: if (WorldSync.IsSnapshot(rel)) WorldSync.EnterGuestMode(); WorldSync.Handle(p.Link, rel); break;
                    }
                }
            }
            Perf.Begin(); AutoSync.Tick(broadcast, AnyConnected); Perf.End("AutoSync.Tick");
            Perf.Begin(); SharedWorld.Tick(broadcast); Perf.End("SharedWorld.Tick");
            Perf.Begin(); AutoPull(); Perf.End("AutoPull");

            Perf.Begin();
            var root = GameObject.Find("Walkster");
            var walker = root != null ? root.GetComponentInChildren<BipedWalkScript>() : null;
            var handler = root != null ? root.GetComponentInChildren<ExploreInputHandler>() : null;
            var stick = root != null ? root.GetComponentInChildren<TwoAxisXInput>() : null;
            var hands = root != null ? root.GetComponentInChildren<BipedHandsScript>() : null;
            GrabLog(hands, "local", lastGrab);
            Perf.End("find player + grab log");
            Perf.Begin(); SendVisibility(root); Perf.End("SendVisibility");

            // my state -> everybody
            if (walker != null && handler != null)
            {
                bool jd = handler.m_JumpInput.GetButtonDown("Jump");
                if (jd) Log("local jump pressed: walker.jumpEnabled=" + walker.jumpEnabled + " scene.JumpEnabled=" + SceneProfile.JumpEnabled + " offGround=" + walker.m_OffGroundTime.ToString("F2") + " inAir=" + walker.m_inAir);
                pendJumpDown |= jd;
                pendJumpUp |= handler.m_JumpInput.GetButtonUp("Jump");
                bool menuOpen = Time.timeScale < 0.01f || (Singleton<PauseManager>.Exists && PauseManager.Paused);
                if (Time.time >= nextSend)
                {
                    nextSend = Time.time + SendInterval;
                    var s = new PlayerState
                    {
                        seq = ++sendSeq,
                        pos = walker.body.position,
                        vel = walker.body.velocity,
                        camForward = Camera.main != null ? Camera.main.transform.forward : Vector3.forward,
                        move = (stick != null && !menuOpen) ? stick.Value : Vector2.zero,
                        skin = (byte)SceneProfile.ActiveSkin,
                        jumpScale = walker.m_JumpScale,
                        held = menuOpen ? new PlayerState.Held[0] : ObjectSync.Collect(hands),
                        jumpDown = pendJumpDown && !menuOpen,
                        jumpUp = pendJumpUp && !menuOpen,
                        leftGrab = hands != null && !menuOpen && hands.hand[0].grabbing,
                        rightGrab = hands != null && !menuOpen && hands.hand[1].grabbing,
                    };
                    pendJumpDown = pendJumpUp = false;
                    broadcast.Send(s.Write());
                }
            }

            // everybody -> their copies
            foreach (var p in peers) ReceiveState(p, root);
        }

        void ReceiveState(Peer p, GameObject root)
        {
            byte[] data;
            while ((data = p.Link.Receive()) != null)
            {
                if (data.Length == 5 && data[0] == 0xC1) { data[0] = 0xC2; p.Link.Send(data); continue; }
                if (data.Length == 5 && data[0] == 0xC2)
                {
                    float rtt = (Time.realtimeSinceStartup - BitConverter.ToSingle(data, 1)) * 1000f;
                    p.RttSum += rtt; p.RttN++; if (rtt > p.RttMax) p.RttMax = rtt;
                    continue;
                }
                var s = PlayerState.Read(data);
                if (s == null || s.seq <= p.LastRecvSeq) continue;
                if (s.pos.y < -30f) continue;   // that player is falling out of the world: do not chase them
                p.LastRecvSeq = s.seq;
                if (Time.time - p.LastPacketTime > 10f) { p.ConnectedAt = Time.time; p.AutoPulled = false; Log(p.Label + " connected"); if (!p.IsBot) CoopUI.Toast("SIGNAL ACQUIRED: " + p.Label + " ONLINE"); }
                p.WasConnected = true;
                float gap = Time.time - p.LastPacketTime; if (gap < 5f) { p.GapMax = Mathf.Max(p.GapMax, gap); p.GapSum += gap; p.GapN++; }
                p.LastPacketTime = Time.time;
                if (root == null) continue;
                if (p.Clone == null)
                {
                    SpawnClone(p, s.pos + p.SelfOffset);
                    if (p.Driver == null) continue;
                }
                var d = p.Driver;
                p.RemoteSkin = s.skin;
                ObjectSync.SetRemoteHeld(p.Id, s.held);
                d.hasNet = true; d.netPos = s.pos + p.SelfOffset; d.netVel = s.vel; d.netTime = Time.time;
                d.input.move = s.move; d.input.camForward = s.camForward; d.input.jumpScale = s.jumpScale;
                d.input.leftGrab = s.leftGrab; d.input.rightGrab = s.rightGrab;
                d.input.jumpDown |= s.jumpDown; d.input.jumpUp |= s.jumpUp;
            }
        }

        void SyncLog(Peer p)
        {
            if (Time.time < p.NextSyncLog || p.Driver == null || p.GapN <= 0) return;
            p.NextSyncLog = Time.time + 3f;
            if (p.Driver.errN > 0 && (p.Driver.errMax > 0.8f || p.GapMax > 0.2f))
                Log("sync " + p.Label + ": packets avg gap " + (p.GapSum / p.GapN * 1000f).ToString("F0") + " ms, max " + (p.GapMax * 1000f).ToString("F0") + " ms; err avg " + (p.Driver.errSum / p.Driver.errN).ToString("F2") + " max " + p.Driver.errMax.ToString("F2") + " m; fps " + (1f / Mathf.Max(0.0001f, Time.deltaTime)).ToString("F0") + "; rtt avg " + (p.RttN > 0 ? (p.RttSum / p.RttN).ToString("F0") : "?") + " max " + p.RttMax.ToString("F0") + " ms; " + p.Link.Diagnostics());
            p.RttSum = 0f; p.RttMax = 0f; p.RttN = 0;
            p.GapMax = 0f; p.GapSum = 0f; p.GapN = 0; p.Driver.errSum = 0f; p.Driver.errN = 0; p.Driver.errMax = 0f;
        }

        // In co-op a pause menu must not freeze the world: PauseManager sets timeScale 0, we put it back after the game's Update ran.
        void FixedUpdate() { if (Online) ObjectSync.FixedTick(); }

        string[] lastGrab = new string[2];
        void GrabLog(BipedHandsScript hands, string who, string[] last)
        {
            if (hands == null) return;
            for (int i = 0; i < 2 && i < hands.hand.Length; i++)
            {
                var h = hands.hand[i];
                string now = (h.grabbing && h.grabbedObject != null) ? ObjectSync.Describe(h.grabbedObject) : (h.grabbing ? "(grab pressed, nothing held)" : "");
                if (now != last[i]) { last[i] = now; if (now.Length > 0) Log("grab " + who + (i == 0 ? " L: " : " R: ") + now); }
            }
        }

        InputMethodTracker trackerWeDisabled;

        void LateUpdate()
        {
            bool menuOpen = Online && AnyConnected && Singleton<PauseManager>.Exists && PauseManager.Paused;
            if (menuOpen)
            {
                if (Time.timeScale < 0.5f) Time.timeScale = 1f;
                // InputMethodTracker re-locks and hides the mouse every frame while timeScale > 0, which breaks the menu's mouse
                // handling. Switch it off while the menu is open and manage the cursor ourselves.
                if (trackerWeDisabled == null && Singleton<InputMethodTracker>.Exists)
                {
                    var t = Singleton<InputMethodTracker>.Instance;
                    if (t != null && t.enabled) { t.enabled = false; trackerWeDisabled = t; }
                }
                Screen.lockCursor = false;
                Screen.showCursor = true;
            }
            else if (trackerWeDisabled != null)
            {
                trackerWeDisabled.enabled = true;   // game takes the cursor back on resume
                trackerWeDisabled = null;
            }
        }

        // ---- my own skin ---------------------------------------------------------------------------------------------------------------

        // Each PC remembers its own skin in coop-skin.txt: the game only saves it on a clean quit, and a loaded world must never change it.
        float nextSkinPersist, worldSince = -1f; bool skinRestored; int lastSeenSkin = -1;
        void SkinPersistTick()
        {
            if (Time.time < nextSkinPersist) return;
            nextSkinPersist = Time.time + 0.5f;
            if (GameObject.Find("Walkster") == null) { worldSince = -1f; return; }
            if (worldSince < 0f) worldSince = Time.time;
            string f = Here("coop-skin.txt");
            try
            {
                if (!skinRestored)
                {
                    if (Time.time - worldSince < 3f) return;   // let the game finish loading its own save first
                    skinRestored = true;
                    if (File.Exists(f))
                    {
                        string v = File.ReadAllText(f).Trim();
                        if (Enum.IsDefined(typeof(SceneProfile.CharacterSkin), v))
                        {
                            var want = (SceneProfile.CharacterSkin)Enum.Parse(typeof(SceneProfile.CharacterSkin), v);
                            if (SceneProfile.ActiveSkin != want && SceneProfile.HasSkin(want)) { SceneProfile.ActiveSkin = want; Log("skin: restored my saved skin " + want); }
                        }
                    }
                    lastSeenSkin = (int)SceneProfile.ActiveSkin;
                    return;
                }
                int cur = (int)SceneProfile.ActiveSkin;
                if (cur != lastSeenSkin) { lastSeenSkin = cur; File.WriteAllText(f, SceneProfile.ActiveSkin.ToString()); Log("skin: saved my choice " + SceneProfile.ActiveSkin); }
            }
            catch (Exception e) { Log("skin persist: " + e.GetType().Name + " " + e.Message); }
        }

        // ---- other players' skins ------------------------------------------------------------------------------------------------------

        static string BodyMesh(GameObject g)
        {
            if (g == null) return "none";
            foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                if (mf.name == "Body" && mf.sharedMesh != null) return mf.sharedMesh.name;
            return "?";
        }

        void SkinDiag(Peer p)
        {
            string st = "active=" + SceneProfile.ActiveSkin + " localBodyMesh=" + BodyMesh(GameObject.Find("Walkster")) + " " + p.Label + "=" + (p.RemoteSkin >= 0 ? ((SceneProfile.CharacterSkin)p.RemoteSkin).ToString() : "?") + " cloneBodyMesh=" + BodyMesh(p.Clone);
            if (st != p.LastSkinState) { p.LastSkinState = st; Log("skins: " + st); }
        }

        // The copy starts as my own BUD, so give it that player's skin. SkinSwapper re-skins every copy to MY skin on skin-change and after loads, so re-assert.
        void SkinTick(Peer p)
        {
            if (Time.time >= p.NextSkinDiag) { p.NextSkinDiag = Time.time + 2f; SkinDiag(p); }
            if (p.Clone == null || p.RemoteSkin < 0 || Time.time < p.NextSkinCheck) return;
            p.NextSkinCheck = Time.time + 2f;
            bool need = p.LastAppliedSkin != p.RemoteSkin || (int)SceneProfile.ActiveSkin != p.AppliedForLocalSkin || WorldSync.SkinsMayBeReset;
            if (!need) return;
            var sws = p.Clone.GetComponentsInChildren<SkinSwapper>(true);
            var sw = sws.Length > 0 ? sws[0] : null;
            if (sw == null) return;
            foreach (var info in sw.m_skins)
            {
                if ((int)info.m_skin != p.RemoteSkin || info.m_skinMesh == null) continue;
                var m = typeof(SkinSwapper).GetMethod("ChangeToSkin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                m.Invoke(sw, new object[] { info });
                p.LastAppliedSkin = p.RemoteSkin; p.LastSkinApply = Time.time;
                p.AppliedForLocalSkin = (int)SceneProfile.ActiveSkin;
                Log("skin: applied " + p.Label + "'s skin " + info.m_skin);
                break;
            }
            // clear the "world just loaded" flag once every copy had its chance
            bool allDone = true;
            foreach (var q in peers) if (q.Clone != null && q.AppliedForLocalSkin != (int)SceneProfile.ActiveSkin) allDone = false;
            if (allDone) WorldSync.SkinsMayBeReset = false;
        }

        // F4: put the next other player's BUD 2 m in front of me (rescue if it ended up somewhere odd).
        int bringIndex;
        void BringNextRemote()
        {
            var withClone = new List<Peer>();
            foreach (var p in peers) if (p.Clone != null) withClone.Add(p);
            if (withClone.Count == 0) return;
            var target = withClone[bringIndex++ % withClone.Count];
            var a = GameObject.Find("Walkster");
            var w1 = a != null ? a.GetComponentInChildren<BipedWalkScript>() : null;
            var w2 = target.Clone.GetComponentInChildren<BipedWalkScript>();
            if (w1 == null || w2 == null || Camera.main == null) return;
            Vector3 f = Camera.main.transform.forward; f.y = 0f; f.Normalize();
            Vector3 dest = w1.body.position + f * 2f + Vector3.up * 0.5f;
            target.Clone.transform.position += dest - w2.body.position;
            Log("clone: moved " + target.Label + " next to me by F4");
        }

        // ---- other players' visible parts ----------------------------------------------------------------------------------------------

        // Parts of the BUD that differ per player (goggles, backpack, jetpack rockets, intro hiding...) are mirrored by name: each side lists
        // which parts (identified by their place in the hierarchy) are active/enabled, and the other side applies that to its copy.
        float nextVisSend; byte[] lastVisSent; float lastVisSentAt = -99f;

        static string RelPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Add(t.name + "[" + t.GetSiblingIndex() + "]");
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        void SendVisibility(GameObject root)
        {
            if (root == null || Time.time < nextVisSend) return;
            nextVisSend = Time.time + 1f;
            var entries = new List<KeyValuePair<int, bool>>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root.transform) entries.Add(new KeyValuePair<int, bool>(ObjectSync.Hash("T" + RelPath(t, root.transform)), t.gameObject.activeSelf));
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                entries.Add(new KeyValuePair<int, bool>(ObjectSync.Hash("R" + RelPath(r.transform, root.transform)), r.enabled));
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(MsgVisibility); w.Write((ushort)entries.Count);
            foreach (var kv in entries) { w.Write(kv.Key); w.Write(kv.Value); }
            var msg = ms.ToArray();
            bool same = lastVisSent != null && lastVisSent.Length == msg.Length;
            if (same) for (int i = 0; i < msg.Length; i++) if (msg[i] != lastVisSent[i]) { same = false; break; }
            if (same && Time.time - lastVisSentAt < 5f) return;   // unchanged: resend only now and then, in case someone joined later
            lastVisSent = msg; lastVisSentAt = Time.time;
            broadcast.SendReliable(msg);
        }

        void ReceiveVisibility(Peer p, byte[] msg)
        {
            try
            {
                var r = new BinaryReader(new MemoryStream(msg)); r.ReadByte();
                int n = r.ReadUInt16();
                var map = new Dictionary<int, bool>();
                for (int i = 0; i < n; i++) { int k = r.ReadInt32(); map[k] = r.ReadBoolean(); }
                p.VisMap = map;
            }
            catch (Exception e) { Log("visibility: bad message " + e.GetType().Name); }
        }

        void ApplyVisibility(Peer p)
        {
            if (p.Clone == null || p.VisMap.Count == 0 || Time.time < p.NextVisApply) return;
            p.NextVisApply = Time.time + 0.25f;
            if (Time.time > p.NextVisBuild || p.CloneTsMap.Count == 0)
            {
                p.NextVisBuild = Time.time + 2f;
                p.CloneTsMap.Clear(); p.CloneRsMap.Clear();
                foreach (var t in p.Clone.GetComponentsInChildren<Transform>(true)) if (t != p.Clone.transform) p.CloneTsMap[ObjectSync.Hash("T" + RelPath(t, p.Clone.transform))] = t;
                foreach (var m in p.Clone.GetComponentsInChildren<MeshRenderer>(true)) p.CloneRsMap[ObjectSync.Hash("R" + RelPath(m.transform, p.Clone.transform))] = m;
            }
            int hit = 0;
            foreach (var kv in p.VisMap)
            {
                Transform t; MeshRenderer m;
                if (p.CloneTsMap.TryGetValue(kv.Key, out t) && t != null) { hit++; if (t.gameObject.activeSelf != kv.Value) t.gameObject.SetActive(kv.Value); }
                else if (p.CloneRsMap.TryGetValue(kv.Key, out m) && m != null) { hit++; if (m.enabled != kv.Value) m.enabled = kv.Value; }
            }
            if (!p.VisLogged) { p.VisLogged = true; Log("visibility: mirrored " + hit + " of " + p.VisMap.Count + " listed parts onto " + p.Label); }
        }

        // ---- diagnostics and on-screen display -----------------------------------------------------------------------------------------

        void CloneDiag(Peer p)
        {
            if (p.Clone == null || Time.time < p.NextCloneDiag) return;
            p.NextCloneDiag = Time.time + 5f;
            var a = GameObject.Find("Walkster");
            var w1 = a != null ? a.GetComponentInChildren<BipedWalkScript>() : null;
            var w2 = p.Clone.GetComponentInChildren<BipedWalkScript>();
            if (w1 == null || w2 == null) { Log("clone " + p.Label + ": missing walker (local " + (w1 != null) + ", clone " + (w2 != null) + ")"); return; }
            int rend = 0, on = 0, vis = 0;
            foreach (var r in p.Clone.GetComponentsInChildren<Renderer>(true)) { rend++; if (r.enabled) on++; if (r.isVisible) vis++; }
            Log("clone " + p.Label + ": me at " + w1.body.position + " them at " + w2.body.position + " dist " + Vector3.Distance(w1.body.position, w2.body.position).ToString("F1") + " renderers=" + rend + " enabled=" + on + " onScreen=" + vis);
        }

        void DrawMarker(Peer p)
        {
            if (p.Clone == null || Camera.main == null) return;
            var w2 = p.Clone.GetComponentInChildren<BipedWalkScript>();
            var a = GameObject.Find("Walkster");
            var w1 = a != null ? a.GetComponentInChildren<BipedWalkScript>() : null;
            if (w2 == null || w1 == null) return;
            Vector3 sp = Camera.main.WorldToScreenPoint(w2.body.position + Vector3.up * 1.2f);
            if (sp.z <= 0f) return;
            float d = Vector3.Distance(w1.body.position, w2.body.position);
            CoopUI.Marker(new Vector2(sp.x, Screen.height - sp.y), p.Label.ToUpper(), d);
        }

        // ---- invite codes --------------------------------------------------------------------------------------------------------------

        bool joinOpen, codeSaved; string joinText = "", joinMsg = ""; float codePopupUntil, joinOpenedAt;

        // F1: hide or show everything the mod draws (the setting is remembered in coop-ui-hidden.txt).
        void ToggleUi()
        {
            CoopUI.Hidden = !CoopUI.Hidden;
            try { string f = Here("coop-ui-hidden.txt"); if (CoopUI.Hidden) File.WriteAllText(f, "hidden"); else if (File.Exists(f)) File.Delete(f); } catch (Exception) { }
            if (!CoopUI.Hidden) CoopUI.Toast("CO-OP DISPLAY ON   F1 TO HIDE");
        }

        static void CopyToClipboard(string s)
        {
            try { var te = new TextEditor(); te.content = new GUIContent(s); te.SelectAll(); te.Copy(); } catch (Exception) { }
        }

        // F2: make (or show again) my invite code. Friends type it with F3 and everybody in the lobby connects to everybody.
        void MakeInviteCode()
        {
            if (lobby == null) return;
            if (lobby.InLobby) { CopyToClipboard(lobby.Code); joinMsg = "code copied again: " + lobby.Code; return; }
            lobby.Create();
        }

        void SaveCodeOnce()
        {
            if (lobby == null || !lobby.InLobby || codeSaved) return;
            codeSaved = true; codePopupUntil = Time.time + 12f;
            CopyToClipboard(lobby.Code);
            try { File.WriteAllText(Here("my-invite-code.txt"), lobby.Code); } catch (Exception) { }
        }

        // F3: a small box to type a friend's code. While it is open the game ignores movement keys so typing does not move BUD.
        void OpenJoinBox()
        {
            joinOpen = !joinOpen;
            joinText = ""; joinMsg = ""; joinOpenedAt = Time.time;
            if (joinOpen) InputManager.Push(this, "ui"); else InputManager.Pop(this);
        }

        void CloseJoinBox() { if (joinOpen) { joinOpen = false; InputManager.Pop(this); } }

        void SubmitJoin()
        {
            ulong lobbyId;
            if (!InviteCode.TryDecode(joinText, out lobbyId)) { joinMsg = "That code does not look right. Letters and numbers, 13 in all."; return; }
            if (lobby != null) lobby.Join(lobbyId);
            CloseJoinBox();
        }

        void OnGUI()
        {
            if (!Online) return;
            SaveCodeOnce();
            foreach (var p in peers) DrawMarker(p);

            if (lobby != null && lobby.InLobby && Time.time < codePopupUntil && !joinOpen && !CoopUI.Hidden) CoopUI.CodePopup(lobby.Code, codePopupUntil - 12f);
            if (joinOpen)
            {
                bool ok, cancel;
                joinText = CoopUI.JoinBox(joinText, joinMsg, joinOpenedAt, out ok, out cancel);
                if (ok) SubmitJoin();
                else if (cancel) CloseJoinBox();
            }

            var chips = new List<CoopUI.Chip>();
            foreach (var p in peers) chips.Add(new CoopUI.Chip { Name = p.Label, Ok = p.Connected });
            string hint = peers.Count == 0 ? "NOBODY YET   F2 MAKE A CODE   F3 ENTER A CODE"
                                           : "F2 CODE   F3 JOIN   F7 RE-JOIN HOST WORLD   F4 FIND PLAYER";
            string code = lobby != null && lobby.InLobby ? lobby.Code : "";
            string notice = (WorldSync.Status + " " + SharedWorld.Status).Trim();
            if (code.Length == 0 && lobby != null && lobby.Status.Length > 0) notice = (lobby.Status + "  " + notice).Trim();
            var hostNow = HostPeer();
            string who = hostNow == null ? "PLAYER " + myNumber + "  HOSTING" : "PLAYER " + myNumber + "  GUEST OF " + hostNow.Label.ToUpper();
            CoopUI.StatusBar(who, chips, hint, code, notice.ToUpper());
        }

        // ---- dumps ---------------------------------------------------------------------------------------------------------------------

        static void Dump(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2).Append(t.name).Append(" [");
            foreach (var c in t.GetComponents<Component>()) if (c != null) sb.Append(c.GetType().Name).Append(' ');
            sb.Append("]\n");
            foreach (Transform ch in t) Dump(ch, depth + 1, sb);
        }

        void DumpPlayer()
        {
            var root = GameObject.Find("Walkster");
            if (root == null) { Log("F8: no Walkster in " + Application.loadedLevelName); return; }
            var sb = new StringBuilder();
            Dump(root.transform, 0, sb);
            File.WriteAllText(Here("coopmod-walkster.txt"), sb.ToString());
            Log("F8: dumped Walkster hierarchy at " + root.transform.position);
        }

        // ---- the copy of another player ------------------------------------------------------------------------------------------------

        // Singleton<T>.Awake overwrites the static instance, so the copy's scripts (AbilityManager, PickupHUD, ...) hijack the real ones. Point them back and drop the copies.
        static void DetachSingletons(GameObject root, GameObject clone)
        {
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                for (Type t = mb.GetType(); t != null; t = t.BaseType)
                {
                    if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(Singleton<>)) continue;
                    var field = t.GetField("m_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    var origs = root.GetComponentsInChildren(mb.GetType(), true);
                    var orig = origs.Length > 0 ? origs[0] : null;
                    if (field != null && orig != null) field.SetValue(null, orig);
                    Log("detached singleton " + mb.GetType().Name);
                    Destroy(mb);
                    break;
                }
            }
        }

        void ToggleTestPeer()
        {
            if (testPeer == null) testPeer = new Peer { Number = 2 };
            if (testPeer.Clone != null) { Destroy(testPeer.Clone); testPeer.ClearClone(); Log("test copy removed"); return; }
            SpawnClone(testPeer, null);
        }

        void SpawnClone(Peer p, Vector3? at)
        {
            var root = GameObject.Find("Walkster");
            if (root == null) { Log("spawn: no Walkster"); return; }
            var localWalker = root.GetComponentInChildren<BipedWalkScript>();
            Vector3 pos = at.HasValue ? at.Value - (localWalker.body.position - root.transform.position) : root.transform.position + root.transform.right * 2f;
            var clone = (GameObject)Instantiate(root, pos, root.transform.rotation);
            clone.name = "WalksterRemote" + p.Number;
            p.Clone = clone;
            DetachSingletons(root, clone);
            foreach (var h in clone.GetComponentsInChildren<ExploreInputHandler>(true)) Destroy(h);
            foreach (var i in clone.GetComponentsInChildren<TwoAxisXInput>(true)) Destroy(i);
            foreach (var c in clone.GetComponentsInChildren<Camera>(true)) c.enabled = false;
            foreach (var a in clone.GetComponentsInChildren<AudioListener>(true)) a.enabled = false;
            var walker = clone.GetComponentInChildren<BipedWalkScript>();
            if (walker == null) { Log("spawn: copy has no BipedWalkScript"); return; }
            foreach (var s in clone.GetComponentsInChildren<SaveInfo>(true)) Destroy(s);
            foreach (var j in clone.GetComponentsInChildren<JumpAbility>(true)) Destroy(j);
            foreach (var w in clone.GetComponentsInChildren<WaterWadeSound>(true)) Destroy(w);
            foreach (var w in clone.GetComponentsInChildren<FallToDeath>(true)) Destroy(w);
            foreach (var w in clone.GetComponentsInChildren<PlayerOutOfBounds>(true)) Destroy(w);
            foreach (var w in clone.GetComponentsInChildren<CameraFocus>(true)) Destroy(w);   // the game camera frames every CameraFocus: copies must not pull it
            var driver = walker.gameObject.AddComponent<RemoteDriver>();
            driver.useKeyboard = !Online;
            driver.mirrorFrom = localWalker;
            var h0 = root.GetComponentInChildren<ExploreInputHandler>();
            if (h0 != null)
            {
                driver.maxSpeed = 8f; driver.accScale = h0.m_desiredAccelerationScale; driver.lookRate = h0.m_lookTrackingSpeed;
                driver.jumpCooldown = h0.m_jumpCooldown; driver.jumpFalloff = h0.m_jumpScaleFalloff;
            }
            p.Driver = driver;
            p.CloneTsMap.Clear(); p.CloneRsMap.Clear(); p.VisLogged = false; p.LastAppliedSkin = -1;
            Log("spawned copy of " + p.Label + " at " + pos);
        }
    }
}
