using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    // Audit checks for cutscenes and story flags in a party (StorySync): the message, the story flag list, flags from the leader (and only from it),
    // the leader passing a guest's flag on, a friend's cutscene played here through the game's own trigger, the leader's skip (and never a guest's),
    // Save & Quit and the input lock given back afterwards, a skip that arrives before the cutscene could start, the leader's text skip, the computer
    // endings, and speedrun splits that never count a friend's flag. Fake peers only. Group "story" (audit\check-groups.txt).
    public partial class SelfTest
    {
        const ulong StoryLeader = 76561199000000101UL, StoryGuest = 76561199000000102UL, StoryStranger = 76561199000000103UL;

        List<StorySync.Story> StoryOut(int from)
        {
            var l = new List<StorySync.Story>();
            for (int i = Math.Max(0, from); i < sink.Messages.Count; i++) { StorySync.Story s; if (StorySync.Read(sink.Messages[i], out s)) l.Add(s); }
            return l;
        }

        static int CountOut(List<StorySync.Story> l, byte op, string name, ulong origin)
        {
            int n = 0;
            foreach (var s in l) if (s.Op == op && (name == null || s.Name == name) && s.Origin == origin) n++;
            return n;
        }

        static IEnumerator RealWait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;       // a cutscene may stop game time
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        static byte[] StoryMsg(byte op, ulong origin, string name, string arg) { return StorySync.Build(op, origin, 7, name, arg); }

        // ---- CS-WIRE: the message and the gate (no world needed) -------------------------------------------------------------------

        void CheckStoryPure()
        {
            var bad = new List<string>();
            byte[] m = StorySync.Build(StorySync.OpStart, StoryLeader, 42, "Island2Reveal", "Reveal");
            StorySync.Story s;
            if (!StorySync.Read(m, out s) || s.Op != StorySync.OpStart || s.Origin != StoryLeader || s.Seq != 42 || s.Name != "Island2Reveal" || s.Arg != "Reveal") bad.Add("round trip");
            var cut = new byte[m.Length - 1]; Buffer.BlockCopy(m, 0, cut, 0, cut.Length);
            if (StorySync.Read(cut, out s)) bad.Add("a cut-off message was read");
            var extra = new byte[m.Length + 1]; Buffer.BlockCopy(m, 0, extra, 0, m.Length);
            if (StorySync.Read(extra, out s)) bad.Add("a message with extra bytes was read");
            var odd = (byte[])m.Clone(); odd[1] = 99;
            if (StorySync.Read(odd, out s)) bad.Add("an unknown op was read");
            var big = StorySync.Build(StorySync.OpFlag, StoryLeader, 1, new string('x', 400), "");
            if (StorySync.Read(big, out s)) bad.Add("an oversized message was read");
            if (MessageGate.Classify(m, false) != Route.Shared) bad.Add("gate: a story message is not routed the shared-world way (" + MessageGate.Classify(m, false) + ")");
            if (MessageGate.Classify(big, true) != Route.Ignore) bad.Add("gate: an oversized story message is not refused");
            if (MessageGate.Classify(new byte[] { StorySync.Msg, 1, 2 }, true) != Route.Ignore) bad.Add("gate: a short story message is not refused");
            Check("CS-WIRE", "the story message survives a round trip; cut-off, padded, unknown and oversized ones are refused (also by the gate, which sends it the shared-world way: never in a race, never from a player not in my world)",
                bad.Count == 0, bad.Count == 0 ? m.Length + " bytes" : string.Join("; ", bad.ToArray()));

            bad.Clear();
            foreach (var n in StorySync.NeverFlags) if (StorySync.FlagAllowed(n)) bad.Add(n + " is copied as a plain flag");
            foreach (var n in new[] { "jump", "climbed", "PlayerDied", "bouncedoffleaf", "grabbedLeaf", "ShardCollected" }) if (StorySync.FlagAllowed(n)) bad.Add("the personal flag " + n + " is copied");
            foreach (var n in new[] { "TrunkReachedIsland2", "TrunkReachedFlowerCutsceneHeight", "TeleportedSeed", "FoundAllSeeds" }) if (!StorySync.FlagAllowed(n)) bad.Add(n + " is not copied");
            if (!StorySync.ReflectionOk) bad.Add("the game's cutscene, text, nutrient or lever fields were not found");
            Check("CS-FLAG-LIST", "only story flags are copied: never a cutscene's \"already seen\" flag (the flower, FirstNutrientCutscene, FinalSequenceViewed, the endings, the intro) and never a personal one; the game's fields used for starts and skips exist",
                bad.Count == 0, bad.Count == 0 ? StorySync.Flags.Length + " story flags" : string.Join("; ", bad.ToArray()));
        }

        // ---- in the world ---------------------------------------------------------------------------------------------------------------

        IEnumerator TestStoryLive()
        {
            ulong me = SharedWorld.MyId;
            var mayTake = SharedWorld.MayTakeFrom;
            string A = "CoopAuditStoryA", B = "CoopAuditStoryB";
            StorySync.TestFlagA = A; StorySync.TestFlagB = B; StorySync.TestIgnoreMenus = true; StorySync.TestBlock = null;
            SceneProfile.RemoveAccomplishment(A); SceneProfile.RemoveAccomplishment(B);
            int hookA = 0;
            SceneProfile.StringDelegate count = delegate (string n) { if (n == A) hookA++; };
            SceneProfile.m_OnNewAccomplishment = (SceneProfile.StringDelegate)Delegate.Combine(SceneProfile.m_OnNewAccomplishment, count);
            try
            {
                // ---- CS-FLAG: as a guest ----
                SharedWorld.LeaderId = StoryLeader; StorySync.TestReset();
                yield return null;
                int at = sink.Messages.Count;
                bool hadJump = SceneProfile.HasAccomplishment("jump"); int refused = StorySync.StatRefused, ignored = StorySync.StatIgnored, dupes = StorySync.StatDupes;
                StorySync.Handle(StoryMsg(StorySync.OpFlag, StoryLeader, "jump", ""), StoryLeader);
                bool jumpOk = SceneProfile.HasAccomplishment("jump") == hadJump && StorySync.StatRefused == refused + 1;
                StorySync.Handle(StoryMsg(StorySync.OpFlag, StoryGuest, A, ""), StoryGuest);
                bool guestOk = !SceneProfile.HasAccomplishment(A) && StorySync.StatIgnored == ignored + 1;
                StorySync.Handle(StoryMsg(StorySync.OpFlag, StoryLeader, A, ""), StoryLeader);
                bool added = SceneProfile.HasAccomplishment(A) && hookA == 1;
                StorySync.Handle(StoryMsg(StorySync.OpFlag, StoryLeader, A, ""), StoryLeader);
                bool once = hookA == 1 && StorySync.StatDupes == dupes + 1;
                yield return null; yield return null;
                var outA = StoryOut(at);
                SceneProfile.AddAccomplishment(B);                     // my own story flag: told once
                SceneProfile.RemoveAccomplishment(B); SceneProfile.AddAccomplishment(B);
                yield return null; yield return null;
                var outB = StoryOut(at);
                Check("CS-FLAG", "a guest takes a story flag only from the leader, once (the game's flag event fires once), refuses a personal flag, sends nothing back; its own story flag goes out once",
                    jumpOk && guestOk && added && once && outA.Count == 0 && CountOut(outB, StorySync.OpFlag, B, me) == 1 && outB.Count == 1,
                    "personal refused " + jumpOk + ", from a guest ignored " + guestOk + ", from the leader added " + added + ", repeat ignored " + once + ", echoes " + outA.Count + ", my flag sent " + CountOut(outB, StorySync.OpFlag, B, me) + " of " + outB.Count + " messages");

                // ---- CS-RELAY: as the leader ----
                SharedWorld.LeaderId = me; StorySync.TestReset(); SceneProfile.RemoveAccomplishment(A); hookA = 0;
                SharedWorld.MayTakeFrom = id => id == StoryGuest;
                yield return null;
                at = sink.Messages.Count; ignored = StorySync.StatIgnored;
                StorySync.Handle(StoryMsg(StorySync.OpFlag, StoryGuest, A, ""), StoryGuest);
                bool took = SceneProfile.HasAccomplishment(A) && hookA == 1;
                StorySync.Handle(StoryMsg(StorySync.OpFlag, StoryStranger, "TrunkReachedIsland2", ""), StoryStranger);
                bool strangerOk = StorySync.StatIgnored == ignored + 1;
                StorySync.Handle(StoryMsg(StorySync.OpSkip, StoryGuest, "Island2Reveal", ""), StoryGuest);
                bool guestSkipOk = StorySync.StatIgnored == ignored + 2;
                yield return null; yield return null;
                var relay = StoryOut(at);
                Check("CS-RELAY", "the leader takes a guest's story flag into its save and passes it on once with the guest as origin; a player not in its world and a guest's skip change nothing",
                    took && strangerOk && guestSkipOk && relay.Count == 1 && CountOut(relay, StorySync.OpFlag, A, StoryGuest) == 1,
                    "taken " + took + ", stranger ignored " + strangerOk + ", guest skip ignored " + guestSkipOk + ", passed on " + relay.Count + " (origin " + (relay.Count > 0 ? relay[0].Origin.ToString() : "-") + ")");

                // ---- CS-SR: speedrun splits and race isolation ----
                SharedWorld.LeaderId = StoryLeader; StorySync.TestReset(); SceneProfile.RemoveAccomplishment(A); SceneProfile.RemoveAccomplishment(B);
                yield return null;
                var tap = Speedrun.Tap; int tapA = 0, tapB = 0;
                Speedrun.Tap = e => { if (e.Name == A) tapA++; if (e.Name == B) tapB++; };
                try
                {
                    StorySync.Handle(StoryMsg(StorySync.OpFlag, StoryLeader, A, ""), StoryLeader);   // a friend's flag
                    SceneProfile.AddAccomplishment(B);                                                  // mine
                }
                finally { Speedrun.Tap = tap; }
                SceneProfile.RemoveAccomplishment(B);
                at = sink.Messages.Count;
                SceneProfile.AddAccomplishment(B);
                SharedWorld.Hold();                                       // what a racer (or a guest not yet in the leader's world) does instead of sharing
                yield return null; yield return null;
                var held = StoryOut(at);
                Check("CS-SR", "a friend's story flag is never a speedrun split of mine (my own still is); while not sharing (a race) nothing noticed is sent",
                    tapA == 0 && tapB == 1 && held.Count == 0, "friend's flag split " + tapA + " times, mine " + tapB + "; sent while holding " + held.Count);

                // ---- the cutscenes ----
                yield return StartCoroutine(TestStoryCutscenes(me));
                yield return StartCoroutine(TestStoryText(me));
                TestStoryEndings(me);
            }
            finally
            {
                SceneProfile.m_OnNewAccomplishment = (SceneProfile.StringDelegate)Delegate.Remove(SceneProfile.m_OnNewAccomplishment, count);
                SceneProfile.RemoveAccomplishment(A); SceneProfile.RemoveAccomplishment(B);
                StorySync.TestFlagA = null; StorySync.TestFlagB = null; StorySync.TestIgnoreMenus = false; StorySync.TestBlock = null;
                SharedWorld.LeaderId = 0; SharedWorld.MayTakeFrom = mayTake; StorySync.TestReset();
            }
        }

        static CutsceneController PickCutscene(List<CutsceneController> all)
        {
            CutsceneController best = null; int bestScore = -1;
            foreach (var c in all)
            {
                string n = c.m_AcomplishmentOnComplete ?? "";
                if (n == "" || n.Contains("Intro") || n.Contains("Outro") || n.Contains("Flower") || n.Contains("Nutrient")) continue;
                int score = (c.Completed ? 4 : 0) + (n.Contains("Reveal") ? 2 : n.Contains("Connection") ? 1 : 0);
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        static bool PlayerKinematic() { var rb = ExtendedMonoBehaviour.PlayerRigidBody; return rb != null && rb.isKinematic; }

        // the Save & Quit block not counting growing plant parts: the audit's plants grow on by themselves while the story is tested, and a part
        // that starts or stops growing meanwhile has nothing to do with cutscenes
        static int QuitNoPlants() { return QuitGuard.QuitBlock() - QuitGuard.PlantHolds(); }

        string Blocks() { return "quit " + QuitGuard.QuitBlock() + " (plants " + QuitGuard.PlantHolds() + "), pause " + QuitGuard.PauseBlock() + ", input " + StorySync.InputDepth() + ", time " + Time.timeScale + ", frozen " + PlayerKinematic(); }

        // a cutscene that was set off by the one just ended is ended too (the audit leaves nothing running)
        IEnumerator EndAnyCutscene()
        {
            for (int i = 0; i < 3 && CutsceneController.s_ActiveCutscene != null; i++)
            {
                Note("story: ending the cutscene " + CutsceneController.s_ActiveCutscene.m_AcomplishmentOnComplete + " that followed");
                StorySync.SkipLikePlayer(CutsceneController.s_ActiveCutscene);
                float end = Time.realtimeSinceStartup + 8f;
                while (CutsceneController.s_ActiveCutscene != null && Time.realtimeSinceStartup < end) yield return null;
            }
            float f = Time.realtimeSinceStartup + 5f;
            while (Singleton<FadeScreen>.Exists && FadeScreen.FadeInProgress && Time.realtimeSinceStartup < f) yield return null;
        }

        IEnumerator TestStoryCutscenes(ulong me)
        {
            var all = new List<CutsceneController>();
            foreach (CutsceneController c in UnityEngine.Object.FindObjectsOfType(typeof(CutsceneController))) if (c != null) all.Add(c);
            var names = new HashSet<string>(); var dup = new List<string>(); var copied = new List<string>();
            foreach (var c in all)
            {
                Note("story: cutscene " + c.name + " done-flag " + c.m_AcomplishmentOnComplete + (c.Completed ? " (seen)" : "") + ", length " + c.m_AnimationLength + " s, anim '" + StorySync.TestAnimOf(c) + "'"
                    + (c.m_PauseGameWhenActive ? ", stops time" : c.m_LockWalksterWhenActive ? ", freezes the player" : ""));
                if (!names.Add(c.m_AcomplishmentOnComplete)) dup.Add(c.m_AcomplishmentOnComplete);
                if (StorySync.FlagAllowed(c.m_AcomplishmentOnComplete)) copied.Add(c.m_AcomplishmentOnComplete);
            }
            Check("CS-CENSUS", "the level's cutscenes are found by their done-flags, each unique, none of them copied as a plain flag", all.Count > 0 && dup.Count == 0 && copied.Count == 0,
                all.Count + " cutscenes" + (dup.Count > 0 ? "; shared done-flags " + string.Join(",", dup.ToArray()) : "") + (copied.Count > 0 ? "; copied as flags " + string.Join(",", copied.ToArray()) : ""));
            if (CutsceneController.s_ActiveCutscene != null) yield return StartCoroutine(EndAnyCutscene());

            var cs = PickCutscene(all);
            if (cs == null) { Note("story: no cutscene in this level to play; CS-MIRROR, CS-SKIP-AUTH, CS-LOCAL, CS-ORDER skipped"); yield break; }
            string done = cs.m_AcomplishmentOnComplete, anim = StorySync.TestAnimOf(cs);
            bool hadDone = cs.Completed;
            Note("story: testing with the cutscene " + done + (hadDone ? " (seen in this save; its flag is put back afterwards)" : " (not seen in this save; its flag is removed again afterwards)"));
            int q0 = QuitNoPlants(), p0 = QuitGuard.PauseBlock(), d0 = StorySync.InputDepth(); float ts0 = Time.timeScale; bool k0 = PlayerKinematic();
            string base0 = Blocks();
            try
            {
                // ---- CS-MIRROR: the leader's cutscene plays here; a guest's skip does nothing; the leader's skip ends it ----
                SharedWorld.LeaderId = StoryLeader; StorySync.TestReset(); SceneProfile.RemoveAccomplishment(done);
                yield return null;
                int at = sink.Messages.Count;
                StorySync.Handle(StoryMsg(StorySync.OpStart, StoryLeader, done, anim), StoryLeader);
                float until = Time.realtimeSinceStartup + 3f;
                while (CutsceneController.s_ActiveCutscene != cs && Time.realtimeSinceStartup < until) yield return null;
                bool started = CutsceneController.s_ActiveCutscene == cs && StorySync.Running(cs) && StorySync.IsMirrored(cs);
                string during = Blocks();
                yield return StartCoroutine(RealWait(0.5f));
                int echoes = StoryOut(at).Count;
                Check("CS-MIRROR", "a cutscene the leader saw starts here through the game's own trigger and nothing is sent back", started && echoes == 0,
                    "running " + started + " (" + StorySync.LastLog + "), sent back " + echoes + "; while running " + during);

                StorySync.Handle(StoryMsg(StorySync.OpSkip, StoryGuest, done, ""), StoryGuest);
                yield return StartCoroutine(RealWait(0.4f));
                bool stillRuns = StorySync.Running(cs) && !StorySync.Skipped(cs);
                StorySync.Handle(StoryMsg(StorySync.OpSkip, StoryLeader, done, ""), StoryLeader);
                until = Time.realtimeSinceStartup + 8f;
                while ((StorySync.Running(cs) || CutsceneController.s_ActiveCutscene == cs) && Time.realtimeSinceStartup < until) yield return null;
                bool ended = started && !StorySync.Running(cs) && CutsceneController.s_ActiveCutscene != cs && cs.Completed;
                yield return StartCoroutine(EndAnyCutscene());
                bool clean = QuitNoPlants() == q0 && QuitGuard.PauseBlock() == p0 && StorySync.InputDepth() == d0 && Mathf.Approximately(Time.timeScale, ts0) && PlayerKinematic() == k0;
                Check("CS-SKIP-AUTH", "a guest's skip does not skip the cutscene for others; the leader's skip ends it here too and marks it seen", started && stillRuns && ended,
                    "after the guest's skip still running " + stillRuns + ", after the leader's ended " + ended);
                Check("CS-BLOCKS", "after a friend's cutscene is skipped, Save & Quit, the pause menu, the input lock, game time and the player are all given back", started && clean,
                    "before " + base0 + "; after " + Blocks());

                // ---- CS-LOCAL: my own cutscene goes out once; as the leader my skip goes out once ----
                SharedWorld.LeaderId = me; StorySync.TestReset(); SceneProfile.RemoveAccomplishment(done);
                yield return null;
                at = sink.Messages.Count;
                cs.SignalTrigger(anim);
                yield return null; yield return null;
                bool mine = CutsceneController.s_ActiveCutscene == cs;
                yield return StartCoroutine(RealWait(0.3f));
                StorySync.SkipLikePlayer(cs);                         // as Jump released would
                yield return null; yield return null;
                var outs = StoryOut(at);
                until = Time.realtimeSinceStartup + 8f;
                while ((StorySync.Running(cs) || CutsceneController.s_ActiveCutscene == cs) && Time.realtimeSinceStartup < until) yield return null;
                yield return StartCoroutine(EndAnyCutscene());
                outs = StoryOut(at);
                bool clean2 = QuitNoPlants() == q0 && QuitGuard.PauseBlock() == p0 && StorySync.InputDepth() == d0 && Mathf.Approximately(Time.timeScale, ts0) && PlayerKinematic() == k0;
                int starts = CountOut(outs, StorySync.OpStart, done, me), skips = CountOut(outs, StorySync.OpSkip, done, me);
                Check("CS-LOCAL", "a cutscene that starts here is told to everybody once (with its animation); the leader's skip is told once; everything is given back after",
                    mine && starts == 1 && skips == 1 && clean2 && !StorySync.Running(cs),
                    "started " + mine + ", start sent " + starts + ", skip sent " + skips + " of " + outs.Count + " messages; after " + Blocks());

                // ---- CS-ORDER: the leader's skip before my copy could start; a start that never could ----
                SharedWorld.LeaderId = StoryLeader; StorySync.TestReset(); SceneProfile.RemoveAccomplishment(done); StorySync.TestBlock = "a test block";
                yield return null;
                StorySync.Handle(StoryMsg(StorySync.OpStart, StoryLeader, done, anim), StoryLeader);
                yield return StartCoroutine(RealWait(0.3f));
                bool waits = StorySync.PendingCount == 1 && !StorySync.Running(cs);
                StorySync.Handle(StoryMsg(StorySync.OpSkip, StoryLeader, done, ""), StoryLeader);
                yield return null;
                bool skippedBefore = StorySync.PendingCount == 0 && cs.Completed && !StorySync.Running(cs);
                StorySync.TestReset(); SceneProfile.RemoveAccomplishment(done);
                yield return null;
                StorySync.Handle(StoryMsg(StorySync.OpStart, StoryLeader, done, anim), StoryLeader);
                yield return null;
                StorySync.TestAgePending(StorySync.StartWait + 1f);
                yield return null; yield return null;
                bool gaveUp = StorySync.PendingCount == 0 && cs.Completed && !StorySync.Running(cs);
                StorySync.TestBlock = null;
                yield return StartCoroutine(EndAnyCutscene());       // a cutscene its done-flag set off
                bool clean3 = QuitNoPlants() == q0 && QuitGuard.PauseBlock() == p0 && StorySync.InputDepth() == d0;
                Check("CS-ORDER", "a friend's cutscene that cannot start yet waits; the leader's skip arriving first cancels it (marked seen, nothing played); one that never could start is marked seen after the wait; nothing is left blocked",
                    waits && skippedBefore && gaveUp && clean3, "waited " + waits + ", skipped before it started " + skippedBefore + ", gave up " + gaveUp + "; " + Blocks());
            }
            finally
            {
                StorySync.TestBlock = null;
                if (hadDone) SceneProfile.AddAccomplishment(done); else SceneProfile.RemoveAccomplishment(done);
            }
        }

        static readonly FieldInfo scSkipping = typeof(ScriptScroller).GetField("m_Skipping", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo scCanSkip = typeof(ScriptScroller).GetField("m_CanSkip", BindingFlags.NonPublic | BindingFlags.Instance);

        // ---- CS-SCROLL: the leader's skip of the computer's text --------------------------------------------------------------------
        IEnumerator TestStoryText(ulong me)
        {
            SharedWorld.LeaderId = StoryLeader; StorySync.TestReset();
            yield return null;
            int ignored = StorySync.StatIgnored;
            StorySync.Handle(StoryMsg(StorySync.OpScroll, StoryGuest, "CoopAuditNoText", ""), StoryGuest);
            bool guestIgnored = StorySync.StatIgnored == ignored + 1 && StorySync.PendingCount == 0;
            StorySync.Handle(StoryMsg(StorySync.OpScroll, StoryLeader, "CoopAuditNoText", ""), StoryLeader);
            yield return null;
            bool waits = StorySync.PendingCount == 1;
            StorySync.TestAgePending(StorySync.SkipWait + 1f);
            yield return null; yield return null;
            bool dropped = StorySync.PendingCount == 0;

            var outro = (OutroLever)UnityEngine.Object.FindObjectOfType(typeof(OutroLever));
            var sc = outro != null ? outro.m_endingScript : null;
            if (sc == null || sc.enabled || scSkipping == null || scCanSkip == null)
            {
                Check("CS-SCROLL", "a text skip from a guest is ignored; the leader's waits for the text to catch up, then gives up", guestIgnored && waits && dropped,
                    "guest ignored " + guestIgnored + ", waits " + waits + ", dropped after the wait " + dropped);
                Note("story: the computer's text is " + (sc == null ? "not in this level" : "showing in this save") + "; the live text skip was not tried");
                yield break;
            }
            TextAsset text = sc.m_Text; bool running = ScriptScroller.m_Running;
            bool notYet = false, applied = false; int sent = 0;
            try
            {
                sc.m_Text = outro.m_1stCompleteMessage; sc.Clear(); sc.enabled = true; scCanSkip.SetValue(sc, false);
                StorySync.Handle(StoryMsg(StorySync.OpScroll, StoryLeader, sc.gameObject.name, StorySync.TextName(sc)), StoryLeader);
                notYet = !(bool)scSkipping.GetValue(sc) && StorySync.PendingCount == 1;       // the text does not allow it yet: it waits (looked at at once: the text itself may allow it a frame later)
                scCanSkip.SetValue(sc, true);
                yield return null; yield return null;
                applied = (bool)scSkipping.GetValue(sc) && StorySync.PendingCount == 0;

                // as the leader, my own skip goes out once
                SharedWorld.LeaderId = me; StorySync.TestReset();
                scSkipping.SetValue(sc, false);
                yield return null; yield return null;
                int at = sink.Messages.Count;
                scSkipping.SetValue(sc, true);
                yield return null; yield return null;
                sent = CountOut(StoryOut(at), StorySync.OpScroll, sc.gameObject.name, me);
            }
            finally
            {
                sc.enabled = false; sc.m_Text = text; sc.Clear(); ScriptScroller.m_Running = running;
            }
            Check("CS-SCROLL", "a text skip from a guest is ignored; the leader's waits until the text may be skipped here, then skips it like Jump does; the leader's own skip is told once",
                guestIgnored && waits && dropped && notYet && applied && sent == 1,
                "guest ignored " + guestIgnored + ", waits/drops " + waits + "/" + dropped + ", held back while not skippable " + notYet + ", skipped " + applied + ", my skip sent " + sent);
        }

        // ---- CS-OUTRO and CS-NUTRIENT: the computer endings and the first island's cutscene -----------------------------------------
        void TestStoryEndings(ulong me)
        {
            var outro = (OutroLever)UnityEngine.Object.FindObjectOfType(typeof(OutroLever));
            SharedWorld.LeaderId = StoryLeader; StorySync.TestReset();
            int refused = StorySync.StatRefused;
            StorySync.Handle(StoryMsg(StorySync.OpOutro, StoryLeader, "EXPLODE", ""), StoryLeader);
            bool badRefused = StorySync.StatRefused == refused + 1;
            if (outro == null) { Check("CS-OUTRO", "an unknown ending is refused", badRefused, "no computer in this level"); }
            else
            {
                SharedWorld.LeaderId = me; StorySync.TestReset();
                StorySync.Tick(new Sink());                              // hooks the levers
                int at = sink.Messages.Count;
                var probe = new Sink();
                string want = StorySync.OutroStage(true);
                StorySync.TestLever(true); StorySync.TestLever(true);
                StorySync.Tick(probe);
                int got = 0; foreach (var m in probe.Messages) { StorySync.Story s; if (StorySync.Read(m, out s) && s.Op == StorySync.OpOutro && s.Origin == me) got++; }
                bool lever = got == (want == null ? 0 : 1) && StorySync.TestHooked;
                // a friend's ending already seen here does nothing
                bool seenOk = true; string seenNote = "not tried (this save has not seen the first ending)";
                if (SceneProfile.HasAccomplishment("1stEndingViewed") || SceneProfile.HasAccomplishment("AcceptedNewGamePlus"))
                {
                    SharedWorld.LeaderId = StoryLeader; StorySync.TestReset();
                    int d0 = StorySync.InputDepth(); bool en = outro.m_endingScript != null && outro.m_endingScript.enabled; var t0 = outro.m_endingScript != null ? outro.m_endingScript.m_Text : null;
                    StorySync.Handle(StoryMsg(StorySync.OpOutro, StoryLeader, StorySync.OutroFirst, "lever"), StoryLeader);
                    seenOk = StorySync.PendingCount == 0 && StorySync.InputDepth() == d0 && (outro.m_endingScript == null || (outro.m_endingScript.enabled == en && outro.m_endingScript.m_Text == t0));
                    seenNote = "a first ending seen already: " + (seenOk ? "nothing played" : "something changed");
                }
                Check("CS-OUTRO", "an unknown ending is refused; my lever pull is told once, as the ending the game picks from the flags (" + (want ?? "none") + "); a friend's ending seen here already does nothing",
                    badRefused && lever && seenOk, "refused " + badRefused + ", lever told " + got + " (hooked " + StorySync.TestHooked + "), " + seenNote);
                sink.Messages.RemoveRange(at, sink.Messages.Count - at);
            }

            if (SceneProfile.HasAccomplishment("FirstNutrientCutscene"))
            {
                SharedWorld.LeaderId = StoryLeader; StorySync.TestReset();
                int d0 = StorySync.InputDepth(); bool pe = SceneProfile.PauseEnabled;
                StorySync.Handle(StoryMsg(StorySync.OpNutrient, StoryLeader, "FirstNutrientCutscene", ""), StoryLeader);
                Check("CS-NUTRIENT", "the first island's cutscene from a friend, already seen here, does not play again or freeze anything",
                    StorySync.PendingCount == 0 && StorySync.InputDepth() == d0 && SceneProfile.PauseEnabled == pe, "input " + d0 + " -> " + StorySync.InputDepth() + ", pause " + pe + " -> " + SceneProfile.PauseEnabled);
            }
            else
            {
                // not seen in this save: only the waiting is tried (playing it would change the save)
                SharedWorld.LeaderId = StoryLeader; StorySync.TestReset(); StorySync.TestBlock = "a test block";
                int d0 = StorySync.InputDepth();
                StorySync.Handle(StoryMsg(StorySync.OpNutrient, StoryLeader, "FirstNutrientCutscene", ""), StoryLeader);
                bool waits = StorySync.PendingCount == 1 && StorySync.InputDepth() == d0 && !SceneProfile.HasAccomplishment("FirstNutrientCutscene");
                StorySync.TestBlock = null; StorySync.TestReset();
                Check("CS-NUTRIENT", "the first island's cutscene from a friend waits while it cannot be shown here (nothing frozen meanwhile)", waits, "waiting " + waits + "; the save has not seen it, so it was not played");
            }
            SharedWorld.LeaderId = 0; StorySync.TestReset();
        }
    }
}
