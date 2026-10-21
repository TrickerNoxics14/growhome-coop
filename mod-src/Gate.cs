namespace CoopMod
{
    public enum Route { Ignore, Visibility, Shared, Chat, Oversized, SnapshotNotFromHost, World }

    // The one place that decides what a reliable message from another player is allowed to do.
    //  - plant / crystal / island events: anybody may send them
    //  - a world snapshot replaces my whole world, so it is only accepted from the host
    //  - nothing bigger than MaxMessage is looked at
    public static class MessageGate
    {
        public const byte MsgVisibility = 3;
        public const int MaxMessage = 2000000;

        public static Route Classify(byte[] m, bool senderIsHost)
        {
            if (m == null || m.Length == 0) return Route.Ignore;
            if (m[0] == MsgVisibility) return Route.Visibility;
            if (m[0] == Chat.Msg) return m.Length > Chat.MaxBytes ? Route.Oversized : Route.Chat;
            if (SharedWorld.IsMessage(m)) return m.Length > MaxMessage ? Route.Oversized : Route.Shared;
            if (m.Length > MaxMessage) return Route.Oversized;
            if (WorldSync.IsSnapshot(m) && !senderIsHost) return Route.SnapshotNotFromHost;
            return Route.World;
        }
    }
}
