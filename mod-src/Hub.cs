using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace CoopMod
{
    // The link to ONE other player. Fast-changing packets go over Steam (channel 0) and, if configured, also directly over the LAN;
    // whichever arrives first wins (receivers drop older sequence numbers). Big/reliable messages use Steam channel 1.
    public class PeerLink : ITransport
    {
        public readonly ulong Id;
        readonly SteamHub hub;
        UdpLanHub lan; IPAddress lanIp;
        internal readonly Queue<byte[]> q0 = new Queue<byte[]>();
        internal readonly Queue<byte[]> q1 = new Queue<byte[]>();
        int lanRx, steamRx;

        internal PeerLink(SteamHub hub, ulong id) { this.hub = hub; Id = id; }

        public void SetLan(UdpLanHub lanHub, IPAddress ip) { lan = lanHub; lanIp = ip; lanHub.Register(ip); }

        public void Send(byte[] data)
        {
            hub.SendTo(Id, data, 0, false);
            if (lan != null) lan.SendTo(lanIp, data);
        }

        public byte[] Receive()
        {
            if (lan != null)
            {
                var l = lan.Receive(lanIp);
                if (l != null) { lanRx++; return l; }
            }
            if (q0.Count > 0) { steamRx++; return q0.Dequeue(); }
            return null;
        }

        public void SendReliable(byte[] data) { hub.SendTo(Id, data, 1, true); }
        public byte[] ReceiveReliable() { return q1.Count > 0 ? q1.Dequeue() : null; }
        public string Diagnostics() { return hub.Diagnostics(Id) + (lan != null ? " | lan rx " + lanRx + ", steam rx " + steamRx : ""); }
        public void Close() { }
    }

    // Sends to every other player at once (used for broadcasts: my state, my visible parts, "push my world").
    public class BroadcastTransport : ITransport
    {
        readonly List<PeerLink> links;
        public BroadcastTransport(List<PeerLink> links) { this.links = links; }
        public void Send(byte[] data) { foreach (var l in links) l.Send(data); }
        public void SendReliable(byte[] data) { foreach (var l in links) l.SendReliable(data); }
        public byte[] Receive() { return null; }
        public byte[] ReceiveReliable() { return null; }
        public string Diagnostics() { return "broadcast to " + links.Count; }
        public void Close() { }
    }

    // Steam P2P (relayed, no port forwarding) using the game's bundled Steamworks.NET. One shared receive pump sorts packets by sender.
    public class SteamHub
    {
        public const int MaxQueue = 256;
        public CSteamID Me { get; private set; }
        public Func<ulong, bool> AcceptUnknown;   // asked when an unknown player (e.g. just joined my lobby) contacts me
        public readonly Dictionary<ulong, PeerLink> Links = new Dictionary<ulong, PeerLink>();
        Callback<P2PSessionRequest_t> onRequest;
        Callback<P2PSessionConnectFail_t> onFail;

        // An id of 0 means "myself" (single-machine test of the Steam path).
        public SteamHub(IEnumerable<ulong> peerIds)
        {
            Me = SteamUser.GetSteamID();
            foreach (ulong id in peerIds)
            {
                ulong real = id == 0 ? Me.m_SteamID : id;
                if (!Links.ContainsKey(real)) Links[real] = new PeerLink(this, real);
            }
            onRequest = Callback<P2PSessionRequest_t>.Create(OnRequest);
            onFail = Callback<P2PSessionConnectFail_t>.Create(OnFail);
        }

        public PeerLink AddPeer(ulong id)
        {
            PeerLink l;
            if (!Links.TryGetValue(id, out l)) { l = new PeerLink(this, id); Links[id] = l; }
            return l;
        }

        void OnRequest(P2PSessionRequest_t r)
        {
            if (Links.ContainsKey(r.m_steamIDRemote.m_SteamID) || (AcceptUnknown != null && AcceptUnknown(r.m_steamIDRemote.m_SteamID)))
            {
                SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote);
                CoopBehaviour.Log("steam: accepted P2P session from " + r.m_steamIDRemote.m_SteamID);
            }
            else CoopBehaviour.Log("steam: ignored P2P request from " + r.m_steamIDRemote.m_SteamID + " (not in my player list)");
        }

        void OnFail(P2PSessionConnectFail_t f)
        {
            CoopBehaviour.Log("steam: P2P connect fail, error " + f.m_eP2PSessionError + " remote " + f.m_steamIDRemote.m_SteamID);
        }

        public void SendTo(ulong id, byte[] data, int channel, bool reliable)
        {
            SteamNetworking.SendP2PPacket(new CSteamID(id), data, (uint)data.Length,
                reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay, channel);
        }

        // Call once per frame: move everything Steam has received into the per-player queues.
        public void Pump()
        {
            for (int ch = 0; ch < 2; ch++)
            {
                uint size;
                int guard = 0;
                while (guard++ < 500 && SteamNetworking.IsP2PPacketAvailable(out size, ch))
                {
                    var b = new byte[size];
                    uint read; CSteamID from;
                    if (!SteamNetworking.ReadP2PPacket(b, size, out read, out from, ch)) break;
                    PeerLink l;
                    if (!Links.TryGetValue(from.m_SteamID, out l)) continue;
                    var q = ch == 0 ? l.q0 : l.q1;
                    q.Enqueue(b);
                    if (ch == 0) while (q.Count > MaxQueue) q.Dequeue();   // never let a stalled game fall behind forever
                }
            }
        }

        public string Diagnostics(ulong id)
        {
            P2PSessionState_t s;
            if (!SteamNetworking.GetP2PSessionState(new CSteamID(id), out s)) return "no P2P session";
            string ip = id == Me.m_SteamID ? "self" : ((s.m_nRemoteIP >> 24) + "." + ((s.m_nRemoteIP >> 16) & 255) + "." + ((s.m_nRemoteIP >> 8) & 255) + "." + (s.m_nRemoteIP & 255));
            return (s.m_bUsingRelay != 0 ? "RELAY" : "direct") + " active=" + s.m_bConnectionActive + " connecting=" + s.m_bConnecting + " err=" + s.m_eP2PSessionError
                + " queued=" + s.m_nPacketsQueuedForSend + "pkts/" + s.m_nBytesQueuedForSend + "B remote=" + ip + ":" + s.m_nRemotePort;
        }

        public void Close() { foreach (var id in Links.Keys) SteamNetworking.CloseP2PSessionWithUser(new CSteamID(id)); }
    }

    // Optional direct UDP between PCs on the same network (coop-lan.txt). One socket; datagrams are sorted by the sender's IP address.
    public class UdpLanHub
    {
        readonly UdpClient udp;
        readonly Dictionary<IPAddress, Queue<byte[]>> queues = new Dictionary<IPAddress, Queue<byte[]>>();
        readonly Dictionary<IPAddress, IPEndPoint> endpoints = new Dictionary<IPAddress, IPEndPoint>();
        public readonly int Port;

        public UdpLanHub(int port) { Port = port; udp = new UdpClient(new IPEndPoint(IPAddress.Any, port)); }

        public void Register(IPAddress ip) { queues[ip] = new Queue<byte[]>(); endpoints[ip] = new IPEndPoint(ip, Port); }

        public void SendTo(IPAddress ip, byte[] data)
        {
            IPEndPoint ep;
            if (!endpoints.TryGetValue(ip, out ep)) return;
            try { udp.Send(data, data.Length, ep); } catch (SocketException) { }
        }

        public void Pump()
        {
            try
            {
                int guard = 0;
                while (guard++ < 500 && udp.Available > 0)
                {
                    IPEndPoint from = null;
                    var d = udp.Receive(ref from);
                    Queue<byte[]> q;
                    if (queues.TryGetValue(from.Address, out q)) { q.Enqueue(d); while (q.Count > SteamHub.MaxQueue) q.Dequeue(); }
                }
            }
            catch (SocketException) { }
        }

        public byte[] Receive(IPAddress ip)
        {
            Queue<byte[]> q;
            return queues.TryGetValue(ip, out q) && q.Count > 0 ? q.Dequeue() : null;
        }

        public void Close() { udp.Close(); }
    }
}
