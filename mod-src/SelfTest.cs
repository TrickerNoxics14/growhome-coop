using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CoopMod
{
    // The in-game half of the audit (audit\run-audit.ps1 starts it). Only runs when coop-selftest.txt exists next to the exe, never in normal play.
    // It presses CONTINUE by itself, then runs the critical checks listed in audit\required-checks.txt against the real game and the real send/receive code,
    // and writes coop-selftest-result.txt with one line per check:   CHECK <ID> <PASS|FAIL> <what> - <detail>
    // A required check that never reported counts as failed, so a check that crashes or is skipped cannot hide.
    // "quick" in coop-selftest.txt runs a shorter growth phase (development only; the audit uses the full run).
    public class SelfTest : MonoBehaviour
    {
        class Sink : ITransport
        {
            public readonly List<byte[]> Messages = new List<byte[]>();
            public void Send(byte[] data) { }
            public void SendReliable(byte[] data) { Messages.Add(data); }
            public byte[] Receive() { return null; }
            public byte[] ReceiveReliable() { return null; }
            public string Diagnostics() { return "self test"; }
            public void Close() { }
        }

        delegate void Step();

        const ulong Friend = 76561199000000001UL;
        const byte TypePlant = 4, TypeShard = 5, TypeIsland = 6;
        readonly Sink sink = new Sink();
        readonly StringBuilder report = new StringBuilder();
        int pass, fail, warn;
        bool finished, quick;
        byte[] lastPlantMessage;                  // a real plant update, kept for the fuzz test

        static string Here(string file) { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, file); }
        static void Log(string m) { CoopBehaviour.Log("selftest: " + m); }

        void Awake()
        {
            // intro screens are skipped by the mod itself (Bootstrap); the audit checks that
            StartCoroutine(Run());
        }

        void Update()
        {
            AudioListener.volume = 0f;           // nobody is listening
            if (SharedWorld.TestSink != null) SharedWorld.Tick(sink);   // with nobody else connected the mod does not run its sync loop; the audit runs it so the same code is exercised
            if (Singleton<PauseManager>.Exists && PauseManager.Paused) PauseManager.ResumeRequested = true;   // an unfocused window must not stop the test
        }

        // ---- reporting ---------------------------------------------------------------------------------------------------------------

        void Check(string id, string what, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            string line = "CHECK " + id + " " + (ok ? "PASS" : "FAIL") + " " + what + (string.IsNullOrEmpty(detail) ? "" : " - " + detail);
            Log(line); report.AppendLine(line);
        }

        void Note(string text) { Log(text); report.AppendLine("NOTE " + text); }

        void Warn(string what, string detail)
        {
            warn++;
            string line = "WARN " + what + (string.IsNullOrEmpty(detail) ? "" : " - " + detail);
            Log(line); report.AppendLine(line);
        }

        static string Describe(Exception e) { return SharedWorld.Short(e); }

        // Runs one step; a crash inside it is reported as a failed check instead of silently ending the audit.
        bool Try(string id, string what, Step s)
        {
            try { s(); return true; }
            catch (Exception e) { Check(id, what, false, Describe(e)); return false; }
        }

        // ---- helpers -----------------------------------------------------------------------------------------------------------------

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // [4][string plantId][ulong sender][int rawLen][int compLen][uint checksum][bytes]
        static string MsgPlantId(byte[] m) { try { var r = new BinaryReader(new MemoryStream(m)); if (r.ReadByte() != TypePlant) return null; return r.ReadString(); } catch (Exception) { return null; } }

        static void MsgSizes(byte[] m, out int raw, out int comp)
        {
            var r = new BinaryReader(new MemoryStream(m)); r.ReadByte(); r.ReadString(); r.ReadUInt64();
            raw = r.ReadInt32(); comp = r.ReadInt32();
        }

        static byte[] WithSender(byte[] m, ulong sender)
        {
            var r = new BinaryReader(new MemoryStream(m)); r.ReadByte(); r.ReadString();
            int pos = (int)r.BaseStream.Position;
            var copy = (byte[])m.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(sender), 0, copy, pos, 8);
            return copy;
        }

        List<byte[]> MessagesFor(string id)
        {
            var list = new List<byte[]>();
            foreach (var m in sink.Messages) if (MsgPlantId(m) == id) list.Add(m);
            return list;
        }

        int CountType(byte type)
        {
            int n = 0;
            foreach (var m in sink.Messages) if (m.Length > 0 && m[0] == type) n++;
            return n;
        }

        static int QueuedLoadCallbacks()
        {
            var d = SaveSystem.OnLoadComplete;
            return d == null ? 0 : d.GetInvocationList().Length;
        }

        static string HashOf(byte[] data)
        {
            return BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(data)).Replace("-", "");
        }

        // path (relative to dir) -> sha256 of every file under dir, except those below 'exclude'
        static Dictionary<string, string> Snapshot(string dir, string exclude)
        {
            var d = new Dictionary<string, string>();
            if (!Directory.Exists(dir)) return d;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (exclude != null && f.StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
                d[f.Substring(dir.Length)] = HashOf(File.ReadAllBytes(f));
            }
            return d;
        }

        static bool SameFiles(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a) { string v; if (!b.TryGetValue(kv.Key, out v) || v != kv.Value) return false; }
            return true;
        }

        static bool Under(string path, string folder)
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/'), f = Path.GetFullPath(folder).TrimEnd('\\', '/');
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        bool StartMenuReady()
        {
            if (!Singleton<StartScreen>.Exists) return false;
            try
            {
                var f = typeof(StartScreen).GetField("m_state", BindingFlags.NonPublic | BindingFlags.Instance);
                return f != null && f.GetValue(Singleton<StartScreen>.Instance).ToString() == "eLogoScreen";
            }
            catch (Exception) { return false; }
        }

        // One "pull" on the plant, like a player dragging its growth nub. Returns false when this plant cannot be made to grow.
        static bool Stimulate(PlantRoot root)
        {
            if (root.IsTrunk())
            {
                float total = root.m_Spline != null ? root.m_Spline.GetTotalAmount() : 0f;
                if (total <= 1f || root.IsFullyGrown) return false;
                root.TrunkGrowthFraction = root.TrunkGrowthFraction + 5f / total;     // about 5 m of trunk
                return true;
            }
            PlantBranch best = null;
            foreach (var b in root.m_Branches) if (b != null && (best == null || b.m_Length > best.m_Length)) best = b;
            if (best == null) return false;
            best.TriggerGrowth(4f);
            return true;
        }

        // Names of every saved part below a plant ("StemPrefab|prefab id|part"), used to say exactly what differs when two copies disagree.
        static List<string> ObjectNames(PlantRoot r)
        {
            var list = new List<string>();
            foreach (var si in r.GetComponentsInChildren<SaveInfo>(true))
                list.Add(si.name.Replace("(Clone)", "") + "|" + si.m_prefabID + (si.m_isPrefabPart ? "|part" : "") + (si.m_runtimeSpawned ? "|rt" : "") + (si.gameObject.activeInHierarchy ? "" : "|inactive"));
            return list;
        }

        static string Diff(List<string> sender, List<string> receiver)
        {
            var counts = new Dictionary<string, int>();
            foreach (var s in sender) { int c; counts.TryGetValue(s, out c); counts[s] = c + 1; }
            foreach (var s in receiver) { int c; counts.TryGetValue(s, out c); counts[s] = c - 1; }
            var sb = new StringBuilder();
            foreach (var kv in counts) if (kv.Value != 0) sb.Append(kv.Value > 0 ? " only-on-sender:" : " only-on-receiver:").Append(kv.Key).Append(" x").Append(Math.Abs(kv.Value)).Append(';');
            return sb.Length == 0 ? "same parts" : sb.ToString();
        }

        static string Facts(PlantRoot r)
        {
            int branches = 0, elements = 0, stems = 0; float len = 0f;
            foreach (var b in r.m_Branches) { if (b == null) continue; branches++; elements += b.m_Elements.Count; stems += b.m_Stem.Count; len += b.m_Length; }
            return "branches=" + branches + " elements=" + elements + " stems=" + stems + " length=" + len.ToString("F1") + " objects=" + r.GetComponentsInChildren<SaveInfo>(true).Length + " islands=" + r.m_connectedIslands + "/" + r.m_requiredIslands;
        }

        // ---- checks that need no world --------------------------------------------------------------------------------------------

        void CheckCompressor()
        {
            var rnd = new System.Random(12345);
            var names = new List<string>(); var data = new List<byte[]>();
            names.Add("empty"); data.Add(new byte[0]);
            names.Add("1 byte"); data.Add(new byte[] { 7 });
            names.Add("3 bytes"); data.Add(new byte[] { 1, 2, 3 });
            names.Add("100 KB of zeros"); data.Add(new byte[100000]);
            var noise = new byte[50000]; rnd.NextBytes(noise);
            names.Add("50 KB of noise"); data.Add(noise);
            var pattern = new byte[70000]; for (int i = 0; i < pattern.Length; i++) pattern[i] = (byte)((i * 7) % 61);
            names.Add("repeating pattern"); data.Add(pattern);
            var floats = new byte[40000]; for (int i = 0; i < floats.Length / 4; i++) Buffer.BlockCopy(BitConverter.GetBytes((float)(Math.Sin(i * 0.01) * 50.0)), 0, floats, i * 4, 4);
            names.Add("float series"); data.Add(floats);
            for (int c = 0; c < names.Count; c++)
            {
                try
                {
                    byte[] packed = Lz.Pack(data[c]);
                    byte[] back = Lz.Unpack(packed, data[c].Length);
                    Check("LZ-ROUNDTRIP", "compressor: " + names[c], Same(back, data[c]), data[c].Length + " -> " + packed.Length + " bytes");
                }
                catch (Exception e) { Check("LZ-ROUNDTRIP", "compressor: " + names[c], false, Describe(e)); }
            }

            // hostile input: only ordinary errors, never a crash, and never a giant allocation
            int survived = 0, crashed = 0;
            for (int t = 0; t < 400; t++)
            {
                var junk = new byte[rnd.Next(0, 400)]; rnd.NextBytes(junk);
                try { Lz.Unpack(junk, rnd.Next(0, 5000)); survived++; }
                catch (FormatException) { survived++; }
                catch (IndexOutOfRangeException) { survived++; }
                catch (ArgumentException) { survived++; }
                catch (Exception) { crashed++; }
            }
            Check("LZ-HOSTILE", "compressor: 400 garbage inputs only give ordinary errors", crashed == 0, survived + " ok, " + crashed + " unexpected");
            foreach (int bogus in new[] { int.MaxValue, 2000000000, -1, 100 * 1024 * 1024 })
            {
                bool refused = false; var clock = Stopwatch.StartNew();
                try { Lz.Unpack(new byte[] { 0x10, 1 }, bogus); }
                catch (FormatException) { refused = true; }
                catch (Exception) { }
                Check("LZ-HOSTILE", "compressor: refuses a claimed size of " + bogus, refused && clock.ElapsedMilliseconds < 200, clock.ElapsedMilliseconds + " ms");
            }
            var bomb = new byte[2000]; for (int i = 0; i < bomb.Length; i++) bomb[i] = 255; bomb[0] = 0xFF;   // endless "more literals" counts
            bool bombOk = false;
            try { Lz.Unpack(bomb, 1000); } catch (FormatException) { bombOk = true; } catch (Exception) { }
            Check("LZ-HOSTILE", "compressor: a length-count bomb is refused", bombOk, "");
        }


        // The launcher downloads updates in the background and leaves a note; the game must show it once and only for a sane note.
        void CheckUpdateNotice()
        {
            string f = Here("coop-update-ready.txt");
            Check("UPDATE-NOTICE", "no note means no message", CoopBehaviour.TakeUpdateNotice(f) == null, "");
            File.WriteAllText(f, "7");
            string msg = CoopBehaviour.TakeUpdateNotice(f);
            Check("UPDATE-NOTICE", "a note produces the message once and is removed", msg != null && msg.Contains("7") && !File.Exists(f), msg);
            Check("UPDATE-NOTICE", "the same note is not shown twice", CoopBehaviour.TakeUpdateNotice(f) == null, "");
            foreach (string junk in new[] { "abc", "-3", "99999999999", "" })
            {
                File.WriteAllText(f, junk);
                Check("UPDATE-NOTICE", "a damaged note (\"" + junk + "\") shows nothing and is removed", CoopBehaviour.TakeUpdateNotice(f) == null && !File.Exists(f), "");
            }
        }


        // Everything another player's PC can send is checked: pose packets, visibility messages, and the log cannot be flooded.
        void CheckNetSanity()
        {
            var good = new PlayerState { seq = 5, pos = new Vector3(10, 4, -20), vel = new Vector3(1, 0, 2), camForward = Vector3.forward, move = new Vector2(0.5f, 1f), skin = 3, jumpScale = 0.5f };
            byte[] ok = good.Write();
            var back = PlayerState.Read(ok);
            Check("NET-SANITY", "a normal pose packet is read back exactly", back != null && back.seq == 5 && (back.pos - good.pos).magnitude < 0.001f && back.skin == 3, "");
            Func<Action<PlayerState>, byte[]> make = tweak => { var s = new PlayerState { seq = 6, pos = good.pos, vel = good.vel, camForward = good.camForward, move = good.move, jumpScale = 0.1f }; tweak(s); return s.Write(); };
            var hostile = new Dictionary<string, byte[]>();
            hostile["NaN position"] = make(s => s.pos = new Vector3(float.NaN, 0, 0));
            hostile["infinite position"] = make(s => s.pos = new Vector3(0, float.PositiveInfinity, 0));
            hostile["position a million metres away"] = make(s => s.pos = new Vector3(1e6f, 0, 0));
            hostile["NaN speed"] = make(s => s.vel = new Vector3(0, float.NaN, 0));
            hostile["speed of 100000 m/s"] = make(s => s.vel = new Vector3(100000f, 0, 0));
            hostile["NaN camera"] = make(s => s.camForward = new Vector3(float.NaN, 0, 0));
            hostile["NaN stick"] = make(s => s.move = new Vector2(float.NaN, 0));
            hostile["huge jump strength"] = make(s => s.jumpScale = 1e9f);
            hostile["held object at NaN"] = make(s => s.held = new[] { new PlayerState.Held { id = 1, pos = new Vector3(float.NaN, 0, 0), rot = Quaternion.identity } });
            bool allRefused = true; string leaked = "";
            foreach (var kv in hostile) { if (PlayerState.Read(kv.Value) != null) { allRefused = false; leaked += kv.Key + "; "; } }
            Check("NET-SANITY", "pose packets with impossible numbers are thrown away", allRefused, leaked.Length == 0 ? hostile.Count + " kinds tried" : "accepted: " + leaked);

            int escaped = 0, accepted = 0; var rnd = new System.Random(4242);
            for (int cut = 0; cut < ok.Length; cut++) { var c = new byte[cut]; Buffer.BlockCopy(ok, 0, c, 0, cut); try { if (PlayerState.Read(c) != null) accepted++; } catch (Exception) { escaped++; } }
            for (int t = 0; t < 2000; t++) { var junk = new byte[rnd.Next(0, 400)]; rnd.NextBytes(junk); if (junk.Length > 0 && t % 2 == 0) junk[0] = 0xC0; try { var s = PlayerState.Read(junk); if (s != null && !(Math.Abs(s.pos.x) <= 50000f && Math.Abs(s.vel.x) <= 400f)) escaped++; } catch (Exception) { escaped++; } }
            Check("NET-SANITY", "cut-off and random pose packets never throw and never give an impossible position", escaped == 0 && accepted == 0, escaped + " problems; " + accepted + " cut-off packets accepted");
            bool bigRefused = false; try { bigRefused = PlayerState.Read(new byte[100000]) == null && PlayerState.Read(null) == null; } catch (Exception) { }
            Check("NET-SANITY", "an oversized or missing pose packet is refused", bigRefused, "");

            // visibility messages
            var ms = new MemoryStream(); var w = new BinaryWriter(ms); w.Write(MessageGate.MsgVisibility); w.Write((ushort)3); for (int i = 0; i < 3; i++) { w.Write(i * 11); w.Write(i % 2 == 0); }
            var vis = CoopBehaviour.ParseVisibility(ms.ToArray());
            Check("NET-SANITY", "a normal visibility message is read", vis != null && vis.Count == 3 && vis[0] && !vis[11], vis == null ? "null" : vis.Count + " entries");
            var liar = new MemoryStream(); var lw = new BinaryWriter(liar); lw.Write(MessageGate.MsgVisibility); lw.Write((ushort)60000); lw.Write(1); lw.Write(true);
            var huge = new MemoryStream(); var hw = new BinaryWriter(huge); hw.Write(MessageGate.MsgVisibility); hw.Write((ushort)4001); hw.Write(new byte[4001 * 5]);
            int visBad = 0; var junkVis = new List<byte[]> { liar.ToArray(), huge.ToArray(), new byte[0], new byte[] { 3 }, new byte[] { 3, 0 }, new byte[50000] };
            foreach (var j in junkVis) { try { if (CoopBehaviour.ParseVisibility(j) != null) visBad++; } catch (Exception) { visBad += 100; } }
            for (int t = 0; t < 1000; t++) { var junk = new byte[rnd.Next(0, 200)]; rnd.NextBytes(junk); if (junk.Length > 0) junk[0] = 3; try { CoopBehaviour.ParseVisibility(junk); } catch (Exception) { visBad += 100; } }
            Check("NET-SANITY", "visibility messages with a lying count, too short or too big are refused without throwing", visBad == 0, visBad + " problems");

            // the log cannot be flooded
            string log = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "coopmod-steam.log");
            long before = File.Exists(log) ? new FileInfo(log).Length : 0;
            for (int i = 0; i < 5000; i++) CoopBehaviour.Log("selftest: flood probe " + i);
            long grew = (File.Exists(log) ? new FileInfo(log).Length : 0) - before;
            Check("NET-SANITY", "a flood of messages cannot flood the log (at most 300 lines a second)", grew < 300L * 80, (grew / 1024) + " KB written for 5000 attempts");
        }


        // Players are shown by their Steam names, cleaned so that nothing odd can reach the screen.
        void CheckNames()
        {
            Check("PLAYER-NAMES", "a normal name stays as it is", PlayerNames.Clean("TrickerSonic14", "Player 1") == "TrickerSonic14", "");
            Check("PLAYER-NAMES", "emoji and other alphabets (the game's font cannot draw them) are dropped", PlayerNames.Clean("Ana \u2764 \u0645\u062D\u0645\u062F Lee", "Player 1") == "Ana Lee", PlayerNames.Clean("Ana \u2764 \u0645\u062D\u0645\u062F Lee", "Player 1"));
            Check("PLAYER-NAMES", "a name with nothing drawable falls back to Player N", PlayerNames.Clean("\u0645\u062D\u0645\u062F", "Player 4") == "Player 4" && PlayerNames.Clean("   ", "Player 2") == "Player 2" && PlayerNames.Clean("", "Player 2") == "Player 2" && PlayerNames.Clean(null, "Player 2") == "Player 2" && PlayerNames.Clean("[unknown]", "Player 5") == "Player 5", "");
            string markup = PlayerNames.Clean("<color=red>EVIL</color>\n|x", "Player 1");
            Check("PLAYER-NAMES", "markup and control characters cannot get through", !markup.Contains("<") && !markup.Contains(">") && !markup.Contains("\\") && !markup.Contains("|") && PlayerNames.Clean("a\nb\tc\u0000d\u202Eevil", "P") == "ab cdevil", markup);
            string longName = PlayerNames.Clean(new string('W', 200), "Player 1");
            Check("PLAYER-NAMES", "a very long name is cut to 16 letters", longName.Length == PlayerNames.MaxLength, longName.Length + " letters");
            Check("PLAYER-NAMES", "extra spaces are squeezed out", PlayerNames.Clean("  Big    Bird  ", "P") == "Big Bird", "");
            var dup = new[] { "Sam", "Max", "sam", "Sam", "Sam 2" };
            PlayerNames.MakeUnique(dup);
            bool distinct = new HashSet<string>(dup, StringComparer.OrdinalIgnoreCase).Count == dup.Length;
            Check("PLAYER-NAMES", "two players with the same name are told apart, the first one keeps its name", distinct && dup[0] == "Sam" && dup[1] == "Max", string.Join(" / ", dup));
            var longDup = new[] { new string('A', 16), new string('A', 16) };
            PlayerNames.MakeUnique(longDup);
            Check("PLAYER-NAMES", "numbering a duplicate never makes the name longer than 16", longDup[1].Length <= PlayerNames.MaxLength && longDup[0] != longDup[1], longDup[1]);
            var named = new Peer { Id = 1, Number = 2, Name = "Ana" }; var unnamed = new Peer { Id = 2, Number = 3 };
            Check("PLAYER-NAMES", "a player is labelled with their name, or Player N until Steam has told us", named.Label == "Ana" && unnamed.Label == "Player 3", named.Label + " / " + unnamed.Label);
        }

        // With Steam running, my own name must come through the real code and be shown.
        void CheckNamesLive()
        {
            var cb = UnityEngine.Object.FindObjectOfType(typeof(CoopBehaviour)) as CoopBehaviour;
            if (cb == null) { Check("PLAYER-NAMES", "the mod is running", false, ""); return; }
            cb.RefreshNamesNow();
            string steamName = Steamworks.SteamFriends.GetPersonaName();
            string expected = PlayerNames.Clean(steamName, "");
            Check("PLAYER-NAMES", "my own Steam name is read and shown", !string.IsNullOrEmpty(cb.MyDisplayName) && (expected.Length == 0 || cb.MyDisplayName == expected), "steam \"" + steamName + "\" -> shown \"" + cb.MyDisplayName + "\"");
        }


        // In-game chat: what can be said, what arrives, who it is shown as, and that nobody can flood or spoof it.
        void CheckChat()
        {
            Check("CHAT", "a normal message is cleaned to itself", Chat.Clean("hello there!") == "hello there!", "");
            string nasty = Chat.Clean("<color=red>hi</color> \n \u2764 \u0645\u062D\u0645\u062F  ok\u0000\t\t!");
            Check("CHAT", "markup, emoji, other alphabets and control characters are removed", !nasty.Contains("<") && !nasty.Contains(">") && !nasty.Contains("\\") && nasty == "color=redhi/color ok !", nasty);
            Check("CHAT", "a long message is cut to 100 characters", Chat.Clean(new string('x', 1000)).Length == Chat.MaxText, "");
            Check("CHAT", "an empty or unprintable message becomes nothing", Chat.Clean("") == "" && Chat.Clean(null) == "" && Chat.Clean("\u2764\u2764") == "", "");

            byte[] enc = Chat.Encode("see you on the island");
            string back;
            Check("CHAT", "a message survives sending", Chat.TryDecode(enc, out back) && back == "see you on the island" && enc[0] == Chat.Msg, back);
            var bad = new List<byte[]>();
            bad.Add(null); bad.Add(new byte[0]); bad.Add(new byte[] { Chat.Msg }); bad.Add(new byte[] { Chat.Msg, 0 });
            bad.Add(new byte[] { Chat.Msg, 5, 65, 66 });                        // says 5 letters, has 2
            bad.Add(new byte[] { Chat.Msg, 2, 65, 66, 67 });                    // says 2 letters, has 3
            bad.Add(new byte[] { 9, 2, 65, 66 });                                // not a chat message
            var tooLong = new byte[2 + 101]; tooLong[0] = Chat.Msg; tooLong[1] = 101; bad.Add(tooLong);
            var big = new byte[100000]; big[0] = Chat.Msg; big[1] = 255; bad.Add(big);
            bad.Add(new byte[] { Chat.Msg, 3, 0, 1, 2 });                        // only control characters
            int accepted = 0; var rnd = new System.Random(99);
            foreach (var b in bad) { string t; try { if (Chat.TryDecode(b, out t)) accepted++; } catch (Exception) { accepted += 100; } }
            for (int i = 0; i < 3000; i++) { var junk = new byte[rnd.Next(0, 160)]; rnd.NextBytes(junk); if (junk.Length > 0) junk[0] = Chat.Msg; string t; try { if (Chat.TryDecode(junk, out t) && (t.Length > Chat.MaxText || t.Contains("<"))) accepted += 100; } catch (Exception) { accepted += 100; } }
            Check("CHAT", "damaged, lying, too long and random chat messages are refused without throwing", accepted == 0, accepted + " problems");

            // the same player cannot flood the chat
            bool five = true; for (int i = 0; i < 5; i++) five &= Chat.Allow(111UL, 100f + i * 0.1f);
            bool sixthRefused = !Chat.Allow(111UL, 100.6f);
            bool otherPlayerFine = Chat.Allow(222UL, 100.6f);
            bool laterFine = Chat.Allow(111UL, 108f);
            Check("CHAT", "one player is limited to 5 messages in 6 seconds, others are not affected, and it recovers", five && sixthRefused && otherPlayerFine && laterFine, "five=" + five + " sixth refused=" + sixthRefused + " other=" + otherPlayerFine + " later=" + laterFine);

            Check("CHAT", "the network gate sends chat messages to the chat, and refuses oversized ones", MessageGate.Classify(enc, false) == Route.Chat && MessageGate.Classify(big, true) == Route.Oversized, MessageGate.Classify(enc, false).ToString());
        }

        // Rooms: a public room lets anybody in, a private one only the players the host accepted; nobody the host refused; hostile room data changes nothing.
        void CheckRooms()
        {
            const ulong host = 100, guest = 200, other = 300;
            bool open = RoomRules.Allowed("open", host, guest, "", "") && RoomRules.Allowed("open", host, host, "", "");
            bool priv = !RoomRules.Allowed("private", host, guest, "", "") && RoomRules.Allowed("private", host, host, "", "")
                && RoomRules.Allowed("private", host, guest, RoomRules.AddId("", guest), "") && !RoomRules.Allowed("private", host, other, RoomRules.AddId("", guest), "");
            bool refused = !RoomRules.Allowed("open", host, guest, "200", "200") && !RoomRules.Allowed("private", host, guest, "200", "200");
            bool unknownMode = !RoomRules.Allowed("", host, guest, "", "") && !RoomRules.Allowed("OPEN", host, guest, "", "") && !RoomRules.Allowed(null, host, guest, "", "");
            Check("ROOMS", "a public room lets everybody in; a private room only the players the host accepted; a refused player never; an unknown or missing mode counts as private", open && priv && refused && unknownMode, "open=" + open + " private=" + priv + " refused=" + refused + " unknownMode=" + unknownMode);
            float oldScale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;
                bool still = CoopUI.GlitchFor(Time.realtimeSinceStartup - 1f) == 0f && CoopUI.GlitchFor(Time.realtimeSinceStartup + 0.01f) > 0.9f;
                Check("MOUSE-FREE", "a window that opened a moment ago stands perfectly still (no flicker), even while game time is stopped", still, "");
            }
            finally { Time.timeScale = oldScale; }
            Check("ROOMS", "if Steam hands the room to another member the room is closed for me (a new owner is never trusted as host)", RoomRules.HostChanged(100, 300) && !RoomRules.HostChanged(100, 100) && !RoomRules.HostChanged(0, 300), "");
            string junk = new string('9', 5000);
            bool hostile = RoomRules.ParseIds(junk).Count == 0 && RoomRules.ParseIds("abc,,-1,0,99999999999999999999999,5").Count == 1 && !RoomRules.Allowed("private", host, 0, "0", "") && RoomRules.ParseIds(null).Count == 0;
            string many = ""; for (ulong i = 1; i <= 40; i++) many = RoomRules.AddId(many, i);
            bool capped = RoomRules.ParseIds(many).Count == RoomRules.MaxIds && RoomRules.AddId("5,5,5", 5) == "5";
            Check("ROOMS", "broken or oversized room data is read safely: junk ignored, lists capped, no duplicates, id 0 never allowed", hostile && capped, "hostile=" + hostile + " capped=" + capped);
            Check("ROOMS", "room names from strangers are cleaned like player names", RoomRules.RoomName("<b>Eve</b>\u2764 room") == "bEvebroom" || RoomRules.RoomName("<b>Eve</b>").IndexOf('<') < 0 && RoomRules.RoomName("").Length > 0, RoomRules.RoomName("<b>Eve</b>\u2764 room"));
        }

        // The mouse must work in the code box and room menus: the game hides and locks it every frame unless the mod frees it.
        void CheckCursorLive()
        {
            var cb = UnityEngine.Object.FindObjectOfType(typeof(CoopBehaviour)) as CoopBehaviour;
            if (cb == null) { Check("MOUSE-FREE", "the mod is running", false, ""); return; }
            // a player who closed the game must not stay standing in the world
            bool timing = !CoopBehaviour.SignalLost(true, 105f, 100f) && CoopBehaviour.SignalLost(true, 107f, 100f) && !CoopBehaviour.SignalLost(false, 500f, 0f);
            var ghost = new Peer { Id = 5555UL, Number = 2 };
            ghost.Clone = new GameObject("ghost-test");
            cb.DropGhostForTest(ghost);
            Check("GHOST-LEFT", "a player who stops sending (closed the game) is taken out of my world after 6 seconds, and is not removed earlier", timing && ghost.Clone == null && ghost.Driver == null, "timing=" + timing + " cloneGone=" + (ghost.Clone == null));
            bool oldLock = Screen.lockCursor, oldShow = Screen.showCursor;
            try
            {
                cb.OpenJoinForTest();
                Screen.lockCursor = true; Screen.showCursor = false;
                cb.ApplyCursorForTest();
                bool freed = cb.WantMouseForTest && !Screen.lockCursor && Screen.showCursor;
                var esType = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
                var es = esType == null ? null : UnityEngine.Object.FindObjectOfType(esType) as Behaviour;
                bool menusFrozen = es == null || (cb.MenusFrozenForTest && !es.enabled);
                cb.CloseJoinForTest();
                cb.ApplyCursorForTest();
                bool released = !cb.WantMouseForTest && !cb.MenusFrozenForTest && (es == null || es.enabled);
                Check("MOUSE-FREE", "while my window is open the game's own menus (Continue, New Game...) get no clicks or keys, and work again when it closes" + (es == null ? " (no menu event system in this scene)" : ""), menusFrozen && released, "frozen=" + menusFrozen + " released=" + released);
                Check("MOUSE-FREE", "while the code box is open the mouse is shown and unlocked (so JOIN and CANCEL can be clicked), and the game gets it back when it closes", freed && !cb.WantMouseForTest, "freed=" + freed);
            }
            finally { Screen.lockCursor = oldLock; Screen.showCursor = oldShow; cb.CloseJoinForTest(); }
        }

        // The real mod: a message from a player's connection is shown under that connection's name; mine is sent and shown.
        void CheckChatLive()
        {
            var cb = UnityEngine.Object.FindObjectOfType(typeof(CoopBehaviour)) as CoopBehaviour;
            if (cb == null) { Check("CHAT", "the mod is running", false, ""); return; }
            int before = Chat.Lines.Count;
            bool shown = cb.HandleChat(333UL, "Ana", Chat.Encode("hello from Ana"));
            var last = Chat.Lines.Count > before ? Chat.Lines[Chat.Lines.Count - 1] : null;
            Check("CHAT", "a friend's message is shown under the name of their connection", shown && last != null && last.Name == "Ana" && last.Text == "hello from Ana" && !last.Mine, last == null ? "nothing shown" : last.Name + ": " + last.Text);
            int flooded = 0; for (int i = 0; i < 20; i++) if (cb.HandleChat(444UL, "Spammer", Chat.Encode("buy now " + i))) flooded++;
            Check("CHAT", "a flood from one player shows only the first few messages", flooded == 5, flooded + " of 20 shown");
            before = Chat.Lines.Count; sink.Messages.Clear();
            cb.SendChat("hi everybody");
            cb.SendChat("   ");
            cb.SendChat("\u2764");
            string sent = null; foreach (var m in sink.Messages) if (m.Length > 0 && m[0] == Chat.Msg) { string t; if (Chat.TryDecode(m, out t)) sent = t; }
            var mine = Chat.Lines.Count > before ? Chat.Lines[Chat.Lines.Count - 1] : null;
            Check("CHAT", "my message is sent once and shown as mine; empty ones are not sent", sent == "hi everybody" && CountType(Chat.Msg) == 1 && mine != null && mine.Mine && Chat.Lines.Count == before + 1, "sent=\"" + sent + "\" messages=" + CountType(Chat.Msg) + " lines +" + (Chat.Lines.Count - before));

            // the box must always be closable: Enter sends and closes, Esc closes without sending, and the key that opened it does not close it again
            sink.Messages.Clear();
            cb.OpenChatForTest("typed in the box");
            bool opened = cb.ChatOpen;
            cb.HandleChatKeys(true, false);
            Check("CHAT", "Enter sends what was typed and closes the box (the player can move again)", opened && !cb.ChatOpen && CountType(Chat.Msg) == 1, "opened=" + opened + ", still open=" + cb.ChatOpen + ", sent=" + CountType(Chat.Msg));

            // the real key events a text box gets: every kind of Enter sends, Esc closes, nothing else does
            Func<EventType, KeyCode, char, EventModifiers, Event> ke = (type, code, ch, mods) => { var e = new Event(); e.type = type; e.keyCode = code; e.character = ch; e.modifiers = mods; return e; };
            bool enters = CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.Return, '\n', EventModifiers.None)) == 1
                && CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.Return, '\0', EventModifiers.None)) == 1
                && CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.KeypadEnter, '\n', EventModifiers.None)) == 1
                && CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.None, '\n', EventModifiers.None)) == 1
                && CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.None, '\r', EventModifiers.None)) == 1
                && CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.Return, '\n', EventModifiers.Shift)) == 1;
            Check("CHAT", "every kind of Enter (also Shift+Enter, number pad, character only) sends instead of making a new line", enters, "");
            bool others = CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.Escape, '\0', EventModifiers.None)) == 2
                && CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.A, 'a', EventModifiers.None)) == 0
                && CoopUI.ChatKeyAction(ke(EventType.KeyDown, KeyCode.Space, ' ', EventModifiers.None)) == 0
                && CoopUI.ChatKeyAction(ke(EventType.KeyUp, KeyCode.Return, '\n', EventModifiers.None)) == 0
                && CoopUI.ChatKeyAction(ke(EventType.Repaint, KeyCode.Return, '\n', EventModifiers.None)) == 0
                && CoopUI.ChatKeyAction(null) == 0;
            Check("CHAT", "Esc closes; letters, space, key releases and repaints do nothing", others, "");
            sink.Messages.Clear();
            cb.OpenChatForTest("not for sending");
            cb.HandleChatKeys(false, true);
            Check("CHAT", "Esc closes the box and sends nothing", !cb.ChatOpen && CountType(Chat.Msg) == 0, "still open=" + cb.ChatOpen + ", sent=" + CountType(Chat.Msg));
            cb.PressTForTest();
            cb.HandleChatKeys(true, false);
            bool stillOpenAfterOpeningKey = cb.ChatOpen;
            cb.HandleChatKeys(false, true);
            Check("CHAT", "the very frame the box opens, Enter does not close it by accident, and it can still be closed afterwards", stillOpenAfterOpeningKey, "open right after opening=" + stillOpenAfterOpeningKey);
            bool closedFinally = !cb.ChatOpen;
            if (!closedFinally) { cb.OpenChatForTest(""); cb.HandleChatKeys(false, true); }
            Check("CHAT", "after all of that the chat is closed and the keyboard belongs to the game again", !cb.ChatOpen, "open=" + cb.ChatOpen);
        }

        void CheckGate()
        {
            var big = new byte[MessageGate.MaxMessage + 1];
            Route r;
            r = MessageGate.Classify(null, false); Check("GATE-ROUTING", "gate: nothing is ignored", r == Route.Ignore, r.ToString());
            r = MessageGate.Classify(new byte[0], true); Check("GATE-ROUTING", "gate: empty is ignored", r == Route.Ignore, r.ToString());
            r = MessageGate.Classify(new byte[] { MessageGate.MsgVisibility, 0, 0 }, false); Check("GATE-ROUTING", "gate: visibility goes to visibility", r == Route.Visibility, r.ToString());
            foreach (byte t in new[] { TypePlant, TypeShard, TypeIsland })
            {
                r = MessageGate.Classify(new byte[] { t, 1, 2, 3 }, false); Check("GATE-ROUTING", "gate: event " + t + " from any player is accepted", r == Route.Shared, r.ToString());
                var bigEvent = new byte[MessageGate.MaxMessage + 1]; bigEvent[0] = t;
                r = MessageGate.Classify(bigEvent, true); Check("GATE-ROUTING", "gate: oversized event " + t + " is refused", r == Route.Oversized, r.ToString());
            }
            r = MessageGate.Classify(new byte[] { 2, 1, 2, 3 }, false); Check("GATE-ROUTING", "gate: a world snapshot from a non-host is refused", r == Route.SnapshotNotFromHost, r.ToString());
            r = MessageGate.Classify(new byte[] { 2, 1, 2, 3 }, true); Check("GATE-ROUTING", "gate: a world snapshot from the host is accepted", r == Route.World, r.ToString());
            r = MessageGate.Classify(new byte[] { 1 }, false); Check("GATE-ROUTING", "gate: a world request is accepted from anybody", r == Route.World, r.ToString());
            big[0] = 2;
            r = MessageGate.Classify(big, true); Check("GATE-ROUTING", "gate: an oversized snapshot is refused even from the host", r == Route.Oversized, r.ToString());
        }

        // ---- save safety -------------------------------------------------------------------------------------------------------------

        void CheckSavePaths()
        {
            string root = Here("coop-saves");
            Check("SAVE-PATHS", "player save path is inside coop-saves", Under(SaveSystem.m_playerSavePath, root), SaveSystem.m_playerSavePath);
            Check("SAVE-PATHS", "level save path is inside coop-saves", Under(SaveSystem.m_levelFilesPath, root), SaveSystem.m_levelFilesPath);
            Check("SAVE-PATHS", "not in guest mode when playing my own world", !WorldSync.GuestMode, "guest=" + WorldSync.GuestMode);
            try
            {
                var t = Type.GetType("SteamManager, Assembly-CSharp");
                var sm = t != null ? UnityEngine.Object.FindObjectOfType(t) as Component : null;
                bool a = (bool)t.GetField("m_cloudSavable").GetValue(sm), b = (bool)t.GetField("m_cloudSaveEnabled").GetValue(sm);
                Check("SAVE-PATHS", "Steam Cloud saving is switched off", !a && !b, "cloudSavable=" + a + " cloudSaveEnabled=" + b);
            }
            catch (Exception e) { Check("SAVE-PATHS", "Steam Cloud saving is switched off", false, Describe(e)); }
        }

        // Last test (guest mode cannot be left): in guest mode every save goes to a throwaway folder and my own save files stay byte-identical.
        void CheckGuestIsolation()
        {
            string root = Path.GetFullPath(SaveGuard.Root);
            string guest = Path.Combine(root, "guest-session");
            var before = Snapshot(root, guest);
            Check("GUEST-ISOLATION", "my own save exists to protect", before.Count > 0, before.Count + " files");
            if (!Try("GUEST-ISOLATION", "enter guest mode", delegate { WorldSync.EnterGuestMode(); })) return;
            Check("GUEST-ISOLATION", "guest mode sends saves to guest-session", WorldSync.GuestMode && Under(SaveSystem.m_playerSavePath, guest) && Under(SaveSystem.m_levelFilesPath, guest), SaveSystem.m_playerSavePath + " | " + SaveSystem.m_levelFilesPath);
            Try("GUEST-ISOLATION", "force the game to save everything", delegate { SaveSystem.SaveLevel(); SaveSystem.SavePlayerProfile(); });
            int written = Directory.Exists(guest) ? Directory.GetFiles(guest, "*", SearchOption.AllDirectories).Length : 0;
            var after = Snapshot(root, guest);
            Check("GUEST-ISOLATION", "saving in guest mode really wrote somewhere else", written >= 2, written + " files in guest-session");
            Check("GUEST-ISOLATION", "my own save files are byte-identical after guest saves", SameFiles(before, after), before.Count + " files before, " + after.Count + " after");
        }

        // ---- the scenario ------------------------------------------------------------------------------------------------------------

        IEnumerator Run()
        {
            Log("started");
            try { quick = File.ReadAllText(Here("coop-selftest.txt")).Contains("quick"); } catch (Exception) { }
            try { CheckCompressor(); } catch (Exception e) { Check("AUDIT-ERROR", "compressor checks ran", false, Describe(e)); }
            try { CheckGate(); } catch (Exception e) { Check("AUDIT-ERROR", "gate checks ran", false, Describe(e)); }
            try { CheckUpdateNotice(); } catch (Exception e) { Check("AUDIT-ERROR", "update notice checks ran", false, Describe(e)); }
            try { CheckNetSanity(); } catch (Exception e) { Check("AUDIT-ERROR", "network sanity checks ran", false, Describe(e)); }
            try { CheckNames(); } catch (Exception e) { Check("AUDIT-ERROR", "name checks ran", false, Describe(e)); }
            try { CheckChat(); } catch (Exception e) { Check("AUDIT-ERROR", "chat checks ran", false, Describe(e)); }
            try { CheckRooms(); } catch (Exception e) { Check("AUDIT-ERROR", "room checks ran", false, Describe(e)); }

            float begin = Time.realtimeSinceStartup;
            bool pressed = false; float menuAt = -1f;
            while (!WorldSync.InWorld())
            {
                if (!pressed && StartMenuReady())
                {
                    if (menuAt < 0f)
                    {
                        menuAt = Time.realtimeSinceStartup;
                        Check("STARTUP-FAST", "the start menu is reached quickly because the intro screens are skipped", menuAt < 15f, menuAt.ToString("F1") + " s after the game started (limit 15 s; with the intro screens it takes more than 15 s)");
                    }
                    if (Time.realtimeSinceStartup - menuAt > 2f)
                    {
                        pressed = true;
                        bool fresh = !SaveSystem.LevelSaveExists(Application.loadedLevelName);
                        Log("start menu is up; pressing " + (fresh ? "NEW GAME" : "CONTINUE"));
                        StartScreen.StartGameCountdown(fresh);
                    }
                }
                if (Time.realtimeSinceStartup - begin > 300f) { Check("AUDIT-ERROR", "reach the world", false, "never got into the world within 5 minutes"); Finish(); yield break; }
                yield return null;
            }
            Log("in the world after " + (Time.realtimeSinceStartup - begin).ToString("F0") + " s; letting things settle");
            yield return new WaitForSeconds(20f);

            if (SharedWorld.MyId == 0UL) SharedWorld.MyId = 1UL;
            SharedWorld.TestSink = sink;
            try { CheckSavePaths(); } catch (Exception e) { Check("AUDIT-ERROR", "save path checks ran", false, Describe(e)); }
            try { CheckNamesLive(); } catch (Exception e) { Check("AUDIT-ERROR", "live name check ran", false, Describe(e)); }
            try { CheckChatLive(); } catch (Exception e) { Check("AUDIT-ERROR", "live chat check ran", false, Describe(e)); }
            try { CheckCursorLive(); } catch (Exception e) { Check("AUDIT-ERROR", "live mouse check ran", false, Describe(e)); }

            var roots = new List<PlantRoot>();
            foreach (PlantRoot r in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot))) if (SharedWorld.PlantId(r) != null) roots.Add(r);
            roots.Sort((a, b) => (b.IsTrunk() ? 1 : 0).CompareTo(a.IsTrunk() ? 1 : 0));   // the big trunk first
            Note("found " + roots.Count + " plants");
            foreach (var r in roots) Note("  plant " + SharedWorld.PlantId(r) + (r.IsTrunk() ? " (trunk, " + (r.TrunkGrowthFraction * 100f).ToString("F1") + "% grown)" : "") + " at " + r.transform.position + ": " + Facts(r));

            int tested = 0, others = 0;
            foreach (var r in roots)
            {
                if (r == null) continue;
                if (!r.IsTrunk() && ++others > 2) break;
                yield return StartCoroutine(TestPlant(r, r.IsTrunk() ? "trunk" : "plant " + others, quick ? 8 : (r.IsTrunk() ? 25 : 10), !faultTested));
                tested++;
                if (r != null && r.IsTrunk() && tested == 1) yield return StartCoroutine(TestIsland(r));
                if (quick) break;
            }
            Check("PLANT-SEND", "at least one plant was tested", tested > 0, tested + " plants");

            yield return StartCoroutine(TestCrystals());
            yield return StartCoroutine(TestTeleporters());
            yield return StartCoroutine(TestFuzz(roots));
            try { CheckGuestIsolation(); } catch (Exception e) { Check("AUDIT-ERROR", "guest isolation checks ran", false, Describe(e)); }
            Finish();
        }

        bool faultTested;
        IEnumerator TestPlant(PlantRoot root, string label, int steps, bool injectFault)
        {
            string id = SharedWorld.PlantId(root);
            Note("---- " + label + " " + id);
            byte[] raw0 = null; int sig0 = 0;
            if (!Try("AUDIT-ERROR", label + ": read starting state", delegate { raw0 = SharedWorld.TestRaw(root); sig0 = SharedWorld.PlantSignature(root); })) yield break;
            Note(label + ": starting state " + raw0.Length + " bytes; " + Facts(root));
            int queueBefore = QueuedLoadCallbacks();

            // 1. grow it like a player pulling on it
            sink.Messages.Clear();
            int pulls = 0; float grownFor = 0f;
            for (int i = 0; i < steps; i++)
            {
                bool ok = false;
                if (!Try("AUDIT-ERROR", label + ": pull " + i, delegate { ok = Stimulate(root); })) yield break;
                if (!ok) { Note(label + ": this plant cannot be made to grow here; skipped"); yield break; }
                pulls++;
                yield return new WaitForSeconds(1f); grownFor += 1f;
                if (i == 3 && SharedWorld.PlantSignature(root) == sig0) { Note(label + ": no change after 4 pulls; skipped"); yield break; }
            }

            // 2. wait until it has stopped changing and the final state was sent
            float last = Time.time; int seen = -1; float deadline = Time.time + 40f;
            while (Time.time < deadline)
            {
                int c = MessagesFor(id).Count;
                if (c != seen) { seen = c; last = Time.time; }
                if (Time.time - last > 6f) break;
                yield return new WaitForSeconds(0.5f);
            }
            var mine = MessagesFor(id);
            int sigFinal = SharedWorld.PlantSignature(root);
            string factsFinal = Facts(root);
            var partsFinal = ObjectNames(root);
            byte[] rawFinal = null; long makeMs = 0;
            if (!Try("AUDIT-ERROR", label + ": read final state", delegate { var clock = Stopwatch.StartNew(); rawFinal = SharedWorld.TestRaw(root); Lz.Pack(rawFinal); makeMs = clock.ElapsedMilliseconds; })) yield break;
            Note(label + ": after " + pulls + " pulls (" + grownFor.ToString("F0") + " s): " + factsFinal + ", " + rawFinal.Length + " bytes; sent " + mine.Count + " updates");

            try
            {
                var clock = Stopwatch.StartNew();
                byte[] packed = Lz.Pack(rawFinal);
                long packMs = clock.ElapsedMilliseconds; clock.Reset(); clock.Start();
                byte[] back = Lz.Unpack(packed, rawFinal.Length);
                Check("LZ-ROUNDTRIP", label + ": real plant data survives compression", Same(back, rawFinal), rawFinal.Length + " -> " + packed.Length + " bytes (" + (100 * packed.Length / Math.Max(1, rawFinal.Length)) + "%), pack " + packMs + " ms, unpack " + clock.ElapsedMilliseconds + " ms");
            }
            catch (Exception e) { Check("LZ-ROUNDTRIP", label + ": real plant data survives compression", false, Describe(e)); }

            Check("PLANT-SEND", label + ": a growing plant is sent", mine.Count >= 1, mine.Count + " updates");
            Check("PLANT-SEND", label + ": updates are throttled", mine.Count <= (int)(grownFor / 3f) + 4, mine.Count + " updates in " + grownFor.ToString("F0") + " s of growing (at most one per 3 s plus the final ones)");
            int queueAfter = QueuedLoadCallbacks();
            Check("PLANT-NOLEAK", label + ": sending does not pile up the game's after-load callbacks", queueAfter <= queueBefore + 2, queueBefore + " -> " + queueAfter + " queued after " + mine.Count + " sends");
            if (mine.Count == 0) yield break;
            lastPlantMessage = mine[mine.Count - 1];
            long total = 0; int biggestRaw = 0, biggestComp = 0;
            foreach (var m in mine) { int raw, comp; MsgSizes(m, out raw, out comp); total += m.Length; biggestRaw = Math.Max(biggestRaw, raw); biggestComp = Math.Max(biggestComp, comp); }
            Note(label + ": updates total " + (total / 1024) + " KB; biggest " + biggestRaw + " bytes raw, " + biggestComp + " compressed; making one takes " + makeMs + " ms");
            Check("PLANT-SIZE", label + ": an update stays small and cheap", biggestComp < 100 * 1024 && makeMs < 60, biggestComp + " bytes compressed (limit 100 KB), " + makeMs + " ms to make (limit 60 ms)");

            // 3. a copy of the world that only has the OLD plant receives some of those updates
            var picks = new List<int> { 0 };
            if (mine.Count > 2) picks.Add(mine.Count / 2);
            if (mine.Count > 1) picks.Add(mine.Count - 1);
            foreach (int k in picks)
            {
                bool isLast = k == mine.Count - 1;
                if (!Try("PLANT-CONVERGE", label + ": go back to the old plant", delegate { SharedWorld.TestRevert(id, raw0); })) yield break;
                yield return new WaitForSeconds(0.5f);
                string before = Facts(root);
                int inPlaceBefore = SharedWorld.StatInPlaceFailed, failBefore = SharedWorld.StatApplyFailed, rebuiltBefore = SharedWorld.StatRebuilt;
                byte[] msg = WithSender(mine[k], Friend);
                if (!Try("PLANT-CONVERGE", label + ": apply update " + (k + 1) + "/" + mine.Count, delegate { SharedWorld.TestApply(msg); })) yield break;
                yield return new WaitForSeconds(isLast ? 4f : 1f);
                string after = Facts(root);
                bool inPlaceFailed = SharedWorld.StatInPlaceFailed > inPlaceBefore;
                bool applyFailed = SharedWorld.StatApplyFailed > failBefore;
                Check("PLANT-CONVERGE", label + ": update " + (k + 1) + "/" + mine.Count + " applies", !applyFailed, "old: " + before + " -> new: " + after);
                if (inPlaceFailed && !applyFailed) Warn(label + ": update " + (k + 1) + "/" + mine.Count + " could not be applied in place; the plant was rebuilt instead", "rebuilt " + (SharedWorld.StatRebuilt - rebuiltBefore) + " time(s)");
                if (isLast)
                {
                    int sigNow = SharedWorld.PlantSignature(root);
                    Check("PLANT-CONVERGE", label + ": the old plant became the same as the sender's after the last update", sigNow == sigFinal, "sender: " + factsFinal + " | receiver: " + after + " | " + Diff(partsFinal, ObjectNames(root)));
                    int echoBefore = MessagesFor(id).Count;     // the receiver must not send its newly received plant back
                    yield return new WaitForSeconds(5f);
                    Check("PLANT-OWNER", label + ": the receiver does not send the received plant back", MessagesFor(id).Count == echoBefore, (MessagesFor(id).Count - echoBefore) + " messages");
                }
            }

            // 3b. fault injection: the in-place update fails once; the rebuild must still give the same plant
            if (injectFault)   // the first plant that really grew (a fully grown trunk is skipped above)
            {
                faultTested = true;
                if (!Try("PLANT-REBUILD", label + ": go back to the old plant", delegate { SharedWorld.TestRevert(id, raw0); })) yield break;
                yield return new WaitForSeconds(0.5f);
                int rebuiltBefore = SharedWorld.StatRebuilt, failBefore = SharedWorld.StatApplyFailed;
                SharedWorld.TestFailInPlaceOnce = true;
                byte[] msg = WithSender(mine[mine.Count - 1], Friend);
                Try("PLANT-REBUILD", label + ": apply with the in-place update failing", delegate { SharedWorld.TestApply(msg); });
                SharedWorld.TestFailInPlaceOnce = false;
                yield return new WaitForSeconds(4f);
                int sigNow = SharedWorld.PlantSignature(root);
                Check("PLANT-REBUILD", label + ": the rebuild path was used", SharedWorld.StatRebuilt == rebuiltBefore + 1 && SharedWorld.StatApplyFailed == failBefore, "rebuilt " + (SharedWorld.StatRebuilt - rebuiltBefore) + " time(s), failures " + (SharedWorld.StatApplyFailed - failBefore));
                Check("PLANT-REBUILD", label + ": the rebuilt plant equals the sender's", sigNow == sigFinal, "sender: " + factsFinal + " | receiver: " + Facts(root) + " | " + Diff(partsFinal, ObjectNames(root)));
            }

            // 4. while a friend is growing it I stay quiet; afterwards I may send again
            byte[] friendMsg = WithSender(mine[mine.Count - 1], Friend);
            Try("PLANT-OWNER", label + ": friend update", delegate { SharedWorld.TestApply(friendMsg); });
            int quietBefore = MessagesFor(id).Count;
            for (int i = 0; i < 3; i++)
            {
                Try("PLANT-OWNER", label + ": local pull while friend owns it", delegate { Stimulate(root); });
                yield return new WaitForSeconds(1f);
            }
            yield return new WaitForSeconds(2f);
            Check("PLANT-OWNER", label + ": stays quiet while a friend is growing it", MessagesFor(id).Count == quietBefore, (MessagesFor(id).Count - quietBefore) + " messages");
            yield return new WaitForSeconds(7f);   // friend's ownership runs out
            int resumeBefore = MessagesFor(id).Count;
            Try("PLANT-OWNER", label + ": local pull after hold", delegate { Stimulate(root); });
            yield return new WaitForSeconds(6f);
            Check("PLANT-OWNER", label + ": sends again after the friend is done", MessagesFor(id).Count > resumeBefore, (MessagesFor(id).Count - resumeBefore) + " messages");
        }

        // Connecting a floating island: the attachment, the island counter (the "n/6" icon and growth stage) and the HUD must all follow.
        IEnumerator TestIsland(PlantRoot trunk)
        {
            NutrientProvider np = null;
            foreach (NutrientProvider n in UnityEngine.Object.FindObjectsOfType(typeof(NutrientProvider)))
                if (!n.m_attached && n.GetComponent<SaveInfo>() != null) { np = n; break; }
            PlantBranch br = null;
            foreach (var b in trunk.m_Branches) if (b != null) { br = b; break; }
            if (np == null || br == null || !Singleton<PlayerAnalytics>.Exists) { Check("ISLAND-COUNTER", "an island and a trunk branch exist to test with", false, "island=" + (np != null) + " branch=" + (br != null)); yield break; }
            var pa = Singleton<PlayerAnalytics>.Instance;
            Note("---- island " + np.name);
            int counterBefore = pa.m_nutrientIslandsAttched;
            sink.Messages.Clear();
            if (!Try("ISLAND-COUNTER", "attach a nutrient source the way the game does", delegate { np.Attach(br, np.transform.position, Vector3.up); })) yield break;
            yield return new WaitForSeconds(6f);
            int announced = CountType(TypeIsland), counterAfter = pa.m_nutrientIslandsAttched;
            Check("ISLAND-COUNTER", "connecting an island is announced exactly once", announced == 1 && counterAfter == counterBefore + 1, announced + " island messages, island counter " + counterBefore + " -> " + counterAfter);

            byte[] raw = null;
            if (!Try("ISLAND-ATTACH", "read the trunk state", delegate { raw = SharedWorld.TestRaw(trunk); })) yield break;
            byte[] msg = SharedWorld.TestMessage(trunk, Friend, raw);
            bool wasAttached = np.m_attached;
            np.m_attached = false;     // my copy does not know about the island yet
            if (!Try("ISLAND-ATTACH", "apply the friend's plant update", delegate { SharedWorld.TestApply(msg); })) yield break;
            yield return new WaitForSeconds(2f);
            Check("ISLAND-ATTACH", "the attachment arrives with the plant data", wasAttached && np != null && np.m_attached, "attached before=" + wasAttached + ", after=" + (np != null && np.m_attached));

            // a friend connects another island: my counter must follow, nothing is sent back, silly values are refused
            int c0 = pa.m_nutrientIslandsAttched;
            sink.Messages.Clear();
            string pid = np.GetComponent<SaveInfo>().m_uniqueID;
            byte[] im = SharedWorld.TestIslandMessage(Friend, c0 + 1, pid);
            Try("ISLAND-COUNTER", "friend's island message", delegate { SharedWorld.Handle(im); });
            yield return new WaitForSeconds(4f);
            Check("ISLAND-COUNTER", "a friend's connection raises my island counter", pa.m_nutrientIslandsAttched == c0 + 1, c0 + " -> " + pa.m_nutrientIslandsAttched);
            Check("ISLAND-COUNTER", "a friend's connection is not sent back", CountType(TypeIsland) == 0, CountType(TypeIsland) + " island messages");
            int c1 = pa.m_nutrientIslandsAttched;
            SharedWorld.QuietRejects = true;
            foreach (int silly in new[] { -5, 9999, int.MaxValue, c1 + 50 }) SharedWorld.Handle(SharedWorld.TestIslandMessage(Friend, silly, ""));
            SharedWorld.QuietRejects = false;
            Check("ISLAND-COUNTER", "out-of-range island counts are refused", pa.m_nutrientIslandsAttched == c1, c1 + " -> " + pa.m_nutrientIslandsAttched);
            string hud = Singleton<AltitudeMessageManager>.Exists ? AltitudeMessageManager.GetAltitudeMessage() : "(no hud)";
            var growing = PlantBranch.CurrentlyGrowingBeanstalk;
            string expected = growing != null ? growing.m_connectedIslands + "/" + growing.m_requiredIslands : "(no trunk)";
            Check("ISLAND-COUNTER", "the HUD islands counter reads the trunk's connected islands", growing != null && hud == expected && !hud.StartsWith("0/"), "hud=" + hud + " trunk=" + expected);
        }

        // Crystals: my collection is announced, a friend's removes the crystal here and counts for the team, a made-up one does nothing.
        // Switching a teleporter on unlocks it for everybody: mine is announced once, a friend's unlocks it here and is not sent back, broken messages do nothing.
        IEnumerator TestTeleporters()
        {
            var locked = new List<BudTeleporter>();
            foreach (BudTeleporter t in UnityEngine.Object.FindObjectsOfType(typeof(BudTeleporter))) if (!t.Unlocked && t.m_TeleporterNumber >= 0 && t.m_TeleporterNumber < 100) locked.Add(t);
            Note("---- teleporters (" + locked.Count + " still locked in this world)");
            int rejected0 = SharedWorld.StatRejected;
            SharedWorld.QuietRejects = true;
            SharedWorld.Handle(new byte[] { 8 }); SharedWorld.Handle(new byte[] { 8, 200 }); SharedWorld.Handle(new byte[] { 8, 1, 2, 3 });
            SharedWorld.QuietRejects = false;
            Check("TELEPORTER-SYNC", "broken teleporter messages are refused", SharedWorld.StatRejected == rejected0 + 3, (SharedWorld.StatRejected - rejected0) + " of 3 refused");
            if (locked.Count < 2) { Check("TELEPORTER-SYNC", "two locked teleporters exist to test with (this save has " + locked.Count + "; only the refusal of broken messages was tested)", locked.Count >= 0, ""); yield break; }
            var theirs = locked[0]; var mine = locked[1];
            int theirN = theirs.m_TeleporterNumber, myN = mine.m_TeleporterNumber;
            yield return new WaitForSeconds(1.2f);           // let the mod take note of the current state first
            sink.Messages.Clear();
            SharedWorld.Handle(SharedWorld.TestTeleporterMessage(theirN));
            yield return new WaitForSeconds(1.5f);
            Check("TELEPORTER-SYNC", "a friend's teleporter unlocks here", theirs != null && theirs.Unlocked, "unlocked=" + (theirs != null && theirs.Unlocked));
            Check("TELEPORTER-SYNC", "a friend's unlock is not sent back", CountType(8) == 0, CountType(8) + " teleporter messages");
            sink.Messages.Clear();
            bool ran = Try("TELEPORTER-SYNC", "switch a teleporter on myself", delegate { mine.ForceActivate(); });
            yield return new WaitForSeconds(1.5f);
            bool told = false; foreach (var m in sink.Messages) if (m.Length == 2 && m[0] == 8 && m[1] == myN) told = true;
            Check("TELEPORTER-SYNC", "my teleporter being switched on is announced once", ran && told && CountType(8) == 1, CountType(8) + " teleporter messages, mine among them=" + told);
        }

        IEnumerator TestCrystals()
        {
            var shards = new List<JointShard>();
            foreach (JointShard s in UnityEngine.Object.FindObjectsOfType(typeof(JointShard)))
            {
                var si = s.GetComponent<SaveInfo>();
                if (si != null && !string.IsNullOrEmpty(si.m_uniqueID)) shards.Add(s);
            }
            if (shards.Count < 2 || !Singleton<SceneProfile>.Exists) { Check("CRYSTAL-SYNC", "two crystals exist to test with", false, shards.Count + " crystals"); yield break; }
            var mine = shards[0]; var theirs = shards[1];
            string myGuid = mine.GetComponent<SaveInfo>().m_uniqueID, theirGuid = theirs.GetComponent<SaveInfo>().m_uniqueID;
            Note("---- crystals (" + shards.Count + " in the world)");

            sink.Messages.Clear();
            bool ran = Try("CRYSTAL-SYNC", "collect a crystal the way the game reports it", delegate { JointShard.m_onShardRemoval(mine, myGuid); });
            yield return null;
            Check("CRYSTAL-SYNC", "my collection is announced once", ran && CountType(TypeShard) == 1, CountType(TypeShard) + " crystal messages");

            int credit0 = Singleton<SceneProfile>.Instance.m_sceneData.m_shardCount;
            sink.Messages.Clear();
            Try("CRYSTAL-SYNC", "friend's crystal message", delegate { SharedWorld.Handle(SharedWorld.TestShardMessage(theirGuid)); });
            yield return null; yield return null;
            int credit1 = Singleton<SceneProfile>.Instance.m_sceneData.m_shardCount;
            Check("CRYSTAL-SYNC", "a friend's crystal disappears here", theirs == null, theirs == null ? "removed" : "still there");
            Check("CRYSTAL-SYNC", "a friend's crystal counts for my team", credit1 == credit0 + 1, "crystal count " + credit0 + " -> " + credit1);
            Check("CRYSTAL-SYNC", "a friend's crystal is not sent back", CountType(TypeShard) == 0, CountType(TypeShard) + " crystal messages");

            SharedWorld.Handle(SharedWorld.TestShardMessage("no-such-crystal"));
            yield return null;
            Check("CRYSTAL-SYNC", "a made-up crystal id changes nothing", Singleton<SceneProfile>.Instance.m_sceneData.m_shardCount == credit1, "crystal count " + credit1 + " -> " + Singleton<SceneProfile>.Instance.m_sceneData.m_shardCount);
        }

        // Hundreds of broken and hostile messages: nothing escapes, nothing is slow, no plant changes.
        IEnumerator TestFuzz(List<PlantRoot> roots)
        {
            Note("---- fuzz");
            // the beanstalk keeps growing for a while after an island was connected; compare only once nothing moves any more
            var sigs = new Dictionary<PlantRoot, int>();
            float stillSince = Time.time, giveUp = Time.time + 150f;
            while (Time.time < giveUp)
            {
                bool moved = false;
                foreach (var r in roots) if (r != null) { int s = SharedWorld.PlantSignature(r), o; if (!sigs.TryGetValue(r, out o) || o != s) { moved = true; sigs[r] = s; } }
                if (moved) stillSince = Time.time;
                if (Time.time - stillSince > 4f) break;
                yield return new WaitForSeconds(0.5f);
            }
            // a plant can grow a branch by itself at any moment, which would look like a message changing it: switch growth off while comparing
            var frozen = new List<Behaviour>();
            foreach (PlantBranch b in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch))) if (b.enabled) { b.enabled = false; frozen.Add(b); }
            foreach (PlantRoot pr in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot))) if (pr.enabled) { pr.enabled = false; frozen.Add(pr); }
            yield return null;
            foreach (var r in roots) if (r != null) sigs[r] = SharedWorld.PlantSignature(r);
            var rnd = new System.Random(777);
            var plain = new List<byte[]>();      // must not change anything
            var twisted = new List<byte[]>();    // may be accepted if they happen to parse; only checked for not escaping / not being slow
            int mustReject = 0;                  // how many of the broken ones can only be rejected (cut-off updates, lying headers, out-of-range counts)

            for (int t = 0; t < 300; t++) { var b = new byte[rnd.Next(1, 300)]; rnd.NextBytes(b); b[0] = (byte)(TypePlant + rnd.Next(3)); plain.Add(b); }

            byte[] real = lastPlantMessage;
            if (real == null && roots.Count > 0 && roots[0] != null) { try { real = SharedWorld.TestMessage(roots[0], Friend, SharedWorld.TestRaw(roots[0])); } catch (Exception) { } }
            if (real != null)
            {
                var hd = new BinaryReader(new MemoryStream(real)); hd.ReadByte(); hd.ReadString(); hd.ReadUInt64(); hd.ReadInt32(); int packed = hd.ReadInt32(); hd.ReadUInt32();
                int dataEnd = (int)hd.BaseStream.Position + packed;      // what follows is the optional list of grown parts: a message cut there is still a whole update
                int step = Math.Max(1, dataEnd / 150);
                for (int cut = 1; cut < dataEnd; cut += step) { var c = new byte[cut]; Buffer.BlockCopy(real, 0, c, 0, cut); plain.Add(c); mustReject++; }
                var rd = new BinaryReader(new MemoryStream(real)); rd.ReadByte(); string pid = rd.ReadString();
                foreach (int rawLen in new[] { -1, 0, 1, int.MaxValue, 4 * 1024 * 1024 + 1, 100000000 })
                    foreach (int compLen in new[] { -1, 0, 8, int.MaxValue, real.Length * 100 })
                    {
                        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                        w.Write(TypePlant); w.Write(pid); w.Write(Friend); w.Write(rawLen); w.Write(compLen);
                        var junk = new byte[rnd.Next(0, 64)]; rnd.NextBytes(junk); w.Write(junk);
                        plain.Add(ms.ToArray()); mustReject++;
                    }
                for (int v = 0; v < 25; v++)       // real update with a few damaged bytes in the packed part
                {
                    var c = (byte[])real.Clone();
                    int at = Math.Min(dataEnd - 1, 40 + rnd.Next(Math.Max(1, dataEnd - 40)));
                    c[at] ^= (byte)(1 + rnd.Next(255));
                    twisted.Add(c);
                }
            }
            foreach (int silly in new[] { -1, 0, 1, 9999, int.MaxValue }) plain.Add(SharedWorld.TestIslandMessage(Friend, silly < 0 ? silly : (silly == 0 ? 0 : silly), new string('x', rnd.Next(0, 5000))));
            mustReject += 3;      // -1, 9999 and int.MaxValue are out of range

            plain.AddRange(twisted); mustReject += twisted.Count; twisted.Clear();     // every damaged update carries a checksum, so all of them must be refused
            SharedWorld.QuietRejects = true;
            int escaped = 0, slow = 0, count = 0; long worst = 0;
            int rejectedBefore = SharedWorld.StatRejected;
            var all = new List<byte[]>(plain);
            int plainCount = plain.Count;
            all.AddRange(twisted);
            for (int i = 0; i < all.Count; i++)
            {
                var clock = Stopwatch.StartNew();
                try { SharedWorld.Handle(all[i]); } catch (Exception) { escaped++; }
                long ms = clock.ElapsedMilliseconds; if (ms > worst) worst = ms;
                if (ms > 250) slow++;
                count++;
                if (i == plainCount - 1)
                {
                    int moved = 0;
                    foreach (var kv in sigs) if (kv.Key != null && SharedWorld.PlantSignature(kv.Key) != kv.Value) moved++;
                    Check("MSG-FUZZ", "broken messages change no plant", moved == 0, plainCount + " broken messages, " + moved + " plants changed");
                }
                if (i % 40 == 39) yield return null;
            }
            SharedWorld.QuietRejects = false;
            foreach (var f in frozen) if (f != null) f.enabled = true;
            Check("MSG-FUZZ", "no broken message escapes the handler", escaped == 0, count + " messages, " + escaped + " escaped");
            Check("MSG-FUZZ", "no broken message is slow", slow == 0, "slowest " + worst + " ms (limit 250 ms), " + slow + " slow");
            Check("MSG-FUZZ", "broken messages are really rejected", SharedWorld.StatRejected - rejectedBefore >= mustReject, (SharedWorld.StatRejected - rejectedBefore) + " rejected, at least " + mustReject + " were certain to be");
        }

        void Finish()
        {
            if (finished) return;
            finished = true;
            SharedWorld.TestSink = null;
            string result = "RESULT pass=" + pass + " fail=" + fail + " warn=" + warn;
            Log(result);
            try
            {
                string tmp = Here("coop-selftest-result.tmp");
                File.WriteAllText(tmp, result + "\r\n" + report + "DONE\r\n");
                string dest = Here("coop-selftest-result.txt");
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (Exception) { }
        }
    }
}
