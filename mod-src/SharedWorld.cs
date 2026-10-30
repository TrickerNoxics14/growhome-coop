using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    // Things every player should see the same, synced as small events instead of reloading the whole world:
    //  - crystals: when someone collects one, everybody loses that crystal and the team's crystal count goes up
    //  - plants: when a plant (root + branches + stems, plus the nutrient sources attached to it) changes, that plant is sent to everyone
    // Plants have ONE owner at a time: whoever started changing it. Others only receive, so two players climbing the same plant never overwrite each other.
    // Switch the whole thing off by creating coop-noshare.txt next to the exe.
    public static class SharedWorld
    {
        const byte MsgPlant = 4, MsgShard = 5, MsgIsland = 6, MsgTeleporter = 8;
        const byte MsgPlant2 = 12;           // a plant with its number: [12][id][sender][number][echo of: player u64][echo of: their number i32][the rest as MsgPlant]
        public const byte MsgPickup = 13;    // a flower or leaf pickup was taken: [13][type][x][y][z], always 14 bytes

        // ---- the leader's plant is the real one ----
        // In a party the leader numbers every plant it sends. A guest builds on the number it last got from the leader and sends it along; the
        // leader takes a guest's plant only if it was built on the leader's newest one (else the guest's copy is out of date, and taking it would
        // remove the leader's newer growth). Whatever the leader takes, it passes on to everybody with a new number; guests take plants only from it.
        public static ulong LeaderId;                        // the party leader (0: no party, everybody equal as before)
        static readonly Dictionary<string, int> plantNumber = new Dictionary<string, int>();
        // (leader) the guest plant I took and pass on unchanged: the guest it came from knows it is its own (and keeps growing instead of being put back)
        static readonly Dictionary<string, KeyValuePair<ulong, int>> takenFrom = new Dictionary<string, KeyValuePair<ulong, int>>();
        static readonly Dictionary<string, int> takenSig = new Dictionary<string, int>();
        // (guest) the number my last sends were built on, and how my plant looked at the first of them (the one the leader takes)
        static readonly Dictionary<string, int> sentBase = new Dictionary<string, int>();
        static readonly Dictionary<string, int> sentFirstSig = new Dictionary<string, int>();
        public static int StatEchoes;
        static readonly HashSet<string> forced = new HashSet<string>();
        static float lastStaleLog = -999f;
        public static int StatStale;
        static bool IAmLeader { get { return LeaderId != 0 && LeaderId == MyId; } }
        static bool IAmGuest { get { return LeaderId != 0 && LeaderId != MyId; } }
        public static void ForceSend(string rootId) { if (rootId != null) forced.Add(rootId); }
        public static Func<ulong, bool> MayTakeFrom;         // (leader) this player plays in my world now: asked again when a waiting update finally lands
        // a different world: nothing known about the old one may be used in it (numbers, waiting updates, holds)
        public static void ForgetWorld() { plantNumber.Clear(); forced.Clear(); waiting.Clear(); owner.Clear(); dirty.Clear(); lastSig.Clear(); ForgetEchoes(); ForgetGrowth(); StorySync.ForgetWorld(); }
        static void ForgetEchoes() { takenFrom.Clear(); takenSig.Clear(); sentBase.Clear(); sentFirstSig.Clear(); }
        static void ForgetGrowth() { leases.Clear(); growing.Clear(); finishing.Clear(); realNumber.Clear(); realChange.Clear(); leaseEcho.Clear(); waitSince.Clear(); offSince.Clear(); }

        // ---- grow lease: a guest growing a vine --------------------------------------------------------------------------------------
        // Pressing E on a nub and riding the new vine happens only on the grower's PC. The grower tells the leader "I grow this plant" (start), sends its
        // plant while riding and once more when done (end). Meanwhile the leader takes that guest's copy of the plant whatever number it carries, sends
        // nothing of that plant itself and the guest ignores the leader's copies of it. After the end the leader passes the plant on to everybody.
        // Safety: a guest copy older than the leader's newest REAL growth (a vine the leader grew, or one it took) is refused and the lease is revoked,
        // so a guest can never cut a vine off the leader's plant. The leader's own trunk creeping up by itself does not count as real growth.
        public const byte MsgGrow = 15;      // [15][what: 1 start, 2 end, 3 revoked][plant id][player u64][number i32]
        public const byte GrowStart = 1, GrowEnd = 2, GrowRevoked = 3;
        public const float LeaseTime = 45f;  // the longest a vine is ridden; the leader waits 10 s more for the end
        const float SettleAfterGrow = 1.5f;  // a finished vine still adds its nub and leaves: send it after this
        const float MaxWait = 20f;           // a friend update waits at most this long while I only stand near the plant
        class Lease { public ulong Holder; public float Until; public bool Ended, Took; }
        class Growing { public PlantBranch Branch; public float Since; public float DoneAt = -1f; public bool Pinned; }   // Pinned: the audit says when it is done
        static readonly Dictionary<string, Lease> leases = new Dictionary<string, Lease>();          // (leader) who grows which plant now
        static readonly Dictionary<string, Growing> growing = new Dictionary<string, Growing>();     // (me) the vine I ride
        static readonly HashSet<string> finishing = new HashSet<string>();                            // (guest) my vine is done: send the plant now, then the end
        static readonly Dictionary<string, int> realNumber = new Dictionary<string, int>();          // (leader) the number of my last send with real growth in it
        static readonly HashSet<string> realChange = new HashSet<string>();                           // (leader) real growth not sent yet
        static readonly HashSet<string> leaseEcho = new HashSet<string>();                            // (leader) the next send is the lease holder's own plant
        static readonly Dictionary<string, float> waitSince = new Dictionary<string, float>();
        public static int StatLeases, StatLeaseRefused, StatLeaseDone, StatLeaseTimeouts, StatHeldForGrow, StatIslandsOnLeader;

        static byte[] GrowMessage(byte what, string rootId, ulong player, int number)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgGrow); w.Write(what); w.Write(rootId); w.Write(player); w.Write(number);
            return ms.ToArray();
        }

        static PlantRoot RootOf(PlantBranch b)
        {
            if (b == null) return null;
            return b.m_PlantRoot != null ? b.m_PlantRoot : b.GetComponentInParent<PlantRoot>();
        }

        // the game's own "a bud was popped" call: it fires only on the PC of the player who pressed E (copies of friends never grab)
        static void OnGrowthTriggered(Transform t)
        {
            try
            {
                if (applying || !Enabled || t == null || Speedrun.Isolated || LeaderId == 0) return;
                var b = t.GetComponent<PlantBranch>();
                string id = IdOf(RootOf(b));
                if (id == null) return;
                StartGrowing(id, b);
            }
            catch (Exception e) { CoopBehaviour.Log("shared: grow start: " + Short(e)); }
        }

        static void StartGrowing(string id, PlantBranch b)
        {
            growing[id] = new Growing { Branch = b, Since = Time.time };
            if (IAmLeader)
            {
                Lease ls;
                if (leases.TryGetValue(id, out ls))
                {
                    // the leader keeps priority: the friend's vine on this plant is not taken
                    outbox.Add(GrowMessage(GrowRevoked, id, ls.Holder, 0)); leases.Remove(id); ForceSend(id);
                    CoopBehaviour.Log("shared: I grow plant " + id + " myself; the lease of " + ls.Holder + " is revoked");
                }
                realChange.Add(id);
                return;
            }
            int n; plantNumber.TryGetValue(id, out n);
            waiting.Remove(id); waitSince.Remove(id);          // an older leader copy waiting for me would undo the vine I grow now
            dirty.Add(id);
            outbox.Add(GrowMessage(GrowStart, id, MyId, n));
            CoopBehaviour.Log("shared: I grow a vine on plant " + id + " (built on the leader's number " + n + "); told the leader");
        }

        // (any role) my vine is done (or gone, or ridden too long): a guest sends the plant and the end once the vine has settled
        static void TickGrowing()
        {
            foreach (var id in new List<string>(growing.Keys))
            {
                var g = growing[id];
                bool done = (!g.Pinned && (g.Branch == null || g.Branch.m_DoneGrowing)) || Time.time - g.Since > LeaseTime;
                if (done && g.DoneAt < 0f) g.DoneAt = Time.time;
                if (g.DoneAt < 0f || Time.time - g.DoneAt < SettleAfterGrow) continue;
                growing.Remove(id);
                if (IAmGuest) finishing.Add(id);
                else if (IAmLeader) { realChange.Add(id); dirty.Add(id); }
            }
        }

        // (leader) a lease that ended is passed on once its plant really landed here; one never ended runs out
        static void TickLeases()
        {
            foreach (var id in new List<string>(leases.Keys))
            {
                var ls = leases[id];
                if (ls.Ended && !waiting.ContainsKey(id)) FinishLease(id, ls);
                else if (!ls.Ended && Time.time > ls.Until)          // an ended vine waits for its plant to land here (at most MaxWait, see ApplyWaiting)
                {
                    leases.Remove(id); StatLeaseTimeouts++; ForceSend(id);
                    CoopBehaviour.Log("shared: the vine of " + ls.Holder + " on plant " + id + " never ended; normal rules again, everybody gets my plant");
                }
            }
        }

        static void FinishLease(string id, Lease ls)
        {
            leases.Remove(id); StatLeaseDone++;
            lastSent.Remove(id); ForceSend(id);
            CoopBehaviour.Log("shared: " + ls.Holder + " finished a vine on plant " + id + (ls.Took ? "; passing it on to everybody" : " but no plant came; everybody gets mine"));
        }

        static void ApplyGrow(byte[] msg, ulong from)
        {
            if (msg.Length > 100) throw new FormatException("bad grow message");
            var r = new BinaryReader(new MemoryStream(msg)); r.ReadByte();
            byte what = r.ReadByte(); string id = r.ReadString(); ulong player = r.ReadUInt64(); int number = r.ReadInt32();
            if (r.BaseStream.Position != msg.Length || id.Length == 0 || id.Length > 64 || what < GrowStart || what > GrowRevoked) throw new FormatException("bad grow message");
            ulong sender = SenderOf(from, player);
            if (!WorldSync.InWorld()) return;
            if (what == GrowRevoked)
            {
                if (!IAmGuest || (from != 0 && from != LeaderId) || player != MyId) return;      // only the leader revokes, only mine counts
                bool had = growing.Remove(id) | finishing.Remove(id);
                dirty.Remove(id);
                if (had) { CoopUI.Toast("THE LEADER'S PLANT CHANGED: YOUR VINE IS NOT KEPT"); Status = "your vine was not kept"; }
                CoopBehaviour.Log("shared: the leader did not take my vine on plant " + id);
                return;
            }
            if (!IAmLeader) return;
            if (from != 0 && MayTakeFrom != null && !MayTakeFrom(from)) return;     // not (or no longer) in my world
            Lease ls;
            if (what == GrowEnd)
            {
                if (!leases.TryGetValue(id, out ls) || ls.Holder != sender) return;
                ls.Ended = true;
                if (!waiting.ContainsKey(id)) FinishLease(id, ls);
                return;
            }
            if (!HasPlant(id)) return;
            int real; realNumber.TryGetValue(id, out real);
            string why = null;
            if (leases.TryGetValue(id, out ls) && ls.Holder != sender && Time.time < ls.Until) why = "another player is growing it";
            else if (growing.ContainsKey(id)) why = "I am growing it myself";
            else if (number < real) why = "their copy (" + number + ") is older than my newest vine (" + real + ")";
            else if (realChange.Contains(id) || forced.Contains(id) || LeaderHasUnsent(id)) why = "I have growth on it they have not got yet";   // their plant would cut it off
            if (why != null)
            {
                StatLeaseRefused++;
                outbox.Add(GrowMessage(GrowRevoked, id, sender, 0)); ForceSend(id);
                CoopBehaviour.Log("shared: " + sender + " wants to grow plant " + id + "; refused: " + why);
                return;
            }
            leases[id] = new Lease { Holder = sender, Until = Time.time + LeaseTime + 10f };
            StatLeases++;
            CoopBehaviour.Log("shared: " + sender + " grows a vine on plant " + id + "; their copy of it counts until they are done");
        }

        // (leader) islands a friend's vine connected: the plant data sets them attached, the game's own connect step (trunk growth + counter) is done here
        static HashSet<int> AttachedProviders()
        {
            var set = new HashSet<int>();
            foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider))) if (n.m_attached) set.Add(n.GetInstanceID());
            return set;
        }

        static void ConnectNewIslands(HashSet<int> before, int connBefore)
        {
            if (!Singleton<PlayerAnalytics>.Exists) return;
            var fresh = new List<NutrientProvider>();
            foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider))) if (n.m_attached && !before.Contains(n.GetInstanceID())) fresh.Add(n);
            if (fresh.Count == 0) return;
            EnsureGrowingBeanstalk();
            var trunk = PlantBranch.CurrentlyGrowingBeanstalk;
            var pa = Singleton<PlayerAnalytics>.Instance;
            int grownWith = trunk != null ? trunk.m_connectedIslands - connBefore : 0;      // the trunk came in the same update: it has the connection already
            for (int i = 0; i < fresh.Count; i++)
            {
                try
                {
                    if (i >= grownWith && trunk != null && pa.m_nutrientIslandsAttched < trunk.m_growthAmountsPerIsland.Length) trunk.OnIslandConnect();
                }
                catch (Exception e) { CoopBehaviour.Log("shared: island connect on my trunk: " + Short(e)); }
                if (pa.m_nutrientIslandsAttched < MaxIslands) pa.m_nutrientIslandsAttched++;
                StatIslandsOnLeader++;
                try { fresh[i].StartCoroutine("ShowCurrentBeanstalkHeight"); } catch (Exception) { }
            }
            CoopUI.Toast("A FRIEND CONNECTED AN ISLAND (" + pa.m_nutrientIslandsAttched + ")");
            CoopBehaviour.Log("shared: a friend's vine connected " + fresh.Count + " island(s) in my world; island count is now " + pa.m_nutrientIslandsAttched);
        }

        // ---- self test hooks for the lease --------------------------------------------------------------------------------------------
        public static void TestStartGrowing(PlantRoot r, PlantBranch b) { StartGrowing(IdOf(r), b); Growing g; if (growing.TryGetValue(IdOf(r), out g)) g.Pinned = true; }
        public static void TestEndGrowing(string rootId) { Growing g; if (growing.TryGetValue(rootId, out g)) { g.Pinned = false; g.Branch = null; g.DoneAt = Time.time - SettleAfterGrow - 0.1f; } }
        public static bool TestIsGrowing(string rootId) { return growing.ContainsKey(rootId) || finishing.Contains(rootId); }
        public static ulong TestLeaseHolder(string rootId) { Lease ls; return leases.TryGetValue(rootId, out ls) ? ls.Holder : 0UL; }
        public static void TestAgeLease(string rootId, float seconds) { Lease ls; if (leases.TryGetValue(rootId, out ls)) ls.Until -= seconds; }
        public static void TestTickLeases() { TickLeases(); }
        public static void SetRealNumberForTest(string rootId, int n) { realNumber[rootId] = n; }
        public static byte[] TestGrowMessage(byte what, PlantRoot r, ulong player, int number) { return GrowMessage(what, IdOf(r), player, number); }
        public static void ClearGrowthForTest() { ForgetGrowth(); }
        public static void MarkSeenForTest(PlantRoot r) { string id = IdOf(r); lastSig[id] = Signature(r); dirty.Remove(id); }
        public static void MarkDirtyForTest(string rootId) { dirty.Add(rootId); }
        public static void MarkRealChangeForTest(string rootId) { realChange.Add(rootId); }

        // (leader) my plant changed since I last looked at it or has growth not sent yet: a guest's copy cannot have that, whatever number it carries
        static bool LeaderHasUnsent(string rootId)
        {
            if (dirty.Contains(rootId)) return true;
            int seen;
            if (!lastSig.TryGetValue(rootId, out seen)) return false;
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot))) if (IdOf(pr) == rootId) return Signature(pr) != seen;
            return false;
        }

        // (leader) the island counter follows the islands really connected in my world, never a number a guest says
        static void RaiseIslandsToAttached()
        {
            if (!Singleton<PlayerAnalytics>.Exists) return;
            int attached = 0;
            foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider))) if (n.m_attached) attached++;
            var pa = Singleton<PlayerAnalytics>.Instance;
            if (attached > pa.m_nutrientIslandsAttched && attached <= MaxIslands) { pa.m_nutrientIslandsAttched = attached; EnsureGrowingBeanstalk(); CoopBehaviour.Log("shared: a friend's island is connected in my world; island count is now " + attached); }
        }
        public static bool ForcedForTest(string rootId) { return forced.Contains(rootId); }
        public static void ClearForcedForTest() { forced.Clear(); }
        public static int PlantNumberForTest(string rootId) { int n; plantNumber.TryGetValue(rootId, out n); return n; }
        const float MinResend = 3f;          // while a plant keeps growing, send at most this often (the data is big)
        const float NearPlant = 4f;          // my walkster this close to a part of a plant counts as standing on it: updates to that plant wait
        const float OwnerHold = 8f;          // a plant stays owned this long after the owner's last update
        const int MaxRaw = 4 * 1024 * 1024;  // refuse anything that unpacks to more than this
        const int MaxIslands = 60;           // sanity limit for the island counter in a friend's message

        public static bool Enabled = true;
        public static string Status = "";
        public static ulong MyId;

        static MethodInfo miSaveObject, miLoadScene;
        static bool reflectionTried, reflectionOk;
        static bool hooked;
        static bool applying;                               // true while I apply something received: never echo it back
        static float quietUntil;                            // after a world load, ignore the settling period

        static readonly Dictionary<string, int> lastSig = new Dictionary<string, int>();
        static readonly Dictionary<string, float> lastSent = new Dictionary<string, float>();
        static readonly Dictionary<string, int> lastBytes = new Dictionary<string, int>();     // size of the last update sent for each plant (compressed)

        // While a plant keeps growing it is re-sent at most this often: 3 s for a small plant, longer the bigger the last update was (a whole beanstalk is hundreds of KB), never more than 30 s.
        public static float ResendInterval(int compressedBytes) { return Mathf.Clamp(compressedBytes / 15000f, MinResend, 30f); }
        static readonly Dictionary<string, KeyValuePair<ulong, float>> owner = new Dictionary<string, KeyValuePair<ulong, float>>();   // plant id -> (owner, until)
        static readonly HashSet<string> dirty = new HashSet<string>();
        static readonly List<byte[]> outbox = new List<byte[]>();
        static float nextCheck, lastPlantToast;
        // a friend update that arrived while my walkster stood on that plant: it waits until I step off (only the latest is kept: every update is the whole plant)
        static readonly Dictionary<string, KeyValuePair<ulong, byte[]>> waiting = new Dictionary<string, KeyValuePair<ulong, byte[]>>();
        public static bool? TestPlayerOnPlant;                // fault injection: null = measure where my walkster is; true/false = pretend it is or is not on the plant

        // counters and hooks for the automated self test (SelfTest.cs); harmless in normal play
        public static ITransport TestSink;                    // when set, plant updates go here instead of to the network
        public static int StatApplied, StatInPlaceFailed, StatRebuilt, StatApplyFailed, StatRejected;
        public static bool QuietRejects;                      // set by the fuzz test, which sends hundreds of bad messages on purpose: keeps the log readable
        public static bool TestFailInPlaceOnce;               // fault injection: the next in-place plant update fails, so the rebuild path is exercised

        public static bool IsMessage(byte[] m) { return m != null && m.Length > 0 && (m[0] == MsgPlant || m[0] == MsgPlant2 || m[0] == MsgShard || m[0] == MsgIsland || m[0] == MsgTeleporter || m[0] == MsgPickup || m[0] == MsgGrow); }

        // After the whole world was replaced (join): forget what I knew and give things a few seconds to settle.
        public static void Rebaseline() { quietUntil = Time.time + 6f; teleKnown.Clear(); teleSeen = false; lastSig.Clear(); dirty.Clear(); owner.Clear(); lastIslandCount = -1; attachedIds.Clear(); plantNumber.Clear(); forced.Clear(); waiting.Clear(); ForgetEchoes(); ForgetGrowth(); }

        static bool InitReflection()
        {
            if (reflectionTried) return reflectionOk;
            reflectionTried = true;
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public;
                miSaveObject = typeof(SaveSystem).GetMethod("SaveGameObjectToSaveBlock", flags);
                miLoadScene = typeof(SaveSystem).GetMethod("LoadSceneFromSaveBlock", flags);
                reflectionOk = miSaveObject != null && miLoadScene != null;
                if (!reflectionOk) CoopBehaviour.Log("shared world: could not find the game's save methods; plant sync off");
            }
            catch (Exception e) { CoopBehaviour.Log("shared world: reflection failed " + e.GetType().Name); }
            return reflectionOk;
        }

        // One line describing an exception, with the first frames of its stack (the game's Mono prints "[0x00000] in <filename unknown>:0" after every frame: dropped).
        public static string Short(Exception e)
        {
            var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            var sb = new System.Text.StringBuilder(inner.GetType().Name + ": " + inner.Message.Replace('\n', ' '));
            string[] lines = (inner.StackTrace ?? "").Split('\n');
            for (int i = 0; i < lines.Length && i < 9; i++)
            {
                string l = lines[i].Trim();
                int cut = l.IndexOf(" [0x");
                if (cut > 0) l = l.Substring(0, cut);
                if (l.Length > 0) sb.Append(i == 0 ? " @ " : " < ").Append(l);
            }
            return sb.ToString();
        }

        // ---- crystals --------------------------------------------------------------------------------------------------------------

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            JointShard.m_onShardRemoval = (JointShard.OnShardRemoval)Delegate.Combine(JointShard.m_onShardRemoval, new JointShard.OnShardRemoval(OnLocalShardCollected));
            // fires only when a pickup really went into my inventory (not on a grab that was let go), just before the pickup is removed
            ParachutePickup.m_flyablePickedUpObjectDelegate = (ParachutePickup.FlyablePickedUpObject)Delegate.Combine(ParachutePickup.m_flyablePickedUpObjectDelegate, new ParachutePickup.FlyablePickedUpObject(OnLocalPickup));
            // fires when I pop a bud (E on a nub) and start riding the new vine
            PlantBranch.m_growthTriggered = (PlantBranch.GrowthTrigger)Delegate.Combine(PlantBranch.m_growthTriggered, new PlantBranch.GrowthTrigger(OnGrowthTriggered));
        }

        // ---- flower and leaf pickups -------------------------------------------------------------------------------------------------
        // A flower (parachute) or leaf (glider) I take disappears for everybody too. Pickups come back after a while (Respawn) at their first
        // spot but in another place in the hierarchy, so they are known by their type and that spot, not by their path.

        static readonly FieldInfo respawnObjectField = typeof(Respawn).GetField("m_respawnObject", BindingFlags.NonPublic | BindingFlags.Instance);
        public static int StatPickupsApplied;

        // where a pickup first stood: the waiting respawn copy sits there; otherwise where it is now
        static Vector3 PickupSpot(GameObject go)
        {
            var rs = go.GetComponent<Respawn>();
            if (rs != null && respawnObjectField != null)
            {
                var spot = respawnObjectField.GetValue(rs) as GameObject;
                if (spot != null) return spot.transform.position;
            }
            return go.transform.position;
        }

        static void OnLocalPickup(FlyableManager.PickupItem type, GameObject go)
        {
            if (applying || !Enabled || go == null) return;
            if (type != FlyableManager.PickupItem.Parachute && type != FlyableManager.PickupItem.Glider) return;
            if (go.name.EndsWith(" Pickup")) return;          // a leaf somebody dropped: it exists only on that PC, so it must not remove a real one near it
            Vector3 at = PickupSpot(go);
            outbox.Add(PickupMessage(type, at));
            CoopBehaviour.Log("shared: I took a " + (type == FlyableManager.PickupItem.Glider ? "leaf" : "flower") + " at " + at);
        }

        static byte[] PickupMessage(FlyableManager.PickupItem type, Vector3 at)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgPickup); w.Write((byte)type); w.Write(at.x); w.Write(at.y); w.Write(at.z);
            return ms.ToArray();
        }

        public static byte[] TestPickupMessage(FlyableManager.PickupItem type, Vector3 at) { return PickupMessage(type, at); }
        public static Vector3 TestPickupSpot(GameObject go) { return PickupSpot(go); }

        // A friend took this flower/leaf: it shrinks away here and grows back like after my own take. It never goes into MY inventory:
        // the game's own grab and shrink callbacks (which add it to the local player) are never called, and the pickup script is removed first.
        static void ApplyPickup(byte[] m)
        {
            if (m.Length != 14) throw new FormatException("bad pickup message");
            var type = (FlyableManager.PickupItem)m[1];
            if (type != FlyableManager.PickupItem.Parachute && type != FlyableManager.PickupItem.Glider) throw new FormatException("bad pickup type");
            var at = new Vector3(BitConverter.ToSingle(m, 2), BitConverter.ToSingle(m, 6), BitConverter.ToSingle(m, 10));
            for (int i = 0; i < 3; i++) if (float.IsNaN(at[i]) || float.IsInfinity(at[i]) || Math.Abs(at[i]) > 50000f) throw new FormatException("bad pickup position");
            string what = type == FlyableManager.PickupItem.Glider ? "leaf" : "flower";
            ParachutePickup best = null; float bestD = 3f;
            foreach (ParachutePickup pk in UnityEngine.Object.FindObjectsOfType(typeof(ParachutePickup)))
            {
                if (pk == null || pk.m_parachuteType != type || pk.name.EndsWith(" Pickup")) continue;
                if (pk.transform.root.name.StartsWith("Walkster")) continue;
                float d = Mathf.Min((pk.transform.position - at).magnitude, (PickupSpot(pk.gameObject) - at).magnitude);
                if (d <= bestD) { best = pk; bestD = d; }
            }
            if (best == null) { CoopBehaviour.Log("shared: a friend took a " + what + " at " + at + "; not here (already gone or moved)"); return; }
            // already shrinking: I am taking that same one here, so leave it (and never pick a different one near it instead)
            if (best.GetComponent<ShrinkAndDestroy>() != null) { CoopBehaviour.Log("shared: a friend took the " + what + " " + best.name + " at " + at + "; already being taken here"); return; }
            applying = true;
            try
            {
                var go = best.gameObject;
                float destroyTime = best.m_destroyTime; var curve = best.m_animCurve;
                if (go.rigidbody != null) { go.rigidbody.isKinematic = true; go.rigidbody.useGravity = false; }   // kinematic: ObjectSync leaves it alone too
                foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
                var rs = go.GetComponent<Respawn>();
                if (rs != null) rs.StartRespawn();                // grows back here as well
                UnityEngine.Object.Destroy(best);                  // my hand can no longer take it into my inventory
                var sd = go.AddComponent<ShrinkAndDestroy>();       // the same shrink the game uses, without the callback that gives the item away
                sd.m_destroyDelay = 0f; sd.m_useDestroySize = false; sd.m_useDestroyTime = true; sd.m_destroyTime = destroyTime; sd.m_animCurve = curve;
                StatPickupsApplied++;
                Status = "a friend took a " + what; CoopUI.Toast("A FRIEND TOOK A " + what.ToUpper());
                CoopBehaviour.Log("shared: a friend took the " + what + " " + go.name + " at " + at);
            }
            finally { applying = false; }
        }

        static void OnLocalShardCollected(JointShard shard, string guid)
        {
            if (applying || !Enabled || string.IsNullOrEmpty(guid)) return;
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgShard); w.Write(guid);
            outbox.Add(ms.ToArray());
            CoopBehaviour.Log("shared: I collected crystal " + guid);
        }

        static void ApplyShard(byte[] msg)
        {
            var r = new BinaryReader(new MemoryStream(msg)); r.ReadByte();
            string guid = r.ReadString();
            foreach (SaveInfo si in UnityEngine.Object.FindObjectsOfType(typeof(SaveInfo)))
            {
                if (si.m_uniqueID != guid) continue;
                applying = true;
                try
                {
                    SceneProfile.AddShard();                 // team credit: crystals count for everybody
                    UnityEngine.Object.Destroy(si.gameObject);
                    Status = "a friend collected a crystal"; CoopUI.Toast("CRYSTAL EXTRACTED BY A FRIEND");
                    CoopBehaviour.Log("shared: removed crystal " + guid + " collected by a friend");
                }
                finally { applying = false; }
                return;
            }
            CoopBehaviour.Log("shared: crystal " + guid + " not found here (already collected or not loaded)");
        }

        // ---- plants ----------------------------------------------------------------------------------------------------------------

        static int Signature(PlantRoot root)
        {
            int s = 17;
            foreach (var b in root.m_Branches)
            {
                if (b == null) continue;
                s = s * 31 + b.m_Elements.Count;
                s = s * 31 + b.m_Branches.Count;
                s = s * 31 + b.m_Stem.Count;
                s = s * 31 + (int)(b.m_Length * 4f);
            }
            s = s * 31 + root.m_Branches.Count;
            s = s * 31 + root.GetComponentsInChildren<SaveInfo>(true).Length;
            s = s * 31 + root.m_connectedIslands;                       // the "3/6" islands counter on the HUD
            s = s * 31 + root.m_requiredIslands;
            s = s * 31 + (int)(root.TrunkGrowthFraction * 1000f);
            return s;
        }

        static string IdOf(PlantRoot root)
        {
            var si = root.GetComponent<SaveInfo>();
            return si != null ? si.m_uniqueID : null;
        }

        // The plant tree plus every nutrient source that is attached to something (their state changes when a growing plant reaches them).
        static List<SaveInfo> GroupFor(PlantRoot root)
        {
            var list = new List<SaveInfo>(root.GetComponentsInChildren<SaveInfo>(true));
            var attachedField = typeof(NutrientProvider).GetField("m_attached", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider)))
            {
                var si = n.GetComponent<SaveInfo>();
                if (si == null || list.Contains(si)) continue;
                if (attachedField == null || (bool)attachedField.GetValue(n)) list.Add(si);
            }
            return list;
        }

        // Same layout the game uses for a whole level, but only for the given objects.
        // Saving a plant makes the game queue "after the next load" callbacks for every branch (and nutrient source). I never load from this data, so
        // put the queue back as it was; otherwise it grows with every send and later runs against branches that no longer exist.
        static byte[] Serialize(List<SaveInfo> infos)
        {
            var queued = SaveSystem.OnLoadComplete;
            try { return SerializeInner(infos); }
            finally { SaveSystem.OnLoadComplete = queued; }
        }

        static byte[] SerializeInner(List<SaveInfo> infos)
        {
            SaveSystem.m_loadedDataVersion = 91;
            var scene = new SaveBlock(true) { Type = SaveBlock.BlockType.Scene };
            foreach (var si in infos)
            {
                var block = new SaveBlock(true) { Type = SaveBlock.BlockType.GameObject };
                if (!string.IsNullOrEmpty(si.m_prefabID))
                {
                    miSaveObject.Invoke(null, new object[] { block, si });
                    scene.AddChildBlock(block);
                    foreach (var part in si.gameObject.GetComponentsInChildren<SaveInfo>())
                    {
                        if (part.gameObject == si.gameObject) continue;
                        var pb = new SaveBlock(true) { Type = SaveBlock.BlockType.PrefabPart };
                        miSaveObject.Invoke(null, new object[] { pb, part });
                        block.AddChildBlock(pb);
                    }
                }
                else if (!si.m_isPrefabPart)
                {
                    miSaveObject.Invoke(null, new object[] { block, si });
                    scene.AddChildBlock(block);
                }
            }
            var ser = new Serializer(true);
            scene.Serialize(ser);
            return ser.Bytes;
        }

        // The game's Mono cannot do GZipStream (no native zlib helper), so this uses our own small compressor (Lz.cs).
        public static byte[] Compress(byte[] raw) { return Lz.Pack(raw); }

        static byte[] Decompress(byte[] comp, int rawLen)
        {
            if (rawLen < 0 || rawLen > MaxRaw) throw new FormatException("bad size");
            return Lz.Unpack(comp, rawLen);
        }

        // Reads an update into the game's block tree without touching the world. A damaged update fails here, before anything was changed.
        static SaveBlock Parse(byte[] data)
        {
            SaveSystem.m_loadedDataVersion = 91;
            var ser = new Serializer(false); ser.Bytes = data;
            var block = new SaveBlock(false); block.Serialize(ser);
            return block;
        }

        static bool HasPlant(string rootId)
        {
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot))) if (IdOf(pr) == rootId) return true;
            return false;
        }

        static byte[] SnapshotOf(string rootId)
        {
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot))) if (IdOf(pr) == rootId) return Serialize(GroupFor(pr));
            return null;
        }

        // Is my walkster standing on (or climbing) this plant? The camera follows its grip on the branches, so a plant it stands on is not rebuilt under it.
        // 0: no, 1: my body is near a part of it, 2: I hold or ride it (a hand on it, a nub grabbed, a vine popped and not done).
        public const int OffPlant = 0, NearThePlant = 1, HoldingPlant = 2;
        public static Vector3? TestBodyOverride;              // the audit puts my body somewhere else without moving anything
        static bool OnPlant(string rootId) { return OnPlantState(rootId) != OffPlant; }

        static int OnPlantState(string rootId)
        {
            if (TestPlayerOnPlant.HasValue) return TestPlayerOnPlant.Value ? HoldingPlant : OffPlant;
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
                if (IdOf(pr) == rootId) return PlayerOn(pr, BodyPosition());
            return OffPlant;
        }

        // Where my walkster really is: its body. The "Walkster" object itself stays at the level start.
        public static Vector3? BodyPosition()
        {
            if (TestBodyOverride.HasValue) return TestBodyOverride.Value;
            var me = GameObject.Find("Walkster");
            if (me == null) return null;
            var w = me.GetComponentInChildren<BipedWalkScript>();
            return w != null && w.body != null ? w.body.position : me.transform.position;
        }

        public static int PlayerOn(PlantRoot pr, Vector3? body)
        {
            if (pr == null) return OffPlant;
            foreach (var b in pr.GetComponentsInChildren<PlantBranch>(true))
                if (b != null && (b.m_PlayerLockedOn || b.IsGrabbed || (b.m_BudTriggered && !b.m_DoneGrowing))) return HoldingPlant;
            var me = GameObject.Find("Walkster");
            if (me != null)
                foreach (var h in me.GetComponentsInChildren<BipedHandScript>())
                    if (h != null && h.grabbedObject != null && h.grabbedObject.IsChildOf(pr.transform)) return HoldingPlant;
            if (!body.HasValue) return OffPlant;
            Vector3 at = body.Value;
            foreach (var c in Physics.OverlapSphere(at, NearPlant)) if (c != null && c.transform.IsChildOf(pr.transform)) return NearThePlant;
            foreach (var t in pr.GetComponentsInChildren<Transform>(true))           // parts whose colliders are off right now
                if ((t.position - at).sqrMagnitude < NearPlant * NearPlant) return NearThePlant;
            return OffPlant;
        }

        // Applies the waited updates of the plants I have stepped off. Standing near a plant holds an update at most MaxWait; holding or riding it
        // holds it until I let go (a finished friend's vine on the leader also lands after MaxWait: everybody waits for it).
        static void ApplyWaiting()
        {
            foreach (var id in new List<string>(waiting.Keys))
            {
                int st = OnPlantState(id);
                float since; if (!waitSince.TryGetValue(id, out since)) since = Time.time;
                bool overdue = Time.time - since > MaxWait;
                Lease ls; bool leaseEnded = IAmLeader && leases.TryGetValue(id, out ls) && ls.Ended;
                if (st == HoldingPlant && !(overdue && leaseEnded)) continue;
                if (st == NearThePlant && !overdue) continue;
                var w = waiting[id];
                waiting.Remove(id); waitSince.Remove(id);
                landing = true;
                try { ApplyPlant(w.Value, w.Key); CoopBehaviour.Log("shared: applied a friend plant update that waited " + (Time.time - since).ToString("F0") + " s while I stood on the plant" + (st != OffPlant ? " (waited long enough)" : "")); }
                catch (Exception e) { StatRejected++; CoopBehaviour.Log("shared: a waited plant update failed (" + Short(e) + ")"); }
                finally { landing = false; }
            }
        }
        static bool landing;                                  // a waited update lands now: it does not wait again

        // One try at loading a plant update into the scene. Everything not in the update keeps its state.
        static void LoadUpdate(byte[] data)
        {
            var block = Parse(data);
            var existing = new List<SaveInfo>();
            var prior = new List<bool>();
            foreach (SaveInfo si in UnityEngine.Object.FindObjectsOfType(typeof(SaveInfo))) { existing.Add(si); prior.Add(si.m_hasSaveData); si.m_hasSaveData = true; }   // keeps the loader from deleting everything not in this update
            applying = true;
            try
            {
                SaveSystem.OnLoadComplete = null;      // callbacks left over from earlier saves point at objects that may be gone; only this load's own should run
                miLoadScene.Invoke(null, new object[] { block });
            }
            finally
            {
                applying = false;
                SaveSystem.loadingScene = false;
                SaveSystem.OnLoadComplete = null;      // a failed load must not leave its callbacks queued for the next one
                for (int i = 0; i < existing.Count; i++) if (existing[i] != null) existing[i].m_hasSaveData = prior[i];
            }
        }

        // If updating in place fails (my copy of the plant differs in structure), throw away what the plant grew at runtime and rebuild it from the update.
        // Parts the level designers placed in the scene (the crown on top of the trunk, ...) can never be re-created by a load, so they are kept.
        static void RemovePlantCopy(string rootId)
        {
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                if (IdOf(pr) != rootId) continue;
                var doomed = new List<GameObject>();       // collect first: destroying a branch also destroys the parts below it
                foreach (var si in pr.GetComponentsInChildren<SaveInfo>(true))
                    if (si != null && si.gameObject != pr.gameObject && si.m_runtimeSpawned) doomed.Add(si.gameObject);
                foreach (var go in doomed)
                {
                    if (go == null) continue;
                    foreach (var keep in go.GetComponentsInChildren<SaveInfo>(true))               // a scene-placed part hanging below a doomed one moves up to the plant root
                        if (keep != null && keep.gameObject != go && !keep.m_runtimeSpawned) keep.transform.parent = pr.transform;
                    if (pr.m_CrownObject != null && pr.m_CrownObject.IsChildOf(go.transform)) pr.m_CrownObject.parent = pr.transform;
                }
                foreach (var go in doomed) if (go != null) { QuitGuard.ReleaseHeld(go); UnityEngine.Object.DestroyImmediate(go); }   // a growing part gives back its block on Save & Quit first
                pr.m_Branches.Clear();
                Unlink(pr); CleanPending();
            }
            guardBoostUntil = Time.time + 3f;
        }

        // Parts the mod removed may still be listed by a branch that stays (a scene-placed one, or one the update keeps). The game would use them
        // next frame (a NullReferenceException in PlantStemNode.SetScale, which stops that branch's Update before it turns colliders back on).
        public static int Unlink(PlantRoot pr)
        {
            int n = 0;
            if (pr == null) return 0;
            foreach (var b in pr.GetComponentsInChildren<PlantBranch>(true))
            {
                if (b == null) continue;
                n += b.m_Stem.RemoveAll(s => s == null || s.m_StemObject == null);
                n += b.m_ScalingStem.RemoveAll(s => s == null || s.m_StemObject == null);
                n += b.m_Elements.RemoveAll(e => e == null || e.m_StemObject == null);
                n += b.m_Branches.RemoveAll(x => x == null || x.m_Branch == null);
                n += b.m_GrabbedNubs.RemoveAll(x => x == null);
            }
            pr.m_Branches.RemoveAll(b => b == null);
            return n;
        }

        // A list of what is wrong with a plant's links after parts were removed (empty when nothing is): for the audit.
        public static string DeadLinks(PlantRoot pr)
        {
            int stems = 0, elements = 0, branches = 0, nubs = 0, pending = 0;
            foreach (var b in pr.GetComponentsInChildren<PlantBranch>(true))
            {
                if (b == null) continue;
                foreach (var s in b.m_Stem) if (s == null || s.m_StemObject == null) stems++;
                foreach (var s in b.m_ScalingStem) if (s == null || s.m_StemObject == null) stems++;
                foreach (var e in b.m_Elements) if (e == null || e.m_StemObject == null) elements++;
                foreach (var x in b.m_Branches) if (x == null || x.m_Branch == null) branches++;
                foreach (var x in b.m_GrabbedNubs) if (x == null) nubs++;
            }
            for (var node = ColliderLOD.s_Pending.Root; node != null; node = node.Next) if (node.m_Subject == null || node.m_Owner == null) pending++;
            if (stems + elements + branches + nubs + pending == 0) return "";
            return "dead stems " + stems + ", elements " + elements + ", branches " + branches + ", nubs " + nubs + ", collider queue " + pending;
        }

        // ---- collision guard ------------------------------------------------------------------------------------------------------------
        // A new stem starts with its colliders off (ColliderLOD.Start) and the game turns them on slowly (2 per frame, after a random wait), so after a
        // rebuild the vine under a player can have no collision for seconds; a resize can also leave one off. Near me, a stem collider that stays off
        // (or any, just after the mod rebuilt a plant) is turned on again, the way the game does for stems near the player.
        public const float GuardRadius = 40f;
        const float OffTooLong = 2f;
        static readonly Dictionary<int, float> offSince = new Dictionary<int, float>();
        static float guardBoostUntil;
        public static int StatCollidersFixed, StatPendingCleaned;

        public static int GuardCollision(Vector3 at, float radius, bool now)
        {
            int fixedN = 0; float r2 = radius * radius;
            guardSeen.Clear();
            if (now || Time.time >= guardCacheUntil) { stemCache.Clear(); guardCacheUntil = Time.time + 10f; }   // fresh after a load, prune or rebuild, and every 10 s
            foreach (PlantBranch b in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch)))
            {
                if (b == null) continue;
                bool riding = b.m_PlayerLockedOn;
                foreach (var node in b.m_Stem)
                {
                    if (node == null || node.m_StemObject == null) continue;
                    if ((node.m_StemObject.transform.position - at).sqrMagnitude > r2) continue;
                    if (riding && node.m_Scaling) continue;          // the game keeps a resizing stem off while I ride its branch
                    bool any = false;
                    foreach (var c in CachedStemColliders(node))
                    {
                        if (c == null) continue;
                        int key = c.GetInstanceID();
                        if (c.enabled) { offSince.Remove(key); continue; }
                        guardSeen.Add(key);
                        float t0; if (!offSince.TryGetValue(key, out t0)) { t0 = Time.time; offSince[key] = t0; }
                        if (!now && Time.time - t0 < OffTooLong) continue;
                        c.enabled = true; offSince.Remove(key); fixedN++; any = true;
                    }
                    if (any && node.m_ColliderLOD != null) node.m_ColliderLOD.m_ExpectEnabled = true;
                }
            }
            if (offSince.Count > 0)
            {
                guardDrop.Clear();
                foreach (var k in offSince.Keys) if (!guardSeen.Contains(k)) guardDrop.Add(k);
                foreach (var k in guardDrop) offSince.Remove(k);
            }
            int cleaned = 0;
            if (now || Time.time >= nextPendingClean) { nextPendingClean = Time.time + 10f; cleaned = CleanPending(); }   // dead entries come from removed parts
            StatCollidersFixed += fixedN; StatGuardScans++;
            if (fixedN > 0 || cleaned > 0) CoopBehaviour.Log("collision: re-enabled " + fixedN + " plant colliders near " + at + (cleaned > 0 ? ", dropped " + cleaned + " removed colliders from the game's queue" : ""));
            return fixedN;
        }

        // the scan is cheap when nothing was rebuilt: the colliders of a stem are looked up once (and again after a rebuild or every 10 s), the lists are reused
        static readonly Dictionary<int, Collider[]> stemCache = new Dictionary<int, Collider[]>();
        static readonly HashSet<int> guardSeen = new HashSet<int>();
        static readonly List<int> guardDrop = new List<int>();
        static float guardCacheUntil, nextPendingClean, nextGuard;
        public static int StatGuardScans;
        public const float GuardEvery = 2f;            // between full scans when nothing was rebuilt (every tick for 3 s after one)
        static Collider[] CachedStemColliders(PlantStemNode node)
        {
            int key = node.m_StemObject.GetInstanceID();
            Collider[] cs;
            if (stemCache.TryGetValue(key, out cs)) return cs;
            cs = StemColliders(node).ToArray();
            stemCache[key] = cs;
            return cs;
        }

        // the colliders the game itself turns on for a stem near the player: the stem's own and those its ColliderLOD looks after
        static List<Collider> StemColliders(PlantStemNode node)
        {
            var list = new List<Collider>();
            var own = node.m_StemObject.Collider;
            if (own != null) list.Add(own);
            if (node.m_ColliderLOD != null) foreach (var c in node.m_ColliderLOD.GetComponentsInChildren<Collider>(true)) if (c != null && !list.Contains(c)) list.Add(c);
            return list;
        }

        // the game's queue of colliders to turn on keeps entries for removed ones for ever (and searches all of them every frame)
        static int CleanPending()
        {
            int n = 0;
            var node = ColliderLOD.s_Pending.Root;
            while (node != null)
            {
                var next = node.Next;
                if (node.m_Subject == null || node.m_Owner == null)
                {
                    node.Remove(); node.m_Subject = null; node.m_Owner = null; ColliderLOD.s_Pool.InsertAtEnd(node); n++;
                }
                node = next;
            }
            StatPendingCleaned += n;
            return n;
        }

        // stem colliders near a point that are off (the riding exception as above): for the audit
        public static int CollidersOff(Vector3 at, float radius, out int total)
        {
            int off = 0; total = 0; float r2 = radius * radius;
            foreach (PlantBranch b in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch)))
            {
                if (b == null) continue;
                foreach (var node in b.m_Stem)
                {
                    if (node == null || node.m_StemObject == null || (node.m_StemObject.transform.position - at).sqrMagnitude > r2) continue;
                    if (b.m_PlayerLockedOn && node.m_Scaling) continue;
                    foreach (var c in StemColliders(node)) { if (c == null) continue; total++; if (!c.enabled) off++; }
                }
            }
            return off;
        }

        static void GuardTick()
        {
            bool boost = Time.time < guardBoostUntil;
            if (!boost && Time.time < nextGuard) return;        // nothing rebuilt: a full scan every GuardEvery seconds is enough (a stem must be off 2 s anyway)
            nextGuard = Time.time + GuardEvery;
            var body = BodyPosition();
            if (body.HasValue) GuardCollision(body.Value, GuardRadius, boost);
        }

        // The grown parts of a plant (their ids) travel after the update, so a receiver can tell which of ITS parts the sender no longer has. An in-place update never removes anything,
        // so without this a PC that once had more branches kept them for ever: extra physics objects, a plant that differs between players, and a game that gets slower.
        const int MaxKeep = 20000;
        static void WriteKeep(BinaryWriter w, PlantRoot root) { WriteKeep(w, root, null); }
        static void WriteKeep(BinaryWriter w, PlantRoot root, string drop)
        {
            var ids = new List<string>();
            foreach (var si in root.GetComponentsInChildren<SaveInfo>(true))
                if (si != null && si.gameObject != root.gameObject && si.m_runtimeSpawned && !string.IsNullOrEmpty(si.m_uniqueID) && si.m_uniqueID.Length <= 64 && si.m_uniqueID != drop) ids.Add(si.m_uniqueID);
            if (ids.Count > MaxKeep) ids.Clear();
            var ms = new MemoryStream(); var pw = new BinaryWriter(ms);
            pw.Write(ids.Count);
            foreach (var id in ids) pw.Write(id);
            byte[] payload = ms.ToArray();
            w.Write(payload.Length); w.Write(Lz.Checksum(payload)); w.Write(payload);       // checksummed like the update itself: a garbled list must never remove parts
        }

        static HashSet<string> ReadKeep(BinaryReader r)
        {
            try
            {
                if (r.BaseStream.Length - r.BaseStream.Position < 8) return null;
                int len = r.ReadInt32(); uint sum = r.ReadUInt32();
                if (len < 4 || len > MaxKeep * 70 || r.BaseStream.Length - r.BaseStream.Position != len) return null;
                byte[] payload = r.ReadBytes(len);
                if (Lz.Checksum(payload) != sum) return null;
                var pr = new BinaryReader(new MemoryStream(payload));
                int n = pr.ReadInt32();
                if (n == 0) return null;                    // an empty list means "nothing known" (also what an over-large plant sends): never prune on it
                if (n < 0 || n > MaxKeep) return null;
                var set = new HashSet<string>();
                for (int i = 0; i < n; i++) { string s = pr.ReadString(); if (s.Length > 64) return null; set.Add(s); }
                return set;
            }
            catch (Exception) { return null; }
        }

        static void PruneExtras(string rootId, HashSet<string> keep)
        {
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                if (IdOf(pr) != rootId) continue;
                var doomed = new List<GameObject>();
                var held = HeldByMe();
                foreach (var si in pr.GetComponentsInChildren<SaveInfo>(true))
                    if (si != null && si.gameObject != pr.gameObject && si.m_runtimeSpawned && !keep.Contains(si.m_uniqueID))
                    {
                        bool inHand = false;
                        foreach (var h in held) if (h.IsChildOf(si.transform)) inHand = true;
                        if (inHand) { CoopBehaviour.Log("shared: kept a plant part the sender does not have: my hand is on it"); continue; }   // never pull a nub or stem out of my hand
                        doomed.Add(si.gameObject);
                    }
                if (doomed.Count == 0) return;
                foreach (var go in doomed)
                {
                    if (go == null) continue;
                    foreach (var k in go.GetComponentsInChildren<SaveInfo>(true))
                        if (k != null && k.gameObject != go && (keep.Contains(k.m_uniqueID) || !k.m_runtimeSpawned)) k.transform.parent = pr.transform;   // a part that stays never goes down with a part that goes
                    if (pr.m_CrownObject != null && pr.m_CrownObject.IsChildOf(go.transform)) pr.m_CrownObject.parent = pr.transform;
                }
                foreach (var go in doomed) if (go != null) { QuitGuard.ReleaseHeld(go); UnityEngine.Object.DestroyImmediate(go); }   // a growing part gives back its block on Save & Quit first
                int links = Unlink(pr); CleanPending();
                guardBoostUntil = Time.time + 3f;
                CoopBehaviour.Log("shared: removed " + doomed.Count + " parts my copy of the plant had and the sender does not" + (links > 0 ? " (and " + links + " links to them)" : ""));
            }
        }

        // what my hands hold now
        static List<Transform> HeldByMe()
        {
            var list = new List<Transform>();
            var me = GameObject.Find("Walkster");
            if (me != null) foreach (var h in me.GetComponentsInChildren<BipedHandScript>()) if (h != null && h.grabbedObject != null) list.Add(h.grabbedObject);
            return list;
        }

        static void ApplyPlant(byte[] msg, ulong from)
        {
            if (!WorldSync.InWorld() || !InitReflection()) return;
            var r = new BinaryReader(new MemoryStream(msg)); byte kind = r.ReadByte();
            string rootId = r.ReadString();
            ulong sender = SenderOf(from, r.ReadUInt64());
            int number = kind == MsgPlant2 ? r.ReadInt32() : -1;
            ulong echoOf = kind == MsgPlant2 ? r.ReadUInt64() : 0UL;
            int echoNumber = kind == MsgPlant2 ? r.ReadInt32() : -1;
            int rawLen = r.ReadInt32(); int compLen = r.ReadInt32(); uint sum = r.ReadUInt32();
            if (compLen < 0 || compLen > msg.Length) throw new FormatException("bad packed size");
            byte[] data = Decompress(r.ReadBytes(compLen), rawLen);
            HashSet<string> keep = ReadKeep(r);                              // which grown parts the sender has (absent from older senders)
            if (Lz.Checksum(data) != sum) throw new FormatException("the update is damaged (checksum)");
            if (!HasPlant(rootId)) throw new FormatException("a plant I do not have");
            Parse(data);                              // a damaged update is thrown out here: the fallback below would otherwise destroy my plant for nothing

            if (from != 0 && IAmGuest && from != LeaderId) return;      // a guest takes plants only from the leader (who passes on every change that counts)
            if (IAmGuest && (growing.ContainsKey(rootId) || finishing.Contains(rootId)))
            {
                // I ride a vine on this plant: the leader's copy has not got it yet and must not take it back from under me (the leader takes mine at the end)
                StatHeldForGrow++;
                if (!QuietRejects) CoopBehaviour.Log("shared: the leader's copy of plant " + rootId + " is ignored while I grow a vine on it");
                return;
            }
            if (from != 0 && IAmGuest && echoOf != 0 && echoOf == MyId)
            {
                int myBase;
                if (plantNumber.TryGetValue(rootId, out myBase) ? myBase == echoNumber : echoNumber == 0)
                {
                    // the leader took my plant and passes it on as it was: I have that already (and maybe more). Build on its number; nothing is put back.
                    StatEchoes++;
                    plantNumber[rootId] = number; waiting.Remove(rootId);
                    int firstSig; bool grewSince = !sentFirstSig.TryGetValue(rootId, out firstSig) || CurrentSignature(rootId) != firstSig;
                    if (grewSince && growing.ContainsKey(rootId)) dirty.Add(rootId);        // what I grew after that first send goes to the leader on the new number (a guest sends only its own vines)
                    sentBase.Remove(rootId); sentFirstSig.Remove(rootId);
                    return;
                }
            }
            int leaderMine = 0; Lease lease = null;
            if (from != 0 && IAmLeader)
            {
                if (MayTakeFrom != null && !MayTakeFrom(from)) return;     // not (or no longer) in my world
                int mine; plantNumber.TryGetValue(rootId, out mine); leaderMine = mine;
                Lease ls;
                if (leases.TryGetValue(rootId, out ls))
                {
                    if (ls.Holder != from) { StatStale++; return; }          // somebody else grows it now; they all get mine once that is done
                    int real; realNumber.TryGetValue(rootId, out real);
                    if (number < real || (!ls.Took && realChange.Contains(rootId)))
                    {
                        // their copy misses a vine I grew or took: taking it would cut that vine off. The lease ends; they get mine.
                        StatStale++; StatLeaseRefused++; leases.Remove(rootId);
                        outbox.Add(GrowMessage(GrowRevoked, rootId, from, 0)); ForceSend(rootId);
                        CoopBehaviour.Log("shared: the vine plant of " + from + " was built on my copy " + number + ", older than my newest vine (" + real + "); not taken");
                        return;
                    }
                    lease = ls;                                              // their vine: my own copy of this plant does not count meanwhile
                }
                else if (number != mine || realChange.Contains(rootId) || LeaderHasUnsent(rootId))
                {
                    // built on an older copy of my plant: taking it would undo my newer growth. They get mine instead.
                    StatStale++;
                    if (Time.realtimeSinceStartup - lastStaleLog > 10f) { lastStaleLog = Time.realtimeSinceStartup; CoopBehaviour.Log("shared: a friend's plant was built on an older copy of mine (" + number + ", mine is " + mine + "); not taken, sending them mine"); }
                    ForceSend(rootId);
                    return;
                }
            }

            owner[rootId] = new KeyValuePair<ulong, float>(sender, Time.time + OwnerHold);   // they are growing it: I only receive until they are done
            if (!landing && OnPlant(rootId))
            {
                if (!waiting.ContainsKey(rootId)) waitSince[rootId] = Time.time;
                waiting[rootId] = new KeyValuePair<ulong, byte[]>(from, msg);
                if (!QuietRejects) CoopBehaviour.Log("shared: a friend plant update waits until I step off that plant");
                return;     // the owner is already set above, so my copy stays quiet meanwhile
            }
            HashSet<int> attachedBefore = null; int connBefore = 0;
            if (from != 0 && IAmLeader)
            {
                attachedBefore = AttachedProviders();
                EnsureGrowingBeanstalk();
                if (PlantBranch.CurrentlyGrowingBeanstalk != null) connBefore = PlantBranch.CurrentlyGrowingBeanstalk.m_connectedIslands;
            }
            try { if (TestFailInPlaceOnce) { TestFailInPlaceOnce = false; throw new InvalidOperationException("[test] injected in-place failure"); } LoadUpdate(data); Status = "a friend's plant updated"; StatApplied++; if (keep != null) PruneExtras(rootId, keep); guardBoostUntil = Time.time + 3f; }
            catch (Exception e)
            {
                StatInPlaceFailed++;
                if (!QuietRejects) CoopBehaviour.Log("shared: in-place plant update failed (" + Short(e) + "); rebuilding my copy of that plant");
                byte[] mine = null;
                try { mine = SnapshotOf(rootId); } catch (Exception) { }          // so a rebuild that fails too can be undone
                try { RemovePlantCopy(rootId); LoadUpdate(data); Status = "a friend's plant rebuilt"; StatRebuilt++; }
                catch (Exception e2)
                {
                    StatApplyFailed++;
                    if (!QuietRejects) CoopBehaviour.Log("shared: rebuilding the plant failed too: " + Short(e2));
                    if (mine != null)
                    {
                        try { RemovePlantCopy(rootId); LoadUpdate(mine); if (!QuietRejects) CoopBehaviour.Log("shared: put my own plant back"); }
                        catch (Exception e3) { CoopBehaviour.Log("shared: could not put my own plant back: " + Short(e3)); }
                    }
                    if (from != 0 && IAmGuest && from == LeaderId) { WorldSync.LoadedEpoch = 0; CoopBehaviour.Log("shared: the leader's plant could not be applied here; copying their whole world again"); }
                    return;
                }
            }
            if (Time.time - lastPlantToast > 8f) { lastPlantToast = Time.time; CoopUI.Toast("BEANSTALK SIGNAL RECEIVED"); }
            EnsureGrowingBeanstalk();
            // what I have now is what they have: do not send it straight back
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                string id = IdOf(pr);
                if (id != null && id == rootId) { lastSig[id] = Signature(pr); dirty.Remove(id); lastSent[id] = Time.time; }
            }
            if (from != 0 && IAmGuest && number >= 0) plantNumber[rootId] = number;   // what I build on from now
            if (from != 0 && IAmLeader)
            {
                // what I took goes to everybody, with a new number; another guest's copy built on the old one is now out of date
                plantNumber[rootId] = Math.Max(leaderMine, number) + 1;
                int took; takenFrom[rootId] = new KeyValuePair<ulong, int>(from, number); takenSig[rootId] = lastSig.TryGetValue(rootId, out took) ? took : 0;
                realChange.Add(rootId);                                    // what I took is real growth: an older copy can never undo it
                if (lease != null) { lease.Took = true; leaseEcho.Add(rootId); }   // passed on once their vine is done
                else { lastSent.Remove(rootId); ForceSend(rootId); }
                try { ConnectNewIslands(attachedBefore, connBefore); } catch (Exception e) { CoopBehaviour.Log("shared: islands of a friend's vine: " + Short(e)); }
                RaiseIslandsToAttached();
            }
        }

        static int CurrentSignature(string rootId)
        {
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot))) if (IdOf(pr) == rootId) return Signature(pr);
            return 0;
        }

        // ---- teleporters -------------------------------------------------------------------------------------------------------------
        // Switching a teleporter on (its levers) unlocks it for the whole team: the message is just its number, a byte. Only a change from locked to unlocked is announced.
        static readonly HashSet<int> teleKnown = new HashSet<int>();
        static bool teleSeen;

        static void CheckTeleporters()
        {
            foreach (BudTeleporter t in UnityEngine.Object.FindObjectsOfType(typeof(BudTeleporter)))
            {
                int n = t.m_TeleporterNumber;
                if (n < 0 || n > 99 || !t.Unlocked) continue;
                if (teleKnown.Add(n) && teleSeen && !applying && Time.time >= quietUntil)
                {
                    outbox.Add(new byte[] { MsgTeleporter, (byte)n });
                    CoopBehaviour.Log("shared: teleporter " + n + " unlocked, told the others");
                }
            }
            teleSeen = true;
        }

        static void ApplyTeleporter(byte[] m)
        {
            if (m.Length != 2 || m[1] > 99) throw new FormatException("bad teleporter message");
            int n = m[1];
            teleKnown.Add(n);                                  // it is their unlock: never send it back
            foreach (BudTeleporter t in UnityEngine.Object.FindObjectsOfType(typeof(BudTeleporter)))
            {
                if (t.m_TeleporterNumber != n || t.Unlocked) continue;
                applying = true;
                try { t.ForceActivate(); } finally { applying = false; }
                CoopUI.Toast("TELEPORTER " + n + " ONLINE");
                CoopBehaviour.Log("shared: teleporter " + n + " unlocked by a friend");
            }
        }

        public static byte[] TestTeleporterMessage(int n) { return new byte[] { MsgTeleporter, (byte)n }; }

        // ---- islands -----------------------------------------------------------------------------------------------------------------
        // Connecting a floating island to the beanstalk changes more than the plant: the game's island counter (PlayerAnalytics) goes up, which
        // drives the "3/6" islands icon and the next growth stage, and a HUD message pops up. None of that is part of the plant data, so it is sent as its own event.

        static int lastIslandCount = -1;
        static readonly HashSet<string> attachedIds = new HashSet<string>();

        static FieldInfo growingBeanstalkField;

        // The islands HUD reads the counter from the trunk that "is currently growing"; on a player who never grew it themselves that is empty.
        static void EnsureGrowingBeanstalk()
        {
            try
            {
                if (PlantBranch.CurrentlyGrowingBeanstalk != null) return;
                PlantRoot best = null;
                foreach (PlantRoot r in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
                    if (r.IsTrunk() && r.TrunkGrowthFraction > 0.0001f && (best == null || r.TrunkGrowthFraction > best.TrunkGrowthFraction)) best = r;
                if (best == null) return;
                if (growingBeanstalkField == null) growingBeanstalkField = typeof(PlantBranch).GetField("s_currentlyGrowingBeanstalk", BindingFlags.NonPublic | BindingFlags.Static);
                if (growingBeanstalkField != null) growingBeanstalkField.SetValue(null, best);
            }
            catch (Exception e) { CoopBehaviour.Log("shared: islands hud: " + Short(e)); }
        }

        static void CheckIslands()
        {
            if (!Singleton<PlayerAnalytics>.Exists) return;
            int now = Singleton<PlayerAnalytics>.Instance.m_nutrientIslandsAttched;
            var current = new HashSet<string>();
            foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider)))
            {
                if (!n.m_attached) continue;
                var si = n.GetComponent<SaveInfo>();
                if (si != null && !string.IsNullOrEmpty(si.m_uniqueID)) current.Add(si.m_uniqueID);
            }
            string fresh = "";
            foreach (var id in current) if (!attachedIds.Contains(id)) { fresh = id; break; }
            attachedIds.Clear();
            foreach (var id in current) attachedIds.Add(id);
            int before = lastIslandCount;
            lastIslandCount = now;
            if (before < 0 || applying || Time.time < quietUntil || now <= before) return;   // first look, world just loaded, or a counter that went down

            // I connected an island: tell everybody, and send the trunk right away
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgIsland); w.Write(MyId); w.Write(now); w.Write(fresh);
            outbox.Add(ms.ToArray());
            CoopBehaviour.Log("shared: I connected an island (" + now + " connected" + (fresh.Length > 0 ? ", " + fresh : "") + ")");
            foreach (PlantRoot r in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                string id = IdOf(r);
                if (id == null || !r.IsTrunk() || OwnedByOther(id)) continue;
                if (IAmGuest && !growing.ContainsKey(id) && !finishing.Contains(id)) continue;   // a guest's vine brings the island with its plant; the leader grows the trunk for it
                lastSent.Remove(id); dirty.Add(id);
            }
        }

        static void ApplyIsland(byte[] msg, ulong from)
        {
            var r = new BinaryReader(new MemoryStream(msg)); r.ReadByte();
            ulong sender = SenderOf(from, r.ReadUInt64()); int counter = r.ReadInt32(); string pid = r.ReadString();
            if (!WorldSync.InWorld() || !Singleton<PlayerAnalytics>.Exists) return;
            var pa = Singleton<PlayerAnalytics>.Instance;
            // the next growth stage depends on this number, so a damaged or hostile value must not move it: the game has a few dozen islands at most
            if (counter < 0 || counter > MaxIslands || counter > pa.m_nutrientIslandsAttched + 8) throw new FormatException("island count out of range: " + counter);
            if (from != 0 && IAmLeader)
            {
                // the leader's save counts only islands connected in the leader's world: they come with the guest's plant, once it is taken
                RaiseIslandsToAttached();
                return;
            }
            if (counter > pa.m_nutrientIslandsAttched) pa.m_nutrientIslandsAttched = counter;
            lastIslandCount = pa.m_nutrientIslandsAttched;                                      // it is not my connection: never send it back
            EnsureGrowingBeanstalk();
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                string id = IdOf(pr);
                if (id != null && pr.IsTrunk()) owner[id] = new KeyValuePair<ulong, float>(sender, Time.time + OwnerHold);   // they are growing the trunk now
            }
            if (!string.IsNullOrEmpty(pid))
            {
                attachedIds.Add(pid);
                foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider)))
                {
                    var si = n.GetComponent<SaveInfo>();
                    if (si == null || si.m_uniqueID != pid) continue;
                    try { n.StartCoroutine("ShowCurrentBeanstalkHeight"); } catch (Exception e) { CoopBehaviour.Log("shared: islands hud popup: " + Short(e)); }   // same altitude + "n/6" popup the connecting player gets
                    break;
                }
            }
            Status = "a friend connected an island";
            CoopUI.Toast("A FRIEND CONNECTED AN ISLAND (" + pa.m_nutrientIslandsAttched + ")");
            CoopBehaviour.Log("shared: a friend connected an island; island count is now " + pa.m_nutrientIslandsAttched);
        }

        public static byte[] TestShardMessage(string guid)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgShard); w.Write(guid);
            return ms.ToArray();
        }

        public static byte[] TestIslandMessage(ulong sender, int counter, string providerId)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgIsland); w.Write(sender); w.Write(counter); w.Write(providerId ?? "");
            return ms.ToArray();
        }

        // ---- per frame -------------------------------------------------------------------------------------------------------------

        static bool OwnedByOther(string id)
        {
            KeyValuePair<ulong, float> o;
            return owner.TryGetValue(id, out o) && o.Value > Time.time && o.Key != MyId;
        }

        // ---- self test hooks (see SelfTest.cs) ---------------------------------------------------------------------------------------

        public static string PlantId(PlantRoot r) { return IdOf(r); }
        public static int PlantSignature(PlantRoot r) { return Signature(r); }
        public static byte[] TestRaw(PlantRoot r) { InitReflection(); return Serialize(GroupFor(r)); }

        // The same packet Tick would send, as if a friend with the given id had sent it.
        public static byte[] TestMessage(PlantRoot r, ulong sender, byte[] raw)
        {
            byte[] comp = Compress(raw);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgPlant); w.Write(IdOf(r)); w.Write(sender); w.Write(raw.Length); w.Write(comp.Length); w.Write(Lz.Checksum(raw)); w.Write(comp); WriteKeep(w, r);
            return ms.ToArray();
        }

        public static void TestApply(byte[] msg) { ApplyPlant(msg, 0UL); }
        public static void TestApplyFrom(byte[] msg, ulong from) { ApplyPlant(msg, from); }

        // A numbered plant message, as a guest that built on the leader's plant number `number` would send it.
        public static byte[] TestNumberedMessage(PlantRoot r, ulong sender, byte[] raw, int number) { return TestNumberedMessage(r, sender, raw, number, 0UL, -1); }
        public static byte[] TestNumberedMessage(PlantRoot r, ulong sender, byte[] raw, int number, ulong echoOf, int echoNumber)
        {
            byte[] comp = Compress(raw);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgPlant2); w.Write(IdOf(r)); w.Write(sender); w.Write(number); w.Write(echoOf); w.Write(echoNumber); w.Write(raw.Length); w.Write(comp.Length); w.Write(Lz.Checksum(raw)); w.Write(comp); WriteKeep(w, r);
            return ms.ToArray();
        }
        public static void SetPlantNumberForTest(string rootId, int n) { plantNumber[rootId] = n; }
        public static void TestRevert(string rootId, byte[] raw) { InitReflection(); RemovePlantCopy(rootId); LoadUpdate(raw); }
        public static void TestApplyWaiting() { ApplyWaiting(); }
        public static int TestWaiting { get { return waiting.Count; } }
        public static int TestOnPlantState(string rootId) { return OnPlantState(rootId); }
        // a plant update whose list of grown parts leaves one out: the receiver removes that part (PruneExtras)
        public static byte[] TestMessageDropping(PlantRoot r, ulong sender, byte[] raw, string dropId)
        {
            byte[] comp = Compress(raw);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(MsgPlant); w.Write(IdOf(r)); w.Write(sender); w.Write(raw.Length); w.Write(comp.Length); w.Write(Lz.Checksum(raw)); w.Write(comp); WriteKeep(w, r, dropId);
            return ms.ToArray();
        }
        // one guard tick around a point, with the same "just rebuilt" rule GuardTick uses
        public static int TestGuardAt(Vector3 at) { return GuardCollision(at, GuardRadius, Time.time < guardBoostUntil); }
        public static void TestGuardTick() { GuardTick(); }
        public static void EndGuardBoostForTest() { guardBoostUntil = 0f; nextGuard = 0f; }
        public static Collider FirstStemColliderNear(Vector3 at)
        {
            foreach (PlantBranch b in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch)))
            {
                if (b == null || b.m_PlayerLockedOn) continue;
                foreach (var node in b.m_Stem)
                    if (node != null && node.m_StemObject != null && (node.m_StemObject.transform.position - at).sqrMagnitude < 100f)
                        foreach (var c in StemColliders(node)) if (c != null && c.enabled) return c;
            }
            return null;
        }
        public static void TestForget(string rootId) { owner.Remove(rootId); lastSig.Remove(rootId); lastSent.Remove(rootId); lastBytes.Remove(rootId); dirty.Remove(rootId); }

        // a guest not yet in the leader's world: whatever it noticed in its own world is thrown away, never sent
        public static void Hold() { outbox.Clear(); lastSig.Clear(); dirty.Clear(); forced.Clear(); growing.Clear(); finishing.Clear(); StorySync.Hold(); }

        public static void Tick(ITransport broadcast)
        {
            if (TestSink != null) broadcast = TestSink;
            if (!Enabled || broadcast == null) return;
            Hook();
            StorySync.Tick(broadcast);      // cutscenes and story flags (every frame: a skip must go out at once)
            foreach (var m in outbox) broadcast.SendReliable(m);
            outbox.Clear();
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 0.5f;
            if (!WorldSync.InWorld() || !InitReflection()) return;

            ApplyWaiting();
            try { TickGrowing(); if (IAmLeader) TickLeases(); } catch (Exception e) { CoopBehaviour.Log("shared: vines: " + Short(e)); }
            try { CheckIslands(); } catch (Exception e) { CoopBehaviour.Log("shared: islands: " + Short(e)); }
            try { CheckTeleporters(); } catch (Exception e) { CoopBehaviour.Log("shared: teleporters: " + Short(e)); }
            try { GuardTick(); } catch (Exception e) { CoopBehaviour.Log("collision: " + Short(e)); }
            foreach (var m in outbox) broadcast.SendReliable(m);
            outbox.Clear();

            bool settling = Time.time < quietUntil;
            foreach (PlantRoot root in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                string id = IdOf(root);
                if (id == null) continue;
                int sig = Signature(root);
                bool changedThisTick = false;
                if (IAmLeader && leases.ContainsKey(id)) { lastSig[id] = sig; continue; }       // a friend grows a vine on it: their copy counts until they are done; I send nothing of it
                float since; if (!lastSent.TryGetValue(id, out since)) since = -99f;
                bool finish = IAmGuest && finishing.Contains(id);                               // my vine is done: the leader gets the final plant now
                bool forceNow = finish || (IAmLeader && forced.Contains(id) && Time.time - since >= 2f);    // the leader passes on a plant it took (or answers an outdated one)
                if (forceNow) { forced.Remove(id); lastSig[id] = sig; }
                else
                {
                    if (IAmGuest && waiting.ContainsKey(id)) continue;       // the leader's newer plant waits for me to step off: build nothing on my older copy
                    int old;
                    if (!lastSig.TryGetValue(id, out old)) { lastSig[id] = sig; continue; }   // first look: just remember
                    changedThisTick = sig != old;
                    if (changedThisTick)
                    {
                        lastSig[id] = sig;
                        // someone else is growing it: my copy changes only because I got their update. The leader's own growth always counts (and makes guests'
                        // copies old: LeaderHasUnsent, and the number goes up when it is sent).
                        if (IAmLeader && !settling) dirty.Add(id);
                        else if (IAmGuest) { if (!settling && growing.ContainsKey(id)) dirty.Add(id); }   // a guest sends only a vine it grows itself, never its trunk creeping up
                        else if (!settling && !OwnedByOther(id)) dirty.Add(id);
                    }
                    if (!dirty.Contains(id)) continue;
                    // while it keeps changing send at most every ResendInterval (3-30 s by size); once it has been still for a tick, send the final state,
                    // but never sooner than MinResend after the last one (a plant that grows in short bursts would otherwise send every second)
                    int lastSize; lastBytes.TryGetValue(id, out lastSize);
                    if (Time.time - since < (changedThisTick ? ResendInterval(lastSize) : MinResend)) continue;
                }
                try
                {
                    byte[] raw = Serialize(GroupFor(root));
                    byte[] comp = Compress(raw);
                    int number; plantNumber.TryGetValue(id, out number);
                    if (IAmLeader) { number++; plantNumber[id] = number; }
                    var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                    if (LeaderId != 0)
                    {
                        // in a party: numbered (the leader's is the real one). A guest plant I pass on unchanged says whose it is, so that guest does not put its own growth back.
                        KeyValuePair<ulong, int> tf; int ts; ulong echoOf = 0UL; int echoNumber = -1;
                        if (IAmLeader && takenFrom.TryGetValue(id, out tf) && takenSig.TryGetValue(id, out ts) && (ts == sig || leaseEcho.Contains(id))) { echoOf = tf.Key; echoNumber = tf.Value; }
                        if (IAmLeader) { takenFrom.Remove(id); takenSig.Remove(id); leaseEcho.Remove(id); }
                        if (IAmLeader && realChange.Contains(id)) { realNumber[id] = number; if (!growing.ContainsKey(id)) realChange.Remove(id); }   // older guest copies can no longer undo this
                        if (IAmGuest) { int b; if (!sentBase.TryGetValue(id, out b) || b != number) { sentBase[id] = number; sentFirstSig[id] = sig; } }
                        w.Write(MsgPlant2); w.Write(id); w.Write(MyId); w.Write(number); w.Write(echoOf); w.Write(echoNumber);
                    }
                    else { w.Write(MsgPlant); w.Write(id); w.Write(MyId); }                                // no party: as before, everybody equal
                    w.Write(raw.Length); w.Write(comp.Length); w.Write(Lz.Checksum(raw)); w.Write(comp); WriteKeep(w, root);
                    broadcast.SendReliable(ms.ToArray());
                    lastSent[id] = Time.time;
                    lastBytes[id] = comp.Length;
                    owner[id] = new KeyValuePair<ulong, float>(MyId, Time.time + OwnerHold);
                    if (!changedThisTick) dirty.Remove(id);
                    CoopBehaviour.Log("shared: sent plant " + id + " (" + raw.Length + " bytes, " + comp.Length + " compressed)" + (finish ? ", the final one of my vine" : ""));
                }
                catch (Exception e) { CoopBehaviour.Log("shared: sending plant failed " + Short(e)); dirty.Remove(id); }
                if (finish)
                {
                    // after the plant (same reliable channel, so it arrives after it): the leader passes it on now
                    finishing.Remove(id); dirty.Remove(id);
                    int n; plantNumber.TryGetValue(id, out n);
                    broadcast.SendReliable(GrowMessage(GrowEnd, id, MyId, n));
                    CoopBehaviour.Log("shared: my vine on plant " + id + " is done; told the leader");
                }
            }
        }

        public static void Handle(byte[] msg) { Handle(msg, 0UL); }

        // Who a plant or island is credited to: the player whose connection it came in on. Only the self test (which builds its own messages) passes 0 and gets the sender written inside the message.
        public static ulong SenderOf(ulong fromConnection, ulong senderInMessage) { return fromConnection != 0 ? fromConnection : senderInMessage; }

        public static void Handle(byte[] msg, ulong from)
        {
            if (!Enabled || msg == null || msg.Length == 0) return;
            try
            {
                if (msg[0] == MsgShard) ApplyShard(msg);
                else if (msg[0] == MsgPlant || msg[0] == MsgPlant2) ApplyPlant(msg, from);
                else if (msg[0] == MsgIsland) ApplyIsland(msg, from);
                else if (msg[0] == MsgTeleporter) ApplyTeleporter(msg);
                else if (msg[0] == MsgPickup) ApplyPickup(msg);
                else if (msg[0] == MsgGrow) ApplyGrow(msg, from);
                else if (msg[0] == StorySync.Msg) StorySync.Handle(msg, from);
            }
            catch (Exception e) { StatRejected++; if (!QuietRejects) CoopBehaviour.Log("shared: rejected a bad message (" + Short(e) + ")"); }
        }
    }
}
