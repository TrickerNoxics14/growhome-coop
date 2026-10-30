using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // Speedruns with other players.
    //  RACE: the party leader arms a race, every member gets a fresh speedrun save slot of their own (no world of anybody else ever reaches it),
    //  then one countdown, and every racer starts the game's own NEW GAME on the same moment. Each racer's own timer is the official one; the others
    //  are shown live from their PROGRESS messages, and the results are ranked at the end.
    //  CO-OP RUN: the leader starts a new game in its own speedrun slot and the party follows into it the normal co-op way (guests). The leader's
    //  timer is the only timer; members show it and report the splits, databank entries and ends that happen in their own copy of the world.
    //
    // Layers: SrMsg (encode / parse), SrRateLimit and RaceFsm are pure. SrNode runs the messages and the race against two small interfaces
    // (ISrLink: the players, ISrGame: the game), so the audit plays a whole race with fake players in one game. The live ones are
    // CoopBehaviour (ISrLink) and SrLiveGame (ISrGame) below.
    //
    // Older mods (v13 / v14) do not know these message types: their gate hands them to the world code, which ignores unknown types, so they
    // never answer READY and are simply left out of a race when its ready time is up.

    public struct SrNetMsg
    {
        public byte Type, Ver, Mode, Cat, Flags, State, Reason, SplitIdx, SplitCount, RunState, Kind;
        public uint RunId; public int MsToGo, ElapsedMs, LastSplitMs; public bool Valid; public ushort Arg;
    }

    // ---- the messages (pure) ---------------------------------------------------------------------------------------------------------------

    public static class SrMsg
    {
        public const byte Arm = 20, Ready = 21, Go = 22, Cancel = 23, Progress = 24, Event = 25;
        public const byte Version = 1;                       // a member with another version answers READY 2
        public const int MaxElapsedMs = 172800000;           // 48 h
        public const int MaxMsToGo = 10000;
        public const byte ReadyRefused = 0, ReadyOk = 1, ReadyVersion = 2;
        public const byte ModeRace = 1, ModeCoop = 2;
        public const byte KindSplit = 1, KindDataBank = 2, KindLever = 3, KindSeed = 4, KindFinal = 5;
        public const byte CancelByHost = 0, CancelRaceOver = 1, CancelNobody = 2, CancelLeft = 3;
        public const byte NoSplit = 255;

        public static bool Is(byte t) { return t >= Arm && t <= Event; }
        public static bool HostOnly(byte t) { return t == Arm || t == Go || t == Cancel; }

        // the exact size of each type: anything else is ignored
        public static int Length(byte t)
        {
            switch (t) { case Arm: return 9; case Ready: return 6; case Go: return 9; case Cancel: return 6; case Progress: return 17; case Event: return 8; }
            return -1;
        }

        static byte[] Head(byte type, uint runId)
        {
            var m = new byte[Length(type)];
            m[0] = type;
            int at = type == Arm ? 2 : 1;
            Buffer.BlockCopy(BitConverter.GetBytes(runId), 0, m, at, 4);
            return m;
        }

        // [20][ver][runId u32][mode 1=race 2=co-op][cat 0-3][flags: the level's seed total]
        public static byte[] ArmMsg(uint runId, byte mode, SrCategory cat, int seeds)
        {
            var m = Head(Arm, runId); m[1] = Version; m[6] = mode; m[7] = (byte)cat; m[8] = (byte)SrCategories.ClampSeeds(seeds);
            return m;
        }
        // [21][runId][state 0=refused 1=slot ready 2=version mismatch]
        public static byte[] ReadyMsg(uint runId, byte state) { var m = Head(Ready, runId); m[5] = state; return m; }
        // [22][runId][msToGo i32 0..10000]
        public static byte[] GoMsg(uint runId, int msToGo)
        {
            var m = Head(Go, runId);
            Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(0, Math.Min(MaxMsToGo, msToGo))), 0, m, 5, 4);
            return m;
        }
        // [23][runId][reason]
        public static byte[] CancelMsg(uint runId, byte reason) { var m = Head(Cancel, runId); m[5] = reason; return m; }
        // [24][runId][elapsedMs i32][splitIdx u8][splitCount u8][state u8][valid u8][lastSplitMs i32]
        public static byte[] ProgressMsg(uint runId, long elapsedMs, int splitIdx, int splitCount, SrState state, bool valid, long lastSplitMs)
        {
            var m = Head(Progress, runId);
            int el = (int)Math.Max(0, Math.Min(MaxElapsedMs, elapsedMs));
            Buffer.BlockCopy(BitConverter.GetBytes(el), 0, m, 5, 4);
            m[9] = splitIdx < 0 || splitIdx >= splitCount ? NoSplit : (byte)splitIdx;
            m[10] = (byte)Math.Max(0, Math.Min(SrCategories.MaxSplits, splitCount));
            m[11] = (byte)state; m[12] = (byte)(valid ? 1 : 0);
            int ls = lastSplitMs < 0 ? -1 : (int)Math.Min(MaxElapsedMs, lastSplitMs);
            Buffer.BlockCopy(BitConverter.GetBytes(ls), 0, m, 13, 4);
            return m;
        }
        // [25][runId][kind][arg u16]
        public static byte[] EventMsg(uint runId, byte kind, int arg)
        {
            var m = Head(Event, runId); m[5] = kind;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)Math.Max(0, Math.Min(65535, arg))), 0, m, 6, 2);
            return m;
        }

        // Reads a message and checks every field. False for anything out of range (the message is then dropped).
        public static bool TryParse(byte[] m, out SrNetMsg r)
        {
            r = new SrNetMsg();
            if (m == null || m.Length == 0 || !Is(m[0]) || m.Length != Length(m[0])) return false;
            r.Type = m[0];
            r.RunId = BitConverter.ToUInt32(m, r.Type == Arm ? 2 : 1);
            if (r.RunId == 0) return false;
            switch (r.Type)
            {
                case Arm:
                    r.Ver = m[1]; r.Mode = m[6]; r.Cat = m[7]; r.Flags = m[8];
                    return (r.Mode == ModeRace || r.Mode == ModeCoop) && r.Cat <= 3 && r.Flags >= 1 && r.Flags <= 10;
                case Ready:
                    r.State = m[5];
                    return r.State <= ReadyVersion;
                case Go:
                    r.MsToGo = BitConverter.ToInt32(m, 5);
                    return r.MsToGo >= 0 && r.MsToGo <= MaxMsToGo;
                case Cancel:
                    r.Reason = m[5];
                    return r.Reason <= CancelLeft;
                case Progress:
                    r.ElapsedMs = BitConverter.ToInt32(m, 5); r.SplitIdx = m[9]; r.SplitCount = m[10]; r.RunState = m[11]; r.Valid = m[12] == 1; r.LastSplitMs = BitConverter.ToInt32(m, 13);
                    return r.ElapsedMs >= 0 && r.ElapsedMs <= MaxElapsedMs && r.SplitCount <= SrCategories.MaxSplits && (r.SplitIdx == NoSplit || r.SplitIdx < r.SplitCount)
                        && r.RunState <= (byte)SrState.CannotFinish && m[12] <= 1 && r.LastSplitMs >= -1 && r.LastSplitMs <= MaxElapsedMs;
                case Event:
                    r.Kind = m[5]; r.Arg = BitConverter.ToUInt16(m, 6);
                    if (r.Kind == KindSplit) return r.Arg < SrCategories.MaxSplits;
                    if (r.Kind == KindDataBank) return r.Arg < 20;
                    if (r.Kind == KindSeed) return r.Arg <= 10;
                    return r.Kind == KindLever || r.Kind == KindFinal;
            }
            return false;
        }

        // the lobby's "sr" key: "<runId>|race|<cat>" while a race is armed or running; a member then never follows the leader into a game
        public static string RaceKey(uint runId, SrMode mode, SrCategory cat) { return runId + "|" + (mode == SrMode.Coop ? "coop" : "race") + "|" + SrCategories.FileKey(cat); }
        public static bool RaceBlocksFollow(string key) { return !string.IsNullOrEmpty(key) && key.Contains("|race|"); }

        public static uint NewRunId() { uint id = (uint)((DateTime.UtcNow.Ticks ^ ((long)Environment.TickCount << 16)) & 0x7FFFFFFF); return id == 0 ? 1u : id; }
    }

    // At most N messages a second from one player (PROGRESS); the rest are dropped.
    public class SrRateLimit
    {
        readonly int max; readonly float window;
        readonly Dictionary<ulong, float> since = new Dictionary<ulong, float>();
        readonly Dictionary<ulong, int> count = new Dictionary<ulong, int>();
        public int Dropped;
        public SrRateLimit(int perWindow, float windowSec) { max = perWindow; window = windowSec; }
        public bool Allow(ulong id, float now)
        {
            float t; int n;
            if (!since.TryGetValue(id, out t) || now - t >= window || now < t) { since[id] = now; count[id] = 1; return true; }
            count.TryGetValue(id, out n);
            if (n >= max) { Dropped++; return false; }
            count[id] = n + 1;
            return true;
        }
    }

    // ---- the race (pure) -------------------------------------------------------------------------------------------------------------------

    public class SrRacer
    {
        public ulong Id; public string Name = ""; public bool Me;
        public byte ReadyState = 255;                  // 255 = no answer yet
        public bool InRace, Started, Offline;
        public int ElapsedMs, SplitIdx = SrMsg.NoSplit, SplitCount, LastSplitMs = -1;
        public int DoneMask;                           // the splits this racer has reached
        public SrState State = SrState.Running;
        public bool Valid = true;
        public float HeardAt;
        public string Note = "";                       // OFFLINE, DNF (silent) ...

        public int Done { get { int n = 0, m = DoneMask; while (m != 0) { n += m & 1; m >>= 1; } return n; } }
        public bool Running { get { return State == SrState.Running || State == SrState.CannotFinish; } }
        public bool Terminal { get { return !InRace || (Started && !Running); } }
        public long LiveMs(float now) { return Started && Running && !Offline ? ElapsedMs + (long)Math.Max(0f, (now - HeardAt) * 1000f) : ElapsedMs; }
    }

    public class RaceFsm
    {
        public enum Phase { Idle = 0, Armed = 1, Counting = 2, Running = 3, Over = 4 }
        public Phase Ph;
        public uint RunId; public SrCategory Cat; public SrMode Mode = SrMode.Race; public int Seeds = 8;
        public bool IAmHost, Spectating; public ulong HostId, MyId;
        public float ArmedAt, GoAt, OverAt = -999f;
        public float ReadyTimeout = 15f, OfflineAfter = 30f, DnfAfter = 120f, CountdownSec = 5f;
        public readonly List<SrRacer> Racers = new List<SrRacer>();

        public bool Active { get { return Ph == Phase.Armed || Ph == Phase.Counting || Ph == Phase.Running; } }
        public SrRacer Find(ulong id) { foreach (var r in Racers) if (r.Id == id) return r; return null; }
        public SrRacer Mine { get { foreach (var r in Racers) if (r.Me) return r; return null; } }

        void Reset(uint runId, SrCategory cat, int seeds, ulong me, float now)
        {
            Racers.Clear(); RunId = runId; Cat = cat; Seeds = SrCategories.ClampSeeds(seeds); MyId = me; ArmedAt = now; GoAt = 0f; OverAt = -999f; Spectating = false;
        }

        public void ArmAsHost(uint runId, SrCategory cat, int seeds, ulong me, string myName, List<ulong> others, float now)
        {
            Reset(runId, cat, seeds, me, now);
            IAmHost = true; HostId = me; Ph = Phase.Armed;
            Racers.Add(new SrRacer { Id = me, Name = myName ?? "", Me = true, ReadyState = SrMsg.ReadyOk, InRace = true, HeardAt = now });
            foreach (var id in others) if (id != me && Find(id) == null) Racers.Add(new SrRacer { Id = id, HeardAt = now });
        }

        public void ArmAsMember(uint runId, SrCategory cat, int seeds, ulong host, ulong me, string myName, float now)
        {
            Reset(runId, cat, seeds, me, now);
            IAmHost = false; HostId = host; Ph = Phase.Armed;
            Racers.Add(new SrRacer { Id = me, Name = myName ?? "", Me = true, ReadyState = SrMsg.ReadyOk, HeardAt = now });
            Racers.Add(new SrRacer { Id = host, ReadyState = SrMsg.ReadyOk, InRace = true, HeardAt = now });
        }

        // I joined while a race runs: I only watch it
        public void Spectate(uint runId, SrCategory cat, int seeds, ulong me, float now)
        {
            Reset(runId, cat, seeds, me, now);
            IAmHost = false; Spectating = true; Ph = Phase.Running;
        }

        public bool OnReady(ulong id, uint runId, byte state, float now)
        {
            if (!IAmHost || runId != RunId) return false;
            var r = Find(id);
            if (r == null || r.Me) return false;
            if (Ph == Phase.Armed) { r.ReadyState = state; r.HeardAt = now; return true; }
            // a racer who left after the countdown began: out of the race at once (no 120 s wait for a DNF)
            if ((Ph == Phase.Counting || Ph == Phase.Running) && state != SrMsg.ReadyOk && r.InRace && r.Running)
            {
                r.Started = true; r.State = SrState.Dnf; r.Note = "LEFT"; r.HeardAt = now;
                return true;
            }
            return false;
        }

        public int ReadyCount { get { int n = 0; foreach (var r in Racers) if (!r.Me && r.ReadyState == SrMsg.ReadyOk) n++; return n; } }
        public int Total { get { return Racers.Count; } }
        public bool AllAnswered { get { foreach (var r in Racers) if (!r.Me && r.ReadyState == 255) return false; return true; } }
        public bool AllReady { get { foreach (var r in Racers) if (!r.Me && r.ReadyState != SrMsg.ReadyOk) return false; return true; } }
        public bool GoDue(float now) { return IAmHost && Ph == Phase.Armed && (AllReady || now - ArmedAt >= ReadyTimeout); }

        // (host) the countdown: the racers who said ready are in, everybody else is out. Returns the others who are in.
        public List<SrRacer> StartCountdown(float now)
        {
            var going = new List<SrRacer>();
            foreach (var r in Racers)
            {
                r.InRace = r.Me || r.ReadyState == SrMsg.ReadyOk;
                if (r.InRace && !r.Me) going.Add(r);
                r.HeardAt = now;
            }
            Ph = Phase.Counting; GoAt = now + CountdownSec;
            return going;
        }

        // (member) the host's GO: my start is msToGo from now
        public bool OnGo(uint runId, int msToGo, float now)
        {
            if (IAmHost || Ph != Phase.Armed || runId != RunId) return false;
            Ph = Phase.Counting; GoAt = now + msToGo / 1000f;
            var me = Mine; if (me != null) me.InRace = true;
            return true;
        }

        public bool OnCancel(uint runId, float now)
        {
            if (runId != RunId || Ph == Phase.Idle) return false;
            Ph = Racers.Count > 0 && GoAt > 0f ? Phase.Over : Phase.Idle; OverAt = now;
            return true;
        }

        // a racer's PROGRESS (also my own, so my row is the same as everybody else's)
        public bool OnProgress(ulong id, SrNetMsg m, float now)
        {
            if (Ph == Phase.Idle || m.RunId != RunId) return false;
            var r = Find(id);
            if (r == null)
            {
                if (!Spectating && Ph != Phase.Running && Ph != Phase.Counting) return false;
                r = new SrRacer { Id = id };
                Racers.Add(r);
            }
            r.InRace = true; r.Started = true; r.Offline = false; r.Note = "";
            r.ElapsedMs = m.ElapsedMs; r.SplitCount = m.SplitCount; r.State = (SrState)m.RunState; r.Valid = m.Valid; r.HeardAt = now;
            if (m.SplitIdx != SrMsg.NoSplit)
            {
                r.SplitIdx = m.SplitIdx; r.LastSplitMs = m.LastSplitMs;
                if (m.LastSplitMs >= 0) r.DoneMask |= 1 << m.SplitIdx;
            }
            if (Ph == Phase.Counting) Ph = Phase.Running;
            return true;
        }

        // silence: OFFLINE after 30 s, DNF after 120 s
        public void Tick(float now)
        {
            if (Ph != Phase.Running && Ph != Phase.Counting) return;
            foreach (var r in Racers)
            {
                if (r.Me || !r.InRace || !r.Running) continue;
                float quiet = now - r.HeardAt;
                if (quiet > DnfAfter) { r.State = SrState.Dnf; r.Started = true; r.Offline = true; r.Note = "DNF"; }
                else if (quiet > OfflineAfter) { r.Offline = true; r.Note = "OFFLINE"; }
            }
        }

        public bool AllDone
        {
            get
            {
                if (Ph != Phase.Running && Ph != Phase.Counting) return false;
                int inRace = 0;
                foreach (var r in Racers) { if (!r.InRace) continue; inRace++; if (!r.Terminal) return false; }
                return inRace > 0;
            }
        }

        // finished first (fastest first), then still going (more splits, then earlier last split), then reset / DNF
        public List<SrRacer> Ranked()
        {
            var l = new List<SrRacer>();
            foreach (var r in Racers) if (r.InRace) l.Add(r);
            l.Sort((a, b) =>
            {
                int ga = Group(a), gb = Group(b);
                if (ga != gb) return ga.CompareTo(gb);
                if (ga == 0) return a.ElapsedMs.CompareTo(b.ElapsedMs);
                if (ga == 1) { int d = b.Done.CompareTo(a.Done); if (d != 0) return d; return a.LastSplitMs.CompareTo(b.LastSplitMs); }
                return a.Id.CompareTo(b.Id);
            });
            return l;
        }
        static int Group(SrRacer r) { return r.State == SrState.Finished ? 0 : r.Running && !r.Offline ? 1 : 2; }

        // the places, ties share a place: 1, 1, 3
        public static int[] Places(List<SrRacer> ranked)
        {
            var p = new int[ranked.Count];
            for (int i = 0; i < ranked.Count; i++)
            {
                bool tie = i > 0 && ranked[i].State == SrState.Finished && ranked[i - 1].State == SrState.Finished && ranked[i].ElapsedMs == ranked[i - 1].ElapsedMs;
                p[i] = tie ? p[i - 1] : i + 1;
            }
            return p;
        }
    }

    // ---- the players and the game, as the node sees them -----------------------------------------------------------------------------------

    public interface ISrLink
    {
        ulong SrMyId { get; }
        string SrMyName { get; }
        bool SrIsPartyHost { get; }           // I own the party
        ulong SrPartyHostId { get; }
        List<ulong> SrOthers();               // party members I can talk to now
        void SrSend(ulong to, byte[] m);
        float SrRttMs(ulong id);
        string SrNameOf(ulong id);
        void SrSetRaceKey(string v);
        bool SrInMyWorld(ulong id);           // (co-op leader) this member plays in my world now
    }

    public interface ISrGame
    {
        float Now { get; }
        long Clock();
        bool StartIdle { get; }               // on the start screen, nothing starting, no fade, no run going
        bool InWorld { get; }
        int SeedTotal { get; }
        string EnterSlot(SrCategory c, SrMode m, uint runId);    // null when the slot is ready (and held on the start screen)
        void ReleaseSlot();
        bool BeginNewGame(out string why);
        SrRun Run { get; }
        string StartCoopLeader(SrCategory c, uint runId);        // null when started
        void CoopEvent(byte kind, int arg, SrCategory c, int seeds);
        void EndRun(SrState state, string why);
        void Toast(string m);
    }

    // ---- the node: messages, race and co-op run ----------------------------------------------------------------------------------------------

    public class SrNode
    {
        public ISrLink Link; public ISrGame Game;
        public readonly RaceFsm Race = new RaceFsm();
        public readonly SrRateLimit Limit = new SrRateLimit(5, 1f);
        public readonly SrRateLimit NewsLimit = new SrRateLimit(20, 1f);
        readonly Dictionary<ulong, long> lastNews = new Dictionary<ulong, long>();

        // true when this PROGRESS says something new about that player (another run, split or state) and not only the time
        static long NewsKey(SrNetMsg m) { return ((long)m.RunId << 16) | ((long)m.SplitIdx << 8) | m.RunState; }
        bool NewsFrom(ulong from, SrNetMsg m) { long had; return !lastNews.TryGetValue(from, out had) || had != NewsKey(m); }
        public float HeartbeatSec = 2f, CoopBeatSec = 1f, HostLostAfter = 10f, MemberArmWait = 60f;
        public float SpectateSettle = 5f;      // a watcher first hears every racer's heartbeat before it can call the race over
        public int Refused, Taken;
        public string LastRefusal = "";
        float nextBeat, beginTriedUntil;
        bool beginDone;

        // co-op run
        public bool CoopHost, CoopMember; public uint CoopRunId; public SrCategory CoopCat; public int CoopSeeds = 8;
        public SrRun Mirror;                    // (member) the leader's run as its messages say
        public float MirrorHeardAt = -999f;
        public bool HostLost;
        public int EventsTaken, EventsRefused;
        readonly HashSet<int> sentEvents = new HashSet<int>();

        public SrNode(ISrLink link, ISrGame game) { Link = link; Game = game; }

        void Log(string m) { CoopBehaviour.Log("speedrun net: " + m); }
        void ToAll(byte[] m) { foreach (var id in Link.SrOthers()) Link.SrSend(id, m); }

        // the race's end goes to the racers and to everybody else in the party too (players watching it must leave it as well)
        void SendCancelToEveryone(byte[] m)
        {
            var sent = new HashSet<ulong>();
            foreach (var r in Race.Racers) if (!r.Me && sent.Add(r.Id)) Link.SrSend(r.Id, m);
            foreach (var id in Link.SrOthers()) if (sent.Add(id)) Link.SrSend(id, m);
        }

        // (member) I left the race on my own: the leader is told, so it does not wait for me
        void TellHostILeft()
        {
            if (Race.IAmHost || Race.Spectating || Race.HostId == 0) return;
            Link.SrSend(Race.HostId, SrMsg.ReadyMsg(Race.RunId, SrMsg.ReadyRefused));
        }

        public bool Armed { get { return Race.Ph == RaceFsm.Phase.Armed || Race.Ph == RaceFsm.Phase.Counting; } }

        // ---- the leader --------------------------------------------------------------------------------------------------------------

        public string HostStartRace(SrCategory cat)
        {
            if (!Link.SrIsPartyHost) return "HOST A PARTY FOR A RACE";
            var others = Link.SrOthers();
            if (others.Count == 0) return "INVITE SOMEBODY TO RACE";
            if (Race.Active || CoopHost) return "A RUN IS ALREADY ARMED";
            if (!Game.StartIdle) return "ONLY ON THE START SCREEN";
            uint id = SrMsg.NewRunId();
            string why = Game.EnterSlot(cat, SrMode.Race, id);
            if (why != null) return why;
            Race.ArmAsHost(id, cat, Game.SeedTotal, Link.SrMyId, Link.SrMyName, others, Game.Now);
            foreach (var r in Race.Racers) if (!r.Me) r.Name = Link.SrNameOf(r.Id);
            Link.SrSetRaceKey(SrMsg.RaceKey(id, SrMode.Race, cat));
            var arm = SrMsg.ArmMsg(id, SrMsg.ModeRace, cat, Game.SeedTotal);
            foreach (var o in others) Link.SrSend(o, arm);
            beginDone = false;
            Log("race " + id + " armed (" + SrCategories.Label(cat) + ") for " + others.Count + " other players");
            Game.Toast("RACE ARMED: WAITING FOR RACERS");
            return null;
        }

        // START RACE NOW: go with whoever is ready
        public void HostGoNow() { if (Race.IAmHost && Race.Ph == RaceFsm.Phase.Armed) Countdown(); }

        void Countdown()
        {
            float now = Game.Now;
            if (Race.ReadyCount == 0) { HostCancel(SrMsg.CancelNobody); Game.Toast("RACE CANCELLED: NOBODY WAS READY"); return; }
            var going = Race.StartCountdown(now);
            foreach (var r in going)
            {
                int ms = (int)(Race.CountdownSec * 1000f - Link.SrRttMs(r.Id) / 2f);
                Link.SrSend(r.Id, SrMsg.GoMsg(Race.RunId, ms));
            }
            foreach (var r in Race.Racers) if (!r.Me && !r.InRace) Link.SrSend(r.Id, SrMsg.CancelMsg(Race.RunId, SrMsg.CancelLeft));   // not ready in time: out (their slot is let go)
            beginDone = false; beginTriedUntil = Race.GoAt + 3f;
            Log("race " + Race.RunId + ": GO in " + Race.CountdownSec + " s for " + (going.Count + 1) + " racers");
        }

        // CANCEL RACE / END RACE
        public void HostCancel(byte reason)
        {
            if (!Race.IAmHost || Race.Ph == RaceFsm.Phase.Idle) return;
            SendCancelToEveryone(SrMsg.CancelMsg(Race.RunId, reason));
            bool wasRunning = Race.Ph == RaceFsm.Phase.Running || Race.Ph == RaceFsm.Phase.Counting;
            Race.OnCancel(Race.RunId, Game.Now);
            Link.SrSetRaceKey("");
            if (wasRunning && reason == SrMsg.CancelByHost) { var run = Game.Run; if (run != null && run.Live && run.RunId == Race.RunId) Game.EndRun(SrState.Dnf, "the race was ended"); }
            Game.ReleaseSlot();
            Log("race " + Race.RunId + " ended by me (reason " + reason + ")");
        }

        public string HostStartCoop(SrCategory cat)
        {
            if (!Link.SrIsPartyHost) return "HOST A PARTY FOR A CO-OP RUN";
            var others = Link.SrOthers();
            if (others.Count == 0) return "INVITE SOMEBODY FOR A CO-OP RUN";
            if (Race.Active || CoopHost) return "A RUN IS ALREADY ARMED";
            uint id = SrMsg.NewRunId();
            string why = Game.StartCoopLeader(cat, id);
            if (why != null) return why;
            CoopHost = true; CoopRunId = id; CoopCat = cat; CoopSeeds = Game.SeedTotal;
            Link.SrSetRaceKey(SrMsg.RaceKey(id, SrMode.Coop, cat));
            var arm = SrMsg.ArmMsg(id, SrMsg.ModeCoop, cat, Game.SeedTotal);
            foreach (var o in others) Link.SrSend(o, arm);
            nextBeat = 0f;
            Log("co-op run " + id + " started (" + SrCategories.Label(cat) + "); " + others.Count + " players follow");
            return null;
        }

        // ---- a member ------------------------------------------------------------------------------------------------------------------

        public void LeaveRace()
        {
            if (Race.IAmHost || Race.Ph == RaceFsm.Phase.Idle) return;
            if (Race.Active) TellHostILeft();
            Race.Ph = RaceFsm.Phase.Idle;
            Game.ReleaseSlot();
            Game.Toast("YOU LEFT THE RACE");
        }

        // ---- messages ------------------------------------------------------------------------------------------------------------------

        public void Handle(ulong from, byte[] raw, bool fromHost)
        {
            SrNetMsg m;
            if (!SrMsg.TryParse(raw, out m)) { Refused++; LastRefusal = "bad message"; return; }
            if (SrMsg.HostOnly(m.Type) && !fromHost) { Refused++; LastRefusal = "not from the leader"; return; }
            float now = Game.Now;
            switch (m.Type)
            {
                case SrMsg.Arm: OnArm(from, m, now); break;
                case SrMsg.Ready:
                    if (Race.OnReady(from, m.RunId, m.State, now)) { Taken++; Log(Link.SrNameOf(from) + (m.State == SrMsg.ReadyOk ? " is ready" : m.State == SrMsg.ReadyVersion ? " has another mod version" : " cannot race now")); }
                    else { Refused++; LastRefusal = "ready for no armed race"; }
                    break;
                case SrMsg.Go:
                    if (Race.OnGo(m.RunId, m.MsToGo, now)) { Taken++; beginDone = false; beginTriedUntil = Race.GoAt + 3f; Log("race " + m.RunId + ": GO in " + m.MsToGo + " ms"); }
                    else { Refused++; LastRefusal = "go for no armed race"; }
                    break;
                case SrMsg.Cancel: OnCancel(m, now); break;
                case SrMsg.Progress:
                    // only repeated heartbeats are cut: a new split or a new state (a finish) always gets through, at most 20 a second
                    if (!(NewsFrom(from, m) ? NewsLimit.Allow(from, now) : Limit.Allow(from, now))) { Refused++; LastRefusal = "too many progress messages"; return; }
                    lastNews[from] = NewsKey(m);
                    OnProgress(from, m, now);
                    break;
                case SrMsg.Event: OnEvent(from, m); break;
            }
        }

        void OnArm(ulong from, SrNetMsg m, float now)
        {
            if (m.Ver != SrMsg.Version) { Link.SrSend(from, SrMsg.ReadyMsg(m.RunId, SrMsg.ReadyVersion)); Refused++; LastRefusal = "another version"; return; }
            var cat = (SrCategory)m.Cat;
            if (m.Mode == SrMsg.ModeCoop)
            {
                CoopMember = true; CoopHost = false; CoopRunId = m.RunId; CoopCat = cat; CoopSeeds = m.Flags;
                memberWasIn = false; memberOutSince = -1f;
                Mirror = null; MirrorHeardAt = now; HostLost = false; sentEvents.Clear();
                Link.SrSend(from, SrMsg.ReadyMsg(m.RunId, SrMsg.ReadyOk));
                Taken++;
                Log("the leader started a co-op run (" + SrCategories.Label(cat) + "); I follow into their world");
                return;
            }
            if (Race.Active && Race.RunId == m.RunId) { Link.SrSend(from, SrMsg.ReadyMsg(m.RunId, SrMsg.ReadyOk)); return; }   // the same ARM again
            var run = Game.Run;
            string why = null;
            if (run != null && run.Live) why = "a run is going";
            else if (Race.Active) why = "another race";
            else if (!Game.StartIdle) why = "not on the start screen";
            else why = Game.EnterSlot(cat, SrMode.Race, m.RunId);
            if (why != null)
            {
                Link.SrSend(from, SrMsg.ReadyMsg(m.RunId, SrMsg.ReadyRefused));
                Refused++; LastRefusal = "cannot race: " + why;
                Log("race " + m.RunId + " refused: " + why);
                Game.Toast("COULD NOT JOIN THE RACE: " + why.ToUpper());
                return;
            }
            Race.ArmAsMember(m.RunId, cat, m.Flags, from, Link.SrMyId, Link.SrMyName, now);
            Race.Find(from).Name = Link.SrNameOf(from);
            CoopMember = false;
            beginDone = false;
            Link.SrSend(from, SrMsg.ReadyMsg(m.RunId, SrMsg.ReadyOk));
            Taken++;
            Log("race " + m.RunId + " armed by " + Link.SrNameOf(from) + " (" + SrCategories.Label(cat) + "): my speedrun slot is ready");
            Game.Toast("RACE ARMED: " + SrCategories.Label(cat) + ". SAVE SLOT READY");
        }

        void OnCancel(SrNetMsg m, float now)
        {
            if (CoopMember && m.RunId == CoopRunId) { Taken++; return; }      // the co-op run is over: its time stays up until I am back on the start screen
            bool wasRunning = Race.Ph == RaceFsm.Phase.Running || Race.Ph == RaceFsm.Phase.Counting;
            if (!Race.OnCancel(m.RunId, now)) { Refused++; LastRefusal = "cancel for another race"; return; }
            Taken++;
            if (m.Reason == SrMsg.CancelLeft) Race.Ph = RaceFsm.Phase.Idle;
            var run = Game.Run;
            if (wasRunning && m.Reason == SrMsg.CancelByHost && run != null && run.Live && run.RunId == m.RunId) Game.EndRun(SrState.Dnf, "the leader ended the race");
            Game.ReleaseSlot();
            Game.Toast(m.Reason == SrMsg.CancelRaceOver ? "RACE OVER" : m.Reason == SrMsg.CancelLeft ? "THE RACE STARTED WITHOUT YOU" : "THE LEADER CANCELLED THE RACE");
            Log("race " + m.RunId + " over (reason " + m.Reason + ")");
        }

        void OnProgress(ulong from, SrNetMsg m, float now)
        {
            if (CoopMember && m.RunId == CoopRunId && from == Link.SrPartyHostId) { ApplyMirror(m); Taken++; return; }
            // a race that started before I came (or without me): I only watch it
            bool idle = (Race.Ph == RaceFsm.Phase.Idle || (Race.Ph == RaceFsm.Phase.Over && m.RunId != Race.RunId)) && !CoopHost && !CoopMember && (Game.Run == null || !Game.Run.Live);
            if (idle) Race.Spectate(m.RunId, SrCategory.Any, 8, Link.SrMyId, now);
            if (Race.OnProgress(from, m, now)) { Taken++; var r = Race.Find(from); if (r != null && r.Name.Length == 0) r.Name = Link.SrNameOf(from); }
            else { Refused++; LastRefusal = "progress for another run"; }
        }

        void OnEvent(ulong from, SrNetMsg m)
        {
            var run = Game.Run;
            if (!CoopHost || run == null || !run.Live || run.Mode != SrMode.Coop || m.RunId != CoopRunId) { EventsRefused++; Refused++; LastRefusal = "event outside a co-op run"; return; }
            if (!Link.SrInMyWorld(from)) { EventsRefused++; Refused++; LastRefusal = "event from a player not in my world"; return; }
            EventsTaken++; Taken++;
            Game.CoopEvent(m.Kind, m.Arg, CoopCat, CoopSeeds);
        }

        // (co-op member) the leader's run as a local copy: the timer re-anchored on each message, the splits as they come
        void ApplyMirror(SrNetMsg m)
        {
            var st = (SrState)m.RunState;
            bool live = st == SrState.Running || st == SrState.CannotFinish;
            // the copy has ended: it stays frozen on its final time (the leader's heartbeats after the end change nothing); a live message again means a new run
            if (Mirror != null && !Mirror.Live) { MirrorHeardAt = Game.Now; if (!live) return; Mirror = null; }
            if (Mirror == null || Mirror.Engine.Count != m.SplitCount)
            {
                Mirror = new SrRun(CoopCat, SrMode.Coop, CoopSeeds, Game.Clock) { RunId = CoopRunId };
                if (Mirror.Engine.Count != m.SplitCount) Mirror = new SrRun(CoopCat, SrMode.Coop, 8, Game.Clock) { RunId = CoopRunId };
            }
            MirrorHeardAt = Game.Now;
            bool wasLost = HostLost; HostLost = false;
            long now = Game.Clock();
            // the message is about half a round trip old when it arrives
            long oneWay = (long)Math.Max(0f, Math.Min(1000f, Link.SrRttMs(Link.SrPartyHostId) / 2f));
            long start = now - SpeedTimer.FromMs(m.ElapsedMs + (live ? oneWay : 0));
            var t = Mirror.Timer;
            if (!t.Started || (t.Stopped && wasLost) || (!t.Stopped && SpeedTimer.ToMs(Math.Abs(t.StartTicks - start)) > 50)) t.StartAt(start);      // small differences are left alone: no jitter
            var e = Mirror.Engine;
            if (m.SplitIdx != SrMsg.NoSplit && m.SplitIdx < e.Count && e.States[m.SplitIdx] == SrSplitState.Pending)
            {
                int i = m.SplitIdx;
                if (m.LastSplitMs >= 0)
                {
                    if (!e.Unordered) for (int j = e.Current; j < i; j++) if (e.States[j] == SrSplitState.Pending) e.States[j] = SrSplitState.Skipped;
                    e.States[i] = SrSplitState.Done; e.AtMs[i] = m.LastSplitMs; e.Order.Add(i);
                    if (!e.Unordered && i + 1 > e.Current) e.Current = i + 1;
                }
                else e.States[i] = SrSplitState.Skipped;
            }
            if (!m.Valid) Mirror.MarkInvalid("THE LEADER'S RUN IS INVALID");
            if (!live)
            {
                // the run really ended (finished, reset, DNF): stop on the leader's exact time
                Mirror.State = st;
                if (st == SrState.Finished) { e.Ended = true; e.EndMs = m.ElapsedMs; Mirror.FinishedAtRealtime = Time.realtimeSinceStartup; }
                t.StopAt(t.StartTicks + SpeedTimer.FromMs(m.ElapsedMs));
            }
            else Mirror.State = st;          // running, or CANNOT FINISH (still timing)
        }

        // (co-op member) something happened in my copy of the leader's world: the leader takes it for the run
        public void OnLocalEvent(SrEvent e)
        {
            if (!CoopMember || !Game.InWorld || Mirror != null && !Mirror.Live) return;
            byte kind = 0; int arg = 0;
            switch (e.Kind)
            {
                case SrEvKind.Accomplishment:
                    {
                        var defs = SrCategories.Splits(CoopCat, CoopSeeds);
                        for (int i = 0; i < defs.Count; i++) if (defs[i].Rule == SplitRule.Acc && defs[i].Name == e.Name) { kind = SrMsg.KindSplit; arg = i; }
                        if (kind == 0 && e.Name == "CompletedDataBank") return;
                    }
                    break;
                case SrEvKind.DataBank: if (e.Arg >= 0 && e.Arg < 20) { kind = SrMsg.KindDataBank; arg = e.Arg; } break;
                case SrEvKind.LeverEnd: kind = SrMsg.KindLever; break;
                case SrEvKind.Seed: if (e.HasState) { kind = SrMsg.KindSeed; arg = Math.Min(10, e.Seeds); } break;
                case SrEvKind.FinalFadeEnd: kind = SrMsg.KindFinal; break;
            }
            if (kind == 0) return;
            int key = kind * 1000 + arg;
            if (!sentEvents.Add(key)) return;          // each thing once
            Link.SrSend(Link.SrPartyHostId, SrMsg.EventMsg(CoopRunId, kind, arg));
        }

        // ---- my own run changed (Speedrun.Changed) --------------------------------------------------------------------------------------

        int lastIdx = -1;
        public void OnRunChanged(SrRun run, string what, int idx)
        {
            if (run == null || run.Shadow) return;
            if (what == "start") lastIdx = -1;
            if (what == "start" && run.Mode != SrMode.Solo) Game.ReleaseSlot();     // the slot was held for the start only: a reset racer gets their own save back at once
            if (idx >= 0) lastIdx = idx;
            // sent from Tick, at most 4 a second (a finish can skip many splits in one frame; the receivers take 5 a second)
            bool ours = (run.Mode == SrMode.Race && Race.Active && run.RunId == Race.RunId) || (run.Mode == SrMode.Coop && CoopHost && run.RunId == CoopRunId);
            if (!ours) return;
            dirty = true;
            if (idx >= 0 && idx < run.Engine.Count && run.Engine.States[idx] == SrSplitState.Done) dirtyIdx = idx;
            if (run.Mode == SrMode.Race) { var me = Race.Mine; if (me != null) { me.Started = true; me.InRace = true; } }
        }

        public byte[] ProgressFor(SrRun run, int idx)
        {
            var e = run.Engine;
            if (idx < 0 && e.Order.Count > 0) idx = e.Order[e.Order.Count - 1];
            long last = idx >= 0 && idx < e.Count && e.States[idx] == SrSplitState.Done ? e.AtMs[idx] : -1;
            return SrMsg.ProgressMsg(run.RunId, run.FinalMs, idx, e.Count, run.State, run.Valid, last);
        }

        bool dirty; int dirtyIdx = -1; float nextSendAt;
        public const float MinSendGap = 0.25f;

        void SendProgress(SrRun run, int idx)
        {
            dirty = false; dirtyIdx = -1; nextSendAt = Game.Now + MinSendGap;
            var msg = ProgressFor(run, idx);
            if (run.Mode == SrMode.Race) { ToAll(msg); SrNetMsg mine; if (SrMsg.TryParse(msg, out mine)) Race.OnProgress(Link.SrMyId, mine, Game.Now); }
            else ToAll(msg);
            nextBeat = Game.Now + (run.Mode == SrMode.Coop ? CoopBeatSec : HeartbeatSec);
        }

        // ---- every frame -----------------------------------------------------------------------------------------------------------------

        public void Tick()
        {
            float now = Game.Now;
            var run = Game.Run;
            // the leader: everybody answered, or the ready time is up
            if (Race.GoDue(now)) Countdown();
            // the party is gone (I left it, or the leader did) while the race waits: out of it
            if (Race.Ph == RaceFsm.Phase.Armed && now >= nextPartyCheck)
            {
                nextPartyCheck = now + 1f;
                if (Race.IAmHost && !Link.SrIsPartyHost) { Log("race " + Race.RunId + ": I no longer lead a party, cancelled"); Race.Ph = RaceFsm.Phase.Idle; Link.SrSetRaceKey(""); Game.ReleaseSlot(); }
                else if (!Race.IAmHost && !Link.SrOthers().Contains(Race.HostId)) { Log("race " + Race.RunId + ": the leader is gone, leaving the race"); TellHostILeft(); Race.Ph = RaceFsm.Phase.Idle; Game.ReleaseSlot(); }
            }
            if (!Race.IAmHost && Race.Ph == RaceFsm.Phase.Armed && now - Race.ArmedAt > MemberArmWait) { Log("race " + Race.RunId + ": no GO from the leader, leaving the race"); TellHostILeft(); Race.Ph = RaceFsm.Phase.Idle; Game.ReleaseSlot(); }
            // my start, on the frame my GO time is reached
            if (Race.Ph == RaceFsm.Phase.Counting || Race.Ph == RaceFsm.Phase.Running)
            {
                var me = Race.Mine;
                if (me != null && me.InRace && !beginDone && now >= Race.GoAt && !Race.Spectating)
                {
                    string why;
                    if (Game.BeginNewGame(out why)) { beginDone = true; Log("race " + Race.RunId + ": started " + ((now - Race.GoAt) * 1000f).ToString("F0") + " ms after GO"); }
                    else if (now > beginTriedUntil)
                    {
                        beginDone = true; me.Started = true; me.State = SrState.Dnf; me.Note = "DID NOT START";
                        ToAll(SrMsg.ProgressMsg(Race.RunId, 0, -1, 0, SrState.Dnf, false, -1));
                        // my own saves come back at once (the slot is only held for a start); a member is out of the race, the leader keeps running it for the others
                        Game.ReleaseSlot();
                        if (!Race.IAmHost) { Race.Ph = RaceFsm.Phase.Over; Race.OverAt = now; }
                        Game.Toast("YOUR RACE DID NOT START: " + why);
                        Log("race " + Race.RunId + ": my start did not happen within 3 s of GO (" + why + "), DNF");
                    }
                }
            }
            Race.Tick(now);
            bool raceRun = run != null && Race.Active && run.RunId == Race.RunId && MeStarted();
            if (raceRun && ((dirty && now >= nextSendAt) || now >= nextBeat)) SendProgress(run, dirty ? dirtyIdx : -1);
            if (Race.IAmHost && Race.Ph == RaceFsm.Phase.Running && Race.AllDone)
            {
                Log("race " + Race.RunId + ": everybody is done");
                SendCancelToEveryone(SrMsg.CancelMsg(Race.RunId, SrMsg.CancelRaceOver));
                Race.Ph = RaceFsm.Phase.Over; Race.OverAt = now;
                Link.SrSetRaceKey("");
                Game.ReleaseSlot();
            }
            else if (!Race.IAmHost && Race.Ph == RaceFsm.Phase.Running && Race.AllDone && (!Race.Spectating || now - Race.ArmedAt > SpectateSettle))
            {
                // a racer or a watcher: everybody I know of is done (a silent one is DNF after 120 s), so the race is over here too
                Race.Ph = RaceFsm.Phase.Over; Race.OverAt = now;
                if (!Race.Spectating) Game.ReleaseSlot();
            }
            // co-op run
            if (CoopHost)
            {
                bool mine = run != null && run.RunId == CoopRunId;
                if (mine && ((dirty && now >= nextSendAt) || now >= nextBeat)) SendProgress(run, dirty ? dirtyIdx : -1);              // after the end too: the final time reaches everybody
                if (mine && run.Live) coopDoneAt = -1f;
                else if (coopDoneAt < 0f) coopDoneAt = now;
                else if (now - coopDoneAt > 10f) { CoopHost = false; coopDoneAt = -1f; Link.SrSetRaceKey(""); ToAll(SrMsg.CancelMsg(CoopRunId, SrMsg.CancelRaceOver)); Log("co-op run " + CoopRunId + " over"); }
            }
            // a member back on the start screen for a while after the run: the leader's timer goes away
            if (CoopMember)
            {
                if (Game.InWorld) { memberWasIn = true; memberOutSince = -1f; }
                else if (memberWasIn && memberOutSince < 0f) memberOutSince = now;
                else if (memberWasIn && now - memberOutSince > 5f && (Mirror == null || !Mirror.Live || HostLost)) { CoopMember = false; Mirror = null; memberWasIn = false; Log("co-op run: back on the start screen, the leader's timer is put away"); }
            }
            if (CoopMember && Mirror != null && Mirror.Live && now - MirrorHeardAt > HostLostAfter && !HostLost)
            {
                HostLost = true; Mirror.Timer.StopAt(Game.Clock()); Log("co-op run: nothing from the leader for " + HostLostAfter + " s (HOST LOST)");
            }
        }

        float coopDoneAt = -1f;
        bool memberWasIn; float memberOutSince = -1f;
        float nextPartyCheck;
        bool MeStarted() { var me = Race.Mine; return me != null && me.Started && me.InRace; }

        public bool ShowResults(float now) { return Race.Ph == RaceFsm.Phase.Over && Race.Racers.Count > 1 && now - Race.OverAt < 20f; }
    }

    // ---- the live game behind the node ----------------------------------------------------------------------------------------------------

    public class SrLiveGame : ISrGame
    {
        public delegate string CoopLeaderStart(SrCategory c);
        public CoopLeaderStart LeaderStart;      // CoopBehaviour: the slot, then the party's own leader start with the game's NEW GAME
        public float Now { get { return Time.realtimeSinceStartup; } }
        public long Clock() { return Speedrun.Clock(); }
        public bool StartIdle { get { return Speedrun.StartScreenIdle && !Speedrun.SlotActive && !WorldSync.StartingNow() && (Speedrun.Current == null || !Speedrun.Current.Live); } }
        public bool InWorld { get { return WorldSync.InWorld(); } }
        public int SeedTotal { get { return Singleton<SceneProfile>.Exists ? Singleton<SceneProfile>.Instance.m_numberOfSeeds : 8; } }
        public SrRun Run { get { return Speedrun.Current; } }

        public string EnterSlot(SrCategory c, SrMode m, uint runId)
        {
            // the same safety check the start makes: a racer who could not start at GO is left out before the countdown instead
            if (!Speedrun.CloudOff()) return "STEAM CLOUD SAVING IS ON";
            if (WorldSync.GuestMode && !WorldSync.StartingNow()) WorldSync.LeaveGuestMode();     // back from a co-op game: my own saves first, then the slot
            Speedrun.Category = c; Speedrun.Mode = m; Speedrun.ForcedRunId = runId;
            string why = Speedrun.EnterSlot(c);
            if (why == null && !Speedrun.SlotPathsActive()) { Speedrun.LeaveSlot("slot paths not in place"); why = "THE SPEEDRUN SAVE IS NOT READY"; }
            if (why != null) { Speedrun.ForcedRunId = 0; return why; }
            Speedrun.HoldSlot = true;
            return null;
        }

        public void ReleaseSlot() { Speedrun.HoldSlot = false; }

        public bool BeginNewGame(out string why) { return Speedrun.BeginNewGame(out why); }

        public string StartCoopLeader(SrCategory c, uint runId)
        {
            if (LeaderStart == null) return "CO-OP RUNS NEED STEAM";
            Speedrun.ForcedRunId = runId;
            string why = LeaderStart(c);
            if (why != null) Speedrun.ForcedRunId = 0;
            return why;
        }

        public void CoopEvent(byte kind, int arg, SrCategory c, int seeds) { Speedrun.CoopMemberEvent(kind, arg, c, seeds); }
        public void EndRun(SrState state, string why) { if (Speedrun.Current != null) Speedrun.End(Speedrun.Current, state, why); }
        public void Toast(string m) { CoopUI.Toast(m); }
    }

    // the one live node
    public static class SpeedrunNet
    {
        public static SrNode Node;
        public static readonly SrLiveGame LiveGame = new SrLiveGame();

        public static void Init(ISrLink link, SrLiveGame.CoopLeaderStart leaderStart)
        {
            LiveGame.LeaderStart = leaderStart;
            Node = new SrNode(link, LiveGame);
            Speedrun.RaceStarter = cat => Node.HostStartRace(cat);
            Speedrun.CoopStarter = cat => Node.HostStartCoop(cat);
            Speedrun.Changed = Node.OnRunChanged;
            Speedrun.Tap = null;
        }

        public static void Handle(ulong from, byte[] m, bool fromHost)
        {
            if (Node == null) return;
            try { Node.Handle(from, m, fromHost); } catch (Exception e) { CoopBehaviour.Log("speedrun net: message " + e.GetType().Name + " " + e.Message); }
        }

        public static void Tick()
        {
            if (Node == null) return;
            try
            {
                Node.Tick();
                // a co-op member reports what happens in its copy of the leader's world
                if (Node.CoopMember && Speedrun.Tap == null) Speedrun.Tap = Node.OnLocalEvent;
                else if (!Node.CoopMember && Speedrun.Tap != null) Speedrun.Tap = null;
            }
            catch (Exception e) { CoopBehaviour.Log("speedrun net: tick " + e.GetType().Name + " " + e.Message); }
        }
    }
}
