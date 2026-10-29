using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // Everything we track about ONE other player: the link to them, their copy in my world, their skin and visible parts, and link statistics.
    public class Peer
    {
        public ulong Id;
        public ITransport Link;
        public bool IsBot; public int BotIndex; public byte BotSkin;
        public int Number;                       // 1-based rank among ALL players by SteamID, so every PC labels everyone the same way
        public Vector3 SelfOffset;               // self-loopback test only: show my own echo 3 m to the side

        public GameObject Clone;
        public RemoteDriver Driver;

        public uint LastRecvSeq;
        public float LastPacketTime = -999f, ConnectedAt = -999f;
        public bool AutoPulled;
        public float LastPullAt = -999f;
        public bool Connected { get { return Time.realtimeSinceStartup - LastPacketTime < 2f; } }   // real time: game time stands still on the start screen
        public float LastStateTime = -999f;   // last position packet (the gap figures in the log), apart from pings
        public int HasMyWorld;                // (I lead) the number of my world this player says they play in: their changes count while it is my world now
        public int ToldHaveWorld;             // (I am a guest, this is the leader) the number of their world I told them I play in
        public float NextEpochSend;
        public float LastRefusedLog = -999f;

        public int RemoteSkin = -1, LastAppliedSkin = -1, AppliedForLocalSkin = -1;
        public float NextSkinCheck, LastSkinApply, NextSkinDiag;
        public string LastSkinState = "";

        public Dictionary<int, bool> VisMap = new Dictionary<int, bool>();
        public Dictionary<int, Transform> CloneTsMap = new Dictionary<int, Transform>();
        public Dictionary<int, MeshRenderer> CloneRsMap = new Dictionary<int, MeshRenderer>();
        public float NextVisBuild, NextVisApply;
        public bool VisLogged;
        public bool WasConnected;

        public float NextCloneDiag, NextPing, NextSyncLog;
        public float GapMax, GapSum, RttSum, RttMax;
        public int GapN, RttN;

        public string Name;                      // the Steam name, cleaned (see PlayerNames); empty until Steam has told us
        public string Label { get { return IsBot ? "Bot " + BotIndex + " (" + ((SceneProfile.CharacterSkin)BotSkin) + ")" : (string.IsNullOrEmpty(Name) ? "Player " + Number : Name); } }

        public void ClearClone() { Clone = null; Driver = null; CloneTsMap.Clear(); CloneRsMap.Clear(); VisLogged = false; LastAppliedSkin = -1; }
    }
}
