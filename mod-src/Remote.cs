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
        public const float TeleportDist = 10f;
        float nextLagLog;
        public float errSum, errMax; public int errN;
        Controllable c;
        BipedHandsScript hands;
        SmoothDirection look = new SmoothDirection();
        public AnimationCurve jumpFalloff = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.83333f, 0f));
        float jumpTime, jumpCooldownTime;
        bool expectingRelease;
        bool jumpPressed, jumpReleased;
        public float maxSpeed = 8f, accScale = 2f, jumpCooldown = 1f, lookRate = 6f;

        void Start()
        {
            c = GetComponent<Controllable>();
            hands = GetComponent<BipedHandsScript>();
            look.SetTrackingRate(lookRate);
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

        // Direct speed correction: the input-driven walk alone cannot close a gap quickly, so steer the body's horizontal velocity toward the real player.
        void FixedUpdate()
        {
            if (!hasNet || c == null || c.body == null) return;
            Vector3 predicted = netPos + netVel * (Time.time - netTime);
            Vector3 full = predicted - c.body.position;
            if (full.magnitude > 14f) return;   // Update() teleports in that case
            Vector3 err = full; err.y = 0f;
            float horiz = err.magnitude;
            // Vertical catch-up: the peer is climbing or jumping and the input-driven walk cannot follow a rock face on its own.
            bool grabbing = input.leftGrab || input.rightGrab || (hands != null && hands.IsClimbing());
            bool followY = grabbing || Mathf.Abs(full.y) > 2f;
            if (horiz < 0.3f && !(followY && Mathf.Abs(full.y) > 0.3f)) return;
            Vector3 v = c.body.velocity;
            Vector3 want = netVel + Vector3.ClampMagnitude(err * 6f, 12f); want.y = 0f;
            Vector3 h = new Vector3(v.x, 0f, v.z);
            h = Vector3.Lerp(h, want, Mathf.Clamp01(0.2f + horiz * 0.15f));
            float vy = v.y;
            if (followY)
            {
                float wantY = netVel.y + Mathf.Clamp(full.y * 6f, -12f, 12f);
                vy = Mathf.Lerp(v.y, wantY, Mathf.Clamp01(0.3f + Mathf.Abs(full.y) * 0.1f));
            }
            c.body.velocity = new Vector3(h.x, vy, h.z);
        }

        void Update()
        {
            if (c == null) return;
            if (mirrorFrom != null) c.jumpEnabled = mirrorFrom.jumpEnabled;
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
            if (hasNet)
            {
                Vector3 predicted = netPos + netVel * (Time.time - netTime);
                Vector3 err = predicted - c.body.position;
                float horiz = new Vector2(err.x, err.z).magnitude;
                errSum += horiz; errN++; if (horiz > errMax) errMax = horiz;
                if (err.magnitude > 14f) { CoopBehaviour.Log("remote teleport, err=" + horiz.ToString("F1") + " y=" + err.y.ToString("F1")); transform.root.position += err; }
                else
                {
                    err.y = 0f;
                    target += Vector3.ClampMagnitude(err * 3f, 8f);
                    if (horiz > 3f && Time.time >= nextLagLog)
                    {
                        nextLagLog = Time.time + 1f;
                        CoopBehaviour.Log("lag: err=" + horiz.ToString("F1") + " cloneSpeed=" + c.body.velocity.magnitude.ToString("F1") + " netSpeed=" + netVel.magnitude.ToString("F1")
                            + " inAir=" + c.m_inAir + " grab=" + (hands != null && (hands.hand[0].grabbing || hands.hand[1].grabbing)) + " climbing=" + (hands != null && hands.IsClimbing())
                            + " move=" + input.move.magnitude.ToString("F2") + " age=" + (Time.time - netTime).ToString("F2"));
                    }
                }
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

        void Grab(int i, bool want)
        {
            var h = hands.hand[i];
            if (h.released) h.released = want; else h.grabbing = want;
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
                    c.m_TriggerJump = true;
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
