using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    public enum CreatureRole { None, Leader, Guest }

    // Sheep, dodos (the rare ones too) and glo worms are the same for everybody in a party. The LEADER's game runs them:
    //  - the leader numbers every creature and sends the list (name, alive or not, where) now and then, and each creature's pose to every guest
    //    near it (10 times a second within 80 m, once a second up to 150 m, nothing further; at most 24 per packet and 8 KB/s per guest)
    //  - the leader keeps creatures near a guest awake (the game freezes creatures far from its own camera)
    //  - a guest switches the creatures' own brains off and walks them where the leader says, with the game's own legs
    //  - only the leader kills: a guest's copy is never turned to chunks by its own game; when the leader's creature dies (eaten, a venus trap,
    //    water) the guest's copy is turned to chunks too, and its replacement shows up when the leader's does
    //  - a creature a guest holds is moved by that guest (the leader follows it, its brain off, while held and 1.5 s after letting go)
    //  - a venus trap the plant destroyed on the leader is destroyed for everybody
    // Nothing of it in a race (Speedrun.Isolated). A guest whose leader never sends a creature list (an older version) keeps its own creatures.
    // The bestiary, the "dodo seen" check and the achievements stay each player's own.
    public static class CreatureSync
    {
        public const byte Msg = 17;            // reliable, leader -> guest: [17][op][epoch i32]...
        public const byte OpTable = 1, OpDeath = 2, OpTrapGone = 3;
        public const int MinMsg = 6, MaxMsg = 16000;
        public const byte MsgPose = 0xC3;      // unreliable: [C3][version][epoch i32][seq u16][count u8] + count x entry
        public const byte PoseVersion = 1;
        public const int PoseHeader = 9, EntrySize = 29, MaxEntries = 24;
        public const int MaxPoseBytes = PoseHeader + MaxEntries * EntrySize;
        public const int MaxCreatures = 400, MaxTraps = 200, MaxMade = 64;
        public const float NearRange = 80f, FarRange = 150f, NearInterval = 0.1f, FarInterval = 1f;
        public const int BytesPerSecond = 8000;                 // per guest
        public const float TableEvery = 10f, TableSilence = 30f;
        public const float ClaimTail = 1.5f, ClaimHold = 0.6f, ClaimReach = 15f;
        public const float SnapDistance = 3f, TargetFresh = 1.5f;
        public const byte FlagSleep = 1, FlagHeld = 2;
        public static readonly string[] Known = { "Sheep", "SheepRare", "Dodo", "DodoRare", "GloWorm" };

        public static bool TestMode;           // the audit drives Tick itself; the mod's own calls do nothing meanwhile
        public static int BudgetBytes = BytesPerSecond;

        // ---- the wire ----------------------------------------------------------------------------------------------------------------
        public struct Pose { public ushort Id; public byte Life, Flags, State; public Vector3 Pos, Euler, Vel; }

        public class TableEntry { public ushort Id; public byte Life; public bool Alive; public int Key; public string Name; public Vector3 Pos; }

        static ushort Ang(float a) { return (ushort)Mathf.RoundToInt(Mathf.Repeat(a, 360f) / 360f * 65535f); }
        static short Cm(float v) { return (short)Mathf.Clamp(Mathf.RoundToInt(v * 100f), -32767, 32767); }
        static bool Sane(Vector3 v) { return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && Mathf.Abs(v.x) < 100000f && Mathf.Abs(v.y) < 100000f && Mathf.Abs(v.z) < 100000f; }

        public static byte[] BuildPose(int epoch, ushort seq, List<Pose> list)
        {
            int n = Math.Min(list.Count, MaxEntries);
            var ms = new MemoryStream(PoseHeader + n * EntrySize); var w = new BinaryWriter(ms);
            w.Write(MsgPose); w.Write(PoseVersion); w.Write(epoch); w.Write(seq); w.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                var p = list[i];
                w.Write(p.Id); w.Write(p.Life); w.Write(p.Flags); w.Write(p.State);
                w.Write(p.Pos.x); w.Write(p.Pos.y); w.Write(p.Pos.z);
                w.Write(Ang(p.Euler.x)); w.Write(Ang(p.Euler.y)); w.Write(Ang(p.Euler.z));
                w.Write(Cm(p.Vel.x)); w.Write(Cm(p.Vel.y)); w.Write(Cm(p.Vel.z));
            }
            return ms.ToArray();
        }

        // the exact size for its count, a known version, sane numbers: anything else is thrown away whole
        public static bool PoseOk(byte[] m) { return m != null && m.Length >= PoseHeader && m.Length <= MaxPoseBytes && m[0] == MsgPose && m[1] == PoseVersion && m[8] <= MaxEntries && m.Length == PoseHeader + m[8] * EntrySize; }

        public static bool ReadPose(byte[] m, out int epoch, out ushort seq, List<Pose> into)
        {
            epoch = 0; seq = 0; into.Clear();
            if (!PoseOk(m)) return false;
            var r = new BinaryReader(new MemoryStream(m));
            r.ReadByte(); r.ReadByte(); epoch = r.ReadInt32(); seq = r.ReadUInt16(); int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var p = new Pose();
                p.Id = r.ReadUInt16(); p.Life = r.ReadByte(); p.Flags = r.ReadByte(); p.State = r.ReadByte();
                p.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                p.Euler = new Vector3(r.ReadUInt16() / 65535f * 360f, r.ReadUInt16() / 65535f * 360f, r.ReadUInt16() / 65535f * 360f);
                p.Vel = new Vector3(r.ReadInt16() / 100f, r.ReadInt16() / 100f, r.ReadInt16() / 100f);
                if (p.Id == 0 || !Sane(p.Pos)) { into.Clear(); return false; }
                into.Add(p);
            }
            return true;
        }

        public static byte[] BuildTable(int epoch, List<TableEntry> list, List<int> trapsGone)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Msg); w.Write(OpTable); w.Write(epoch);
            int n = Math.Min(list.Count, MaxCreatures); w.Write((ushort)n);
            for (int i = 0; i < n; i++)
            {
                var e = list[i];
                w.Write(e.Id); w.Write(e.Life); w.Write(e.Alive); w.Write(e.Key); w.Write(Clip(e.Name));
                w.Write(e.Pos.x); w.Write(e.Pos.y); w.Write(e.Pos.z);
            }
            int t = Math.Min(trapsGone.Count, MaxTraps); w.Write((ushort)t);
            for (int i = 0; i < t; i++) w.Write(trapsGone[i]);
            return ms.ToArray();
        }

        static string Clip(string s) { s = s ?? ""; return s.Length > 32 ? s.Substring(0, 32) : s; }

        public static bool ReadTable(byte[] m, out int epoch, List<TableEntry> into, List<int> trapsGone)
        {
            epoch = 0; into.Clear(); trapsGone.Clear();
            if (m == null || m.Length < MinMsg || m.Length > MaxMsg || m[0] != Msg || m[1] != OpTable) return false;
            try
            {
                var r = new BinaryReader(new MemoryStream(m));
                r.ReadByte(); r.ReadByte(); epoch = r.ReadInt32();
                int n = r.ReadUInt16(); if (n > MaxCreatures) return false;
                for (int i = 0; i < n; i++)
                {
                    var e = new TableEntry();
                    e.Id = r.ReadUInt16(); e.Life = r.ReadByte(); e.Alive = r.ReadBoolean(); e.Key = r.ReadInt32(); e.Name = r.ReadString();
                    e.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    if (e.Id == 0 || e.Name.Length == 0 || e.Name.Length > 32 || !Sane(e.Pos)) { into.Clear(); return false; }
                    into.Add(e);
                }
                int t = r.ReadUInt16(); if (t > MaxTraps) { into.Clear(); return false; }
                for (int i = 0; i < t; i++) trapsGone.Add(r.ReadInt32());
                if (r.BaseStream.Position != m.Length) { into.Clear(); trapsGone.Clear(); return false; }   // nothing extra after it
                return true;
            }
            catch (Exception) { into.Clear(); trapsGone.Clear(); return false; }
        }

        public static byte[] BuildDeath(int epoch, ushort id, byte life)
        {
            var m = new byte[9]; m[0] = Msg; m[1] = OpDeath;
            Buffer.BlockCopy(BitConverter.GetBytes(epoch), 0, m, 2, 4); Buffer.BlockCopy(BitConverter.GetBytes(id), 0, m, 6, 2); m[8] = life;
            return m;
        }

        public static byte[] BuildTrapGone(int epoch, int key)
        {
            var m = new byte[10]; m[0] = Msg; m[1] = OpTrapGone;
            Buffer.BlockCopy(BitConverter.GetBytes(epoch), 0, m, 2, 4); Buffer.BlockCopy(BitConverter.GetBytes(key), 0, m, 6, 4);
            return m;
        }

        // which message this is, at its exact size: 0 when it is none of them
        public static byte OpOf(byte[] m)
        {
            if (m == null || m.Length < MinMsg || m.Length > MaxMsg || m[0] != Msg) return 0;
            if (m[1] == OpDeath) return m.Length == 9 ? OpDeath : (byte)0;
            if (m[1] == OpTrapGone) return m.Length == 10 ? OpTrapGone : (byte)0;
            if (m[1] == OpTable) return m.Length >= 10 ? OpTable : (byte)0;
            return 0;
        }

        // ---- who gets what (pure: the audit runs it on made-up creatures) ----------------------------------------------------------------
        public struct Cand { public ushort Id; public float Dist, Last; }

        // The creatures due for one guest's next packet: near ones every 0.1 s, far ones every second, nothing beyond 150 m; the most overdue first.
        public static int Plan(List<Cand> cands, float now, int max, List<ushort> into)
        {
            into.Clear();
            var due = new List<KeyValuePair<float, ushort>>();
            foreach (var c in cands)
            {
                if (c.Dist > FarRange) continue;
                float every = c.Dist <= NearRange ? NearInterval : FarInterval;
                float late = (now - c.Last) / every;
                if (late >= 0.95f) due.Add(new KeyValuePair<float, ushort>(late + (c.Dist <= NearRange ? 0.5f : 0f), c.Id));
            }
            due.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0; i < due.Count && into.Count < max; i++) into.Add(due[i].Value);
            return into.Count;
        }

        // A guest's bandwidth: a packet goes out only when there is room for it.
        public class Bucket
        {
            public float Rate, Cap, Tokens, Last = -1f;
            public Bucket(float rate, float cap) { Rate = rate; Cap = cap; }
            public bool Take(int bytes, float now)
            {
                if (Last < 0f) { Last = now; Tokens = Cap; }
                Tokens = Mathf.Min(Cap, Tokens + Mathf.Max(0f, now - Last) * Rate); Last = now;
                if (Tokens < bytes) return false;
                Tokens -= bytes; return true;
            }
        }

        // Only a guest that runs this version gets creatures (and has them kept awake): its poses carry a motion session, an older one's never do.
        public static bool WantsCreatures(bool isBot, uint motionSession) { return isBot || motionSession != 0u; }

        public static CreatureRole RoleFor(bool isolated, bool inWorld, bool connected, bool amGuest, bool inLeaderWorld)
        {
            if (isolated || !inWorld || !connected) return CreatureRole.None;
            if (amGuest) return inLeaderWorld ? CreatureRole.Guest : CreatureRole.None;
            return CreatureRole.Leader;
        }

        // ---- the creatures ------------------------------------------------------------------------------------------------------------
        public class Rec
        {
            public ushort Id; public string Name = ""; public int Key; public byte Life;
            public bool Worm; public GameObject Go; public BehaviourController Ai; public CreaturePathfinding Path; public CreatureWalkScript Walk; public GloWormBehaviour WormAi; public Rigidbody Body;
            public CreatureAgent Agent; public FlyingSheep Fly;
            public bool Dead, Hidden, Spare, Made, NoBody;
            public bool AiOff, PathOff, WormOff;           // what I switched off (and switch on again)
            public bool HasTarget; public Pose Target; public float TargetAt;
            public ulong ClaimBy; public float ClaimUntil;   // (leader) a guest holds it
            public float MineUntil = -1f;                    // (guest) I hold it (and a little after)
            public int Snaps; public float LastErr;
            public bool Alive { get { return !Dead && Go != null && Go.activeInHierarchy && Body != null && (Worm ? WormAi != null : Ai != null); } }
        }

        public class Viewer { public ulong Id; public Vector3 Pos; public ITransport Link; public bool Bot; }

        public class ViewStats { public int Packets, Bytes, MaxPacket, Entries, Tables, Throttled, MaxEntriesSeen; public float Since; public readonly Dictionary<ushort, int> PerId = new Dictionary<ushort, int>(); }

        class ViewState
        {
            public readonly Dictionary<ushort, float> LastSent = new Dictionary<ushort, float>();
            public Bucket Budget; public float NextPacket, NextTable, NextTableMin, LastSeen; public int TableSent = -1; public ushort Seq;
            public readonly ViewStats Stats = new ViewStats();
        }

        static CreatureRole role = CreatureRole.None;
        public static CreatureRole Role { get { return role; } }
        static int epochNow;
        static readonly List<Rec> recs = new List<Rec>();
        static readonly Dictionary<ushort, Rec> byId = new Dictionary<ushort, Rec>();
        static readonly Dictionary<GameObject, Rec> byGo = new Dictionary<GameObject, Rec>();
        static readonly Dictionary<ulong, ViewState> views = new Dictionary<ulong, ViewState>();
        static readonly Dictionary<int, VenusTrap> traps = new Dictionary<int, VenusTrap>();
        static readonly List<int> trapsGone = new List<int>();
        static readonly HashSet<int> trapsApplied = new HashSet<int>();
        static readonly List<byte[]> outbox = new List<byte[]>();
        static ushort nextId = 1; static int tableVersion; static float nextScan, nextClaimSend; static ushort claimSeq;
        static bool guestActive; static float lastTableAt; static ulong leaderId; static int lastPoseSeq = -1; static float lastPoseAt;
        static int made;
        static float nextSummary;
        static bool mappedOnce;              // (guest) the leader's list was matched to my creatures: a new one of mine is a spare
        static BipedHandsScript localHands;
        public static int StatDeathsSent, StatDeathsApplied, StatBlocked, StatTrapsSent, StatTrapsApplied, StatRefused, StatClaims, StatShown, StatMade, StatTables;
        public static string LastLog = "";

        public static List<Rec> Records { get { return recs; } }
        public static Rec Find(ushort id) { Rec r; return byId.TryGetValue(id, out r) ? r : null; }
        public static Rec FindGo(GameObject go) { Rec r; return go != null && byGo.TryGetValue(go, out r) ? r : null; }
        public static bool GuestActive { get { return guestActive; } }
        public static ViewStats StatsFor(ulong viewer) { ViewState v; return views.TryGetValue(viewer, out v) ? v.Stats : null; }
        public static int TrapsGoneCount { get { return trapsGone.Count; } }

        static void Log(string m) { LastLog = m; CoopBehaviour.Log("creatures: " + m); }

        // ---- reflection into the game ------------------------------------------------------------------------------------------------
        const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly FieldInfo fInhibit = typeof(BipedWalkScript).GetField("m_distanceDisabledInhibit", Priv);
        static readonly FieldInfo fQueue = typeof(CreatureRespawnHandler).GetField("m_creaturesToRemove", Priv);
        static readonly FieldInfo fTrapDying = typeof(VenusTrap).GetField("m_selfDestructing", Priv);
        static readonly MethodInfo mTrapDestroy = typeof(VenusTrap).GetMethod("DestroyTrap", Priv);
        static readonly FieldInfo fFlyGrabs = typeof(FlyingSheep).GetField("m_grabCount", Priv);
        public static bool ReflectionOk { get { return fInhibit != null && fQueue != null && fTrapDying != null && mTrapDestroy != null && fFlyGrabs != null; } }

        public static int KeyOf(Transform t)
        {
            var si = t.GetComponent<SaveInfo>();
            if (si != null && !string.IsNullOrEmpty(si.m_uniqueID)) return ObjectSync.Hash("S" + si.m_uniqueID);
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name + "[" + x.GetSiblingIndex() + "]");
            parts.Reverse();
            return ObjectSync.Hash("P" + string.Join("/", parts.ToArray()));
        }

        static T First<T>(GameObject go) where T : Component { var a = go.GetComponentsInChildren<T>(true); return a.Length > 0 ? a[0] : null; }

        static void Bind(Rec r, GameObject go)
        {
            if (r.Go != null) byGo.Remove(r.Go);
            // the same body passed on from another record keeps what was switched off on it (so it is switched on again later)
            var had = go.GetComponent<CreatureAgent>();
            var prev = had != null && had.Rec != null && had.Rec != r && had.Rec.Go == go ? had.Rec : null;
            r.Go = go; r.AiOff = prev != null && prev.AiOff; r.PathOff = prev != null && prev.PathOff; r.WormOff = prev != null && prev.WormOff;
            if (prev != null) { prev.Go = null; prev.AiOff = prev.PathOff = prev.WormOff = false; }
            r.WormAi = go.GetComponent<GloWormBehaviour>(); r.Worm = r.WormAi != null;
            r.Ai = r.Worm ? null : go.GetComponent<BehaviourController>();
            r.Walk = r.Worm ? null : First<CreatureWalkScript>(go);
            r.Path = r.Worm ? null : First<CreaturePathfinding>(go);
            r.Body = r.Worm ? go.GetComponent<Rigidbody>() : (r.Walk != null && r.Walk.body != null ? r.Walk.body : go.GetComponent<Rigidbody>());
            r.Fly = First<FlyingSheep>(go);
            r.Agent = go.GetComponent<CreatureAgent>() ?? go.AddComponent<CreatureAgent>();
            r.Agent.Rec = r; r.Agent.KeepAwake = false; r.Agent.Steer = false; r.Agent.AllowDeath = false;
            r.Agent.Guarded = role == CreatureRole.Guest;
            r.Name = go.name;
            byGo[go] = r;
        }

        // every sheep, dodo and glo worm that is switched on here (never a player or a player's copy)
        static List<GameObject> LocalCreatures()
        {
            var l = new List<GameObject>();
            foreach (BehaviourController ai in UnityEngine.Object.FindObjectsOfType(typeof(BehaviourController)))
            {
                if (ai == null || ai.name.StartsWith("Walkster") || First<CreatureWalkScript>(ai.gameObject) == null) continue;
                l.Add(ai.gameObject);
            }
            foreach (GloWormBehaviour g in UnityEngine.Object.FindObjectsOfType(typeof(GloWormBehaviour)))
                if (g != null && g.rigidbody != null) l.Add(g.gameObject);
            return l;
        }

        static void ScanTraps()
        {
            foreach (VenusTrap t in UnityEngine.Object.FindObjectsOfType(typeof(VenusTrap)))
            {
                if (t == null) continue;
                int k = KeyOf(t.transform);
                if (!traps.ContainsKey(k) && traps.Count < MaxTraps) traps[k] = t;
            }
        }

        static void Puppet(Rec r, bool on)
        {
            if (on)
            {
                if (r.Ai != null && r.Ai.enabled) { r.Ai.enabled = false; r.AiOff = true; }
                if (r.Path != null && r.Path.enabled) { r.Path.enabled = false; r.PathOff = true; }
                if (r.WormAi != null && r.WormAi.enabled) { r.WormAi.enabled = false; r.WormOff = true; }
            }
            else
            {
                if (r.AiOff && r.Ai != null) r.Ai.enabled = true;
                if (r.PathOff && r.Path != null) r.Path.enabled = true;
                if (r.WormOff && r.WormAi != null) r.WormAi.enabled = true;
                r.AiOff = r.PathOff = r.WormOff = false;
            }
        }

        public static bool BrainOn(Rec r)
        {
            if (r == null) return false;
            if (r.Worm) return r.WormAi != null && r.WormAi.enabled;
            return r.Ai != null && r.Ai.enabled && (r.Path == null || r.Path.enabled);
        }

        // ---- every frame ---------------------------------------------------------------------------------------------------------------
        public static void Idle() { if (role != CreatureRole.None || recs.Count > 0) Tick(CreatureRole.None, null, null, 0UL); }

        public static void Tick(CreatureRole want, List<Viewer> viewers, ITransport leaderLink, ulong leader)
        {
            try
            {
                if (want != role || (want == CreatureRole.Guest && leader != leaderId) || EpochNow(want) != epochNow)
                {
                    Release(role == CreatureRole.None ? "" : "now " + want);
                    role = want; leaderId = leader; epochNow = EpochNow(want);
                }
                CreatureAgent.TickBlocked();
                if (role == CreatureRole.None) return;
                if (recs.Count > 0 && Time.realtimeSinceStartup >= nextSummary) { nextSummary = Time.realtimeSinceStartup + 10f; Log(Summary()); }
                var root = GameObject.Find("Walkster");
                localHands = root != null ? root.GetComponentInChildren<BipedHandsScript>() : null;
                if (role == CreatureRole.Leader) LeaderTick(viewers ?? new List<Viewer>());
                else GuestTick(leaderLink);
            }
            catch (Exception e) { Log("tick " + e.GetType().Name + " " + e.Message); }
        }

        static int EpochNow(CreatureRole r) { return r == CreatureRole.Leader ? WorldSync.MyEpoch : r == CreatureRole.Guest ? WorldSync.LoadedEpoch : 0; }
        public static int ExpectedEpoch { get { return WorldSync.LoadedEpoch; } }

        // Everything back to the game's own: brains on, kept copies back in, dead ones handed to the game's respawn, my scripts removed.
        public static void Release(string why)
        {
            int n = 0;
            foreach (var r in recs)
            {
                try
                {
                    if (r.Go == null) continue;
                    Puppet(r, false);
                    if (r.Agent != null) UnityEngine.Object.Destroy(r.Agent);
                    if (r.Hidden && r.Go != null && !r.Go.activeSelf)
                    {
                        if (r.Spare || !r.Dead) r.Go.SetActive(true);
                        else if (Singleton<CreatureRespawnHandler>.Exists) CreatureRespawnHandler.RespawnCreature(r.Go);
                    }
                    else if (!r.Hidden && !r.Go.activeSelf) r.Go.SetActive(true);     // switched off for a frame by the chunk guard
                    n++;
                }
                catch (Exception e) { Log("release " + e.GetType().Name); }
            }
            if (n > 0 && why.Length > 0) Log("gave " + n + " creatures back to the game (" + why + ")");
            recs.Clear(); byId.Clear(); byGo.Clear(); views.Clear(); traps.Clear(); trapsGone.Clear(); trapsApplied.Clear(); outbox.Clear();
            nextId = 1; tableVersion++; nextScan = 0f; guestActive = false; mappedOnce = false; lastPoseSeq = -1; made = 0; leaderId = 0UL;
            role = CreatureRole.None;
        }

        // ---- the leader ----------------------------------------------------------------------------------------------------------------
        static void LeaderTick(List<Viewer> viewers)
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextScan) { nextScan = now + 1f; LeaderScan(); }
            foreach (var r in recs)
            {
                if (!r.Dead && (r.Go == null || (r.Worm ? r.WormAi == null : r.Ai == null))) LeaderDied(r, "gone");
                if (r.Dead || r.Agent == null) continue;
                // a guest who held it let go: its own brain again
                if (r.ClaimBy != 0 && now > r.ClaimUntil) { r.ClaimBy = 0; r.Agent.Steer = false; Puppet(r, false); }
                bool near = r.ClaimBy != 0;
                if (!near && r.Body != null) foreach (var v in viewers) if ((v.Pos - r.Body.position).sqrMagnitude < NearRange * NearRange) { near = true; break; }
                r.Agent.KeepAwake = near;
            }
            if (traps.Count > 0)
            {
                var gone = new List<int>();
                foreach (var kv in traps) if (kv.Value == null || (bool)fTrapDying.GetValue(kv.Value)) gone.Add(kv.Key);
                foreach (var k in gone) { traps.Remove(k); if (!trapsGone.Contains(k)) { trapsGone.Add(k); outbox.Add(BuildTrapGone(epochNow, k)); tableVersion++; StatTrapsSent++; Log("a venus trap went (" + k + "): told everybody"); } }
            }
            foreach (var v in viewers)
            {
                ViewState vs;
                if (!views.TryGetValue(v.Id, out vs)) { vs = new ViewState { Budget = new Bucket(BudgetBytes, MaxPoseBytes * 2) }; vs.Stats.Since = now; views[v.Id] = vs; }
                vs.LastSeen = now;
                if (v.Link == null) continue;
                foreach (var m in outbox) if (!v.Bot) v.Link.SendReliable(m);
                if ((vs.TableSent != tableVersion && now >= vs.NextTableMin) || now >= vs.NextTable)
                {
                    vs.TableSent = tableVersion; vs.NextTableMin = now + 1f; vs.NextTable = now + TableEvery;
                    if (!v.Bot) v.Link.SendReliable(Table()); vs.Stats.Tables++;
                }
                if (now >= vs.NextPacket) { vs.NextPacket = now + NearInterval; SendPoses(v, vs, now); }
            }
            outbox.Clear();
            var old = new List<ulong>();
            foreach (var kv in views) if (now - kv.Value.LastSeen > 10f) old.Add(kv.Key);
            foreach (var k in old) views.Remove(k);
        }

        static void LeaderScan()
        {
            foreach (var go in LocalCreatures())
            {
                if (byGo.ContainsKey(go)) continue;
                // a replacement of one that died keeps its number
                Rec r = null;
                foreach (var d in recs) if (d.Dead && d.Name == go.name && (r == null || d.TargetAt < r.TargetAt)) r = d;
                if (r != null) { r.Dead = false; r.Life++; Bind(r, go); tableVersion++; continue; }
                if (recs.Count >= MaxCreatures || nextId == ushort.MaxValue) continue;
                r = new Rec { Id = nextId++, Key = KeyOf(go.transform) };
                Bind(r, go);
                recs.Add(r); byId[r.Id] = r; tableVersion++;
            }
            ScanTraps();
        }

        // the leader's creature died (eaten, chunked, drowned): everybody's copy goes too
        public static void LeaderDied(Rec r, string how)
        {
            if (r.Dead || role != CreatureRole.Leader) return;
            r.Dead = true; r.TargetAt = Time.realtimeSinceStartup; r.ClaimBy = 0;
            if (r.Go != null) byGo.Remove(r.Go);
            outbox.Add(BuildDeath(epochNow, r.Id, r.Life)); tableVersion++; StatDeathsSent++;
            Log(r.Name + " #" + r.Id + " died here (" + how + "): told everybody");
        }

        public static byte[] Table()
        {
            var l = new List<TableEntry>();
            foreach (var r in recs) l.Add(new TableEntry { Id = r.Id, Life = r.Life, Alive = r.Alive, Key = r.Key, Name = r.Name, Pos = r.Body != null && !r.Dead ? r.Body.position : Vector3.zero });
            return BuildTable(epochNow, l, trapsGone);
        }

        static Pose PoseOf(Rec r)
        {
            var b = r.Body;
            var p = new Pose { Id = r.Id, Life = r.Life, Pos = b.position, Euler = (r.Worm ? b.rotation : r.Go.transform.rotation).eulerAngles, Vel = b.isKinematic ? Vector3.zero : b.velocity };
            if (r.Walk != null && r.Walk.Sleep) p.Flags |= FlagSleep;
            if (r.ClaimBy != 0 || r.MineUntil > Time.realtimeSinceStartup) p.Flags |= FlagHeld;
            if (r.Ai != null) p.State = (byte)r.Ai.ActiveBehaviourType;
            return p;
        }

        // Several packets a tick when more creatures are due than one packet holds: only the bandwidth cap limits how many go out.
        public const int MaxPacketsPerTick = 4;

        static void SendPoses(Viewer v, ViewState vs, float now)
        {
            var cands = new List<Cand>();
            foreach (var r in recs)
            {
                if (!r.Alive || r.ClaimBy == v.Id) continue;      // the one holding it moves it
                cands.Add(new Cand { Id = r.Id, Dist = Vector3.Distance(v.Pos, r.Body.position) });
            }
            var pick = new List<ushort>();
            for (int k = 0; k < MaxPacketsPerTick; k++)
            {
                for (int i = 0; i < cands.Count; i++) { var c = cands[i]; float last; if (!vs.LastSent.TryGetValue(c.Id, out last)) last = -999f; c.Last = last; cands[i] = c; }
                if (Plan(cands, now, MaxEntries, pick) == 0) return;
                var list = new List<Pose>();
                foreach (var id in pick) list.Add(PoseOf(byId[id]));
                byte[] m = BuildPose(epochNow, ++vs.Seq, list);
                if (!vs.Budget.Take(m.Length, now)) { vs.Stats.Throttled++; return; }
                v.Link.Send(m);
                foreach (var id in pick) { vs.LastSent[id] = now; int c; vs.Stats.PerId.TryGetValue(id, out c); vs.Stats.PerId[id] = c + 1; }
                vs.Stats.Packets++; vs.Stats.Bytes += m.Length; vs.Stats.Entries += pick.Count;
                vs.Stats.MaxPacket = Math.Max(vs.Stats.MaxPacket, m.Length); vs.Stats.MaxEntriesSeen = Math.Max(vs.Stats.MaxEntriesSeen, pick.Count);
            }
        }

        // ---- poses that arrive -------------------------------------------------------------------------------------------------------
        // fromHost: the leader's poses (I am a guest). Otherwise a guest's own held creatures (I lead): taken only from a player in my world,
        // and only for a creature within reach of that player's BUD.
        public static void HandlePose(ulong from, byte[] data, bool fromHost, bool mayClaim, Vector3 claimantPos, bool hasPos)
        {
            try
            {
                int epoch; ushort seq; var list = new List<Pose>();
                if (!ReadPose(data, out epoch, out seq, list)) { StatRefused++; return; }
                float now = Time.realtimeSinceStartup;
                if (fromHost)
                {
                    if (role != CreatureRole.Guest || !guestActive || from != leaderId || epoch != epochNow) { StatRefused++; return; }
                    if (lastPoseSeq >= 0 && now - lastPoseAt < 5f && (short)(seq - (ushort)lastPoseSeq) <= 0) return;   // late: a newer one was used already
                    lastPoseSeq = seq; lastPoseAt = now;
                    foreach (var p in list) GuestPose(p);
                    return;
                }
                if (role != CreatureRole.Leader || !mayClaim || !hasPos || epoch != epochNow) { StatRefused++; return; }
                foreach (var p in list)
                {
                    var r = Find(p.Id);
                    if (r == null || !r.Alive || (p.Pos - claimantPos).magnitude > ClaimReach || (r.Body.position - claimantPos).magnitude > ClaimReach * 2f) { StatRefused++; continue; }
                    if (r.ClaimBy != 0 && r.ClaimBy != from && now <= r.ClaimUntil) { StatRefused++; continue; }   // somebody else holds it
                    if (r.ClaimBy != from) { StatClaims++; Log(r.Name + " #" + r.Id + " is held by a guest: following them"); }
                    r.ClaimBy = from; r.ClaimUntil = now + ClaimHold;
                    r.Target = p; r.HasTarget = true; r.TargetAt = Time.time;
                    Puppet(r, true); r.Agent.Steer = true; r.Agent.KeepAwake = true;
                }
            }
            catch (Exception e) { Log("pose " + e.GetType().Name + " " + e.Message); }
        }

        // ---- the guest ------------------------------------------------------------------------------------------------------------------
        static void GuestTick(ITransport leaderLink)
        {
            if (!guestActive) return;           // the leader has not sent a creature list (yet, or it is an older version): my own creatures run
            float now = Time.realtimeSinceStartup;
            if (now - lastTableAt > TableSilence) { Release("the leader stopped sending creatures"); role = CreatureRole.Guest; return; }
            if (now >= nextScan) { nextScan = now + 1f; GuestScan(); }
            // what I hold is mine to move (and a little after letting go)
            if (localHands != null)
                for (int i = 0; i < localHands.hand.Length && i < 2; i++)
                {
                    var h = localHands.hand[i];
                    if (!h.grabbing || h.grabbedObject == null) continue;
                    var r = Holding(h.grabbedObject);
                    if (r != null && r.Id != 0 && !r.Hidden) r.MineUntil = now + ClaimTail;
                }
            var mine = new List<Pose>();
            foreach (var r in recs)
            {
                if (r.Agent == null || r.Go == null) continue;
                bool held = r.MineUntil > now && r.Alive;
                bool fresh = r.HasTarget && Time.time - r.TargetAt < TargetFresh;
                r.Agent.Steer = fresh && !held && !r.Hidden;
                r.Agent.KeepAwake = (fresh || held) && !r.Hidden;
                if (held && r.Id != 0 && mine.Count < MaxEntries) mine.Add(PoseOf(r));
            }
            if (mine.Count > 0 && leaderLink != null && now >= nextClaimSend)
            {
                nextClaimSend = now + NearInterval;
                leaderLink.Send(BuildPose(epochNow, ++claimSeq, mine));
            }
        }

        static Rec Holding(Transform t)
        {
            for (; t != null; t = t.parent) { Rec r; if (byGo.TryGetValue(t.gameObject, out r)) return r; }
            return null;
        }

        // every local creature is the leader's puppet; one the leader has no number for is kept switched off
        static void GuestScan()
        {
            foreach (var go in LocalCreatures())
            {
                if (byGo.ContainsKey(go)) continue;
                var r = new Rec { Key = KeyOf(go.transform) };
                Bind(r, go); Puppet(r, true); recs.Add(r);
                // a creature of the leader's still without a body here takes it
                Rec want = null;
                foreach (var w in recs) if (w.Id != 0 && w.Go == null && w.Name == go.name && !w.Dead) { want = w; break; }
                if (want != null) { recs.Remove(r); Bind(want, go); Puppet(want, true); continue; }
                if (mappedOnce) Spare(r);
            }
            if (lastTrapsGone.Count > trapsApplied.Count) ApplyTrapsGone(lastTrapsGone);
        }

        static void Spare(Rec r) { r.Spare = true; r.Hidden = true; if (r.Go != null && r.Go.activeSelf) r.Go.SetActive(false); }

        static readonly List<TableEntry> lastTable = new List<TableEntry>();
        static readonly List<int> lastTrapsGone = new List<int>();

        // a reliable message from the leader (MessageGate lets it through only from the host)
        public static void Handle(ulong from, byte[] m)
        {
            try
            {
                byte op = OpOf(m);
                if (op == 0 || role != CreatureRole.Guest || from != leaderId || BitConverter.ToInt32(m, 2) != epochNow) { StatRefused++; return; }
                if (op == OpTable)
                {
                    int epoch; var list = new List<TableEntry>(); var gone = new List<int>();
                    if (!ReadTable(m, out epoch, list, gone)) { StatRefused++; return; }
                    lastTableAt = Time.realtimeSinceStartup; StatTables++;
                    lastTable.Clear(); lastTable.AddRange(list); lastTrapsGone.Clear(); lastTrapsGone.AddRange(gone);
                    if (!guestActive) { guestActive = true; Log("the leader runs the creatures: mine follow theirs"); }
                    Map(list);
                    ApplyTrapsGone(gone);
                }
                else if (op == OpDeath)
                {
                    if (!guestActive) return;
                    var r = Find(BitConverter.ToUInt16(m, 6));
                    if (r == null) return;
                    if (!r.Hidden) KillLocal(r, "the leader's died");
                    r.Dead = true; r.Life = m[8]; StatDeathsApplied++;
                }
                else if (op == OpTrapGone)
                {
                    int k = BitConverter.ToInt32(m, 6);
                    if (!lastTrapsGone.Contains(k)) lastTrapsGone.Add(k);
                    ApplyTrapsGone(new List<int> { k });
                }
            }
            catch (Exception e) { Log("message " + e.GetType().Name + " " + e.Message); }
        }

        static void Map(List<TableEntry> table)
        {
            if (nextScan <= 0f || recs.Count == 0) { GuestScan(); nextScan = Time.realtimeSinceStartup + 1f; }
            var inTable = new HashSet<ushort>();
            foreach (var e in table)
            {
                inTable.Add(e.Id);
                Rec r = Find(e.Id);
                if (r == null)
                {
                    // one of my creatures of that kind: the same one if it can be told, otherwise the nearest
                    Rec best = null; float bestD = float.MaxValue;
                    foreach (var c in recs)
                    {
                        if (c.Id != 0 || c.Go == null || c.Name != e.Name) continue;
                        float d = c.Key == e.Key ? -1f : (c.Body != null ? Vector3.Distance(c.Body.position, e.Pos) : 1e6f);
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    if (best == null && e.Alive) best = Make(e.Name, e.Pos);
                    if (best == null) { r = new Rec { Id = e.Id, Name = e.Name, Key = e.Key, Life = e.Life, Dead = !e.Alive, Hidden = true }; recs.Add(r); byId[e.Id] = r; continue; }
                    r = best; r.Id = e.Id; r.Life = e.Life; r.Spare = false; byId[e.Id] = r;
                    if (!e.Alive) { r.Dead = true; Hide(r); }
                    else { if (r.Hidden) Show(r, e.Pos, e.Life); else if (r.Body != null && Vector3.Distance(r.Body.position, e.Pos) > SnapDistance) MoveTo(r, e.Pos, null); }
                    continue;
                }
                if (!e.Alive && !r.Hidden) { r.Dead = true; r.Life = e.Life; Hide(r); }
            }
            // what the leader has not got: switched off here
            foreach (var r in recs) if (!r.Hidden && (r.Id == 0 || !inTable.Contains(r.Id))) { if (r.Id != 0) byId.Remove(r.Id); r.Id = 0; Spare(r); }
            mappedOnce = true;
        }

        static bool HasPrefab(string name)
        {
            try { foreach (var p in PrefabInformation.Instance.m_prefabInfos) if (p != null && p.m_name == name) return true; } catch (Exception) { }
            return false;
        }

        static Rec Make(string name, Vector3 pos)
        {
            if (made >= MaxMade || Array.IndexOf(Known, name) < 0 || !HasPrefab(name)) return null;
            GameObject go = null;
            try { go = ExtendedMonoBehaviour.Instantiate(name, pos, Quaternion.identity); } catch (Exception) { }
            if (go == null) return null;
            if (go.GetComponent<BehaviourController>() == null && go.GetComponent<GloWormBehaviour>() == null) { UnityEngine.Object.Destroy(go); return null; }
            made++; StatMade++;
            go.name = name;
            var r = new Rec { Key = KeyOf(go.transform), Made = true };
            Bind(r, go); Puppet(r, true); recs.Add(r);
            Log("made a " + name + " the leader has and I had not");
            return r;
        }

        static void Hide(Rec r) { r.Hidden = true; r.HasTarget = false; if (r.Go != null && r.Go.activeSelf) r.Go.SetActive(false); }

        static void Show(Rec r, Vector3 pos, byte life)
        {
            if (r.Go == null || (!r.Worm && r.Ai == null))
            {
                var go = TakeQueued(r.Name);
                if (go != null) Bind(r, go);
                else { if (r.NoBody) return; var m = Make(r.Name, pos); if (m == null) { r.NoBody = true; return; } recs.Remove(m); Bind(r, m.Go); r.Made = true; }
            }
            Puppet(r, true);
            r.Hidden = false; r.Dead = false; r.Spare = false; r.Life = life;
            r.Go.SetActive(true);
            if (r.Walk != null && r.Walk.body != null) r.Body = r.Walk.body;     // set up by the game once it is switched on
            MoveTo(r, pos, null);
            try { r.Go.BroadcastMessage("OnRespawn", SendMessageOptions.DontRequireReceiver); } catch (Exception) { }
            Puppet(r, true);
            StatShown++;
        }

        // the game's replacement of a creature I just turned to chunks, out of its respawn queue (the leader says when it comes back)
        static GameObject TakeQueued(string name)
        {
            if (!Singleton<CreatureRespawnHandler>.Exists || fQueue == null) return null;
            var q = fQueue.GetValue(Singleton<CreatureRespawnHandler>.Instance) as List<GameObject>;
            if (q == null) return null;
            for (int i = q.Count - 1; i >= 0; i--) if (q[i] != null && q[i].name == name) { var go = q[i]; q.RemoveAt(i); return go; }
            return null;
        }

        static void KillLocal(Rec r, string why)
        {
            if (r.Go == null) return;
            if (r.Go.activeInHierarchy && !r.Worm && Singleton<Chunker>.Exists)
            {
                if (r.Agent != null) r.Agent.AllowDeath = true;
                var old = r.Go; byGo.Remove(old); r.Go = null;
                Chunker.TurnToChunks(old, 2f);
                var rep = TakeQueued(r.Name);
                if (rep != null) { Bind(r, rep); Puppet(r, true); }
            }
            Hide(r);
            Log(r.Name + " #" + r.Id + " turned to chunks (" + why + ")");
        }

        static void GuestPose(Pose p)
        {
            var r = Find(p.Id);
            if (r == null) return;
            if (r.Hidden)
            {
                if (r.Dead && p.Life == r.Life) return;          // a late pose of the one that died
                Show(r, p.Pos, p.Life);
            }
            else if (p.Life != r.Life) { KillLocal(r, "it died on the leader"); Show(r, p.Pos, p.Life); }
            if (r.Go == null || r.Hidden) return;
            r.Target = p; r.HasTarget = true; r.TargetAt = Time.time;
        }

        static void ApplyTrapsGone(List<int> keys)
        {
            bool scanned = false;
            foreach (var k in keys)
            {
                if (trapsApplied.Contains(k)) continue;
                VenusTrap t;
                if (!traps.TryGetValue(k, out t) && !scanned) { scanned = true; ScanTraps(); traps.TryGetValue(k, out t); }
                if (t == null) continue;      // not found (yet)
                trapsApplied.Add(k);
                if (!DestroyTrap(t)) continue;
                StatTrapsApplied++;
                Log("a venus trap went on the leader: gone here too");
            }
        }

        // the game's own end of a trap the plant touched (false: it is going already)
        public static bool DestroyTrap(VenusTrap t)
        {
            if (t == null || (bool)fTrapDying.GetValue(t)) return false;
            fTrapDying.SetValue(t, true);
            t.StartCoroutine((IEnumerator)mTrapDestroy.Invoke(t, null));
            return true;
        }

        // ---- moving a puppet (from its CreatureAgent, every physics step) --------------------------------------------------------------
        public static void Steer(Rec r)
        {
            if (!r.HasTarget || r.Body == null || r.Go == null) return;
            float age = Mathf.Clamp(Time.time - r.TargetAt, 0f, 0.25f);
            var t = r.Target;
            Vector3 want = t.Pos + t.Vel * age;
            Vector3 err = want - r.Body.position;
            r.LastErr = err.magnitude;
            var rot = Quaternion.Euler(t.Euler);
            if (r.LastErr > SnapDistance || r.Body.isKinematic) { MoveTo(r, want, rot); r.Snaps++; return; }
            if (r.Walk != null)
            {
                var fwd = rot * Vector3.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = r.Go.transform.forward;
                fwd.Normalize();
                r.Walk.m_creatureTargetPosition = want + t.Vel * 0.3f;
                r.Walk.m_creatureTargetBodyDirection = fwd;
                r.Walk.CreatureTargetLookDirection = fwd;
                r.Walk.m_CurrentMaxSpeed = Mathf.Clamp(t.Vel.magnitude / Mathf.Max(0.5f, r.Walk.m_maxSpeed) + 0.2f, 0.2f, 1.5f);
                bool sleep = (t.Flags & FlagSleep) != 0;
                if (r.Walk.Sleep != sleep) { try { r.Walk.Sleep = sleep; } catch (Exception) { } }
                r.Body.velocity += Vector3.ClampMagnitude(t.Vel + err * 4f - r.Body.velocity, 6f) * 0.25f;
            }
            else
            {
                r.Body.velocity = t.Vel + Vector3.ClampMagnitude(err * 6f, 10f);
                r.Body.MoveRotation(Quaternion.Slerp(r.Body.rotation, rot, 0.2f));
            }
        }

        // the whole creature (every part) to a place at once
        public static void MoveTo(Rec r, Vector3 pos, Quaternion? rot)
        {
            if (r.Go == null) return;
            Vector3 from = r.Body != null ? r.Body.position : r.Go.transform.position;
            r.Go.transform.position += pos - from;
            if (rot.HasValue && r.Worm) r.Go.transform.rotation = rot.Value;
            else if (rot.HasValue) { float dy = Mathf.DeltaAngle(r.Go.transform.eulerAngles.y, rot.Value.eulerAngles.y); r.Go.transform.rotation = Quaternion.AngleAxis(dy, Vector3.up) * r.Go.transform.rotation; }   // a walker only turns about up
            foreach (var b in r.Go.GetComponentsInChildren<Rigidbody>())
                if (b != null && !b.isKinematic) { b.velocity = r.HasTarget ? r.Target.Vel : Vector3.zero; b.angularVelocity = Vector3.zero; }
        }

        // ---- keeping a creature awake and its achievements honest (from its CreatureAgent) ---------------------------------------------
        public static void Wake(Rec r)
        {
            var w = r.Walk;
            if (w == null || fInhibit == null) return;
            fInhibit.SetValue(w, true);
            if (w.m_distanceDisabled)
            {
                foreach (var b in w.GetComponentsInChildren<Rigidbody>(true)) if (b != null) b.isKinematic = false;
                w.m_distanceDisabled = false;
            }
        }

        // "flying sheep" counts the grabs of any hand, also a friend's copy's: only my own hands count for my achievement
        public static void FixFlyingSheep(Rec r)
        {
            if (r.Fly == null || fFlyGrabs == null) return;
            int n = 0;
            if (localHands != null)
                for (int i = 0; i < localHands.hand.Length && i < 2; i++)
                {
                    var h = localHands.hand[i];
                    if (h.grabbing && h.grabbedObject != null && Holding(h.grabbedObject) == r) n++;
                }
            if ((int)fFlyGrabs.GetValue(r.Fly) != n) fFlyGrabs.SetValue(r.Fly, n);
        }

        public static int FlyGrabs(FlyingSheep f) { return f != null && fFlyGrabs != null ? (int)fFlyGrabs.GetValue(f) : -1; }
        public static bool TrapDying(VenusTrap t) { return t != null && fTrapDying != null && (bool)fTrapDying.GetValue(t); }
        public static bool InQueue(GameObject go)
        {
            if (!Singleton<CreatureRespawnHandler>.Exists || fQueue == null) return false;
            var q = fQueue.GetValue(Singleton<CreatureRespawnHandler>.Instance) as List<GameObject>;
            return q != null && q.Contains(go);
        }

        public static string Summary()
        {
            int alive = 0, hidden = 0, off = 0;
            foreach (var r in recs) { if (r.Alive) alive++; if (r.Hidden) hidden++; if (r.Go != null && !BrainOn(r)) off++; }
            return role + ": " + recs.Count + " creatures (" + alive + " alive, " + hidden + " kept off, " + off + " brains off), traps gone " + trapsGone.Count + "/" + trapsApplied.Count;
        }
    }

    // On every creature the sync looks after: keeps it awake near a guest, walks it where the leader says, and (on a guest) keeps the
    // game from turning it to chunks by itself. The chunker first tells it OnDeath and stops when it is no longer active, so it switches
    // itself off and is switched back on in the next frame (as CloneGuard does for players' copies).
    public class CreatureAgent : MonoBehaviour
    {
        public CreatureSync.Rec Rec;
        public bool KeepAwake, Steer, Guarded, AllowDeath;
        public int Blocked;
        int blockedFrame = -1;
        static readonly List<CreatureAgent> blocked = new List<CreatureAgent>();

        void OnEnable() { StartCoroutine(Steps()); }

        // WaitForFixedUpdate resumes after each physics step: set there, "do not freeze" is seen by the creature's next step
        IEnumerator Steps()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                if (KeepAwake && Rec != null) { try { CreatureSync.Wake(Rec); } catch (Exception) { } }
            }
        }

        void FixedUpdate() { if (Steer && Rec != null) { try { CreatureSync.Steer(Rec); } catch (Exception) { } } }

        void LateUpdate() { if (Rec != null) { try { CreatureSync.FixFlyingSheep(Rec); } catch (Exception) { } } }

        void OnDeath()
        {
            if (Rec == null) return;
            if (!Guarded) { if (CreatureSync.Role == CreatureRole.Leader) CreatureSync.LeaderDied(Rec, "chunks"); return; }
            if (AllowDeath || !gameObject.activeSelf) return;
            Blocked++; CreatureSync.StatBlocked++;
            blockedFrame = Time.frameCount;
            blocked.Add(this);
            gameObject.SetActive(false);
        }

        public static void TickBlocked()
        {
            for (int i = blocked.Count - 1; i >= 0; i--)
            {
                var a = blocked[i];
                if (a == null) { blocked.RemoveAt(i); continue; }
                if (Time.frameCount <= a.blockedFrame) continue;
                blocked.RemoveAt(i);
                if (a.Rec != null && !a.Rec.Hidden) a.gameObject.SetActive(true);
            }
        }
    }
}
