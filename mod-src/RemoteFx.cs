using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    // Reads my own jetpack, flower and leaf for the pose packet. Read only: nothing here changes the game.
    public static class LocalFx
    {
        const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        public static readonly FieldInfo FlyableObject = typeof(Flyable).GetField("m_flyableObject", Inst);         // the leaf while it is out
        public static readonly FieldInfo ParachuteObject = typeof(Flyable).GetField("m_ParachuteGameObject", Inst);  // its parent object
        public static readonly FieldInfo InHand = typeof(GlideParachute).GetField("m_inHandGameObject", Inst);      // the flower while it is out
        public static readonly FieldInfo Stored = typeof(GlideParachute).GetField("m_storedFlyable", Inst);         // the flower while it is put away
        static bool missingLogged;
        static GameObject cachedRoot;
        static JetPack[] jets = new JetPack[0];
        static Glider glider;
        static GlideParachute flower;

        public static GameObject Get(FieldInfo f, object o)
        {
            if (f == null || o == null) return null;
            try { return f.GetValue(o) as GameObject; } catch (Exception) { return null; }
        }

        public static void Fill(GameObject root, Rigidbody body, PlayerState s)
        {
            if (root == null || body == null) return;
            if (root != cachedRoot)
            {
                cachedRoot = root;
                jets = root.GetComponentsInChildren<JetPack>(true);
                var g = root.GetComponentsInChildren<Glider>(true); glider = g.Length > 0 ? g[0] : null;
                var f = root.GetComponentsInChildren<GlideParachute>(true); flower = f.Length > 0 ? f[0] : null;
            }
            // the same test the jetpack uses to show its own flame
            foreach (var jp in jets) if (jp != null && jp.Enabled() && jp.ThrottleIsOn() && jp.HasFuel) { s.jet = true; break; }

            if (FlyableObject == null || InHand == null)
            {
                if (!missingLogged) { missingLogged = true; CoopBehaviour.Log("fx: the game's flower/leaf fields were not found; a friend will not see mine"); }
                return;
            }
            // the leaf: out and still in my hand (a dropped one is moved out of my walkster)
            var leaf = glider != null ? Get(FlyableObject, glider) : null;
            if (leaf != null && leaf.activeInHierarchy && leaf.transform.root == root.transform)
            {
                Vector3 off = leaf.transform.position - body.position;
                if (off.magnitude < 9f) { s.fly = 1; s.flyOffset = off; s.flyRot = leaf.transform.rotation; s.wear = (byte)Mathf.Clamp(glider.CurrentDegradationStep(), 0, 255); return; }
            }
            // the flower: its petals are its children (a petal that falls off leaves). A dropped or spent one keeps its
            // m_inHandGameObject and stays under my root while it falls, so it only counts while the flower is open.
            var fl = flower != null && flower.ParachuteOpen ? Get(InHand, flower) : null;
            if (fl != null && fl.activeInHierarchy && fl.transform.root == root.transform && fl.GetComponent<DetachChildrenDestroy>() == null)
            {
                Vector3 off = fl.transform.position - body.position;
                if (off.magnitude < 9f) { s.fly = 2; s.flyOffset = off; s.flyRot = fl.transform.rotation; s.petals = (byte)Mathf.Min(fl.transform.childCount, 64); }
            }
        }
    }

    // On another player's copy: shows their jetpack flame and sound and the flower/leaf in their hand. Visual only: it never touches the
    // copy's body, my HUD, my inventory, the flight lock or anything that uses my own walkster (ExtendedMonoBehaviour.Player).
    public class RemoteFx : MonoBehaviour
    {
        public const string HolderName = "CoopFx";
        public const float Timeout = 0.5f;               // no news for this long: flame and flower/leaf off (a lost link cannot leave them on)
        public Rigidbody body;
        public Transform root;
        public ParticleSystem[] flames = new ParticleSystem[0];
        public Transform[] flameRoots = new Transform[0];
        public GameObject leafPrefab, flowerPrefab;
        public string leafSound = "Toys.GliderFly";
        public AnimationCurve leafVolume;
        public static string LastStrip = "";             // what was taken off the last flower/leaf copy (for the audit log)

        bool jet; byte fly; Vector3 offset; Quaternion rot = Quaternion.identity; byte petals; float stateTime = -999f;
        public byte Wear;                                // how worn their leaf is (sent along; the game shows no wear on the leaf itself)
        bool flameOn; AudioSource thrust; float offSoundAt = -1f;
        byte shownKind; GameObject holder, visual; AudioSource glide;
        bool prefabMissingLogged;

        public GameObject Visual { get { return visual; } }
        public bool FlameOn { get { return flameOn; } }

        public void Set(bool jet, byte fly, Vector3 offset, Quaternion rot, byte petals, byte wear, float time)
        {
            this.jet = jet; this.fly = fly; this.offset = offset; this.petals = petals; Wear = wear; stateTime = time;
            float m = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
            this.rot = m < 0.01f ? Quaternion.identity : new Quaternion(rot.x / m, rot.y / m, rot.z / m, rot.w / m);
        }

        void LateUpdate()
        {
            bool fresh = Time.time - stateTime <= Timeout;
            UpdateFlame(fresh && jet);
            UpdateFly(fresh ? fly : (byte)0);
        }

        // ---- jetpack -------------------------------------------------------------------------------------------------------------------

        void UpdateFlame(bool on)
        {
            foreach (var ps in flames)
            {
                if (ps == null) continue;
                if (on) { if (!ps.isPlaying) ps.Play(); ps.enableEmission = true; }
                else if (ps.enableEmission) ps.enableEmission = false;
            }
            foreach (var t in flameRoots) if (t != null) t.rotation = Quaternion.LookRotation(Vector3.up);   // the flame always points up, as on the real jetpack
            bool sound = body != null && Singleton<TempSourceManager>.Exists;
            if (on != flameOn)
            {
                flameOn = on;
                if (on)
                {
                    offSoundAt = -1f;
                    if (sound)
                    {
                        if (thrust == null) thrust = Singleton<TempSourceManager>.Instance.PlayClipOnObject(body.gameObject, "Ability.JetpackThrust");
                        Singleton<TempSourceManager>.Instance.PlayClipOnObject(body.gameObject, "Ability.JetpackOn");
                    }
                }
                else { StopThrust(); offSoundAt = Time.time + 0.15f; }
            }
            if (offSoundAt > 0f && Time.time >= offSoundAt)
            {
                offSoundAt = -1f;
                if (sound) Singleton<TempSourceManager>.Instance.PlayClipOnObject(body.gameObject, "Ability.JetpackOff");
            }
        }

        void StopThrust()
        {
            // a pooled source the pool has handed on is a destroyed one (null here), so this never stops somebody else's sound
            if (thrust != null) thrust.StopOrFadeOut();
            thrust = null;
        }

        // ---- flower and leaf -----------------------------------------------------------------------------------------------------------

        void UpdateFly(byte kind)
        {
            if (kind != shownKind)
            {
                CloseFly();
                shownKind = kind;
                if (kind != 0) OpenFly(kind);
            }
            if (visual == null || body == null) return;
            var t = visual.transform;
            Vector3 want = body.position + offset;
            if ((t.position - want).magnitude > 2f) { t.position = want; t.rotation = rot; }
            else { t.position = Vector3.Lerp(t.position, want, 0.5f); t.rotation = Quaternion.Slerp(t.rotation, rot, 0.5f); }
            if (kind == 2) TrimPetals(t);
            if (kind == 1 && Singleton<TempSourceManager>.Exists)
            {
                if (glide == null) glide = Singleton<TempSourceManager>.Instance.GetSource();
                if (glide != null)
                {
                    if (!glide.isPlaying && !string.IsNullOrEmpty(leafSound) && leafSound.Contains(".")) glide.Play(leafSound);
                    glide.transform.position = t.position;
                    float v = leafVolume != null ? leafVolume.Evaluate(body.velocity.magnitude) : 0.75f;
                    glide.volume = v * (Singleton<PlayerProfile>.Exists ? PlayerProfile.SFXVolume : 1f);
                }
            }
        }

        void TrimPetals(Transform t)
        {
            while (t.childCount > petals) DestroyImmediate(t.GetChild(t.childCount - 1).gameObject);
        }

        void OpenFly(byte kind)
        {
            var prefab = kind == 1 ? leafPrefab : flowerPrefab;
            if (prefab == null || root == null)
            {
                if (!prefabMissingLogged) { prefabMissingLogged = true; CoopBehaviour.Log("fx: no " + (kind == 1 ? "leaf" : "flower") + " model on this copy; it is not shown"); }
                return;
            }
            var copy = StrippedCopy(prefab, kind == 1 ? "leaf" : "flower");
            if (copy == null) return;
            // a holder with a name the other player's BUD never has, so their visibility list can never switch it
            holder = new GameObject(HolderName);
            holder.transform.parent = root;
            visual = copy;
            visual.transform.parent = holder.transform;
            visual.transform.position = body != null ? body.position + offset : root.position;
            visual.transform.rotation = rot;
            if (kind == 2) TrimPetals(visual.transform);
            visual.SetActive(true);
        }

        void CloseFly()
        {
            StopGlide();
            if (holder != null) Destroy(holder);
            holder = null; visual = null;
        }

        void StopGlide()
        {
            if (glide != null) glide.StopOrFadeOut();
            glide = null;
        }

        void OnDestroy() { StopThrust(); StopGlide(); }

        // The copy is switched off (their BUD died, or a chunk was blocked): LateUpdate stops, so stop the looping thrust and glide sounds
        // and the flames here. Once it is shown again, LateUpdate builds everything up again from fresh packets.
        void OnDisable()
        {
            StopThrust(); CloseFly();
            flameOn = false; shownKind = 0; offSoundAt = -1f;
            foreach (var ps in flames) if (ps != null && ps.enableEmission) ps.enableEmission = false;
        }

        public AudioSource ThrustForTest { get { return thrust; } }
        public AudioSource GlideForTest { get { return glide; } }

        // ---- one-off events: their voice lines, hard landings and deaths ---------------------------------------------------------------

        public AudioSource voice;                        // the copy's own voice source (never mine)
        public CloneGuard guard;
        float nextVoice, nextLand, nextDeath;
        public GameObject LastParticle, LastSound;       // what the last landing/death made (for the self test)
        public string LastVoice = "";
        public int LastChunks;
        static int TerrainMask { get { return 1 << LayerMask.NameToLayer("Terrain"); } }

        // senderPos is where their body was in the same packet: a landing spot is placed by its offset from that, so it lands under the copy.
        public void Play(FxEvent e, Vector3 senderPos)
        {
            if (e == null || body == null) return;
            if (e.kind == FxEvent.Voice) PlayVoice(e.voice);
            else if (e.kind == FxEvent.Grunt || e.kind == FxEvent.Lethal) Land(e.kind == FxEvent.Lethal, body.position + (e.point - senderPos), e.normal);
            else if (e.kind == FxEvent.Death) Die(e.cause);
        }

        public bool PlayVoice(string path)
        {
            if (!FxEvent.IsVoicePath(path) || voice == null || !voice.gameObject.activeInHierarchy || Time.time < nextVoice) return false;
            if (voice.isPlaying) return false;           // the copy is already saying something (often the same line, from its own jump)
            try { if (SoundManager.GetEvent(path) == null) return false; } catch (Exception) { return false; }
            nextVoice = Time.time + 0.15f;
            voice.Play(path, false);
            voice.loop = false;                          // a loop of theirs is sent again each time round
            LastVoice = path;
            return true;
        }

        // The game's own hard landing (FallToDeath.OnCollisionEnter): dust from the ground's texture and a grunt, both at the copy.
        public void Land(bool lethal, Vector3 point, Vector3 normal)
        {
            if (Time.time < nextLand) return;
            nextLand = Time.time + 0.3f;
            LastParticle = null; LastSound = null;
            if (!lethal) LastSound = ExtendedMonoBehaviour.PlayClipAtPoint("Voice.Impact", body.position);
            RaycastHit res = default(RaycastHit);
            if (PhysicsUtil.RaycastNearest(point + Vector3.up, -normal, 2f, ref res, TerrainMask))
            {
                var rot = Quaternion.LookRotation(body.transform.up);
                LastParticle = lethal ? DebrisManager.HugeUVOffsetParticle(point, rot, res.textureCoord) : DebrisManager.BigUVOffsetParticle(point, rot, res.textureCoord);
            }
        }

        // Their BUD blew up (fall, water, a creature): the game's explosion sounds and chunks of a looks-only copy, then the copy hides until they respawn.
        public void Die(byte cause)
        {
            if (Time.time < nextDeath || root == null || !root.gameObject.activeSelf) return;
            nextDeath = Time.time + 3f;
            Vector3 at = body.position;
            LastSound = ExtendedMonoBehaviour.PlayClipAtPoint("Voice.Explode", at);
            ExtendedMonoBehaviour.PlayClipAtPoint("Environment.Explode", at);
            var g = guard != null ? guard : root.GetComponent<CloneGuard>();
            var driver = body.GetComponent<RemoteDriver>();
            Vector3 netAt = driver != null && driver.hasNet ? driver.netPos : at;
            if (g != null) g.HideForDeath(netAt); else root.gameObject.SetActive(false);
            LastChunks = 0;
            try { LastChunks = Chunks(root.gameObject); } catch (Exception ex) { CoopBehaviour.Log("fx: death chunks failed: " + ex.GetType().Name + " " + ex.Message); }
            CoopBehaviour.Log("fx: " + root.name + " died (" + (cause == FxEvent.CauseWater ? "water" : cause == FxEvent.CauseImpact ? "impact" : "other") + "), " + LastChunks + " chunks");
        }

        // Called with the copy switched off: its copy starts off too, so no script on it ever wakes up; every script is removed before it is switched on
        // and handed to the game's chunker, which then breaks only looks (no replacement is spawned, nothing of mine is touched).
        static int Chunks(GameObject hiddenClone)
        {
            if (!Singleton<Chunker>.Exists) return 0;
            var copy = (GameObject)UnityEngine.Object.Instantiate(hiddenClone, hiddenClone.transform.position, hiddenClone.transform.rotation);
            copy.name = "CoopChunks";
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) DestroyImmediate(mb);
            foreach (var a in copy.GetComponentsInChildren<AudioSource>(true)) if (a != null) DestroyImmediate(a);
            copy.SetActive(true);
            var parts = Chunker.TurnToChunks(copy, 0f, false, false);
            if (copy != null) Destroy(copy);
            return parts != null ? parts.Length : 0;
        }

        // A copy of the game's flower/leaf model with nothing but its looks: no scripts, colliders, rigidbodies, joints or sounds.
        // It is made from the prefab while the prefab is switched off, so the copy starts switched off too and none of its scripts
        // wake up (Awake/OnEnable) before they are removed. The prefab is switched back on at once; the game's own copies are not affected.
        public static GameObject StrippedCopy(GameObject prefab, string label)
        {
            if (prefab == null) return null;
            bool wasActive = prefab.activeSelf;
            GameObject copy;
            if (wasActive) prefab.SetActive(false);
            try { copy = (GameObject)UnityEngine.Object.Instantiate(prefab); }
            finally { if (wasActive) prefab.SetActive(true); }
            if (copy == null) return null;
            copy.name = prefab.name;
            var removed = new Dictionary<string, int>();
            foreach (var c in copy.GetComponentsInChildren<Joint>(true)) Strip(c, removed);
            foreach (var c in copy.GetComponentsInChildren<MonoBehaviour>(true)) Strip(c, removed);
            foreach (var c in copy.GetComponentsInChildren<Collider>(true)) Strip(c, removed);
            foreach (var c in copy.GetComponentsInChildren<Rigidbody>(true)) Strip(c, removed);
            foreach (var c in copy.GetComponentsInChildren<AudioSource>(true)) Strip(c, removed);
            var parts = new List<string>();
            foreach (var kv in removed) parts.Add(kv.Key + (kv.Value > 1 ? " x" + kv.Value : ""));
            LastStrip = label + " (" + prefab.name + "): " + (parts.Count == 0 ? "nothing to remove" : "removed " + string.Join(", ", parts.ToArray()));
            return copy;
        }

        static void Strip(Component c, Dictionary<string, int> removed)
        {
            if (c == null) return;
            string n = c.GetType().Name;
            int k; removed.TryGetValue(n, out k); removed[n] = k + 1;
            DestroyImmediate(c);
        }

        // ---- setting up a new copy ---------------------------------------------------------------------------------------------------

        // Called on a fresh copy of my walkster: keeps the jetpack flame and the flower/leaf models, then removes every flying script
        // (they would read my controller, use my inventory and the flight lock and push MY walkster) and any flower/leaf the copy inherited from me.
        public static RemoteFx Attach(GameObject localRoot, GameObject clone, BipedWalkScript walker)
        {
            var flames = new List<ParticleSystem>();
            var flameRoots = new List<Transform>();
            foreach (var jp in clone.GetComponentsInChildren<JetPack>(true))
            {
                Transform pt = jp.m_particleTransform;
                if (pt == null) pt = jp.transform.FindChild("JetPackParticle");
                if (pt == null && jp.m_particleSystem != null) pt = jp.m_particleSystem.transform;
                if (pt != null)
                {
                    flameRoots.Add(pt);
                    foreach (var ps in pt.GetComponentsInChildren<ParticleSystem>(true)) { ps.enableEmission = false; ps.Stop(); flames.Add(ps); }
                }
                Destroy(jp);   // the copy's jetpack would talk to MY fuel meter (the HUD is shared): copies have none
            }

            GameObject leafPrefab = null, flowerPrefab = null; string leafSound = null; AnimationCurve leafVolume = null;
            var gl = clone.GetComponentsInChildren<Glider>(true);
            if (gl.Length > 0) { leafPrefab = gl[0].m_flyablePrefab; leafSound = gl[0].m_soundEvent; leafVolume = gl[0].m_volumeAgainstSpeed; }
            var gp = clone.GetComponentsInChildren<GlideParachute>(true);
            if (gp.Length > 0) flowerPrefab = gp[0].m_flyablePrefab;

            // a flower or leaf I had out (or put away) when the copy was made is a copy of mine: remove it, the real one comes from their packets
            int inherited = 0;
            if (localRoot != null)
                foreach (var lf in localRoot.GetComponentsInChildren<Flyable>(true))
                {
                    var mine = new List<GameObject> { LocalFx.Get(LocalFx.FlyableObject, lf), LocalFx.Get(LocalFx.ParachuteObject, lf) };
                    if (lf is GlideParachute) { mine.Add(LocalFx.Get(LocalFx.InHand, lf)); mine.Add(LocalFx.Get(LocalFx.Stored, lf)); }
                    foreach (var go in mine)
                    {
                        if (go == null || go.transform.root != localRoot.transform || go.transform == localRoot.transform) continue;
                        var twin = Twin(go.transform, localRoot.transform, clone.transform);
                        if (twin != null) { Destroy(twin.gameObject); inherited++; }
                    }
                }
            foreach (var f in clone.GetComponentsInChildren<Flyable>(true))
            {
                var theirs = new List<GameObject> { LocalFx.Get(LocalFx.FlyableObject, f), LocalFx.Get(LocalFx.ParachuteObject, f) };
                if (f is GlideParachute) { theirs.Add(LocalFx.Get(LocalFx.InHand, f)); theirs.Add(LocalFx.Get(LocalFx.Stored, f)); }
                foreach (var go in theirs) if (go != null && go.transform.root == clone.transform && go.transform != clone.transform) { Destroy(go); inherited++; }
                Destroy(f);
            }
            foreach (var f in clone.GetComponentsInChildren<FreeFallControl>(true)) Destroy(f);

            var fx = walker.gameObject.AddComponent<RemoteFx>();
            fx.body = walker.body;
            fx.root = clone.transform;
            fx.flames = flames.ToArray();
            fx.flameRoots = flameRoots.ToArray();
            fx.leafPrefab = leafPrefab; fx.flowerPrefab = flowerPrefab;
            if (!string.IsNullOrEmpty(leafSound)) fx.leafSound = leafSound;
            fx.leafVolume = leafVolume;
            var vt = walker.transform.FindChild("AudioSources/Voice");
            fx.voice = vt != null ? vt.audio : null;
            CoopBehaviour.Log("fx: copy has " + flames.Count + " jetpack flames, leaf model " + (leafPrefab != null) + ", flower model " + (flowerPrefab != null) + (inherited > 0 ? ", removed " + inherited + " flower/leaf objects copied from mine" : ""));
            return fx;
        }

        // The object at the same place in the copy (a copy keeps the order of children), or null when the names do not match.
        static Transform Twin(Transform t, Transform from, Transform to)
        {
            var path = new List<int>();
            for (var x = t; x != null && x != from; x = x.parent) path.Add(x.GetSiblingIndex());
            path.Reverse();
            var y = to;
            foreach (int i in path) { if (i >= y.childCount) return null; y = y.GetChild(i); }
            return y != to && y.name == t.name ? y : null;
        }
    }
}
