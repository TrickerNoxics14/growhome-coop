using System;
using System.IO;

// The launcher's settings are plain files next to the game (the mod re-reads the same files every few seconds): a file means the unusual choice.
// The one setting that is a choice rather than a switch is the room the F2 menu's Enter key picks: it is stored as one word in coop-roomdefault.txt.
static class CoopSettings
{
    public const string RoomFile = "coop-roomdefault.txt";

    // "public", "ask" (listed, I accept each player) or "private". Anything else counts as public.
    public static string RoomDefault(string text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant();
        return t == "ask" || t == "private" ? t : "public";
    }

    public static string NextRoom(string room)
    {
        switch (RoomDefault(room))
        {
            case "public": return "ask";
            case "ask": return "private";
            default: return "public";
        }
    }

    public static string RoomLabel(string room)
    {
        switch (RoomDefault(room))
        {
            case "ask": return "Listed, you accept";
            case "private": return "Private";
            default: return "Public";
        }
    }

    // The BUD skins the game has, in the order the settings page cycles through them. The mod reads the name from coop-skin.txt.
    public static readonly string[] Skins = { "Standard", "Ninja", "Native", "Desert", "Oceanic", "WhyAye", "Facebook" };

    public static string SkinLabel(string skin)
    {
        switch (skin) { case "Standard": return "Red"; case "Oceanic": return "Blue"; case "WhyAye": return "Why aye"; default: return skin; }
    }

    public static string NextSkin(string skin)
    {
        int i = Array.IndexOf(Skins, skin);
        return Skins[(i + 1) % Skins.Length];
    }

    public static string ReadSkin(string gameDir)
    {
        try
        {
            string f = Path.Combine(gameDir, "coop-skin.txt");
            string s = File.Exists(f) ? File.ReadAllText(f).Trim() : "";
            return Array.IndexOf(Skins, s) >= 0 ? s : "Standard";
        }
        catch (Exception) { return "Standard"; }
    }

    public static string ReadRoom(string gameDir)
    {
        try
        {
            string f = Path.Combine(gameDir, RoomFile);
            return File.Exists(f) ? RoomDefault(File.ReadAllText(f)) : "public";
        }
        catch (Exception) { return "public"; }
    }
}
