using System;
using System.IO;
using UnityEngine;

namespace CoopMod
{
    // Everybody plays in the leader's world, so when the leader leaves it (Save & Quit, closes the game, drops off, leaves the party) every guest in it
    // goes back to the start menu through the game's own quit-to-menu, with "THE LEADER LEFT THE WORLD". A short network blip is no leave: only
    // the leader's own word, the party itself, or 15 s of silence count. The rules are pure so the audit checks them; the wiring is below.
    public class LeaderWatch
    {
        public const byte MsgLeft = 14;                 // leader -> everybody: [14][epoch] I left world number epoch
        public static float SilenceLimit = 15f;         // seconds without the leader's beat advancing (the audit shortens it for a moment)
        public const float LongFrame = 1f;              // a frame of mine this long (a load) does not count as the leader's silence
        public const string Message = "THE LEADER LEFT THE WORLD";

        public ulong Leader;                            // whose world I am in (0 = not watching)
        public float Silence;
        public bool SawPlay;                            // the leader's "play" note was on while I was in their world
        public float NoPlay;                            // seconds in their world without ever seeing the play note
        public const float NoPlayLimit = 10f;           // a leader in its world always has the note on: this long without it, it left while I was loading
        public bool Done;                               // a verdict was given for this stay
        public string Verdict;

        public void Reset() { Leader = 0; Silence = 0f; SawPlay = false; NoPlay = 0f; Done = false; Verdict = null; BeatsKnown = false; }

        // "Heard from the leader" means: a counter only the leader advances moved (its pose numbers, its once-a-second beat; see LeaderBeat).
        // Pings, their echoes, creature packets and anybody else's packets never move it, so a dead, frozen or restarted leader whose link still
        // answers pings is still noticed after SilenceLimit.
        public uint LastBeats; public bool BeatsKnown;
        public string StepBeats(bool guestInWorld, ulong candidate, bool inParty, string play, float dt, uint leaderBeats, bool leftSaid)
        {
            bool heard = BeatsKnown && leaderBeats != LastBeats;
            string v = Step(guestInWorld, candidate, inParty, play, dt, heard, leftSaid);
            if (Leader != 0) { LastBeats = leaderBeats; BeatsKnown = true; } else BeatsKnown = false;
            return v;
        }

        // One frame. guestInWorld: I play as a guest in somebody's world (not in a race, not after leaving the party myself). candidate: the party's
        // leader (0 without a party). Returns why I must go back to the start menu, once, or null.
        public string Step(bool guestInWorld, ulong candidate, bool inParty, string play, float dt, bool heard, bool leftSaid)
        {
            if (!guestInWorld) { Reset(); return null; }
            if (Leader == 0)
            {
                if (candidate == 0 || !inParty) return null;     // a guest without a party leader (an F7 pull, a code room before it is joined) is left alone
                Leader = candidate; Silence = 0f; SawPlay = false; NoPlay = 0f; Done = false; Verdict = null;
            }
            if (Done) return null;
            if (heard) Silence = 0f;
            else if (dt > 0f && dt < LongFrame) Silence += dt;
            if (!string.IsNullOrEmpty(play)) SawPlay = true;
            else if (!SawPlay && dt > 0f && dt < LongFrame) NoPlay += dt;
            string why = null;
            if (leftSaid) why = "the leader said it left the world";
            else if (!inParty) why = "the leader left the party";
            else if (SawPlay && string.IsNullOrEmpty(play)) why = "the leader went back to the start screen";
            else if (!SawPlay && NoPlay >= NoPlayLimit) why = "the leader went back to the start screen while I was loading";
            else if (Silence >= SilenceLimit) why = "no signal from the leader for " + (int)SilenceLimit + " s";
            if (why != null) { Done = true; Verdict = why; }
            return why;
        }
    }

    // The leader's beat: [0xC5][session][number], sent once a second by a player who is in its own world (not a guest) to everybody, on the
    // unreliable channel. Only the sender advances the number; the session is its game run (CoopBehaviour.MotionSession). Older versions
    // read it as nothing (a pose packet is at least 52 bytes and starts with 0xC0).
    public static class LeaderBeat
    {
        public const byte Msg = 0xC5;
        public const int Size = 9;
        public const float Interval = 1f;

        public static byte[] Write(uint session, uint seq)
        {
            var b = new byte[Size]; b[0] = Msg;
            Buffer.BlockCopy(BitConverter.GetBytes(session), 0, b, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, b, 5, 4);
            return b;
        }

        public static bool Read(byte[] b, out uint session, out uint seq)
        {
            session = 0; seq = 0;
            if (b == null || b.Length != Size || b[0] != Msg) return false;
            session = BitConverter.ToUInt32(b, 1); seq = BitConverter.ToUInt32(b, 5);
            return session != 0 && seq != 0;
        }

        // True when this beat is newer than every earlier one of that player. A new session (they restarted their game) counts from its first beat.
        // A replayed or late beat moves nothing.
        public static bool Advance(Peer p, uint session, uint seq)
        {
            if (p == null) return false;
            if (session != p.BeatSession) { p.BeatSession = session; p.BeatSeq = seq; return true; }
            if (seq <= p.BeatSeq) return false;
            p.BeatSeq = seq;
            return true;
        }
    }

    // The screen at start: the co-op copy always runs in a window (a frozen fullscreen game leaves a black screen that takes the PC).
    public static class ScreenRules
    {
        public static bool Windowed(bool fullScreen, bool allowed, int w, int h, int deskW, int deskH, out int nw, out int nh)
        {
            nw = w; nh = h;
            if (!fullScreen || allowed) return false;
            if (w <= 0 || h <= 0 || (deskW > 0 && w >= deskW) || (deskH > 0 && h >= deskH))
            {
                nw = 1280; nh = 720;
                if (deskW > 0 && deskH > 0 && (nw >= deskW || nh >= deskH)) { nw = Math.Max(640, deskW * 2 / 3); nh = Math.Max(360, deskH * 2 / 3); }
            }
            return true;
        }
    }

    public partial class CoopBehaviour
    {
        readonly LeaderWatch leaderWatch = new LeaderWatch();
        bool leaderWatchOff, leaderLeftSaid, kickDue, wasGameForLeft;
        int leftSaidEpoch;                              // the world number the leader's leave message (leaderLeftSaid) was about
        public bool TestOutOfWorld;                     // the audit plays one frame of my walkster being switched off (I died) right after the message
        public int LeftParked;                          // leave messages kept through a moment out of the world (counted for the audit)
        string pendingKick; float pendingKickSince;
        int leftEpoch;

        // my own beat while I am in my own world (I may be somebody's leader): made here every second, sent to everybody by NetTick
        float nextBeatAt; uint beatSeq; byte[] pendingBeat;
        public int BeatsMade; public uint LastBeatSeq { get { return beatSeq; } }
        public byte[] NextLeaderBeat() { BeatsMade++; return LeaderBeat.Write(MotionSession, ++beatSeq); }
        void BeatTick()
        {
            if (WorldSync.GuestMode || !WorldSync.InGameOrLoading()) { pendingBeat = null; return; }
            float now = Time.realtimeSinceStartup;
            if (now < nextBeatAt) return;
            nextBeatAt = now + LeaderBeat.Interval;
            pendingBeat = NextLeaderBeat();
        }
        byte[] TakeBeat() { var b = pendingBeat; pendingBeat = null; return b; }
        public delegate uint BeatSource();
        public BeatSource TestLeaderBeats;              // the audit plays the leader's beat counter (with LeaderWatchTestLeader)
        ulong earlyLeftFrom; int earlyLeftEpoch; float earlyLeftAt;     // a leave message heard while I was still loading the leader's world
        public ulong EarlyLeftFromForTest { get { return earlyLeftFrom; } }
        public void ForgetEarlyLeftForTest() { earlyLeftFrom = 0; }

        ulong PartyLeaderId()
        {
            if (LeaderWatchTestLeader != 0) return LeaderWatchTestLeader;
            return lobby != null && lobby.InLobby && lobby.HostId != myId ? lobby.HostId : 0UL;
        }
        public int Kicks; public string LastKickWhy = "";
        public ulong LeaderWatchTestLeader; public bool LeaderWatchTestParty; public string LeaderWatchTestPlay = "";   // the audit plays a party guest
        public LeaderWatch LeaderWatchForTest { get { return leaderWatch; } }

        void LeaderWatchTick()
        {
            bool game = WorldSync.InGameOrLoading();
            // I lead: my guests hear it the moment I leave my world
            if (game) leftEpoch = WorldSync.MyEpoch;
            if (wasGameForLeft && !game) SendLeftWorld("I left the world");
            wasGameForLeft = game;

            if (pendingKick != null) { KickTick(); return; }
            bool inWorld = WorldSync.InWorld() && !TestOutOfWorld;
            if (!inWorld) leaderWatchOff = false;
            if (kickDue && !game && Singleton<StartScreen>.Exists)
            {
                kickDue = false;
                CoopUI.Toast(LeaderWatch.Message);
                if (lobby != null) lobby.Status = "the leader left the world";
            }
            bool testing = LeaderWatchTestLeader != 0;
            bool inParty = testing ? LeaderWatchTestParty : lobby != null && lobby.InLobby;
            ulong candidate = testing ? LeaderWatchTestLeader : (inParty ? lobby.HostId : 0UL);
            if (candidate == myId && !testing) candidate = 0;
            string play = testing ? LeaderWatchTestPlay : (inParty ? lobby.Get("play") : "");
            bool guestInWorld = WorldSync.GuestMode && inWorld && !Speedrun.Isolated && !leaderWatchOff;
            // the leader's own beat counter (pose numbers and beats it sent): pings and echoes do not move it
            uint beats = 0;
            ulong lead = leaderWatch.Leader != 0 ? leaderWatch.Leader : candidate;
            if (testing && TestLeaderBeats != null) beats = TestLeaderBeats();
            else foreach (var p in peers) if (!p.IsBot && p.Id == lead) { beats = p.Beats; break; }
            bool said = leaderLeftSaid; leaderLeftSaid = false;
            // the leader's leave came while I watch, and this frame I am out of the world (dead: the game switches the walkster off until the respawn):
            // the watch resets below, so the message is kept like one heard while loading and counts once I am back in that world
            if (said && !inWorld && WorldSync.GuestMode && game && leaderWatch.Leader != 0 && !Speedrun.Isolated && !leaderWatchOff)
            {
                earlyLeftFrom = leaderWatch.Leader; earlyLeftEpoch = leftSaidEpoch; earlyLeftAt = Time.realtimeSinceStartup; said = false; LeftParked++;
                Log("party: the leader says it left its world " + leftSaidEpoch + " while I am out of the world for a moment; kept until I am back");
            }
            // the leader said it left while I was still loading its world: it counts once I am in that world
            if (earlyLeftFrom != 0)
            {
                if (!game || Time.realtimeSinceStartup - earlyLeftAt > 120f) earlyLeftFrom = 0;
                else if (guestInWorld && earlyLeftFrom == candidate && (WorldSync.LoadedEpoch == 0 || earlyLeftEpoch == WorldSync.LoadedEpoch || earlyLeftEpoch == WorldSync.LeaderEpoch))
                {
                    said = true; earlyLeftFrom = 0;
                    Log("party: the leader left its world " + earlyLeftEpoch + " while I was loading it");
                }
            }
            string why = leaderWatch.StepBeats(guestInWorld, candidate, inParty, play, Time.unscaledDeltaTime, beats, said);
            if (why != null) { pendingKick = why; pendingKickSince = Time.realtimeSinceStartup; KickTick(); }
        }

        // only the leader I play with, and only about the world I have from them
        void HeardLeaderLeft(Peer p, int epoch)
        {
            if (leaderWatch.Leader == 0 && WorldSync.GuestMode && WorldSync.InGameOrLoading() && p.Id == PartyLeaderId())
            {
                // still loading the leader's world: kept until I am in it
                earlyLeftFrom = p.Id; earlyLeftEpoch = epoch; earlyLeftAt = Time.realtimeSinceStartup;
                Log("party: the leader says it left its world " + epoch + " while I am still loading it; kept until I am in");
                return;
            }
            if (leaderWatch.Leader == 0 || p.Id != leaderWatch.Leader) { Log("party: " + p.Label + " says it left its world; I am not in it"); return; }
            if (WorldSync.LoadedEpoch != 0 && epoch != WorldSync.LoadedEpoch && epoch != WorldSync.LeaderEpoch) { Log("party: the leader left world " + epoch + ", not the one I play"); return; }
            Log("party: the leader says it left its world " + epoch);
            leaderLeftSaid = true; leftSaidEpoch = epoch;
        }

        public void HeardLeaderLeftForTest(Peer p, int epoch) { HeardLeaderLeft(p, epoch); }
        // the audit's stand-in for a real (not bot) player in the peer list: the real send and receive loops see it for a few seconds
        public void AddPeerForTest(Peer p) { if (p != null && !peers.Contains(p)) peers.Add(p); }
        public void RemovePeerForTest(Peer p) { if (p != null) { DropGhost(p); peers.Remove(p); } }

        void SendLeftWorld(string why)
        {
            if (WorldSync.GuestMode) return;          // only a leader has guests in its world
            var m = new byte[5]; m[0] = LeaderWatch.MsgLeft; Buffer.BlockCopy(BitConverter.GetBytes(leftEpoch), 0, m, 1, 4);
            int n = 0;
            foreach (var p in peers) if (!p.IsBot && p.Link != null) { try { p.Link.SendReliable(m); n++; } catch (Exception) { } }
            if (n > 0) Log("party: told " + n + " other players that I left my world (" + why + ")");
        }

        // The game's own quit to the start menu, once nothing else is fading or loading (a Save & Quit already on its way is never doubled).
        public bool HoldKickForTest;                    // the audit looks at a verdict without being sent to the start menu (then cancels it)
        public string PendingKickForTest { get { return pendingKick; } }
        public void CancelKickForTest() { pendingKick = null; kickDue = false; leaderWatch.Reset(); }
        void KickTick()
        {
            if (HoldKickForTest) return;
            if (!WorldSync.InGameOrLoading()) { Log("party: " + pendingKick + "; I am already out of the world"); pendingKick = null; kickDue = true; return; }
            bool busy = (Singleton<FadeScreen>.Exists && FadeScreen.FadeInProgress) || SaveSystem.loadingScene || !Singleton<PauseManager>.Exists || PauseManager.ResetRequested;
            if (busy && Time.realtimeSinceStartup - pendingKickSince < 8f) return;
            string why = pendingKick; pendingKick = null;
            Kicks++; LastKickWhy = why; kickDue = true;
            Log("party: " + why + ": " + LeaderWatch.Message + ". Back to the start menu (my own save is not touched, nothing is sent to the leader)");
            CoopUI.Toast(LeaderWatch.Message);
            CloseChat(); if (joinOpen || hostMenuOpen) CloseCoopPage();
            if (Singleton<PauseManager>.Exists) PauseManager.ResetLevel();
        }

        void OnApplicationQuit()
        {
            // the game may still refuse to close (PlayerProfile blocks quitting while a plant grows): only a quit that goes ahead is announced
            bool goes = !Singleton<PlayerProfile>.Exists || PlayerProfile.CanApplicationQuit;
            if (goes && WorldSync.InGameOrLoading()) SendLeftWorld("the game is closing");
        }

        // ---- the screen and the hang guard --------------------------------------------------------------------------------------------

        bool screenGuardDone; float nextScreenCheck;
        void ScreenGuardTick()
        {
            if (screenGuardDone) return;
            float t = Time.realtimeSinceStartup;
            if (t > 25f) { screenGuardDone = true; return; }
            if (t < nextScreenCheck) return;
            nextScreenCheck = t + 1f;
            int w, h; var desk = Screen.currentResolution;
            if (!ScreenRules.Windowed(Screen.fullScreen, File.Exists(Here("coop-fullscreen.txt")), Screen.width, Screen.height, desk.width, desk.height, out w, out h)) return;
            Screen.SetResolution(w, h, false);
            Log("screen: the game started fullscreen; switched to a " + w + "x" + h + " window (coop-fullscreen.txt next to the exe allows fullscreen)");
        }

        public static bool WatchdogFromAwake;
        void StartWatchdog()
        {
            if (Watchdog.Main != null) return;
            var dog = new Watchdog(Here(logName), 5000);
            dog.HangFile = Here("coop-hang.txt");
            if (!File.Exists(Here("coop-nohangguard.txt"))) dog.Guard(15000, 60000, 120000, dog.ReleaseScreen, Watchdog.EndGame);
            Watchdog.Main = dog;
            dog.Start();
        }
    }
}
