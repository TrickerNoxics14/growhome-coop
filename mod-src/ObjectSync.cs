using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CoopMod
{
    // Objects a player is holding (and for 1.5 s after letting go): the holder is authoritative and the other PC steers its copy to match.
    // Objects are identified the same way on both PCs: by SaveInfo id when they have one, otherwise by their place in the scene hierarchy.
    public static class ObjectSync
    {
        const float ReleaseTail = 1.5f;

        class Target { public Rigidbody rb; public Vector3 pos, vel; public Quaternion rot; public float time; }
        class Tracked { public Rigidbody rb; public float until; }

        static readonly Dictionary<int, Rigidbody> map = new Dictionary<int, Rigidbody>();
        static float mapBuilt = -99f;
        static readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();   // what I hold or just released
        static readonly Dictionary<ulong, List<Target>> targetsByPeer = new Dictionary<ulong, List<Target>>();   // per other player: what they hold or just released
        static readonly HashSet<int> reportedMissing = new HashSet<int>();
        static readonly HashSet<int> following = new HashSet<int>();
        static float nextFollowLog;

        // FNV-1a so the hash is identical on every machine.
        public static int Hash(string s)
        {
            uint h = 2166136261u;
            if (s != null) foreach (char c in s) { h ^= c; h *= 16777619u; }
            return (int)h;
        }

        static bool IsPlayerPart(Transform t) { return t.root != null && t.root.name.StartsWith("Walkster"); }

        // Scene-hierarchy path with sibling indices, e.g. "Level/Cactus[3]/CactusFlower[1]"; identical on both PCs for scene-defined objects.
        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name + "[" + t.GetSiblingIndex() + "]");
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        public static Rigidbody BodyOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                if (t.rigidbody != null) return t.rigidbody;
            }
            return null;
        }

        public static int KeyOf(Rigidbody rb)
        {
            var si = rb.GetComponent<SaveInfo>();
            if (si != null && !string.IsNullOrEmpty(si.m_uniqueID)) return Hash("S" + si.m_uniqueID);
            return Hash("P" + PathOf(rb.transform));
        }

        public static string Describe(Transform t)
        {
            if (t == null) return "-";
            var rb = BodyOf(t);
            return t.name + " tag=" + t.tag + " layer=" + LayerMask.LayerToName(t.gameObject.layer)
                + (rb == null ? " body=none" : " body=" + rb.name + "#" + KeyOf(rb) + (rb.isKinematic ? " kinematic" : " physics") + (rb.GetComponent<SaveInfo>() != null ? " saved" : " scene-object"));
        }

        // What I hold, plus what I let go of in the last 1.5 s.
        public static PlayerState.Held[] Collect(BipedHandsScript hands)
        {
            float now = Time.time;
            if (hands != null)
            {
                for (int i = 0; i < hands.hand.Length && i < 2; i++)
                {
                    var h = hands.hand[i];
                    if (!h.grabbing || h.grabbedObject == null) continue;
                    var rb = BodyOf(h.grabbedObject);
                    if (rb == null || rb.isKinematic || IsPlayerPart(rb.transform)) continue;
                    int id = KeyOf(rb);
                    Tracked tr;
                    if (!tracked.TryGetValue(id, out tr)) { tr = new Tracked(); tracked[id] = tr; }
                    if (tr.rb != rb) CoopBehaviour.Log("objects: I hold #" + id + " " + rb.name + " at " + rb.position + (rb.GetComponent<SaveInfo>() != null ? " (saved)" : " (scene object)"));
                    tr.rb = rb; tr.until = now + ReleaseTail;
                }
            }
            var list = new List<PlayerState.Held>();
            var gone = new List<int>();
            foreach (var kv in tracked)
            {
                if (kv.Value.rb == null || now > kv.Value.until) { gone.Add(kv.Key); continue; }
                var b = kv.Value.rb;
                list.Add(new PlayerState.Held { id = kv.Key, pos = b.position, rot = b.rotation, vel = b.velocity });
                if (list.Count >= 6) break;
            }
            foreach (var g in gone) tracked.Remove(g);
            return list.ToArray();
        }

        static Rigidbody Lookup(int id)
        {
            Rigidbody rb;
            if (map.TryGetValue(id, out rb) && rb != null) return rb;
            if (Time.time - mapBuilt > 3f)
            {
                mapBuilt = Time.time;
                map.Clear();
                foreach (Rigidbody b in Object.FindObjectsOfType(typeof(Rigidbody)))
                {
                    if (b.isKinematic || IsPlayerPart(b.transform)) continue;
                    map[KeyOf(b)] = b;
                }
                if (map.TryGetValue(id, out rb)) return rb;
            }
            return null;
        }

        // Called when a packet from the peer arrives.
        public static void SetRemoteHeld(ulong peer, PlayerState.Held[] held)
        {
            List<Target> targets;
            if (!targetsByPeer.TryGetValue(peer, out targets)) { targets = new List<Target>(); targetsByPeer[peer] = targets; }
            targets.Clear();
            foreach (var h in held)
            {
                var rb = Lookup(h.id);
                if (rb == null)
                {
                    if (reportedMissing.Add(h.id)) CoopBehaviour.Log("objects: peer moves object #" + h.id + " that I cannot find");
                    continue;
                }
                if (!following.Contains(h.id)) { following.Add(h.id); CoopBehaviour.Log("objects: peer moves #" + h.id + " " + rb.name + "; my copy is at " + rb.position + ", theirs at " + h.pos + ", " + (rb.position - h.pos).magnitude.ToString("F1") + " m apart; kinematic=" + rb.isKinematic + " joints=" + rb.GetComponents<Joint>().Length); }
                targets.Add(new Target { rb = rb, pos = h.pos, rot = h.rot, vel = h.vel, time = Time.time });
            }
        }

        // Call from FixedUpdate: steer my copy of each remote-held object to where the holder has it.
        public static void FixedTick()
        {
            foreach (var kv in targetsByPeer)
            {
                var targets = kv.Value;
                for (int i = 0; i < targets.Count; i++)
                {
                    var t = targets[i];
                    if (t.rb == null || Time.time - t.time > 0.3f) continue;
                    Vector3 err = t.pos - t.rb.position;
                    if (Time.time >= nextFollowLog) { nextFollowLog = Time.time + 1f; CoopBehaviour.Log("objects: following " + t.rb.name + " error " + err.magnitude.ToString("F2") + " m"); }
                    if (err.magnitude > 4f) { t.rb.position = t.pos; t.rb.rotation = t.rot; t.rb.velocity = t.vel; }
                    else
                    {
                        t.rb.velocity = t.vel + Vector3.ClampMagnitude(err * 10f, 20f);
                        t.rb.MoveRotation(Quaternion.Slerp(t.rb.rotation, t.rot, 0.25f));
                    }
                }
            }
        }
    }
}
