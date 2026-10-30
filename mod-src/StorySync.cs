using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    // Cutscenes and story flags for everybody in the party.
    //  - whoever sees a cutscene start (island reveal, island connection, flower, the first nutrient, the computer endings), everybody gets it
    //  - story flags (trunk reached island 2, first seed sent, all seeds found...) reach everybody, so the leader's save gets them too
    //  - the LEADER skipping a cutscene or the computer's text skips it for everybody; a guest's skip only skips their own view
    // Only the start of a sequence and the leader's skip travel; once started, a sequence runs by itself on each PC with the game's own code.
    // Guests take story messages only from the leader; the leader checks a guest's message, plays it and passes it on (the origin is kept).
    // It rides on the shared-world path, so a race (Speedrun.Isolated) shares none of it and a player not in my world yet changes nothing.
    public static class StorySync
    {
        public const byte Msg = 16;              // [16][op][origin u64][seq u32][name str][arg str]
        public const int MinBytes = 16, MaxBytes = 300;
        public const byte OpFlag = 1, OpStart = 2, OpSkip = 3, OpScroll = 4, OpOutro = 5, OpNutrient = 7;
        public const float StartWait = 30f;      // a cutscene that cannot start yet (pause menu, load, another cutscene) waits this long
        public const float SkipWait = 10f;       // a leader's skip waits this long for my copy of the cutscene (or text) to catch up
        public const float OutroWait = 200f;     // a computer ending waits for the one before it (the first ending's credits take 80 s)
        public const float MirrorMax = 120f;     // a cutscene started for a friend that still runs after this is ended (never left holding Save & Quit)
        public const string OutroFirst = "FIRST", OutroAccept = "ACCEPT", OutroDeny = "DENY";

        // Story flags that are copied as they are. Never on it: a cutscene's own "done" flag, FirstNutrientCutscene, FinalSequenceViewed,
        // openingCutsceneComplete, 1stEndingViewed, AcceptedNewGamePlus (the receiver's own sequence adds those, so it is never told "already seen"
        // before it saw it), and anything personal (jumps, grabs, deaths, hints). FlowerGrowth is the flower cutscene's done-flag (the game also sets it in
        // GrowBeanstalkFlower.StartFlowerGrowth): copied as a flag it would stop that cutscene, and with it the flower and its seeds, from ever playing
        // on a PC that got the flag first. It travels as the cutscene's start instead; each PC's own flower sets it.
        public static readonly string[] Flags = {
            "TrunkReachedIsland2", "TrunkReachedIsland3", "TrunkReachedAsteroid", "TrunkSmashedAsteroid", "TrunkReachedSpaceShip",
            "TriggerFinalTrunkGrowthCutscene", "TrunkReachedFlowerCutsceneHeight", "teleportunlocked", "TeleportedSeed", "FoundAllSeeds" };
        public static readonly string[] NeverFlags = { "FlowerGrowth", "FirstNutrientCutscene", "FinalSequenceViewed", "openingCutsceneComplete", "1stEndingViewed", "AcceptedNewGamePlus", "c1", "c2", "c3", "c4" };
        public static string TestFlagA, TestFlagB;   // the audit's own made-up flags count as story flags while set

        public static bool FlagAllowed(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == TestFlagA || name == TestFlagB) return true;
            return Array.IndexOf(Flags, name) >= 0;
        }

        // ---- the message -------------------------------------------------------------------------------------------------------------
        public struct Story { public byte Op; public ulong Origin; public uint Seq; public string Name, Arg; }

        public static byte[] Build(byte op, ulong origin, uint seq, string name, string arg)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Msg); w.Write(op); w.Write(origin); w.Write(seq); w.Write(name ?? ""); w.Write(arg ?? "");
            return ms.ToArray();
        }

        public static bool Read(byte[] m, out Story s)
        {
            s = new Story();
            if (m == null || m.Length < MinBytes || m.Length > MaxBytes || m[0] != Msg) return false;
            try
            {
                var r = new BinaryReader(new MemoryStream(m));
                r.ReadByte(); s.Op = r.ReadByte(); s.Origin = r.ReadUInt64(); s.Seq = r.ReadUInt32(); s.Name = r.ReadString(); s.Arg = r.ReadString();
                if (r.BaseStream.Position != m.Length) return false;               // nothing extra after it
                return s.Op == OpFlag || s.Op == OpStart || s.Op == OpSkip || s.Op == OpScroll || s.Op == OpOutro || s.Op == OpNutrient;
            }
            catch (Exception) { return false; }
        }

        // each thing once per world: a flag, a cutscene's start, its skip, an ending
        public static string Key(byte op, string name, string arg) { return op + "|" + name + (op == OpScroll ? "|" + arg : ""); }

        // ---- state ---------------------------------------------------------------------------------------------------------------------
        static ulong MyId { get { return SharedWorld.MyId; } }
        static ulong LeaderId { get { return SharedWorld.LeaderId; } }
        static bool IAmLeader { get { return LeaderId != 0 && LeaderId == MyId; } }
        static bool IAmGuest { get { return LeaderId != 0 && LeaderId != MyId; } }

        static int applying;
        public static bool Applying { get { return applying > 0; } }      // a friend's story is being applied: what the game does meanwhile is theirs, not mine
        static readonly List<byte[]> outbox = new List<byte[]>();
        static readonly HashSet<string> seen = new HashSet<string>();
        static readonly HashSet<int> mirrored = new HashSet<int>();         // cutscenes started for a friend (never reported as mine)
        static readonly HashSet<int> skippedForLeader = new HashSet<int>(); // cutscenes the leader's skip ended here
        static uint seq;
        static CutsceneController lastActive; static bool skipSent; static float mirrorSince = -1f;
        static bool wasInWorld;
        public static int StatSent, StatApplied, StatIgnored, StatRefused, StatDupes, StatDropped, StatMirrorEnded;
        public static string LastLog = "";

        class Pending { public Story S; public float Since; }
        static readonly List<Pending> pending = new List<Pending>();
        public static int PendingCount { get { return pending.Count; } }

        // ---- reflection into the game ------------------------------------------------------------------------------------------------
        const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly FieldInfo fRunning = typeof(CutsceneController).GetField("m_IsRunning", Priv);
        static readonly FieldInfo fSkipped = typeof(CutsceneController).GetField("m_Skipped", Priv);
        static readonly FieldInfo fTriggers = typeof(CutsceneController).GetField("m_Triggers", Priv);
        static readonly MethodInfo mEnd = typeof(CutsceneController).GetMethod("EndCutscene", Priv);
        static readonly FieldInfo fScSkipping = typeof(ScriptScroller).GetField("m_Skipping", Priv);
        static readonly FieldInfo fScCanSkip = typeof(ScriptScroller).GetField("m_CanSkip", Priv);
        static readonly FieldInfo fScPrint = typeof(ScriptScroller).GetField("m_PrintMode", Priv);
        static readonly FieldInfo fScTextMode = typeof(ScriptScroller).GetField("m_TextMode", Priv);
        static readonly MethodInfo mScLineFeed = typeof(ScriptScroller).GetMethod("AddLineFeed", Priv, null, Type.EmptyTypes, null);
        static readonly MethodInfo mScAddText = typeof(ScriptScroller).GetMethod("AddText", Priv, null, new[] { typeof(string) }, null);
        static readonly MethodInfo mNutrient = typeof(NutrientCutscene).GetMethod("StartNutrientCutscene", Priv);
        static readonly FieldInfo fLeverEvent = typeof(TeleporterLever).GetField("m_onEnable", Priv);
        static readonly FieldInfo fInputDepth = typeof(InputManager).GetField("m_StackDepth", Priv);

        public static bool Running(CutsceneController c) { return c != null && fRunning != null && (bool)fRunning.GetValue(c); }
        public static bool Skipped(CutsceneController c) { return c != null && fSkipped != null && (bool)fSkipped.GetValue(c); }
        public static int InputDepth() { return Singleton<InputManager>.Exists && fInputDepth != null ? (int)fInputDepth.GetValue(Singleton<InputManager>.Instance) : -1; }
        public static bool ReflectionOk { get { return fRunning != null && fSkipped != null && fTriggers != null && mEnd != null && fScSkipping != null && fScCanSkip != null && fScPrint != null && fScTextMode != null && mScLineFeed != null && mScAddText != null && mNutrient != null && fLeverEvent != null; } }

        static void Log(string m) { LastLog = m; CoopBehaviour.Log("story: " + m); }

        // ---- hooks into the game's events ----------------------------------------------------------------------------------------------
        static readonly SceneProfile.StringDelegate flagHook = OnFlag;
        static readonly BudTeleporter.OnTeleportObject seedHook = OnTeleport;
        static readonly UnityEngine.Events.UnityAction<bool> leftHook = OnLeft, rightHook = OnRight;
        static OutroLever outro; static TeleporterLever leftHooked, rightHooked; static float nextOutroFind;
        static ScriptScroller[] scrollers = new ScriptScroller[0]; static readonly Dictionary<int, bool> wasSkipping = new Dictionary<int, bool>(); static float nextScrollerFind;

        static void Hook()
        {
            SceneProfile.m_OnNewAccomplishment = (SceneProfile.StringDelegate)Delegate.Combine(Delegate.Remove(SceneProfile.m_OnNewAccomplishment, flagHook), flagHook);
            BudTeleporter.m_onTeleportObject = (BudTeleporter.OnTeleportObject)Delegate.Combine(Delegate.Remove(BudTeleporter.m_onTeleportObject, seedHook), seedHook);
            if (outro == null && Time.realtimeSinceStartup >= nextOutroFind)
            {
                nextOutroFind = Time.realtimeSinceStartup + 2f;
                outro = (OutroLever)UnityEngine.Object.FindObjectOfType(typeof(OutroLever));
            }
            if (outro != null)
            {
                if (outro.m_leftLeverScript != null && outro.m_leftLeverScript != leftHooked) { Listen(outro.m_leftLeverScript, leftHook); leftHooked = outro.m_leftLeverScript; }
                if (outro.m_rightLeverScript != null && outro.m_rightLeverScript != rightHooked) { Listen(outro.m_rightLeverScript, rightHook); rightHooked = outro.m_rightLeverScript; }
            }
        }

        static void Listen(TeleporterLever lever, UnityEngine.Events.UnityAction<bool> a)
        {
            var ev = fLeverEvent == null ? null : fLeverEvent.GetValue(lever) as UnityEngine.Events.UnityEvent<bool>;
            if (ev != null) { ev.RemoveListener(a); ev.AddListener(a); }
        }

        static void OnFlag(string name)
        {
            try
            {
                if (Applying || name == null) return;
                if (FlagAllowed(name)) Send(OpFlag, name, "");
                else if (name == "FirstNutrientCutscene") Send(OpNutrient, name, "");
            }
            catch (Exception e) { Log("flag hook " + e.GetType().Name); }
        }

        // a lever pulled here (a mirrored ending calls the game's lever code directly, so this never fires for a friend's pull)
        static void OnLeft(bool on) { try { if (on && !Applying) { string st = OutroStage(true); if (st != null) Send(OpOutro, st, "lever"); } } catch (Exception e) { Log("lever hook " + e.GetType().Name); } }
        static void OnRight(bool on) { try { if (on && !Applying) { string st = OutroStage(false); if (st != null) Send(OpOutro, st, "lever"); } } catch (Exception e) { Log("lever hook " + e.GetType().Name); } }

        // which ending a lever pull starts, from the flags (as OutroLever.OnLeftLeverPull / OnRightLeverPull decide it)
        public static string OutroStage(bool left)
        {
            if (SceneProfile.HasAccomplishment("AcceptedNewGamePlus")) return null;
            bool first = SceneProfile.HasAccomplishment("1stEndingViewed");
            if (left) return first ? OutroAccept : (SceneProfile.HasAccomplishment("TeleportedSeed") ? OutroFirst : null);
            return first ? OutroDeny : null;
        }

        // a star seed sent after the first ending accepts the mission, like the left lever
        static void OnTeleport(Transform t)
        {
            try
            {
                if (Applying || t == null || !Label.Has(t.gameObject, "BeanstalkSeed")) return;
                if (SceneProfile.HasAccomplishment("1stEndingViewed") && !SceneProfile.HasAccomplishment("AcceptedNewGamePlus")) Send(OpOutro, OutroAccept, "seed");
            }
            catch (Exception e) { Log("seed hook " + e.GetType().Name); }
        }

        static void Send(byte op, string name, string arg)
        {
            if (!seen.Add(Key(op, name, arg))) return;
            outbox.Add(Build(op, MyId, ++seq, name, arg));
            StatSent++;
            Log("sent " + OpName(op) + " " + name + (string.IsNullOrEmpty(arg) ? "" : " (" + arg + ")"));
        }

        public static string OpName(byte op)
        {
            switch (op) { case OpFlag: return "flag"; case OpStart: return "cutscene start"; case OpSkip: return "cutscene skip"; case OpScroll: return "text skip"; case OpOutro: return "ending"; case OpNutrient: return "first nutrient"; }
            return "op " + op;
        }

        // ---- every frame (from SharedWorld.Tick, so never in a race and never before a guest has the leader's world) ---------------
        public static void Tick(ITransport broadcast)
        {
            try { Hook(); Track(true); TickPending(); }
            catch (Exception e) { Log("tick " + e.GetType().Name + " " + e.Message); }
            foreach (var m in outbox) broadcast.SendReliable(m);
            outbox.Clear();
        }

        // not sharing now (a guest not yet in the leader's world, or a race): nothing noticed meanwhile is ever sent
        public static void Hold()
        {
            outbox.Clear(); pending.Clear();
            try { Track(false); } catch (Exception) { }
        }

        public static void ForgetWorld()
        {
            seen.Clear(); pending.Clear(); mirrored.Clear(); skippedForLeader.Clear(); wasSkipping.Clear(); outbox.Clear();
            lastActive = null; skipSent = false; mirrorSince = -1f; outro = null; leftHooked = null; rightHooked = null; scrollers = new ScriptScroller[0];
        }

        // what started or was skipped here since the last frame
        static void Track(bool report)
        {
            bool inWorld = WorldSync.InWorld();
            if (inWorld != wasInWorld) { wasInWorld = inWorld; ForgetWorld(); }      // another world (or the start screen): nothing of the old one counts
            if (!inWorld) return;
            var a = CutsceneController.s_ActiveCutscene;
            if (a != lastActive)
            {
                if (a != null && report && !mirrored.Contains(a.GetInstanceID()) && !Applying) Send(OpStart, a.m_AcomplishmentOnComplete, AnimOf(a));
                lastActive = a; skipSent = false;
                mirrorSince = a != null && mirrored.Contains(a.GetInstanceID()) ? Time.realtimeSinceStartup : -1f;
            }
            // the leader skipped: everybody skips (a guest's skip stays on the guest's screen)
            if (a != null && report && IAmLeader && !skipSent && Skipped(a) && !skippedForLeader.Contains(a.GetInstanceID())) { skipSent = true; Send(OpSkip, a.m_AcomplishmentOnComplete, ""); }
            // a cutscene started for a friend never runs forever (its pause block would hide Save & Quit)
            if (a != null && mirrorSince >= 0f && Time.realtimeSinceStartup - mirrorSince > MirrorMax && Running(a) && !Skipped(a))
            {
                StatMirrorEnded++; mirrorSince = -1f;
                Log("the cutscene " + a.m_AcomplishmentOnComplete + " started for a friend still runs after " + MirrorMax + " s; ending it");
                SkipLikePlayer(a);
            }
            // the computer's typed text
            if (Time.realtimeSinceStartup >= nextScrollerFind) { nextScrollerFind = Time.realtimeSinceStartup + 2f; scrollers = (ScriptScroller[])UnityEngine.Object.FindObjectsOfType(typeof(ScriptScroller)); }
            foreach (var s in scrollers)
            {
                if (s == null || fScSkipping == null) continue;
                bool now = (bool)fScSkipping.GetValue(s), was;
                wasSkipping.TryGetValue(s.GetInstanceID(), out was);
                if (now == was) continue;
                wasSkipping[s.GetInstanceID()] = now;
                if (now && report && IAmLeader && !Applying) Send(OpScroll, s.gameObject.name, TextName(s));
            }
        }

        // the animation the game would play: that of the trigger nearest the camera (CutsceneController.IsNearestTrigger)
        static string AnimOf(CutsceneController c)
        {
            var list = fTriggers == null ? null : fTriggers.GetValue(c) as List<CutsceneTrigger>;
            if (list == null || list.Count == 0 || Camera.main == null) return "";
            CutsceneTrigger best = null; float bestD = float.MaxValue; Vector3 at = Camera.main.transform.position;
            foreach (var t in list) { if (t == null) continue; float d = (t.transform.position - at).sqrMagnitude; if (d < bestD) { bestD = d; best = t; } }
            return best != null ? best.m_AnimToTrigger ?? "" : "";
        }

        // the text without its language ending (OutroScriptFrench and OutroScript are the same text)
        public static string TextName(ScriptScroller s)
        {
            string n = s.m_Text != null ? s.m_Text.name : "";
            foreach (var l in new[] { "French", "German", "Spanish", "Japanese", "Chinese", "Portuguese", "Russian" })
                if (n.EndsWith(l)) return n.Substring(0, n.Length - l.Length);
            return n;
        }

        // ---- a friend's message --------------------------------------------------------------------------------------------------------
        public static void Handle(byte[] msg, ulong from)
        {
            Story s;
            if (!Read(msg, out s)) { StatRefused++; return; }
            if (s.Origin == MyId) return;                                            // mine, passed back by the leader
            if (!WorldSync.InWorld()) { StatIgnored++; return; }                     // the story belongs to a world: none is loaded here
            bool fromLeader = LeaderId != 0 && from == LeaderId;
            if (IAmGuest && !fromLeader) { StatIgnored++; return; }                 // guests take the story only from the leader
            if ((s.Op == OpSkip || s.Op == OpScroll) && (LeaderId == 0 || s.Origin != LeaderId || !fromLeader))
            {
                StatIgnored++;                                                       // only the leader's skip skips for everybody
                return;
            }
            if (IAmLeader && from != 0 && SharedWorld.MayTakeFrom != null && !SharedWorld.MayTakeFrom(from)) { StatIgnored++; return; }   // not in my world
            if (s.Op == OpFlag && !FlagAllowed(s.Name)) { StatRefused++; Log("refused the flag " + s.Name + " (not a story flag)"); return; }
            if (s.Op == OpOutro && s.Name != OutroFirst && s.Name != OutroAccept && s.Name != OutroDeny) { StatRefused++; return; }
            if (!seen.Add(Key(s.Op, s.Name, s.Arg))) { StatDupes++; return; }
            Log("got " + OpName(s.Op) + " " + s.Name + " from " + s.Origin);
            if (!TryApply(s, false)) pending.Add(new Pending { S = s, Since = Time.realtimeSinceStartup });
            if (IAmLeader && from != 0) { outbox.Add(msg); Log("passed " + OpName(s.Op) + " " + s.Name + " on to everybody"); }    // the origin stays theirs
        }

        static void TickPending()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                bool done;
                try { done = TryApply(p.S, Time.realtimeSinceStartup - p.Since > Wait(p.S.Op)); }
                catch (Exception e) { done = true; Log("applying " + OpName(p.S.Op) + " " + p.S.Name + " failed: " + e.GetType().Name + " " + e.Message); }
                if (done) pending.RemoveAt(i);
            }
        }

        static float Wait(byte op) { return op == OpOutro ? OutroWait : (op == OpSkip || op == OpScroll ? SkipWait : StartWait); }

        // a cutscene can start here now: in the world, nothing loading or fading, no pause menu, no other cutscene, not respawning
        public static string CannotStart()
        {
            if (!WorldSync.InWorld()) return "not in the world";
            if (SaveSystem.loadingScene) return "loading";
            if (CutsceneController.s_ActiveCutscene != null) return "another cutscene runs";
            if (Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive) return "a menu is open";
            if (Singleton<FadeScreen>.Exists && FadeScreen.FadeInProgress) return "the screen is fading";
            var root = ExtendedMonoBehaviour.PlayerRoot;
            if (root != null && !root.activeSelf) return "respawning";
            return null;
        }
        public static bool TestIgnoreMenus;      // the audit's own menus may be up while it tests
        public static string TestBlock;          // the audit pretends a cutscene cannot start here now

        static string Blocked() { if (TestBlock != null) return TestBlock; string why = CannotStart(); return TestIgnoreMenus && why == "a menu is open" ? null : why; }

        // true: done with it (applied, or no longer needed, or given up)
        static bool TryApply(Story s, bool late)
        {
            switch (s.Op)
            {
                case OpFlag:
                    if (!SceneProfile.HasAccomplishment(s.Name)) { AddFlag(s.Name); StatApplied++; }
                    return true;
                case OpStart: return ApplyStart(s, late);
                case OpSkip: return ApplySkip(s, late);
                case OpScroll: return ApplyScroll(s, late);
                case OpOutro: return ApplyOutro(s, late);
                case OpNutrient: return ApplyNutrient(late);
            }
            return true;
        }

        // the game's own AddAccomplishment, marked as a friend's (the flag hook and speedrun splits ignore it; a cutscene it starts is the friend's)
        static void AddFlag(string name) { Apply(delegate { SceneProfile.AddAccomplishment(name); }); }

        delegate void Step();
        static void Apply(Step step)
        {
            var before = CutsceneController.s_ActiveCutscene;
            applying++;
            try { step(); }
            finally { applying--; }
            var after = CutsceneController.s_ActiveCutscene;
            if (after != null && after != before) Mirror(after);
        }

        static void Mirror(CutsceneController c)
        {
            mirrored.Add(c.GetInstanceID());
            if (c == CutsceneController.s_ActiveCutscene) { lastActive = c; skipSent = false; mirrorSince = Time.realtimeSinceStartup; }
        }

        public static CutsceneController FindCutscene(string doneFlag)
        {
            if (string.IsNullOrEmpty(doneFlag)) return null;
            foreach (CutsceneController c in UnityEngine.Object.FindObjectsOfType(typeof(CutsceneController))) if (c != null && c.m_AcomplishmentOnComplete == doneFlag) return c;
            return null;
        }

        static bool ApplyStart(Story s, bool late)
        {
            var c = FindCutscene(s.Name);
            if (c == null) { StatDropped++; Log("no cutscene " + s.Name + " here; not played"); return true; }
            if (c.Completed) { Log("the cutscene " + s.Name + " was seen here already"); return true; }
            if (c == CutsceneController.s_ActiveCutscene || Running(c)) { Mirror(c); return true; }    // it started here too (the same flag set it off)
            string why = Blocked();
            if (why != null)
            {
                if (!late) return false;
                // it could not be shown in time: the save keeps step with the friend's anyway
                StatDropped++; AddFlag(s.Name);
                Log("the cutscene " + s.Name + " could not be shown (" + why + " for " + StartWait + " s); marked as seen");
                return true;
            }
            Apply(delegate { c.SignalTrigger(s.Arg ?? ""); });
            if (CutsceneController.s_ActiveCutscene == c) { Mirror(c); StatApplied++; Log("mirrored the cutscene " + s.Name); }
            else { StatDropped++; Log("the cutscene " + s.Name + " did not start here (the game refused it)"); }
            return true;
        }

        static bool ApplySkip(Story s, bool late)
        {
            // its start still waits here: it is not shown at all, only marked as seen
            for (int i = pending.Count - 1; i >= 0; i--)
                if (pending[i].S.Op == OpStart && pending[i].S.Name == s.Name) { pending.RemoveAt(i); AddFlag(s.Name); Log("the leader skipped " + s.Name + " before it started here; marked as seen"); return true; }
            var c = FindCutscene(s.Name);
            if (c != null && c == CutsceneController.s_ActiveCutscene && Running(c))
            {
                if (Skipped(c)) return true;            // already being skipped here
                skippedForLeader.Add(c.GetInstanceID());
                SkipLikePlayer(c); StatApplied++;
                Log("the leader skipped the cutscene " + s.Name + "; skipped here too");
                return true;
            }
            if (c != null && c.Completed) return true;
            return late;        // my copy may still be a moment behind
        }

        // What CutsceneController.CheckForSkip does when Jump is released: fade out, end the cutscene, fade back in.
        public static void SkipLikePlayer(CutsceneController c)
        {
            if (c == null || !Running(c)) return;
            c.StartCoroutine(SkipRoutine(c));
        }

        static IEnumerator SkipRoutine(CutsceneController c)
        {
            c.m_ScreenOverlay = 0f;
            FadeScreen.ForceFade(new Color(0f, 0f, 0f, 0f));
            fSkipped.SetValue(c, true);
            FadeScreen.FadeOut(2f);
            yield return c.StartCoroutine(FadeScreen.Yield());
            yield return c.StartCoroutine((IEnumerator)mEnd.Invoke(c, null));
            FadeScreen.FadeIn(2f);
            yield return c.StartCoroutine(FadeScreen.Yield());
        }

        static bool ApplyScroll(Story s, bool late)
        {
            ScriptScroller target = null;
            foreach (ScriptScroller sc in UnityEngine.Object.FindObjectsOfType(typeof(ScriptScroller)))
            {
                if (sc == null || sc.gameObject.name != s.Name || !sc.enabled || sc.IsComplete) continue;
                if (!string.IsNullOrEmpty(s.Arg) && TextName(sc) != s.Arg) continue;

                if ((bool)fScCanSkip.GetValue(sc)) { target = sc; break; }
            }
            if (target == null) return late;            // not that far yet here (the text says when it may be skipped): wait a little
            SkipText(target); StatApplied++;
            Log("the leader skipped the text " + s.Arg + "; skipped here too");
            return true;
        }

        // What ScriptScroller does when Jump is released while it may be skipped.
        public static void SkipText(ScriptScroller sc)
        {
            fScSkipping.SetValue(sc, true);
            fScCanSkip.SetValue(sc, false);
            fScPrint.SetValue(sc, false);
            sc.m_CurrWait = 0f;
            fScTextMode.SetValue(sc, 2);
            mScLineFeed.Invoke(sc, null);
            sc.PlayWarningSound();
            mScAddText.Invoke(sc, new object[] { "> break" });
            mScLineFeed.Invoke(sc, null);
            mScLineFeed.Invoke(sc, null);
            wasSkipping[sc.GetInstanceID()] = true;     // not reported as mine
        }

        static bool ApplyOutro(Story s, bool late)
        {
            if (outro == null) outro = (OutroLever)UnityEngine.Object.FindObjectOfType(typeof(OutroLever));
            if (outro == null) { StatDropped++; Log("no computer here; the ending " + s.Name + " is not played"); return true; }
            if (SceneProfile.HasAccomplishment("AcceptedNewGamePlus")) return true;
            bool first = SceneProfile.HasAccomplishment("1stEndingViewed");
            string why = Blocked();
            if (s.Name == OutroFirst)
            {
                if (first) return true;
                if (why != null) { if (late) { StatDropped++; Log("the first ending could not be shown (" + why + ")"); } return late; }
                Apply(delegate { outro.OnLeftLeverPull(); });
                StatApplied++; Log("a friend pulled the lever: the first ending plays here too");
                return true;
            }
            // accept or deny follow the first ending: wait until its credits are over here
            if (!first || !Singleton<SceneProfile>.Instance.m_pauseEnabled || why != null)
            {
                if (late) { StatDropped++; Log("the ending " + s.Name + " could not be shown (" + (why ?? "the first ending is not over here") + ")"); }
                return late;
            }
            if (s.Name == OutroAccept) Apply(delegate { outro.OnLeftLeverPull(); });
            else Apply(delegate { outro.OnRightLeverPull(); });
            StatApplied++; Log("a friend chose " + s.Name + ": that ending plays here too");
            return true;
        }

        static bool ApplyNutrient(bool late)
        {
            if (SceneProfile.HasAccomplishment("FirstNutrientCutscene")) return true;
            var n = (NutrientCutscene)UnityEngine.Object.FindObjectOfType(typeof(NutrientCutscene));
            string why = n == null ? "no nutrient cutscene here" : Blocked();
            if (why != null)
            {
                if (!late) return false;
                StatDropped++; AddFlag("FirstNutrientCutscene");
                Log("the first nutrient cutscene could not be shown (" + why + "); marked as seen");
                return true;
            }
            Apply(delegate { mNutrient.Invoke(n, null); });
            StatApplied++; Log("a friend connected the first island: its cutscene plays here too");
            return true;
        }

        // ---- for the audit ---------------------------------------------------------------------------------------------------------------
        public static void TestReset() { ForgetWorld(); applying = 0; wasInWorld = WorldSync.InWorld(); nextScrollerFind = 0f; nextOutroFind = 0f; }
        public static void TestAgePending(float seconds) { foreach (var p in pending) p.Since -= seconds; }
        public static string TestAnimOf(CutsceneController c) { return AnimOf(c); }
        public static void TestLever(bool left) { if (left) OnLeft(true); else OnRight(true); }
        public static bool TestHooked { get { return outro == null || (leftHooked == outro.m_leftLeverScript && rightHooked == outro.m_rightLeverScript); } }
        public static void TestTrack() { Track(true); }
        public static bool TestSeen(byte op, string name, string arg) { return seen.Contains(Key(op, name, arg)); }
        public static bool IsMirrored(CutsceneController c) { return c != null && mirrored.Contains(c.GetInstanceID()); }
    }
}
