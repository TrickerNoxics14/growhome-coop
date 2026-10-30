using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    // The game's jump code (BipedWalkScript.UpdateJump) talks to MY walkster through statics: PlayVoice uses MY voice source, a leaf jump adds an
    // achievement to MY save, calls MY analytics, plays its foley on MY body and rumbles MY pad. A copy's jump runs inside one physics step that
    // the copy's driver opens and closes (RemoteDriver.JumpSteps): for that one step the voice points at the copy's own source and the rest is
    // switched off; afterwards everything is put back and anything the step still did to me is undone.
    public static class JumpWindow
    {
        const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static, NI = BindingFlags.NonPublic | BindingFlags.Instance;
        public static readonly FieldInfo VoiceField = typeof(ExtendedMonoBehaviour).GetField("s_voiceAudio", NS);
        static readonly FieldInfo LinkedField = typeof(TempSourceManager).GetField("m_linkedAudioSources", NI);
        const string Bounce = "bouncedoffleaf";

        public static RemoteDriver Owner;            // the copy whose step is open (one at a time)
        public static int Opened, Closed, Unconsumed, FoleyMoved;
        public static float MuteRumbleUntil = -1f;   // a copy's leaf jump rumbled my pad: keep it off this long (see RemoteDriver.LateUpdate)
        public static bool BounceTakenBack;
        static AudioSource savedVoice;
        static BipedWalkScript.OnBigLeafJump savedLeaf;
        static SceneProfile.StringDelegate savedAch;
        static bool hadBounce;
        static GameObject myBody, copyBody;
        static BipedWalkScript copyWalker, myWalker;
        static float airPlayBefore, myAirPlayBefore;
        public static bool MineLeafJumped;           // my own walkster leaf-jumped in the same step (for the self test)
        static readonly List<AudioSource> linkedBefore = new List<AudioSource>();

        public static bool Ready { get { return VoiceField != null; } }

        static Dictionary<AudioSource, GameObject> Linked()
        {
            if (LinkedField == null || !Singleton<TempSourceManager>.Exists) return null;
            try { return LinkedField.GetValue(Singleton<TempSourceManager>.Instance) as Dictionary<AudioSource, GameObject>; } catch (Exception) { return null; }
        }

        // the object a pooled sound follows (TempSourceManager.PlayClipOnObject), for the self test
        public static GameObject LinkedTo(AudioSource src)
        {
            var linked = Linked();
            GameObject on;
            return linked != null && src != null && linked.TryGetValue(src, out on) ? on : null;
        }

        static bool HasBounce()
        {
            try { return Singleton<SceneProfile>.Exists && SceneProfile.HasAccomplishment(Bounce); } catch (Exception) { return true; }   // unsure: never take anything back
        }

        public static bool Open(RemoteDriver owner, AudioSource copyVoice, BipedWalkScript walker)
        {
            if (!Ready || copyVoice == null || walker == null || Owner != null) return false;
            Owner = owner; copyWalker = walker; copyBody = walker.gameObject;
            savedVoice = VoiceField.GetValue(null) as AudioSource;
            VoiceField.SetValue(null, copyVoice);
            savedLeaf = BipedWalkScript.m_OnBigLeafJumpDelegate;
            BipedWalkScript.m_OnBigLeafJumpDelegate = null;
            savedAch = SceneProfile.m_OnNewAccomplishment;
            var pass = savedAch;
            SceneProfile.m_OnNewAccomplishment = name => { if (name != Bounce && pass != null) pass(name); };   // everything but the copy's leaf achievement goes on as usual
            hadBounce = HasBounce();
            airPlayBefore = walker.m_stopAirPlayTime;
            var me = GameObject.Find("Walkster");
            var mw = me != null ? me.GetComponentInChildren<BipedWalkScript>() : null;
            myBody = mw != null ? mw.gameObject : null;
            myWalker = mw != walker ? mw : null;
            myAirPlayBefore = myWalker != null ? myWalker.m_stopAirPlayTime : 0f;
            linkedBefore.Clear();
            var linked = Linked();
            if (linked != null) linkedBefore.AddRange(linked.Keys);
            Opened++;
            return true;
        }

        public static void Close()
        {
            if (Owner == null) return;
            VoiceField.SetValue(null, savedVoice);
            BipedWalkScript.m_OnBigLeafJumpDelegate = savedLeaf;
            SceneProfile.m_OnNewAccomplishment = savedAch;
            BounceTakenBack = false;
            bool leafJump = copyWalker != null && copyWalker.m_stopAirPlayTime != airPlayBefore;   // set only by the game's leaf/mushroom jump
            // my own walker runs its jump in the same step: if it leaf-jumped too, what it did is mine and stays mine
            bool myLeaf = myWalker != null && myWalker.m_stopAirPlayTime != myAirPlayBefore;
            Transform stood = myLeaf ? myWalker.m_objectStoodOn : null;
            bool myBounce = stood != null && stood.tag == "bounce";
            MineLeafJumped = myLeaf;
            if (!hadBounce && HasBounce())
            {
                if (myBounce)
                {
                    // my real achievement: my hooks missed it behind the filter, so tell them now
                    try { if (savedAch != null) savedAch(Bounce); } catch (Exception) { }
                }
                else
                {
                    try { SceneProfile.RemoveAccomplishment(Bounce); BounceTakenBack = true; CoopBehaviour.Log("copy jump: took back the leaf achievement the copy added to my save"); } catch (Exception) { }
                }
            }
            // my leaf hook (analytics and save) was off for the step: call it for my own big-leaf jump
            if (myBounce && stood.name != "DeadLeaf" && savedLeaf != null) { try { savedLeaf(); } catch (Exception) { } }
            if (leafJump)
            {
                // both sounds were put on my body: hand one to the copy and keep mine; my pad keeps its own rumble
                if (!myLeaf) MuteRumbleUntil = Time.time + 0.2f;
                MoveFoley(myLeaf ? 1 : int.MaxValue);
            }
            savedVoice = null; savedLeaf = null; savedAch = null; copyWalker = null; copyBody = null; myBody = null; myWalker = null;
            linkedBefore.Clear();
            Owner = null;
            Closed++;
        }

        // The leaf foley was played on MY body (PlayFoley uses the player): hand the new sound to the copy instead.
        static void MoveFoley(int max)
        {
            var linked = Linked();
            if (linked == null || myBody == null || copyBody == null) return;
            int moved = 0;
            foreach (var src in new List<AudioSource>(linked.Keys))
            {
                if (moved >= max) break;
                if (src == null || linkedBefore.Contains(src)) continue;
                GameObject on;
                if (!linked.TryGetValue(src, out on) || on != myBody) continue;
                linked[src] = copyBody;
                src.transform.position = copyBody.transform.position;
                FoleyMoved++; moved++;
            }
        }
    }

    // On the root of every copy. A copy must never be turned to chunks by the game (a venus trap, a ram, a drowning): the chunker destroys every
    // script on it and leaves a broken copy behind. The chunker first tells the copy OnDeath and stops when it is no longer active, so the copy
    // switches itself off and is switched back on in the next frame. It also hides the copy while its player is dead, and on removal takes its
    // skin script out of the game's load queue (SkinSwapper never does).
    public class CloneGuard : MonoBehaviour
    {
        static readonly List<CloneGuard> all = new List<CloneGuard>();
        public RemoteDriver driver;
        public SkinSwapper[] swappers = new SkinSwapper[0];
        public int Blocked;
        int hiddenFrame = -1;
        float deathAt = -1f;
        Vector3 deathPos;
        public const float DeathShowAfter = 1.5f, DeathMaxHidden = 7f, RespawnMove = 6f;

        public bool HiddenForDeath { get { return deathAt >= 0f; } }

        void Awake() { all.Add(this); }

        void OnDeath()
        {
            if (deathAt >= 0f || !gameObject.activeSelf) return;
            Blocked++;
            hiddenFrame = Time.frameCount;
            gameObject.SetActive(false);
            CoopBehaviour.Log("copy " + name + ": the game tried to destroy it (chunks); kept it");
        }

        // Their BUD blew up: hidden until they respawn (they moved away from where they died) or for at most a few seconds.
        public void HideForDeath(Vector3 at)
        {
            deathAt = Time.time; deathPos = at; hiddenFrame = -1;
            gameObject.SetActive(false);
        }

        void Show()
        {
            hiddenFrame = -1; deathAt = -1f;
            gameObject.SetActive(true);
        }

        public static void Tick()
        {
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var g = all[i];
                if (g == null) { all.RemoveAt(i); continue; }
                if (g.gameObject.activeSelf) continue;
                if (g.deathAt >= 0f)
                {
                    float t = Time.time - g.deathAt;
                    bool moved = g.driver != null && g.driver.hasNet && (g.driver.netPos - g.deathPos).magnitude > RespawnMove && g.driver.netTime > g.deathAt;
                    if ((t > DeathShowAfter && moved) || t > DeathMaxHidden) g.Show();
                }
                else if (g.hiddenFrame >= 0 && Time.frameCount > g.hiddenFrame) g.Show();
            }
        }

        public static int OnLoadQueueCount(SkinSwapper[] list)
        {
            var d = SaveSystem.OnLoadComplete;
            if (d == null || list == null) return 0;
            int n = 0;
            foreach (Delegate x in d.GetInvocationList()) foreach (var s in list) if (object.ReferenceEquals(x.Target, s)) n++;
            return n;
        }

        public static void RemoveFromLoadQueue(SkinSwapper[] list)
        {
            var d = SaveSystem.OnLoadComplete;
            if (d == null || list == null) return;
            foreach (Delegate x in d.GetInvocationList())
                foreach (var s in list)
                    if (object.ReferenceEquals(x.Target, s)) d = (SaveSystem.OnSaveLoadComplete)Delegate.Remove(d, x);
            SaveSystem.OnLoadComplete = d;
        }

        void OnDestroy()
        {
            try { RemoveFromLoadQueue(swappers); } catch (Exception) { }
        }
    }

    // ---- the sending side: one-off events of MY walkster ------------------------------------------------------------------------------

    // My events waiting to go out. Each is sent in every pose packet for Repeat seconds (a lost packet loses nothing), the newest 4 at most.
    public static class FxOutbox
    {
        public const float Repeat = 0.5f;
        static ushort nextId;
        static readonly List<FxEvent> list = new List<FxEvent>();
        static readonly List<float> times = new List<float>();
        public static FxEvent Last;

        public static void Add(FxEvent e)
        {
            e.id = ++nextId;
            list.Add(e); times.Add(Time.realtimeSinceStartup);
            while (list.Count > 8) { list.RemoveAt(0); times.RemoveAt(0); }
            Last = e;
        }

        public static FxEvent[] Current()
        {
            float now = Time.realtimeSinceStartup;
            var r = new List<FxEvent>();
            for (int i = 0; i < list.Count; i++) if (now - times[i] <= Repeat) r.Add(list[i]);
            while (r.Count > FxEvent.MaxPerPacket) r.RemoveAt(0);
            return r.ToArray();
        }

        public static void Clear() { list.Clear(); times.Clear(); Last = null; }
    }

    // Which voice event ("Voice.Jump") a clip belongs to, from the game's own sound banks.
    public static class VoiceNames
    {
        static readonly Dictionary<int, string> cache = new Dictionary<int, string>();

        public static string PathOf(AudioClip clip)
        {
            if (clip == null) return null;
            int key = clip.GetInstanceID();
            string path;
            if (cache.TryGetValue(key, out path)) return path;
            path = null;
            try
            {
                foreach (var bank in AssetSingleton<SoundManager>.Instance.m_soundBanks)
                {
                    if (bank == null || bank.Name != "Voice") continue;
                    foreach (var ev in bank.m_soundEvents)
                        if (ev != null && ev.m_audioClips.Contains(clip) && FxEvent.IsVoicePath("Voice." + ev.Name)) { path = "Voice." + ev.Name; break; }
                    if (path != null) break;
                }
            }
            catch (Exception) { return null; }   // the banks are not ready yet: ask again later
            cache[key] = path;
            return path;
        }

        public static bool Is(AudioSource src, params string[] paths)
        {
            if (src == null || !src.isPlaying) return false;
            string p = PathOf(src.clip);
            return p != null && Array.IndexOf(paths, p) >= 0;
        }
    }

    // Watches MY voice source (not ExtendedMonoBehaviour.GetVoice, which points at a copy during its jump step) and announces each new line.
    public static class VoiceWatch
    {
        static AudioSource src; static GameObject srcRoot;
        static AudioClip lastClip; static bool wasPlaying; static float lastTime;

        public static AudioSource MyVoice(GameObject root)
        {
            var w = root != null ? root.GetComponentInChildren<BipedWalkScript>() : null;
            var t = w != null ? w.transform.FindChild("AudioSources/Voice") : null;
            return t != null ? t.audio : null;
        }

        public static void Poll(GameObject root)
        {
            if (root == null) return;
            if (root != srcRoot || src == null) { srcRoot = root; src = MyVoice(root); wasPlaying = false; lastClip = null; lastTime = 0f; }
            if (src == null) return;
            bool playing = src.isPlaying;
            var clip = src.clip;
            float t = playing ? src.time : 0f;
            if (playing && clip != null && (!wasPlaying || clip != lastClip || t + 0.05f < lastTime))
            {
                string path = VoiceNames.PathOf(clip);
                if (path != null) FxOutbox.Add(new FxEvent { kind = FxEvent.Voice, voice = path });
            }
            wasPlaying = playing; lastClip = clip; lastTime = t;
        }
    }

    // On MY body, next to the game's FallToDeath: works out the same landing energy it does and announces a grunt-level or deadly landing,
    // and announces my death (the chunker tells my whole walkster OnDeath), so my friends see the dust and the explosion on my copy.
    public class LandWatch : MonoBehaviour
    {
        const BindingFlags NI = BindingFlags.NonPublic | BindingFlags.Instance;
        public static readonly FieldInfo VelField = typeof(FallToDeath).GetField("m_bodyVelocity", NI);
        public static readonly FieldInfo KillField = typeof(FallToDeath).GetField("m_energyToKill", NI);
        public static readonly FieldInfo GruntField = typeof(FallToDeath).GetField("m_energyForGrunt", NI);
        static readonly FieldInfo TooDeepField = typeof(WaterDeath).GetField("m_tooDeep", NI);
        public FallToDeath ftd;
        public WaterDeath water;
        float lastLand = -99f, lastDeath = -99f;
        static GameObject cachedRoot; static LandWatch cached;

        public static LandWatch Ensure(GameObject root)
        {
            if (root == null) return null;
            if (root == cachedRoot && cached != null) return cached;
            cachedRoot = root; cached = null;
            var f = root.GetComponentInChildren<FallToDeath>();
            if (f == null) return null;
            var lw = f.GetComponent<LandWatch>();
            if (lw == null) lw = f.gameObject.AddComponent<LandWatch>();
            lw.ftd = f;
            lw.water = root.GetComponentInChildren<WaterDeath>();
            cached = lw;
            return lw;
        }

        // the game's own formula (FallToDeath.CalcImpactEnergy)
        public static float ImpactEnergy(float mass, Vector3 velocity, Vector3 normal)
        {
            float d = Mathf.Clamp(Vector3.Dot(velocity.normalized, normal) * 1.05f, -1f, 1f);
            float v = velocity.magnitude * d;
            return 0.5f * mass * v * v;
        }

        public static byte Classify(float energy, float kill, float grunt)
        {
            if (energy > kill) return FxEvent.Lethal;
            if (energy > grunt) return FxEvent.Grunt;
            return 0;
        }

        static float F(FieldInfo f, object o) { try { return f != null && o != null ? (float)f.GetValue(o) : float.PositiveInfinity; } catch (Exception) { return float.PositiveInfinity; } }

        void OnCollisionEnter(Collision collision)
        {
            try
            {
                if (cached != this || ftd == null || VelField == null || collision.contacts.Length == 0) return;   // only the one on my real walkster (copies of it are not mine)
                if (collision.relativeVelocity.sqrMagnitude < 25f) return;   // walking and small hops: far below any grunt
                var rb = rigidbody;
                Vector3 v = (Vector3)VelField.GetValue(ftd);
                if (collision.rigidbody != null && collision.rigidbody != rb) v -= collision.rigidbody.velocity;
                var pts = new Vector3[collision.contacts.Length]; var nrm = new Vector3[pts.Length];
                for (int i = 0; i < pts.Length; i++) { pts[i] = collision.contacts[i].point; nrm[i] = collision.contacts[i].normal; }
                Hit(rb != null ? rb.mass : 100f, v, pts, nrm);
            }
            catch (Exception) { }
        }

        // One landing: the kind the game would show (nothing, a grunt, a deadly one) with the averaged contact point and normal.
        public byte Hit(float mass, Vector3 velocity, Vector3[] points, Vector3[] normals)
        {
            if (points == null || points.Length == 0) return 0;
            byte kind = Classify(ImpactEnergy(mass, velocity, normals[0]), F(KillField, ftd), F(GruntField, ftd));
            if (kind == 0 || Time.realtimeSinceStartup - lastLand < 0.3f) return 0;
            lastLand = Time.realtimeSinceStartup;
            Vector3 p = Vector3.zero, n = Vector3.zero;
            for (int i = 0; i < points.Length; i++) { p += points[i]; n += normals[i]; }
            p /= points.Length; n = n.sqrMagnitude > 0.0001f ? n.normalized : Vector3.up;
            FxOutbox.Add(new FxEvent { kind = kind, point = p, normal = n });
            return kind;
        }

        void OnDeath() { AnnounceDeath(); }

        // what the chunker's OnDeath does here (public for the self test: a real OnDeath would also reach the game's FallToDeath)
        public void AnnounceDeath()
        {
            if (cached != this || Time.realtimeSinceStartup - lastDeath < 2f) return;
            lastDeath = Time.realtimeSinceStartup;
            byte cause = FxEvent.CauseOther;
            try { if (water != null && TooDeepField != null && (bool)TooDeepField.GetValue(water)) cause = FxEvent.CauseWater; else if (ftd != null && ftd.HasKillEnergy()) cause = FxEvent.CauseImpact; } catch (Exception) { }
            FxOutbox.Add(new FxEvent { kind = FxEvent.Death, cause = cause });
            CoopBehaviour.Log("my walkster died (" + (cause == FxEvent.CauseWater ? "water" : cause == FxEvent.CauseImpact ? "impact" : "other") + "); told the others");
        }
    }
}
