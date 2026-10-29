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
        public static void ForgetWorld() { plantNumber.Clear(); forced.Clear(); waiting.Clear(); owner.Clear(); dirty.Clear(); lastSig.Clear(); ForgetEchoes(); }
        static void ForgetEchoes() { takenFrom.Clear(); takenSig.Clear(); sentBase.Clear(); sentFirstSig.Clear(); }
        public static void MarkSeenForTest(PlantRoot r) { string id = IdOf(r); lastSig[id] = Signature(r); dirty.Remove(id); }
        public static void MarkDirtyForTest(string rootId) { dirty.Add(rootId); }

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

        public static bool IsMessage(byte[] m) { return m != null && m.Length > 0 && (m[0] == MsgPlant || m[0] == MsgPlant2 || m[0] == MsgShard || m[0] == MsgIsland || m[0] == MsgTeleporter); }

        // After the whole world was replaced (join): forget what I knew and give things a few seconds to settle.
        public static void Rebaseline() { quietUntil = Time.time + 6f; teleKnown.Clear(); teleSeen = false; lastSig.Clear(); dirty.Clear(); owner.Clear(); lastIslandCount = -1; attachedIds.Clear(); plantNumber.Clear(); forced.Clear(); waiting.Clear(); ForgetEchoes(); }

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
        static bool OnPlant(string rootId)
        {
            if (TestPlayerOnPlant.HasValue) return TestPlayerOnPlant.Value;
            var me = GameObject.Find("Walkster");
            if (me == null) return false;
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                if (IdOf(pr) != rootId) continue;
                foreach (var t in pr.GetComponentsInChildren<Transform>(true))
                    if ((t.position - me.transform.position).sqrMagnitude < NearPlant * NearPlant) return true;
                return false;
            }
            return false;
        }

        // Applies the waited updates of the plants I have stepped off.
        static void ApplyWaiting()
        {
            foreach (var id in new List<string>(waiting.Keys))
            {
                if (OnPlant(id)) continue;
                var w = waiting[id];
                waiting.Remove(id);
                try { ApplyPlant(w.Value, w.Key); CoopBehaviour.Log("shared: applied a friend plant update that waited while I stood on the plant"); }
                catch (Exception e) { StatRejected++; CoopBehaviour.Log("shared: a waited plant update failed (" + Short(e) + ")"); }
            }
        }

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
                foreach (var go in doomed) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                pr.m_Branches.Clear();
            }
        }

        // The grown parts of a plant (their ids) travel after the update, so a receiver can tell which of ITS parts the sender no longer has. An in-place update never removes anything,
        // so without this a PC that once had more branches kept them for ever: extra physics objects, a plant that differs between players, and a game that gets slower.
        const int MaxKeep = 20000;
        static void WriteKeep(BinaryWriter w, PlantRoot root)
        {
            var ids = new List<string>();
            foreach (var si in root.GetComponentsInChildren<SaveInfo>(true))
                if (si != null && si.gameObject != root.gameObject && si.m_runtimeSpawned && !string.IsNullOrEmpty(si.m_uniqueID) && si.m_uniqueID.Length <= 64) ids.Add(si.m_uniqueID);
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
                foreach (var si in pr.GetComponentsInChildren<SaveInfo>(true))
                    if (si != null && si.gameObject != pr.gameObject && si.m_runtimeSpawned && !keep.Contains(si.m_uniqueID)) doomed.Add(si.gameObject);
                if (doomed.Count == 0) return;
                foreach (var go in doomed)
                {
                    if (go == null) continue;
                    foreach (var k in go.GetComponentsInChildren<SaveInfo>(true))
                        if (k != null && k.gameObject != go && (keep.Contains(k.m_uniqueID) || !k.m_runtimeSpawned)) k.transform.parent = pr.transform;   // a part that stays never goes down with a part that goes
                    if (pr.m_CrownObject != null && pr.m_CrownObject.IsChildOf(go.transform)) pr.m_CrownObject.parent = pr.transform;
                }
                foreach (var go in doomed) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                pr.m_Branches.RemoveAll(b => b == null);
                CoopBehaviour.Log("shared: removed " + doomed.Count + " parts my copy of the plant had and the sender does not");
            }
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
            if (from != 0 && IAmGuest && echoOf != 0 && echoOf == MyId)
            {
                int myBase;
                if (plantNumber.TryGetValue(rootId, out myBase) ? myBase == echoNumber : echoNumber == 0)
                {
                    // the leader took my plant and passes it on as it was: I have that already (and maybe more). Build on its number; nothing is put back.
                    StatEchoes++;
                    plantNumber[rootId] = number; waiting.Remove(rootId);
                    int firstSig; bool grewSince = !sentFirstSig.TryGetValue(rootId, out firstSig) || CurrentSignature(rootId) != firstSig;
                    if (grewSince) dirty.Add(rootId);        // what I grew after that first send goes to the leader on the new number
                    sentBase.Remove(rootId); sentFirstSig.Remove(rootId);
                    return;
                }
            }
            if (from != 0 && IAmLeader)
            {
                if (MayTakeFrom != null && !MayTakeFrom(from)) return;     // not (or no longer) in my world
                int mine; plantNumber.TryGetValue(rootId, out mine);
                if (number != mine || LeaderHasUnsent(rootId))
                {
                    // built on an older copy of my plant: taking it would undo my newer growth. They get mine instead.
                    StatStale++;
                    if (Time.realtimeSinceStartup - lastStaleLog > 10f) { lastStaleLog = Time.realtimeSinceStartup; CoopBehaviour.Log("shared: a friend's plant was built on an older copy of mine (" + number + ", mine is " + mine + "); not taken, sending them mine"); }
                    ForceSend(rootId);
                    return;
                }
            }

            owner[rootId] = new KeyValuePair<ulong, float>(sender, Time.time + OwnerHold);   // they are growing it: I only receive until they are done
            if (OnPlant(rootId)) { waiting[rootId] = new KeyValuePair<ulong, byte[]>(from, msg); if (!QuietRejects) CoopBehaviour.Log("shared: a friend plant update waits until I step off that plant"); return; }   // the owner is already set above, so my copy stays quiet meanwhile
            try { if (TestFailInPlaceOnce) { TestFailInPlaceOnce = false; throw new InvalidOperationException("[test] injected in-place failure"); } LoadUpdate(data); Status = "a friend's plant updated"; StatApplied++; if (keep != null) PruneExtras(rootId, keep); }
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
                plantNumber[rootId] = number + 1;
                int took; takenFrom[rootId] = new KeyValuePair<ulong, int>(from, number); takenSig[rootId] = lastSig.TryGetValue(rootId, out took) ? took : 0;
                lastSent.Remove(rootId); ForceSend(rootId); RaiseIslandsToAttached();
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
        public static void TestForget(string rootId) { owner.Remove(rootId); lastSig.Remove(rootId); lastSent.Remove(rootId); lastBytes.Remove(rootId); dirty.Remove(rootId); }

        // a guest not yet in the leader's world: whatever it noticed in its own world is thrown away, never sent
        public static void Hold() { outbox.Clear(); lastSig.Clear(); dirty.Clear(); forced.Clear(); }

        public static void Tick(ITransport broadcast)
        {
            if (TestSink != null) broadcast = TestSink;
            if (!Enabled || broadcast == null) return;
            Hook();
            foreach (var m in outbox) broadcast.SendReliable(m);
            outbox.Clear();
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 0.5f;
            if (!WorldSync.InWorld() || !InitReflection()) return;

            ApplyWaiting();
            try { CheckIslands(); } catch (Exception e) { CoopBehaviour.Log("shared: islands: " + Short(e)); }
            try { CheckTeleporters(); } catch (Exception e) { CoopBehaviour.Log("shared: teleporters: " + Short(e)); }
            foreach (var m in outbox) broadcast.SendReliable(m);
            outbox.Clear();

            bool settling = Time.time < quietUntil;
            foreach (PlantRoot root in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                string id = IdOf(root);
                if (id == null) continue;
                int sig = Signature(root);
                bool changedThisTick = false;
                float since; if (!lastSent.TryGetValue(id, out since)) since = -99f;
                bool forceNow = IAmLeader && forced.Contains(id) && Time.time - since >= 2f;    // the leader passes on a plant it took (or answers an outdated one)
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
                        // someone else is growing it: my copy changes only because I got their update. The leader's own growth always counts (and makes guests' copies old).
                        if (IAmLeader && !settling) { dirty.Add(id); int n; plantNumber.TryGetValue(id, out n); plantNumber[id] = n + 1; }
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
                        if (IAmLeader && takenFrom.TryGetValue(id, out tf) && takenSig.TryGetValue(id, out ts) && ts == sig) { echoOf = tf.Key; echoNumber = tf.Value; }
                        if (IAmLeader) { takenFrom.Remove(id); takenSig.Remove(id); }
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
                    CoopBehaviour.Log("shared: sent plant " + id + " (" + raw.Length + " bytes, " + comp.Length + " compressed)");
                }
                catch (Exception e) { CoopBehaviour.Log("shared: sending plant failed " + Short(e)); dirty.Remove(id); }
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
            }
            catch (Exception e) { StatRejected++; if (!QuietRejects) CoopBehaviour.Log("shared: rejected a bad message (" + Short(e) + ")"); }
        }
    }
}
