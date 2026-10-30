using UnityEngine;

namespace CoopMod
{
    // What a remote player sends us each tick (camera-relative stick, buttons, their camera forward).
    public class RemoteInput
    {
        public Vector2 move;
        public bool jumpDown, jumpUp, leftGrab, rightGrab;
        public Vector3 camForward = Vector3.forward;
        public float jumpScale;
        public bool airAbility;          // jetpack, flower or leaf in use: the copy has no lift of its own, so it follows the real height
    }

    // Applies RemoteInput through the same fields ExploreInputHandler writes for the local player.
    public class RemoteDriver : MonoBehaviour
    {
        public RemoteInput input = new RemoteInput();
        public Controllable mirrorFrom;
        public bool useKeyboard = true;
        public bool hasNet;
        public Vector3 netPos, netVel;
        public float netTime;
        float nextLagLog, nextSnapLog;
        public float errSum, errMax; public int errN;
        // the path this copy follows (see PoseBuffer), and how it moves along it
        public readonly PoseBuffer Buffer = new PoseBuffer();
        readonly Follower follow = new Follower();
        readonly SnapRule snapRule = new SnapRule();
        PoseOut cur; float curAt; bool haveCur, justEnabled;
        public bool Far;                         // far away or off screen: moved straight along the path, no physics
        public float FrozenTime;                 // time the game had this copy frozen (it never should)
        public int Snaps, Unfreezes, FarSteps, NearSteps;
        public string LastSnap = "";
        Rigidbody[] bodies;
        Controllable c;
        BipedHandsScript hands;
        SmoothDirection look = new SmoothDirection();
        public AnimationCurve jumpFalloff = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.83333f, 0f));
        float jumpTime, jumpCooldownTime;
        bool expectingRelease;
        bool jumpPressed, jumpReleased;
        public float maxSpeed = 8f, accScale = 2f, jumpCooldown = 1f, lookRate = 6f;
        // the jump is not started from Update: it is queued and started inside one physics step that JumpWindow guards (see JumpSteps)
        bool jumpQueued, windowOpen;
        float jumpQueuedAt, windowOpenedAt;
        public bool LastJumpUsed;                // the game's jump code used the last guarded jump (false: it was cleared unused)
        public bool wakeForTest;                 // self test: keep the game's out-of-sight sleep off for the guarded step
        static readonly System.Reflection.FieldInfo InhibitField = typeof(BipedWalkScript).GetField("m_distanceDisabledInhibit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        AudioSource voice;
        bool voiceMissingLogged;
        public AudioSource Voice { get { return voice; } }

        void Start()
        {
            c = GetComponent<Controllable>();
            int terrain = LayerMask.NameToLayer("Terrain");
            if (terrain >= 0)
            {
                int mask = 1 << terrain;
                Buffer.Ground = delegate (Vector3 from, out float y)
                {
                    RaycastHit hit; y = 0f;
                    if (!Physics.Raycast(from, Vector3.down, out hit, 60f, mask)) return false;
                    y = hit.point.y; return true;
                };
            }
            hands = GetComponent<BipedHandsScript>();
            look.SetTrackingRate(lookRate);
            var t = transform.FindChild("AudioSources/Voice");
            voice = t != null ? t.audio : null;
            if (voice == null)
            {
                // no voice of its own: a silent one, so a copy's line can never fall back to mine
                var g = new GameObject("CoopVoice");
                g.transform.parent = transform; g.transform.localPosition = Vector3.zero;
                voice = g.AddComponent<AudioSource>(); voice.volume = 0f;
            }
        }

        // Coroutines stop while the copy is switched off (dead, or saved from the chunker): start again each time it comes back.
        void OnEnable() { justEnabled = true; KeepAwake(); StartCoroutine(JumpSteps()); }
        void OnDisable() { if (windowOpen) CloseWindow(); }
        void OnDestroy() { if (windowOpen) CloseWindow(); }

        public void QueueJumpForTest() { jumpQueued = true; jumpQueuedAt = Time.time; }

        // WaitForFixedUpdate resumes after every physics step. A jump set there is used by the copy's walk script in the very next step, whatever
        // order the scripts run in, and the next resume closes the window again: the swap never lasts more than that one step.
        System.Collections.IEnumerator JumpSteps()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                if (windowOpen) CloseWindow();
                KeepAwake();
                if (!jumpQueued || c == null) continue;
                if (Time.time - jumpQueuedAt > 0.3f) { jumpQueued = false; continue; }   // waited too long behind another copy: dropped
                if (JumpWindow.Owner != null) continue;                                    // another copy jumps in this step: next one
                jumpQueued = false;
                var w = c as BipedWalkScript;
                if (!JumpWindow.Open(this, voice, w))
                {
                    if (!voiceMissingLogged) { voiceMissingLogged = true; CoopBehaviour.Log("copy jump: the game's voice field was not found; copies jump without the jump animation so my voice is never used"); }
                    continue;
                }
                windowOpen = true; windowOpenedAt = Time.realtimeSinceStartup;
                if (wakeForTest && InhibitField != null) InhibitField.SetValue(w, true);
                c.m_TriggerJump = true;
            }
        }

        void CloseWindow()
        {
            windowOpen = false;
            JumpWindow.Close();
            LastJumpUsed = c == null || !c.m_TriggerJump;
            if (c != null && c.m_TriggerJump) { c.m_TriggerJump = false; JumpWindow.Unconsumed++; }   // not used in its step (the copy was asleep or could not jump): never later, outside the guard
        }

        void LateUpdate()
        {
            if (windowOpen && Time.realtimeSinceStartup - windowOpenedAt > 0.25f) CloseWindow();   // no physics step came (the game stood still)
            if (Time.time < JumpWindow.MuteRumbleUntil) { try { XInput.SetVibration(0, 0f, 0f); } catch (System.Exception) { } }   // the copy's leaf jump rumbled MY pad
        }

        // Local test source: IJKL move, U jump, N/M left/right grab (all relative to the main camera).
        void ReadKeyboard()
        {
            float x = (Input.GetKey(KeyCode.L) ? 1f : 0f) - (Input.GetKey(KeyCode.J) ? 1f : 0f);
            float y = (Input.GetKey(KeyCode.I) ? 1f : 0f) - (Input.GetKey(KeyCode.K) ? 1f : 0f);
            input.move = new Vector2(x, y);
            input.jumpDown |= Input.GetKeyDown(KeyCode.U);
            input.jumpUp |= Input.GetKeyUp(KeyCode.U);
            input.leftGrab = Input.GetKey(KeyCode.N);
            input.rightGrab = Input.GetKey(KeyCode.M);
            if (Camera.main != null) input.camForward = Camera.main.transform.forward;
        }

        // A new pose packet of this copy's player (the newest one: also the inputs). Old packets without a motion block use their arrival time.
        public void Take(PlayerState s, Vector3 offset)
        {
            hasNet = true; netPos = s.pos + offset; netVel = s.vel; netTime = Time.time;
            AddSample(s, offset);
        }

        public void AddSample(PlayerState s, Vector3 offset)
        {
            double now = Time.realtimeSinceStartup;
            if (s.hasMotion) Buffer.Add(s.session, s.seq, s.sendMs / 1000.0, s.pos + offset, s.vel, s.mflags, s.tp, now);
            else Buffer.Add(0u, s.seq, now, s.pos + offset, s.vel, 0, 0, now);
        }

        Rigidbody[] Bodies()
        {
            if (bodies == null || bodies.Length == 0 || bodies[0] == null) bodies = transform.root.GetComponentsInChildren<Rigidbody>(true);
            return bodies;
        }

        // The game puts a walkster that is not mine to sleep (frozen) when it thinks the camera cannot see it. A copy must never sleep:
        // its player is still moving. Set before every physics step; a copy found asleep is woken.
        void KeepAwake()
        {
            var w = c as BipedWalkScript;
            if (w == null) return;
            if (InhibitField != null) InhibitField.SetValue(w, true);
            if (w.m_distanceDisabled)
            {
                foreach (var rb in Bodies()) if (rb != null) rb.isKinematic = false;
                w.m_distanceDisabled = false;
                Unfreezes++;
            }
        }

        PoseOut Sample(float dt)
        {
            if (Buffer.Count > 0) return Buffer.Sample(Time.realtimeSinceStartup, dt);
            // no packet in the buffer (a test set the pose by hand): the last pose, guessed ahead for at most a quarter second
            var o = new PoseOut();
            o.valid = true;
            o.pos = netPos + netVel * Mathf.Clamp(Time.time - netTime, 0f, PoseBuffer.MaxAhead);
            o.vel = Time.time - netTime < PoseBuffer.MaxAhead ? netVel : Vector3.zero;
            return o;
        }

        // Height is followed fully while they climb, fly or are in the air (the copy has no grip or lift of its own), or when it is far off.
        bool FollowHeight(Vector3 err)
        {
            if (Mathf.Abs(err.y) > 1.5f) return true;
            if (Buffer.Session != 0 && Buffer.Count > 0) return (cur.flags & (MotionWire.InAir | MotionWire.Climbing | MotionWire.Flying)) != 0;
            bool grabbing = input.leftGrab || input.rightGrab || (hands != null && hands.IsClimbing());
            bool flying = input.airAbility && Time.time - netTime < RemoteFx.Timeout;   // stale news: back to normal walking
            return grabbing || flying;
        }

        // Once a frame: where the path is now, near or far, and a jump only on a teleport mark or a big error that stays.
        void Steer(float dt)
        {
            if (c.body == null) return;
            var o = Sample(dt);
            if (!o.valid) return;
            cur = o; curAt = Time.time; haveCur = true;
            Vector3 at = c.body.position;
            Vector3 err = o.pos - at;
            float horiz = new Vector2(err.x, err.z).magnitude;
            errSum += horiz; errN++; if (horiz > errMax) errMax = horiz;
            var cam = Camera.main;
            if (cam != null)
            {
                float d = Vector3.Distance(cam.transform.position, at);
                Vector3 vp = cam.WorldToViewportPoint(at);
                bool onScreen = vp.z > 0f && vp.x > -0.2f && vp.x < 1.2f && vp.y > -0.2f && vp.y < 1.2f;
                Far = Far ? (d > 18f || (!onScreen && d > 6f)) : (d > 22f || (!onScreen && d > 10f));
            }
            else Far = false;
            bool back = justEnabled; justEnabled = false;
            if (snapRule.Step(err.magnitude, o.vel.magnitude, o.teleport || (back && err.magnitude > 1.5f), dt))
            {
                SnapTo(o, o.teleport ? "teleport mark" : (back ? "shown again" : "far off"), err);
                return;
            }
            c.body.interpolation = Far ? RigidbodyInterpolation.None : RigidbodyInterpolation.Interpolate;
            if (Far)
            {
                if (!follow.Init) follow.Reset(at, c.body.velocity);
                follow.Step(o, dt);
                transform.root.position += follow.Pos - at;
                FarSteps++;
            }
            else { follow.Reset(at, c.body.velocity); NearSteps++; }
            if (horiz > 3f && Time.time >= nextLagLog)
            {
                nextLagLog = Time.time + 1f;
                CoopBehaviour.Log("lag: err=" + horiz.ToString("F1") + " cloneSpeed=" + c.body.velocity.magnitude.ToString("F1") + " pathSpeed=" + o.vel.magnitude.ToString("F1")
                    + " far=" + Far + " ahead=" + o.ahead.ToString("F2") + " delay=" + (Buffer.Delay * 1000f).ToString("F0") + "ms climbing=" + (hands != null && hands.IsClimbing())
                    + " move=" + input.move.magnitude.ToString("F2") + " age=" + (Time.time - netTime).ToString("F2"));
            }
        }

        // Every part of the copy straight to the path, with the path's speed.
        void SnapTo(PoseOut o, string why, Vector3 err)
        {
            transform.root.position += o.pos - c.body.position;
            foreach (var rb in Bodies()) if (rb != null && !rb.isKinematic) rb.velocity = o.vel;
            follow.Reset(o.pos, o.vel);
            Snaps++; LastSnap = why;
            if (Time.time >= nextSnapLog) { nextSnapLog = Time.time + 1f; CoopBehaviour.Log("remote snap (" + why + "), err=" + err.magnitude.ToString("F1") + " y=" + err.y.ToString("F1")); }
        }

        // Near copies: one spring toward the path with its speed fed forward (the only pull; the game's walk is asked for the same speed).
        // Far copies: their parts get the path's speed, so physics does not move them away between frames.
        void FixedUpdate()
        {
            KeepAwake();
            if (!hasNet || c == null || c.body == null || !haveCur) return;
            if (c.body.isKinematic) FrozenTime += Time.fixedDeltaTime;
            float ahead = Mathf.Clamp(Time.time - curAt, -0.1f, 0.1f);
            Vector3 tgt = cur.pos + cur.vel * ahead;
            Vector3 err = tgt - c.body.position;
            if (Far)
            {
                foreach (var rb in Bodies()) if (rb != null && !rb.isKinematic) rb.velocity = cur.vel;
                c.body.useGravity = false;
                return;
            }
            bool followY = FollowHeight(err);
            c.body.useGravity = !followY;
            Vector3 want = Follower.Want(err, cur.vel, 6f);
            Vector3 v = c.body.velocity;
            c.body.velocity = new Vector3(want.x, followY ? want.y : v.y, want.z);
        }

        void Update()
        {
            if (c == null) return;
            if (hasNet) Steer(Time.unscaledDeltaTime);
            if (mirrorFrom != null) c.jumpEnabled = mirrorFrom.jumpEnabled;
            // a hard bump must not "kill" the copy: the game would cut MY voice line and count a death in MY stats (BipedWalkScript.HandleCollision)
            var bw = c as BipedWalkScript;
            if (bw != null && !bw.m_immortal) bw.m_immortal = true;
            if (useKeyboard) ReadKeyboard();
            Jump();
            if (hasNet) c.m_JumpScale = input.jumpScale; // sender's real jump strength: the game's own jump timing depends on frame rate
            GrabLog();
            Vector3 normal = c.averageGroundNormal;
            Vector3 fwd = input.camForward;
            Vector3 vector = (fwd - normal * Vector3.Dot(fwd, normal)).normalized;
            Vector3 vector2 = Vector3.Cross(normal, vector).normalized;
            Vector2 value = input.move;
            bool grabbingAny = false;
            if (hands != null)
            {
                Grab(0, input.leftGrab);
                Grab(1, input.rightGrab);
                grabbingAny = hands.hand[0].grabbing || hands.hand[1].grabbing;
                Vector3 n = Vector3.zero; float num = 0f;
                for (int i = 0; i < 2; i++) if (hands.hand[i].Climbing()) { n += hands.hand[i].grabbedObjNormal; num += 1f; }
                if (num > 0f)
                {
                    if (n.magnitude > 0.01f) { n /= num; n.Normalize(); } else n = -1f * fwd;
                    Vector3 up = Vector3.up;
                    float d = Vector3.Dot(n, up);
                    if (Mathf.Abs(d) > 0.8f)
                    {
                        up = Quaternion.AngleAxis(Mathf.Sign(d) * 90f, Vector3.Cross(Vector3.up, fwd).normalized) * up;
                        d = Vector3.Dot(n, up);
                    }
                    vector = (up - n * d).normalized;
                    vector2 = Vector3.Cross(n, vector);
                    hands.desiredOffset = vector * value.y + vector2 * value.x;
                }
                else hands.desiredOffset = Vector3.zero;
            }
            Vector3 expectedMove = value.x * vector2 + value.y * vector;
            bool anyInput = value.sqrMagnitude > 0.0001f;
            look.Value = (anyInput && !grabbingAny) ? expectedMove.normalized : fwd;
            Vector3 target = expectedMove * maxSpeed;
            if (target.magnitude > maxSpeed) target *= maxSpeed / target.magnitude;
            if (hasNet && haveCur)
            {
                // the walk animation is asked for the path's own speed, so the game's walk and my steering pull the same way
                Vector3 hv = cur.vel; hv.y = 0f;
                target = hv.magnitude > maxSpeed ? hv * (maxSpeed / hv.magnitude) : hv;
            }
            c.desiredMove = Vector3.zero;
            c.lookVector = look.Value;
            c.desiredTurn = Vector3.zero;
            c.jump = false;
            c.crouch = false;
            c.desiredAcc = (target - c.smoothedVelocity) * accScale;
            look.Update();
        }

        string[] lastGrab = new string[2];
        void GrabLog()
        {
            if (hands == null) return;
            for (int i = 0; i < 2 && i < hands.hand.Length; i++)
            {
                var h = hands.hand[i];
                string now = (h.grabbing && h.grabbedObject != null) ? ObjectSync.Describe(h.grabbedObject) : (h.grabbing ? "(grab pressed, nothing held)" : "");
                if (now != lastGrab[i]) { lastGrab[i] = now; if (now.Length > 0) CoopBehaviour.Log("grab remote" + (i == 0 ? " L: " : " R: ") + now); }
            }
        }

        // A remote player never grabs anything in my world. Its hands would pull on the plant I stand on, and the game assumes one set of hands (mine):
        // a grab by the copy makes MY walkster hang on that branch (PlantBranch.HandleGrabFlex) and points MY camera at it (PlantControlNub.OnRelease).
        void Grab(int i, bool want)
        {
            var h = hands.hand[i];
            h.grabbing = false;
            h.released = false;
        }

        void Jump()
        {
            jumpPressed |= input.jumpDown; jumpReleased |= input.jumpUp;
            input.jumpDown = input.jumpUp = false;
            if (SceneProfile.JumpEnabled)
            {
                if (c.m_OffGroundTime < 0.21f && jumpCooldownTime < 0.0001f && jumpPressed)
                {
                    expectingRelease = true;
                    CoopBehaviour.Log("remote jump triggered");
                    jumpCooldownTime = jumpCooldown;
                    jumpTime = 0f;
                    c.m_OffGroundTime = jumpCooldown;
                    jumpQueued = true; jumpQueuedAt = Time.time;   // started in the next guarded physics step (JumpSteps), never from here
                    c.m_JumpScale = 1f;
                    Vector3 f = Vector3.ProjectOnPlane(input.camForward, Vector3.up).normalized;
                    c.m_JumpDirection = Vector3.Cross(Vector3.up, f) * input.move.x + f * input.move.y;
                }
                if (expectingRelease && jumpReleased) { c.m_JumpScale = 0f; expectingRelease = false; }
                else if (expectingRelease) { c.m_JumpScale = jumpFalloff.Evaluate(jumpTime); jumpTime += Time.fixedDeltaTime; }
                else c.m_JumpScale = 0f;
                jumpCooldownTime = Mathf.Max(0f, jumpCooldownTime - Time.fixedDeltaTime);
            }
            jumpPressed = jumpReleased = false;
        }
    }
}
