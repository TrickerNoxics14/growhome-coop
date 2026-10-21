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
    // Switch the whole thing off by creating coop-noshare.txt next to the exe.
    public static class SharedWorld
    {
        const byte MsgPlant = 4, MsgShard = 5;
        public static bool Enabled = true;
        public static string Status = "";

        static MethodInfo miSaveObject, miLoadScene;
        static bool reflectionTried, reflectionOk;
        static bool hooked;
        static bool applying;                               // true while I apply something received: never echo it back
        static float quietUntil;                            // after a world load, ignore the settling period

        static readonly Dictionary<string, int> lastSig = new Dictionary<string, int>();
        static readonly Dictionary<string, float> lastSent = new Dictionary<string, float>();
        static readonly HashSet<string> dirty = new HashSet<string>();
        static readonly List<byte[]> outbox = new List<byte[]>();
        static float nextCheck, lastPlantToast;

        public static bool IsMessage(byte[] m) { return m != null && m.Length > 0 && (m[0] == MsgPlant || m[0] == MsgShard); }

        // After the whole world was replaced (F6/F7, join): forget what I knew and give things a few seconds to settle.
        public static void Rebaseline() { quietUntil = Time.time + 6f; lastSig.Clear(); dirty.Clear(); }

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
        static byte[] Serialize(List<SaveInfo> infos)
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

        static void ApplyPlant(byte[] msg)
        {
            if (!WorldSync.InWorld() || !InitReflection()) return;
            var r = new BinaryReader(new MemoryStream(msg)); r.ReadByte();
            string rootId = r.ReadString();
            int len = r.ReadInt32();
            byte[] data = r.ReadBytes(len);

            var existing = new List<SaveInfo>();
            var prior = new List<bool>();
            foreach (SaveInfo si in UnityEngine.Object.FindObjectsOfType(typeof(SaveInfo))) { existing.Add(si); prior.Add(si.m_hasSaveData); si.m_hasSaveData = true; }   // keeps the loader from deleting everything not in this update
            applying = true;
            try
            {
                SaveSystem.m_loadedDataVersion = 91;
                var ser = new Serializer(false); ser.Bytes = data;
                var block = new SaveBlock(false); block.Serialize(ser);
                miLoadScene.Invoke(null, new object[] { block });
                Status = "a friend's plant updated"; if (Time.time - lastPlantToast > 8f) { lastPlantToast = Time.time; CoopUI.Toast("BEANSTALK SIGNAL RECEIVED"); }
            }
            catch (Exception e) { CoopBehaviour.Log("shared: applying plant failed " + e.GetType().Name + " " + (e.InnerException != null ? e.InnerException.Message : e.Message)); }
            finally
            {
                applying = false;
                SaveSystem.loadingScene = false;
                for (int i = 0; i < existing.Count; i++) if (existing[i] != null) existing[i].m_hasSaveData = prior[i];
            }
            // what I have now is what they have: do not send it straight back
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                string id = IdOf(pr);
                if (id != null) { lastSig[id] = Signature(pr); dirty.Remove(id); lastSent[id] = Time.time; }
            }
        }

        // ---- per frame -------------------------------------------------------------------------------------------------------------

        public static void Tick(ITransport broadcast)
        {
            if (!Enabled || broadcast == null) return;
            Hook();
            foreach (var m in outbox) broadcast.SendReliable(m);
            outbox.Clear();
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 0.5f;
            if (!WorldSync.InWorld() || !InitReflection()) return;

            bool settling = Time.time < quietUntil;
            foreach (PlantRoot root in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                string id = IdOf(root);
                if (id == null) continue;
                int sig = Signature(root);
                int old;
                if (!lastSig.TryGetValue(id, out old)) { lastSig[id] = sig; continue; }   // first look: just remember
                if (sig != old)
                {
                    lastSig[id] = sig;
                    if (!settling) dirty.Add(id);
                }
                if (!dirty.Contains(id)) continue;
                float since; if (!lastSent.TryGetValue(id, out since)) since = -99f;
                bool changedThisTick = sig != old;
                // while it keeps changing send once a second; once it has been still for a tick, send the final state
                if (Time.time - since >= 1f || !changedThisTick)
                {
                    try
                    {
                        byte[] data = Serialize(GroupFor(root));
                        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                        w.Write(MsgPlant); w.Write(id); w.Write(data.Length); w.Write(data);
                        broadcast.SendReliable(ms.ToArray());
                        lastSent[id] = Time.time;
                        if (!changedThisTick) dirty.Remove(id);
                        CoopBehaviour.Log("shared: sent plant " + id + " (" + data.Length + " bytes)");
                    }
                    catch (Exception e) { CoopBehaviour.Log("shared: sending plant failed " + e.GetType().Name + " " + (e.InnerException != null ? e.InnerException.Message : e.Message)); dirty.Remove(id); }
                }
            }
        }

        public static void Handle(byte[] msg)
        {
            if (!Enabled || msg == null || msg.Length == 0) return;
            try
            {
                if (msg[0] == MsgShard) ApplyShard(msg);
                else if (msg[0] == MsgPlant) ApplyPlant(msg);
            }
            catch (Exception e) { CoopBehaviour.Log("shared: " + e.GetType().Name + " " + e.Message); }
        }
    }
}
