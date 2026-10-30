namespace CoopMod
{
    public enum Route { Ignore, Visibility, Shared, Chat, Oversized, SnapshotNotFromHost, World, HaveWorld, Epoch, Speedrun, LeaderLeft, Creature }

    // The one place that decides what a reliable message from another player is allowed to do.
    //  - plant / crystal / island events: anybody may send them
    //  - a world snapshot replaces my whole world, so it is only accepted from the host
    //  - nothing bigger than MaxMessage is looked at
    //  - speedrun messages (20-25) only at their exact size; ARM, GO and CANCEL only from the host
    //  - creature lists, deaths and venus traps (17) only from the host, at their exact size
    public static class MessageGate
    {
        public const byte MsgVisibility = 3;
        public const int MaxMessage = 4500000;   // big messages arrive in pieces (PeerLink) and are whole again here

        public static Route Classify(byte[] m, bool senderIsHost)
        {
            if (m == null || m.Length == 0) return Route.Ignore;
            if (SrMsg.Is(m[0])) return m.Length == SrMsg.Length(m[0]) && (senderIsHost || !SrMsg.HostOnly(m[0])) ? Route.Speedrun : Route.Ignore;
            if (m[0] == MsgVisibility) return Route.Visibility;
            if (m[0] == Chat.Msg) return m.Length > Chat.MaxBytes ? Route.Oversized : Route.Chat;
            if (m[0] == Authority.MsgHaveWorld) return m.Length == 5 ? Route.HaveWorld : Route.Ignore;
            if (m[0] == Authority.MsgEpoch) return m.Length == 5 && senderIsHost ? Route.Epoch : Route.Ignore;
            if (m[0] == LeaderWatch.MsgLeft) return m.Length == 5 && senderIsHost ? Route.LeaderLeft : Route.Ignore;   // only the leader can say it left its world
            if (m[0] == SharedWorld.MsgPickup) return m.Length == 14 ? Route.Shared : Route.Ignore;
            if (m[0] == SharedWorld.MsgGrow) return m.Length >= 16 && m.Length <= 100 ? Route.Shared : Route.Ignore;    // a vine start/end/revoke is small
            if (m[0] == StorySync.Msg) return m.Length >= StorySync.MinBytes && m.Length <= StorySync.MaxBytes ? Route.Shared : Route.Ignore;   // a story flag or cutscene is small
            if (m[0] == CreatureSync.Msg) return senderIsHost && CreatureSync.OpOf(m) != 0 ? Route.Creature : Route.Ignore;   // creatures: only the leader runs them
            if (SharedWorld.IsMessage(m)) return m.Length > MaxMessage ? Route.Oversized : Route.Shared;
            if (m.Length > MaxMessage) return Route.Oversized;
            if (WorldSync.IsSnapshot(m) && !senderIsHost) return Route.SnapshotNotFromHost;
            return Route.World;
        }
    }
}
