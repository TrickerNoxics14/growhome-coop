using UnityEngine;

namespace CoopMod
{
    // A scripted fake player for testing many-player behaviour without other computers. It produces the same packets a real player would send:
    // it runs in a circle around where it was created, jumps now and then, and has its own skin. Nothing goes over the network.
    public class BotLink : ITransport
    {
        readonly Vector3 center;
        readonly float radius, omega, phase, groundOffset, jumpEvery;
        readonly byte skin;
        readonly float t0;
        float nextPacket, jumpStart = -99f, lastY;
        uint seq;
        bool jumpDownSent, jumpUpSent;
        static int terrainMask = -2;

        public BotLink(int index, byte skin, Vector3 center, float groundOffset)
        {
            this.skin = skin; this.center = center; this.groundOffset = groundOffset;
            radius = 3.5f + 1.2f * index;
            omega = (index % 2 == 0 ? 1f : -1f) * (6.5f / radius);     // about 6.5 m/s along the circle, alternate directions
            phase = index * 2.1f;
            jumpEvery = 3.5f + index;
            t0 = Time.time; lastY = center.y;
            if (terrainMask == -2) { int l = LayerMask.NameToLayer("Terrain"); terrainMask = l >= 0 ? (1 << l) : -1; }
        }

        float GroundY(Vector3 p)
        {
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(p.x, p.y + 25f, p.z), Vector3.down, out hit, 60f, terrainMask)) lastY = hit.point.y + groundOffset;
            return lastY;
        }

        public void Send(byte[] data) { }
        public void SendReliable(byte[] data) { }
        public byte[] ReceiveReliable() { return null; }
        public string Diagnostics() { return "bot"; }
        public void Close() { }

        public byte[] Receive()
        {
            if (Time.time < nextPacket) return null;
            nextPacket = Time.time + 0.05f;
            float a = phase + omega * (Time.time - t0);
            var pos = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            pos.y = GroundY(pos);
            float dir = omega > 0f ? 1f : -1f;
            var tangent = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * dir;

            // jump: press at the start of each cycle, hold about 0.35 s, jump strength falls off like the real one
            if (Time.time - jumpStart > jumpEvery) { jumpStart = Time.time; jumpDownSent = false; jumpUpSent = false; }
            float jt = Time.time - jumpStart;
            bool down = false, up = false;
            if (!jumpDownSent) { down = true; jumpDownSent = true; }
            if (!jumpUpSent && jt > 0.35f) { up = true; jumpUpSent = true; }
            float scale = jt < 0.35f ? Mathf.Clamp01(1f - jt / 0.83f) : 0f;

            var s = new PlayerState
            {
                seq = ++seq,
                pos = pos,
                vel = tangent * Mathf.Abs(omega) * radius,
                camForward = Vector3.forward,
                move = new Vector2(tangent.x, tangent.z),
                skin = skin,
                jumpScale = scale,
                jumpDown = down, jumpUp = up,
            };
            return s.Write();
        }
    }
}
