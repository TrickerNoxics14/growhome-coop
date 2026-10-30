using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CoopMod
{
    // Audit checks for vines in a party: "is my walkster on this plant" measured from the body, the grow lease (a guest pressing E on a nub and
    // riding a vine: the leader takes it, passes it on, connects its islands, a guest copy never undoes the leader's vines), guests that send only
    // what they grew, and plant collision after the mod updated, rebuilt or pruned a plant. Group "plants" (audit\check-groups.txt).
    public partial class SelfTest
    {
        const byte TypePlant2 = 12, TypeGrow = 15;

        // [12][id][sender][number][echo of][echo number]...
        static bool ReadPlant2(byte[] m, out string id, out int number, out ulong echoOf)
        {
            id = null; number = -1; echoOf = 0;
            try
            {
                var r = new BinaryReader(new MemoryStream(m));
                if (r.ReadByte() != TypePlant2) return false;
                id = r.ReadString(); r.ReadUInt64(); number = r.ReadInt32(); echoOf = r.ReadUInt64();
                return true;
            }
            catch (Exception) { return false; }
        }

        // [15][what][id][player][number]
        static bool ReadGrow(byte[] m, out byte what, out string id, out ulong player)
        {
            what = 0; id = null; player = 0;
            try
            {
                var r = new BinaryReader(new MemoryStream(m));
                if (r.ReadByte() != TypeGrow) return false;
                what = r.ReadByte(); id = r.ReadString(); player = r.ReadUInt64();
                return true;
            }
            catch (Exception) { return false; }
        }

        // index in the sink of the first numbered plant for id from position `after` (-1: none)
        int Plant2Index(string id, int after)
        {
            for (int i = Math.Max(0, after); i < sink.Messages.Count; i++) { string pid; int n; ulong e; if (ReadPlant2(sink.Messages[i], out pid, out n, out e) && pid == id) return i; }
            return -1;
        }

        int GrowIndex(byte what, string id, ulong player)
        {
            for (int i = 0; i < sink.Messages.Count; i++) { byte w; string pid; ulong p; if (ReadGrow(sink.Messages[i], out w, out pid, out p) && w == what && pid == id && p == player) return i; }
            return -1;
        }

        static PlantBranch LongestBranch(PlantRoot r)
        {
            PlantBranch best = null;
            foreach (var b in r.GetComponentsInChildren<PlantBranch>(true)) if (b != null && (best == null || b.m_Length > best.m_Length)) best = b;
            return best;
        }

        // ---- PLANT-ONPLANT-BODY -----------------------------------------------------------------------------------------------------

        void CheckOnPlantBody(PlantRoot root)
        {
            string id = SharedWorld.PlantId(root);
            var me = GameObject.Find("Walkster");
            var walker = me != null ? me.GetComponentInChildren<BipedWalkScript>() : null;
            if (walker == null || walker.body == null) { Check("PLANT-ONPLANT-BODY", "my walkster exists to measure from", false, "no walkster"); return; }
            SharedWorld.TestPlayerOnPlant = null;
            try
            {
                Vector3? measured = SharedWorld.BodyPosition();
                float off = measured.HasValue ? (measured.Value - walker.body.position).magnitude : 999f;
                Check("PLANT-ONPLANT-BODY", "on-the-plant is measured from my body, not from the Walkster object that stays at the level start", off < 0.01f,
                    "measured " + measured + ", body " + walker.body.position + ", Walkster object " + me.transform.position);

                int real = SharedWorld.TestOnPlantState(id);
                if (real == SharedWorld.HoldingPlant) { Note("PLANT-ONPLANT-BODY: my walkster holds this plant right now; the near/far cases use a moved body anyway"); }

                // a part of the plant far from the Walkster object
                Transform part = null; float best = -1f;
                foreach (var st in root.GetComponentsInChildren<PlantStem>(true))
                {
                    float d = (st.transform.position - me.transform.position).sqrMagnitude;
                    if (d > best) { best = d; part = st.transform; }
                }
                if (part == null) { Check("PLANT-ONPLANT-BODY", "the plant has a stem to stand on", false, "no stems"); return; }
                SharedWorld.TestBodyOverride = part.position + Vector3.up * 1f;
                int near = SharedWorld.TestOnPlantState(id);
                SharedWorld.TestBodyOverride = me.transform.position + Vector3.up * 5000f;     // body far away, the Walkster object where it always is
                int far = SharedWorld.TestOnPlantState(id);
                Check("PLANT-ONPLANT-BODY", "a body near a plant part counts as on the plant (the Walkster object " + Mathf.Sqrt(best).ToString("F0") + " m away); a body far away does not",
                    near != SharedWorld.OffPlant && (far == SharedWorld.OffPlant || real == SharedWorld.HoldingPlant), "near: " + near + ", far: " + far);

                // riding a vine counts wherever the body is
                var b = LongestBranch(root);
                if (b != null && real != SharedWorld.HoldingPlant)
                {
                    bool was = b.m_PlayerLockedOn; b.m_PlayerLockedOn = true;
                    int riding = SharedWorld.TestOnPlantState(id);
                    b.m_PlayerLockedOn = was;
                    Check("PLANT-ONPLANT-BODY", "riding a vine of the plant counts as holding it, wherever the body is", riding == SharedWorld.HoldingPlant, "state " + riding);
                }
            }
            finally { SharedWorld.TestBodyOverride = null; SharedWorld.TestPlayerOnPlant = null; }
        }

        // ---- PLANT-GROW-LEASE, PLANT-LEASE-ISLAND, PLANT-LEASE-TIMEOUT: I lead, a guest grows a vine ------------------------------------

        IEnumerator TestGrowLease(PlantRoot root)
        {
            string id = SharedWorld.PlantId(root);
            ulong me = SharedWorld.MyId;
            const ulong G = Friend;
            byte[] raw0 = null;
            if (!Try("PLANT-GROW-LEASE", "read the plant", delegate { raw0 = SharedWorld.TestRaw(root); })) yield break;
            Note("---- vines: I lead, a guest grows a vine on " + id);
            var mayTake = SharedWorld.MayTakeFrom; SharedWorld.MayTakeFrom = null;
            SharedWorld.TestPlayerOnPlant = false;
            SharedWorld.LeaderId = me; SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest();
            SharedWorld.SetPlantNumberForTest(id, 5); SharedWorld.SetRealNumberForTest(id, 0); SharedWorld.MarkSeenForTest(root);

            // the guest starts on my number 2 (my trunk crept on since: numbers 3-5 had no real growth)
            sink.Messages.Clear();
            Try("PLANT-GROW-LEASE", "the guest says it grows a vine", delegate { SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowStart, root, G, 2), G); });
            bool leased = SharedWorld.TestLeaseHolder(id) == G;
            float fG = root.TrunkGrowthFraction;
            bool grew = Stimulate(root);                              // my own copy changes during the lease
            SharedWorld.MarkDirtyForTest(id);
            yield return new WaitForSeconds(3f);
            int sentDuring = 0; for (int i = Plant2Index(id, 0); i >= 0; i = Plant2Index(id, i + 1)) sentDuring++;
            int applied0 = SharedWorld.StatApplied, stale0 = SharedWorld.StatStale;
            Try("PLANT-GROW-LEASE", "the guest's vine plant built on my old number 2", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(root, G, raw0, 2), G); });
            bool taken = SharedWorld.StatApplied == applied0 + 1 && SharedWorld.StatStale == stale0;
            bool theirs = !grew || root.IsTrunk() == false || Mathf.Abs(root.TrunkGrowthFraction - fG) < 0.0001f;
            Check("PLANT-GROW-LEASE", "while a guest grows a vine, the leader takes the guest's plant whatever number it carries (my own trunk creeping on does not count), and sends nothing of that plant meanwhile",
                leased && taken && theirs && sentDuring == 0, "lease " + leased + ", applied " + (SharedWorld.StatApplied - applied0) + ", stale " + (SharedWorld.StatStale - stale0) + ", guest's copy " + theirs + " (my growth during the lease: " + grew + "), sent during " + sentDuring);
            yield return new WaitForSeconds(1f);
            int sentAfterTake = Plant2Index(id, 0);
            Try("PLANT-GROW-LEASE", "the guest says its vine is done", delegate { SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowEnd, root, G, 2), G); });
            int at = -1;
            yield return StartCoroutine(WaitUntil(delegate { at = Plant2Index(id, 0); return at >= 0; }, 5f, 0f));
            string pid; int number = -1; ulong echoOf = 0;
            if (at >= 0) ReadPlant2(sink.Messages[at], out pid, out number, out echoOf);
            Check("PLANT-GROW-LEASE", "once the vine is done the leader passes it on to everybody with a newer number, telling the guest it is its own",
                sentAfterTake < 0 && SharedWorld.TestLeaseHolder(id) == 0 && at >= 0 && number > 5 && echoOf == G, "sent before the end " + (sentAfterTake >= 0) + ", sent after " + (at >= 0) + ", number " + number + ", echo of " + echoOf);

            // a guest copy older than my newest REAL growth (a vine I grew) is refused: it would cut that vine off
            SharedWorld.SetRealNumberForTest(id, 7); SharedWorld.SetPlantNumberForTest(id, 8); SharedWorld.MarkSeenForTest(root);
            sink.Messages.Clear();
            int applied1 = SharedWorld.StatApplied;
            Try("PLANT-GROW-LEASE", "a guest grows on my copy 3, older than my vine (7)", delegate { SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowStart, root, G, 3), G); });
            Try("PLANT-GROW-LEASE", "its plant", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(root, G, raw0, 3), G); });
            yield return null; yield return null;
            Check("PLANT-GROW-LEASE", "a guest's vine built on a copy older than the leader's newest vine is refused (never cuts the leader's vine off) and the guest is told",
                SharedWorld.TestLeaseHolder(id) == 0 && SharedWorld.StatApplied == applied1 && GrowIndex(SharedWorld.GrowRevoked, id, G) >= 0, "lease " + SharedWorld.TestLeaseHolder(id) + ", applied " + (SharedWorld.StatApplied - applied1) + ", told " + (GrowIndex(SharedWorld.GrowRevoked, id, G) >= 0));

            // my vine (or one I took) is not sent yet: the guest builds on my last SENT number, which does not have it. Refused, my plant untouched.
            bool unsentOk = true; string unsentWhy = "";
            for (int kind = 0; kind < 3; kind++)
            {
                SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 5); SharedWorld.SetRealNumberForTest(id, 5); SharedWorld.MarkSeenForTest(root);
                if (kind == 0) SharedWorld.MarkRealChangeForTest(id);          // a vine that finished here, waiting for its send
                else if (kind == 1) SharedWorld.MarkDirtyForTest(id);          // growth seen but not sent
                else SharedWorld.ForceSend(id);                                 // a plant I took, about to be passed on
                sink.Messages.Clear();
                int appliedU = SharedWorld.StatApplied;
                SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowStart, root, G, 5), G);
                if (kind < 2) SharedWorld.Handle(SharedWorld.TestNumberedMessage(root, G, raw0, 5), G);   // (a passed-on plant: the guest's next copy carries the new number)
                yield return null; yield return null;                           // the refusal goes out with the next send (as in the case above)
                bool ok = SharedWorld.TestLeaseHolder(id) == 0 && SharedWorld.StatApplied == appliedU && GrowIndex(SharedWorld.GrowRevoked, id, G) >= 0;
                if (!ok) { unsentOk = false; unsentWhy += " case " + kind + ": lease " + SharedWorld.TestLeaseHolder(id) + ", applied " + (SharedWorld.StatApplied - appliedU) + ";"; }
            }
            // a lease granted before: growth I took meanwhile from somebody else still wins over the holder's plant
            SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 5); SharedWorld.SetRealNumberForTest(id, 0); SharedWorld.MarkSeenForTest(root);
            SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowStart, root, G, 5), G);
            bool grantedU = SharedWorld.TestLeaseHolder(id) == G;
            SharedWorld.MarkRealChangeForTest(id);
            int appliedL = SharedWorld.StatApplied;
            SharedWorld.Handle(SharedWorld.TestNumberedMessage(root, G, raw0, 5), G);
            bool cutRefused = grantedU && SharedWorld.StatApplied == appliedL && SharedWorld.TestLeaseHolder(id) == 0;
            yield return null;
            Check("PLANT-GROW-LEASE", "a guest's vine is refused while the leader has growth it has not sent yet (a finished vine, a changed plant, or a plant it took and passes on), so it can never cut that growth off",
                unsentOk && cutRefused, (unsentOk ? "3 refusals ok" : unsentWhy) + "; during a lease: granted " + grantedU + ", holder's plant refused " + cutRefused);

            // two guests: the second one is refused while the first grows
            SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 5); SharedWorld.SetRealNumberForTest(id, 0); SharedWorld.MarkSeenForTest(root); sink.Messages.Clear();
            SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowStart, root, G, 5), G);
            SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowStart, root, G + 1, 5), G + 1);
            int applied2 = SharedWorld.StatApplied;
            SharedWorld.Handle(SharedWorld.TestNumberedMessage(root, G + 1, raw0, 5), G + 1);
            yield return null; yield return null;
            Check("PLANT-GROW-LEASE", "one grower per plant: a second guest growing the same plant is refused and told",
                SharedWorld.TestLeaseHolder(id) == G && SharedWorld.StatApplied == applied2 && GrowIndex(SharedWorld.GrowRevoked, id, G + 1) >= 0, "holder " + SharedWorld.TestLeaseHolder(id) + ", second applied " + (SharedWorld.StatApplied - applied2));

            // timeout: no end within the lease time, normal rules again
            SharedWorld.TestAgeLease(id, SharedWorld.LeaseTime + 11f); SharedWorld.ClearForcedForTest(); SharedWorld.TestTickLeases();
            bool cleared = SharedWorld.TestLeaseHolder(id) == 0 && SharedWorld.ForcedForTest(id);
            SharedWorld.MarkSeenForTest(root);
            int stale3 = SharedWorld.StatStale, applied3 = SharedWorld.StatApplied;
            Try("PLANT-LEASE-TIMEOUT", "an old guest plant after the lease ran out", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(root, G, raw0, 2), G); });
            Check("PLANT-LEASE-TIMEOUT", "a vine that never ends frees the plant after the lease time; then a guest's old copy is refused again (normal leader rules)",
                cleared && SharedWorld.StatStale == stale3 + 1 && SharedWorld.StatApplied == applied3, "cleared " + cleared + ", stale " + (SharedWorld.StatStale - stale3) + ", applied " + (SharedWorld.StatApplied - applied3));

            // island: the guest's vine connected an island; on the leader it must become attached and counted
            yield return StartCoroutine(TestLeaseIsland(root, G));

            SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest(); SharedWorld.LeaderId = 0; SharedWorld.MayTakeFrom = mayTake; SharedWorld.SetPlantNumberForTest(id, 0); SharedWorld.TestPlayerOnPlant = null;
        }

        IEnumerator TestLeaseIsland(PlantRoot root, ulong G)
        {
            string id = SharedWorld.PlantId(root);
            NutrientProvider np = null;
            foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider))) if (n.GetComponent<SaveInfo>() != null && (np == null || !n.m_attached)) np = n;
            if (np == null || !Singleton<PlayerAnalytics>.Exists) { Check("PLANT-LEASE-ISLAND", "an island exists to test with", false, "none"); yield break; }
            var pa = Singleton<PlayerAnalytics>.Instance;
            bool wasAttached = np.m_attached;
            byte[] rawG = null;
            np.m_attached = true;                                     // the guest's copy: its vine reached the island
            bool ok = Try("PLANT-LEASE-ISLAND", "read the guest's plant", delegate { rawG = SharedWorld.TestRaw(root); });
            np.m_attached = false;                                    // my copy does not know it yet
            if (!ok) { np.m_attached = wasAttached; yield break; }
            int c0 = pa.m_nutrientIslandsAttched, connected0 = SharedWorld.StatIslandsOnLeader;
            SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 5); SharedWorld.SetRealNumberForTest(id, 0); SharedWorld.MarkSeenForTest(root);
            Try("PLANT-LEASE-ISLAND", "the guest grows", delegate { SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowStart, root, G, 5), G); });
            Try("PLANT-LEASE-ISLAND", "its plant with the island attached", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(root, G, rawG, 5), G); });
            Try("PLANT-LEASE-ISLAND", "its end", delegate { SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowEnd, root, G, 5), G); });
            yield return new WaitForSeconds(1f);
            bool attached = np != null && np.m_attached;
            int c1 = pa.m_nutrientIslandsAttched;
            Check("PLANT-LEASE-ISLAND", "an island a guest's vine connected is attached on the leader and counted in the leader's island counter",
                attached && c1 >= c0 + 1 && SharedWorld.StatIslandsOnLeader == connected0 + 1, "attached " + attached + ", counter " + c0 + " -> " + c1 + ", connected on the leader " + (SharedWorld.StatIslandsOnLeader - connected0));
            if (np != null) np.m_attached = wasAttached;              // the audit's world as it was
            pa.m_nutrientIslandsAttched = c0;
        }

        // ---- PLANT-LEASE-NOREVERT, PLANT-GUEST-QUIET: I am a guest ------------------------------------------------------------------

        IEnumerator TestGrowGuest(PlantRoot root)
        {
            string id = SharedWorld.PlantId(root);
            ulong me = SharedWorld.MyId, L = 76561199000000777UL;
            byte[] raw0 = null;
            if (!Try("PLANT-LEASE-NOREVERT", "read the plant", delegate { raw0 = SharedWorld.TestRaw(root); })) yield break;
            Note("---- vines: I am a guest growing a vine on " + id);
            SharedWorld.TestPlayerOnPlant = false;
            SharedWorld.LeaderId = L; SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 4); SharedWorld.MarkSeenForTest(root);

            // without a vine of mine my copy changing (the trunk creeping on) is never sent
            sink.Messages.Clear();
            bool grew = Stimulate(root);
            yield return new WaitForSeconds(4f);
            int quiet = 0; for (int i = Plant2Index(id, 0); i >= 0; i = Plant2Index(id, i + 1)) quiet++;
            Check("PLANT-GUEST-QUIET", "a guest whose copy of a plant changes without a vine of its own sends nothing (no rebuilds on the leader)", quiet == 0,
                quiet + " plants sent" + (grew ? "" : " (this plant could not be made to change; nothing to send anyway)"));

            // a vine: the leader's copy that arrives meanwhile is ignored
            var b = LongestBranch(root);
            sink.Messages.Clear();
            Try("PLANT-LEASE-NOREVERT", "pop a bud", delegate { SharedWorld.TestStartGrowing(root, b); });
            yield return null; yield return null;
            bool told = GrowIndex(SharedWorld.GrowStart, id, me) >= 0;
            float f = root.TrunkGrowthFraction; int parts = root.GetComponentsInChildren<SaveInfo>(true).Length;
            float len = b != null ? b.m_Length : 0f;
            int applied0 = SharedWorld.StatApplied, held0 = SharedWorld.StatHeldForGrow;
            byte[] older = SharedWorld.TestNumberedMessage(root, L, raw0, 9);
            Try("PLANT-LEASE-NOREVERT", "the leader's (older) copy arrives while I ride", delegate { SharedWorld.Handle(older, L); });
            yield return new WaitForSeconds(1f);
            bool same = SharedWorld.StatApplied == applied0 && SharedWorld.StatHeldForGrow == held0 + 1 && Mathf.Abs(root.TrunkGrowthFraction - f) < 0.0001f
                && root.GetComponentsInChildren<SaveInfo>(true).Length >= parts && (b == null || b.m_Length >= len - 0.01f) && SharedWorld.PlantNumberForTest(id) == 4;
            Check("PLANT-LEASE-NOREVERT", "while I grow a vine the leader's copy of that plant is not applied: my branch keeps its length and parts, growing on is fine (no snap back)",
                told && same, "told the leader " + told + ", applied " + (SharedWorld.StatApplied - applied0) + ", ignored " + (SharedWorld.StatHeldForGrow - held0) + ", parts " + parts + " -> " + root.GetComponentsInChildren<SaveInfo>(true).Length + ", length " + len.ToString("F1") + " -> " + (b != null ? b.m_Length.ToString("F1") : "-"));

            // done: the final plant goes to the leader, then the end
            SharedWorld.TestEndGrowing(id);
            int endAt = -1;
            yield return StartCoroutine(WaitUntil(delegate { endAt = GrowIndex(SharedWorld.GrowEnd, id, me); return endAt >= 0; }, 4f, 0f));
            int plantAt = Plant2Index(id, 0);
            Check("PLANT-LEASE-NOREVERT", "when my vine is done I send the final plant and then the end", endAt >= 0 && plantAt >= 0 && plantAt < endAt && !SharedWorld.TestIsGrowing(id), "plant at " + plantAt + ", end at " + endAt);

            // the leader revokes: my vine is no longer protected
            SharedWorld.TestStartGrowing(root, b);
            Try("PLANT-LEASE-NOREVERT", "the leader revokes", delegate { SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowRevoked, root, me, 0), L); });
            bool revoked = !SharedWorld.TestIsGrowing(id);
            SharedWorld.TestStartGrowing(root, b);
            SharedWorld.Handle(SharedWorld.TestGrowMessage(SharedWorld.GrowRevoked, root, me, 0), Friend);   // not from the leader: no effect
            Check("PLANT-LEASE-NOREVERT", "only the leader can revoke my vine", revoked && SharedWorld.TestIsGrowing(id), "revoked by the leader " + revoked + ", still growing after a non-leader's revoke " + SharedWorld.TestIsGrowing(id));

            SharedWorld.ClearGrowthForTest(); SharedWorld.ClearForcedForTest(); SharedWorld.LeaderId = 0; SharedWorld.SetPlantNumberForTest(id, 0); SharedWorld.TestPlayerOnPlant = null;
            sink.Messages.Clear();
        }

        // ---- PLANT-COLLIDERS --------------------------------------------------------------------------------------------------------

        static HashSet<string> PhysicsProfile(PlantRoot r)
        {
            var set = new HashSet<string>();
            foreach (var c in r.GetComponentsInChildren<Collider>(true)) set.Add(KindOf(c));
            return set;
        }

        // the body a collider moves with: its own or a parent's Rigidbody. (Collider.attachedRigidbody is null while the collider is off, and the
        // game turns stem colliders off away from the player, so it would call the same stem "no body" or "kinematic" by whether it is on.)
        static Rigidbody BodyOf(Collider c)
        {
            for (var t = c.transform; t != null; t = t.parent) { var rb = t.GetComponent<Rigidbody>(); if (rb != null) return rb; }
            return null;
        }

        static string KindOf(Collider c)
        {
            var rb = BodyOf(c);
            return c.GetType().Name + " layer " + LayerMask.LayerToName(c.gameObject.layer) + (c.isTrigger ? " trigger" : "") + (rb == null ? "" : rb.isKinematic ? " kinematic" : " dynamic");
        }

        // where a kind of collider sits (for the report): up to 3 of them, with the object holding their body
        static string WhereKind(PlantRoot r, string kinds)
        {
            var parts = new List<string>();
            foreach (var c in r.GetComponentsInChildren<Collider>(true))
            {
                if (parts.Count >= 3) break;
                if (Array.IndexOf(kinds.Split(new[] { "; " }, StringSplitOptions.None), KindOf(c)) < 0) continue;
                var rb = BodyOf(c);
                parts.Add(c.name + " under " + (c.transform.parent != null ? c.transform.parent.name : "-") + (rb != null ? " (body on " + rb.name + ")" : ""));
            }
            return string.Join(", ", parts.ToArray());
        }

        static string NotIn(HashSet<string> after, HashSet<string> before)
        {
            var bad = new List<string>();
            foreach (var s in after) if (!before.Contains(s)) bad.Add(s);
            return string.Join("; ", bad.ToArray());
        }

        // the stem nearest to my body (the guard works around me); its spot stays the same through a rebuild
        static Vector3 NearestStemSpot(PlantRoot r, out string stemId)
        {
            stemId = null;
            Vector3 body = SharedWorld.BodyPosition() ?? r.transform.position;
            Vector3 best = r.transform.position; float bd = float.MaxValue;
            foreach (var st in r.GetComponentsInChildren<PlantStem>(true))
            {
                var si = st.GetComponent<SaveInfo>();
                float d = (st.transform.position - body).sqrMagnitude;
                if (d < bd) { bd = d; best = st.transform.position; stemId = si != null && si.m_runtimeSpawned ? si.m_uniqueID : null; }
            }
            return best;
        }

        IEnumerator TestPlantColliders(PlantRoot root)
        {
            string id = SharedWorld.PlantId(root);
            byte[] raw = null;
            if (!Try("PLANT-COLLIDERS", "read the plant", delegate { raw = SharedWorld.TestRaw(root); })) yield break;
            Note("---- plant collision " + id);
            SharedWorld.TestPlayerOnPlant = false;
            var before = PhysicsProfile(root);
            string stemId; Vector3 at = NearestStemSpot(root, out stemId);
            byte[] msg = SharedWorld.TestMessage(root, Friend, raw);

            // 1. in place
            Try("PLANT-COLLIDERS", "apply in place", delegate { SharedWorld.TestApply(msg); });
            yield return new WaitForSeconds(0.6f);                    // new stems ran their Start (which turns their colliders off)
            int total, off; SharedWorld.TestGuardAt(at); off = SharedWorld.CollidersOff(at, SharedWorld.GuardRadius, out total);
            string dead = SharedWorld.DeadLinks(root);
            Check("PLANT-COLLIDERS", "after an in-place plant update every stem collider within 40 m is on after one guard tick, and nothing points at removed parts",
                total > 0 && off == 0 && dead.Length == 0, off + " of " + total + " off near " + at + (dead.Length > 0 ? "; " + dead : ""));

            // 2. rebuild (the in-place update fails once)
            int rebuilt0 = SharedWorld.StatRebuilt;
            SharedWorld.TestFailInPlaceOnce = true;
            Try("PLANT-COLLIDERS", "apply with the in-place update failing", delegate { SharedWorld.TestApply(msg); });
            SharedWorld.TestFailInPlaceOnce = false;
            yield return new WaitForSeconds(0.6f);
            SharedWorld.TestGuardAt(at); off = SharedWorld.CollidersOff(at, SharedWorld.GuardRadius, out total);
            dead = SharedWorld.DeadLinks(root);
            string wrong = NotIn(PhysicsProfile(root), before);
            Check("PLANT-COLLIDERS", "a rebuilt plant has every stem collider within 40 m on after one guard tick, the same layers and kinematic bodies as before, and no links to removed parts",
                SharedWorld.StatRebuilt == rebuilt0 + 1 && total > 0 && off == 0 && dead.Length == 0 && wrong.Length == 0,
                "rebuilt " + (SharedWorld.StatRebuilt - rebuilt0) + ", " + off + " of " + total + " off" + (dead.Length > 0 ? "; " + dead : "") + (wrong.Length > 0 ? "; new kinds: " + wrong + " at " + WhereKind(root, wrong) + "; before: " + string.Join(" | ", new List<string>(before).ToArray()) : ""));

            // 3. prune: the sender no longer has one stem
            at = NearestStemSpot(root, out stemId);
            if (stemId == null) { Note("PLANT-COLLIDERS: no grown stem near me to prune; the prune case uses the plant's first grown part"); foreach (var si in root.GetComponentsInChildren<SaveInfo>(true)) if (si.m_runtimeSpawned && si.GetComponent<PlantStem>() != null) { stemId = si.m_uniqueID; at = si.transform.position; break; } }
            if (stemId == null) { Check("PLANT-COLLIDERS", "the plant has a grown stem to prune", false, "none"); yield break; }
            byte[] drop = null;
            if (!Try("PLANT-COLLIDERS", "read the plant", delegate { drop = SharedWorld.TestMessageDropping(root, Friend, SharedWorld.TestRaw(root), stemId); })) yield break;
            Try("PLANT-COLLIDERS", "apply an update without that stem", delegate { SharedWorld.TestApply(drop); });
            bool gone = true;
            foreach (var si in root.GetComponentsInChildren<SaveInfo>(true)) if (si.m_uniqueID == stemId) gone = false;
            dead = SharedWorld.DeadLinks(root);
            yield return new WaitForSeconds(0.6f);                    // a frame of the game's own Update ran: a dead link would have thrown in SetScale
            SharedWorld.TestGuardAt(at); off = SharedWorld.CollidersOff(at, SharedWorld.GuardRadius, out total);
            Check("PLANT-COLLIDERS", "after a prune no surviving branch points at the removed stem, nothing removed stays in the game's collider queue, and the colliders around it are on",
                gone && dead.Length == 0 && off == 0, "removed " + gone + (dead.Length > 0 ? "; " + dead : "") + ", " + off + " of " + total + " off");

            // put the plant back as it was
            Try("PLANT-COLLIDERS", "put the plant back", delegate { SharedWorld.TestApply(msg); });
            yield return new WaitForSeconds(1f);

            // 4. nothing rebuilt: the guard scans at most every GuardEvery seconds (not every half second for good), and a collider left off is still fixed
            SharedWorld.TestBodyOverride = at; SharedWorld.EndGuardBoostForTest();
            int scans0 = SharedWorld.StatGuardScans;
            for (int i = 0; i < 4; i++) { SharedWorld.TestGuardTick(); yield return null; }
            int quick = SharedWorld.StatGuardScans - scans0;
            int fixed0 = SharedWorld.StatCollidersFixed;
            Collider left = SharedWorld.FirstStemColliderNear(at);
            if (left != null) left.enabled = false;
            float until = Time.realtimeSinceStartup + 3f * SharedWorld.GuardEvery + 1f;
            while (Time.realtimeSinceStartup < until && (left == null || !left.enabled)) { SharedWorld.TestGuardTick(); yield return new WaitForSeconds(0.25f); }
            bool fixedLater = left != null && left.enabled;
            if (left != null) left.enabled = true;
            SharedWorld.TestBodyOverride = null;
            Check("PLANT-COLLIDERS", "when nothing was rebuilt the collision guard runs a full scan only every " + SharedWorld.GuardEvery + " s (cheap), yet a stem collider left off near me is still turned on again",
                quick == 1 && fixedLater, "scans in 4 frames " + quick + ", left-off collider " + (left == null ? "none found" : "on again " + fixedLater + " (guard fixed " + (SharedWorld.StatCollidersFixed - fixed0) + ")"));
            SharedWorld.TestPlayerOnPlant = null;
        }
    }
}
