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
        // Who may talk to me, whose host I follow, whose name a message is credited to, and which Steam Cloud names may become files: the rules the mod uses, checked on every run.
        void CheckSecurity()
        {
            bool talk = !RoomRules.MayTalk(true, false) && RoomRules.MayTalk(false, false) && RoomRules.MayTalk(true, true);
            Check("ROOM-TALK", "a player my room does not let in (or that left it) is heard by nobody; without a room the configured players are heard", talk, "");
            bool host = RoomRules.HostFor(100, 50) == 100 && RoomRules.HostFor(0, 50) == 50 && RoomRules.HostFor(0, 0) == 0;
            Check("ROOM-HOST", "the host stays the player I joined, even after Steam hands the room to someone else; a player who merely joins later is never taken for the host", host, "");
            bool sender = SharedWorld.SenderOf(5555, 4444) == 5555 && SharedWorld.SenderOf(0, 4444) == 4444;
            Check("ROOM-DEFAULT", "the room the F2 menu's Enter key picks is read from the settings: ask = listed and I accept, private = private, anything else = public", RoomRules.RoomChoice("ask") == 4 && RoomRules.RoomChoice("private") == 2 && RoomRules.RoomChoice("") == 1 && RoomRules.RoomChoice("garbage") == 1, "");
            Check("SENDER-ID", "a plant or island is credited to the player whose connection it came from, not to a name written inside the message", sender, "");
            bool cloud = !SaveGuard.SafeCloudName("../x") && !SaveGuard.SafeCloudName("/abs") && !SaveGuard.SafeCloudName("C:x") && !SaveGuard.SafeCloudName("a\\b") && !SaveGuard.SafeCloudName("a/../b") && !SaveGuard.SafeCloudName("") && SaveGuard.SafeCloudName("Levels/GrowHome.sav");
            Check("PLANT-THROTTLE", "a big plant is re-sent less often than a small one (3 s up to 30 s)", SharedWorld.ResendInterval(5000) <= 3.01f && SharedWorld.ResendInterval(150000) > 9f && SharedWorld.ResendInterval(150000) > SharedWorld.ResendInterval(40000) && SharedWorld.ResendInterval(100000000) <= 30.01f, SharedWorld.ResendInterval(5000) + " / " + SharedWorld.ResendInterval(150000) + " / " + SharedWorld.ResendInterval(100000000) + " s");
            Environment.SetEnvironmentVariable("SteamAppId", null); Environment.SetEnvironmentVariable("SteamGameId", null);
            Bootstrap.SetSteamEnvironment();
            Check("STEAM-ENV", "the mod puts the Steam app id in the environment first thing, before the game's own Steam check can send the launch back to Steam", Environment.GetEnvironmentVariable("SteamAppId") == "323320" && Environment.GetEnvironmentVariable("SteamGameId") == "323320", Environment.GetEnvironmentVariable("SteamAppId") + " / " + Environment.GetEnvironmentVariable("SteamGameId"));
            Check("CLOUD-NAMES", "a Steam Cloud file name can never take a save outside the save folder (no .., rooted, drive or back-slash names)", cloud, "");
        }

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
            try { CheckSecurity(); } catch (Exception e) { Check("AUDIT-ERROR", "security checks ran", false, Describe(e)); }
            try { CheckRooms(); } catch (Exception e) { Check("AUDIT-ERROR", "room checks ran", false, Describe(e)); }
            try { CheckParty(); } catch (Exception e) { Check("PARTY", "party checks ran", false, Describe(e)); }
            try { CheckLeaderSave(); } catch (Exception e) { Check("WORLD-PARTS", "world part checks ran", false, Describe(e)); }

            float begin = Time.realtimeSinceStartup;
            bool pressed = false, partyTested = false; float menuAt = -1f;
            while (!WorldSync.InWorld())
            {
                if (!pressed && StartMenuReady())
                {
                    if (menuAt < 0f)
                    {
                        menuAt = Time.realtimeSinceStartup;
                        Check("STARTUP-FAST", "the start menu is reached quickly because the intro screens are skipped", menuAt < 15f, menuAt.ToString("F1") + " s after the game started (limit 15 s; with the intro screens it takes more than 15 s)");
                    }
                    if (Time.realtimeSinceStartup - menuAt > 2f && !partyTested)
                    {
                        // the party lobby lives on the start screen: test it there, before the game starts
                        partyTested = true;
                        yield return StartCoroutine(TestPartyMenu());
                        if (CoopMenu.Menu != null && CoopMenu.Menu.gameObject.activeInHierarchy) CoopMenu.Menu.Close();
                    }
                    else if (Time.realtimeSinceStartup - menuAt > 2f)
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
            try { CheckUiDump(); } catch (Exception e) { Check("UI-DUMP", "menu dump check ran", false, Describe(e)); }
            yield return StartCoroutine(TestHudMessages());
            yield return StartCoroutine(TestCoopMenu());

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
                if (r != null && r.IsTrunk() && tested == 1) TestPlantLeader(r);
                if (quick) break;
            }
            Check("PLANT-SEND", "at least one plant was tested", tested > 0, tested + " plants");

            yield return StartCoroutine(TestCrystals());
            yield return StartCoroutine(TestTeleporters());
            yield return StartCoroutine(TestRemoteCopy());
            yield return StartCoroutine(TestWatchdog());
            yield return StartCoroutine(TestFuzz(roots));
            try { CheckGuestIsolation(); } catch (Exception e) { Check("AUDIT-ERROR", "guest isolation checks ran", false, Describe(e)); }
            Finish();
        }

        bool faultTested;
        IEnumerator TestPlant(PlantRoot root, string label, int steps, bool injectFault)
        {
            string id = SharedWorld.PlantId(root);
            Note("---- " + label + " " + id);
            SharedWorld.TestPlayerOnPlant = false;   // the rules above are about updates; the walkster-on-plant rule has its own check (PLANT-DEFER)
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

                // 3c. my walkster stands on the plant: a friend update waits until it steps off, then lands
                if (!Try("PLANT-DEFER", label + ": go back to the old plant", delegate { SharedWorld.TestRevert(id, raw0); })) yield break;
                yield return new WaitForSeconds(0.5f);
                yield return new WaitForSeconds(4f);   // the revert's load can finish a few frames late: let it settle before taking the baseline
                int oldSig = SharedWorld.PlantSignature(root);
                byte[] waitMsg = WithSender(mine[mine.Count - 1], Friend);
                SharedWorld.TestPlayerOnPlant = true;
                Try("PLANT-DEFER", label + ": update while I stand on the plant", delegate { SharedWorld.TestApply(waitMsg); });
                yield return new WaitForSeconds(1f);
                bool held = SharedWorld.PlantSignature(root) == oldSig && SharedWorld.TestWaiting == 1;
                Check("PLANT-DEFER", label + ": the update waits while I stand on the plant", held, "before " + oldSig + ", now " + SharedWorld.PlantSignature(root) + ", unchanged " + (SharedWorld.PlantSignature(root) == oldSig) + ", waiting: " + SharedWorld.TestWaiting);
                SharedWorld.TestPlayerOnPlant = false;
                SharedWorld.TestApplyWaiting();
                yield return new WaitForSeconds(4f);
                Check("PLANT-DEFER", label + ": once I step off, the waiting update lands and the plant equals the sender's", SharedWorld.PlantSignature(root) == sigFinal && SharedWorld.TestWaiting == 0, "sender: " + factsFinal + " | receiver: " + Facts(root) + " | " + Diff(partsFinal, ObjectNames(root)));
                SharedWorld.TestPlayerOnPlant = null;
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

        // In a party the leader's plant is the real one: a guest's plant built on an older copy is not taken (it would undo the leader's growth),
        // one built on the newest is taken and passed on with a new number, and a guest takes plants only from the leader.
        void TestPlantLeader(PlantRoot trunk)
        {
            string id = SharedWorld.PlantId(trunk);
            ulong me = SharedWorld.MyId, leader = 76561199000000777UL;
            byte[] raw = null;
            if (!Try("PLANT-LEADER", "read the trunk state", delegate { raw = SharedWorld.TestRaw(trunk); })) return;
            SharedWorld.TestPlayerOnPlant = false;
            var mayTake = SharedWorld.MayTakeFrom; SharedWorld.MayTakeFrom = null;     // the test's friend is not a connected player
            try
            {
                // I lead: my trunk has number 5 (all messages go through Handle, as they come in from the network)
                SharedWorld.LeaderId = me; SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 5); SharedWorld.MarkSeenForTest(trunk);
                int applied0 = SharedWorld.StatApplied, stale0 = SharedWorld.StatStale;
                Try("PLANT-LEADER", "a guest's trunk built on copy 4", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(trunk, Friend, raw, 4), Friend); });
                bool refused = SharedWorld.StatApplied == applied0 && SharedWorld.StatStale == stale0 + 1 && SharedWorld.ForcedForTest(id);
                Try("PLANT-LEADER", "an unnumbered trunk from a guest", delegate { SharedWorld.Handle(SharedWorld.TestMessage(trunk, Friend, raw), Friend); });
                bool unnumbered = SharedWorld.StatApplied == applied0;
                Check("PLANT-LEADER", "the leader does not take a guest's plant built on an older copy of the leader's plant (it would remove the leader's newer growth) and sends the guest its own instead",
                    refused && unnumbered, "applied " + (SharedWorld.StatApplied - applied0) + ", stale " + (SharedWorld.StatStale - stale0) + ", answers with mine " + SharedWorld.ForcedForTest(id));
                SharedWorld.ClearForcedForTest();
                Try("PLANT-LEADER", "a guest's trunk built on copy 5", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(trunk, Friend, raw, 5), Friend); });
                Check("PLANT-LEADER", "the leader takes a guest's plant built on its newest copy and passes it on to everybody",
                    SharedWorld.StatApplied == applied0 + 1 && SharedWorld.ForcedForTest(id), "applied " + (SharedWorld.StatApplied - applied0) + ", passed on " + SharedWorld.ForcedForTest(id));
                int appliedTaken = SharedWorld.StatApplied;
                Try("PLANT-LEADER", "a second guest's trunk also built on copy 5", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(trunk, Friend + 1, raw, 5), Friend + 1); });
                Check("PLANT-LEADER", "once the leader took one guest's plant, another guest's copy built on the same old number is refused (it would remove the first guest's growth)",
                    SharedWorld.StatApplied == appliedTaken && SharedWorld.PlantNumberForTest(id) == 6, "applied " + (SharedWorld.StatApplied - appliedTaken) + ", leader number " + SharedWorld.PlantNumberForTest(id));

                // my trunk grew and that growth is not sent yet: a guest's copy cannot have it, even with my newest number
                SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 7); SharedWorld.MarkSeenForTest(trunk); SharedWorld.MarkDirtyForTest(id);
                int applied2 = SharedWorld.StatApplied;
                Try("PLANT-LEADER", "a guest's trunk built on copy 7 while my growth is unsent", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(trunk, Friend, raw, 7), Friend); });
                Check("PLANT-LEADER", "the leader does not take a guest's plant while its own newer growth on that plant is not sent yet",
                    SharedWorld.StatApplied == applied2 && SharedWorld.ForcedForTest(id), "applied " + (SharedWorld.StatApplied - applied2) + ", answers with mine " + SharedWorld.ForcedForTest(id));
                SharedWorld.MarkSeenForTest(trunk);

                // I am a guest of `leader`
                SharedWorld.LeaderId = leader; SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 0);
                int applied1 = SharedWorld.StatApplied;
                Try("PLANT-LEADER", "another guest's trunk", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(trunk, Friend, raw, 0), Friend); });
                bool ignoredGuest = SharedWorld.StatApplied == applied1;
                Try("PLANT-LEADER", "the leader's trunk number 9", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(trunk, leader, raw, 9), leader); });
                Check("PLANT-LEADER", "a guest takes plants only from the leader and builds on the leader's number from then on",
                    ignoredGuest && SharedWorld.StatApplied == applied1 + 1 && SharedWorld.PlantNumberForTest(id) == 9, "other guest ignored " + ignoredGuest + ", leader's taken " + (SharedWorld.StatApplied - applied1) + ", number " + SharedWorld.PlantNumberForTest(id));

                // the leader passes on the plant it took from me: that is my own growth coming back, it must not put my newer growth back (or wait to)
                SharedWorld.TestPlayerOnPlant = true;     // I am still on the plant, growing it
                int applied3 = SharedWorld.StatApplied, echoes0 = SharedWorld.StatEchoes, waiting0 = SharedWorld.TestWaiting;
                Try("PLANT-LEADER", "the leader passes on my trunk built on 9", delegate { SharedWorld.Handle(SharedWorld.TestNumberedMessage(trunk, leader, raw, 11, me, 9), leader); });
                Check("PLANT-LEADER", "a guest whose plant the leader took and passed on keeps its own (newer) growth and builds on the leader's new number",
                    SharedWorld.StatApplied == applied3 && SharedWorld.StatEchoes == echoes0 + 1 && SharedWorld.TestWaiting == waiting0 && SharedWorld.PlantNumberForTest(id) == 11,
                    "applied " + (SharedWorld.StatApplied - applied3) + ", echoes " + (SharedWorld.StatEchoes - echoes0) + ", waiting " + (SharedWorld.TestWaiting - waiting0) + ", number " + SharedWorld.PlantNumberForTest(id));
                SharedWorld.TestPlayerOnPlant = false;
            }
            finally { SharedWorld.MayTakeFrom = mayTake; SharedWorld.LeaderId = 0; SharedWorld.ClearForcedForTest(); SharedWorld.SetPlantNumberForTest(id, 0); SharedWorld.TestPlayerOnPlant = null; }
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
        // The game's menus and HUD are written to coopmod-ui.log once per level, so the native co-op menu can be built from the real layout.
        void CheckUiDump()
        {
            string text = File.Exists(NativeUI.DumpPath) ? File.ReadAllText(NativeUI.DumpPath) : "";
            int menus = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance.m_Menus.Count : 0;
            Check("UI-DUMP", "the game's menus and HUD were written to coopmod-ui.log when the level built them", NativeUI.Dumps > 0 && menus > 0 && text.Contains("menu PauseMenu") && text.Contains("---- hud ") && !text.Contains("hud: none") && text.Contains("onClick "),
                "dumps=" + NativeUI.Dumps + " menus=" + menus + " size=" + text.Length + " pauseMenu=" + text.Contains("menu PauseMenu") + " hud=" + text.Contains("---- hud ") + " buttons=" + text.Contains("onClick "));
        }

        // Co-op messages use the game's own M.O.M panel: shown there, in order, a game message pushed aside comes back, a busy panel or a stopped game
        // sends them to the overlay instead, nothing is lost.
        IEnumerator TestHudMessages()
        {
            var hud = Singleton<AbilityHUD>.Exists ? Singleton<AbilityHUD>.Instance : null;
            if (hud == null || hud.m_textBottomLine == null) { Check("HUD-MSG", "the game's HUD is there", false, hud == null ? "no AbilityHUD" : "no bottom line"); yield break; }
            int oldPriority = NativeUI.Priority;
            AbilityHUD.HUDMessage gameMsg = null, block = null;
            NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest();
            Check("HUD-MSG", "in the world the M.O.M panel can take co-op messages", NativeUI.CanShow(), "hidden=" + CoopUI.Hidden + " timeScale=" + Time.timeScale + " allow=" + AbilityHUD.s_allowVisibility + " menu=" + (Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive));

            // a game message is on the panel; ours (made more important for the test) pushes it aside, then it must come back
            gameMsg = new AbilityHUD.HUDMessage("AuditGameMsg", true, 0, AbilityHUD.HUDFlags.SideHint);
            gameMsg.m_sideAlertText = ""; gameMsg.m_stringTop = "M.O.M"; gameMsg.m_stringBottom = "AUDIT GAME MESSAGE";
            AbilityHUD.ShowHUDMessage(gameMsg);
            yield return null;
            NativeUI.Priority = -1000;
            string one = "AUDIT HUD MESSAGE ONE";
            float momBefore = NativeUI.MomTimerForTest;
            CoopUI.Toast(one);
            bool shown = false; float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 2f) { if (hud.m_textBottomLine.text == one && NativeUI.IsCurrent(NativeUI.ActiveName ?? "")) { shown = true; break; } yield return null; }
            Check("HUD-MSG", "a co-op message appears in the game's own M.O.M panel, not on the overlay", shown && !CoopUI.OverlayToastsForTest().Contains(one),
                "panel says \"" + hud.m_textBottomLine.text + "\", panel on=" + (hud.m_sideHintPanel != null && hud.m_sideHintPanel.gameObject.activeInHierarchy) + ", overlay=" + CoopUI.OverlayToastsForTest().Count);
            float momAfter = NativeUI.MomTimerForTest;
            Check("HUD-MSG", "a co-op message does not hold back the game's own M.O.M prompts (their timer is left as it was)", !float.IsNaN(momBefore) && momAfter == momBefore, "timer " + momBefore + " -> " + momAfter);
            t0 = Time.realtimeSinceStartup;
            while (NativeUI.Busy && Time.realtimeSinceStartup - t0 < NativeUI.ShowSeconds + 3f) yield return null;
            yield return null;
            Check("HUD-MSG", "a message disappears after its time and the game's message that was pushed aside comes back", !NativeUI.Busy && NativeUI.IsCurrent("AuditGameMsg") && hud.m_textBottomLine.text == "AUDIT GAME MESSAGE",
                "busy=" + NativeUI.Busy + " game message current=" + NativeUI.IsCurrent("AuditGameMsg") + " panel says \"" + hud.m_textBottomLine.text + "\"");
            AbilityHUD.HideHUDMessage("AuditGameMsg");
            yield return null;

            // a burst: every message is shown, in order, none on the overlay, without a long queue
            CoopUI.ClearOverlayToastsForTest();
            string[] burst = { "AUDIT BURST A", "AUDIT BURST B", "AUDIT BURST C" };
            foreach (var b in burst) CoopUI.Toast(b);
            var seen = new List<string>(); t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 18f)
            {
                string now = hud.m_textBottomLine.text;
                if (Array.IndexOf(burst, now) >= 0 && (seen.Count == 0 || seen[seen.Count - 1] != now)) seen.Add(now);
                if (!NativeUI.Busy) break;
                yield return null;
            }
            float took = Time.realtimeSinceStartup - t0;
            Check("HUD-MSG", "three messages at once are all shown in the panel one after another, in order, none lost, in under 14 s", string.Join("|", seen.ToArray()) == string.Join("|", burst) && CoopUI.OverlayToastsForTest().Count == 0 && took < 14f,
                "seen " + string.Join(", ", seen.ToArray()) + " in " + took.ToString("F1") + " s, overlay " + CoopUI.OverlayToastsForTest().Count);
            NativeUI.Priority = oldPriority;

            // the panel is held by a more important game message: ours goes to the overlay after a short wait and the game's message stays
            CoopUI.ClearOverlayToastsForTest();
            block = new AbilityHUD.HUDMessage("AuditBlock", true, 0, AbilityHUD.HUDFlags.SideHint);
            block.m_sideAlertText = ""; block.m_stringTop = "M.O.M"; block.m_stringBottom = "AUDIT BLOCK";
            AbilityHUD.ShowHUDMessage(block);
            yield return null;
            CoopUI.Toast("AUDIT WAITS"); CoopUI.Toast("AUDIT WAITS 2"); CoopUI.Toast("AUDIT WAITS 3");
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < NativeUI.WaitLimit + 2f && !CoopUI.OverlayToastsForTest().Contains("AUDIT WAITS 3")) yield return null;
            yield return null;
            var ov = CoopUI.OverlayToastsForTest();
            Check("HUD-MSG", "when an important game message holds the panel, co-op messages (all of a burst, together) go to the overlay within a few seconds and the game's message stays", ov.Contains("AUDIT WAITS") && ov.Contains("AUDIT WAITS 2") && ov.Contains("AUDIT WAITS 3") && !NativeUI.Busy && NativeUI.IsCurrent("AuditBlock") && hud.m_textBottomLine.text == "AUDIT BLOCK",
                "overlay " + string.Join(", ", ov.ToArray()) + " after " + (Time.realtimeSinceStartup - t0).ToString("F1") + " s, busy=" + NativeUI.Busy + " block current=" + NativeUI.IsCurrent("AuditBlock"));
            AbilityHUD.HideHUDMessage("AuditBlock");
            yield return null;

            // the game's own M.O.M prompt is on the panel: co-op messages leave it alone and use the overlay
            CoopUI.ClearOverlayToastsForTest();
            var prompt = new AbilityHUD.HUDMessage(NativeUI.MomPromptName, true, 0, AbilityHUD.HUDFlags.SideHint);
            prompt.m_sideAlertText = ""; prompt.m_stringTop = "M.O.M"; prompt.m_stringBottom = "AUDIT MOM PROMPT";
            AbilityHUD.ShowHUDMessage(prompt);
            yield return null;
            CoopUI.Toast("AUDIT NEAR PROMPT");
            Check("HUD-MSG", "while the game's own M.O.M prompt is on the panel, a co-op message goes to the overlay and the prompt stays", CoopUI.OverlayToastsForTest().Contains("AUDIT NEAR PROMPT") && !NativeUI.Busy && NativeUI.IsCurrent(NativeUI.MomPromptName),
                "overlay=" + CoopUI.OverlayToastsForTest().Contains("AUDIT NEAR PROMPT") + " prompt current=" + NativeUI.IsCurrent(NativeUI.MomPromptName));
            AbilityHUD.HideHUDMessage(NativeUI.MomPromptName);

            // the game stopped (or a menu open): straight to the overlay
            CoopUI.ClearOverlayToastsForTest();
            float scale = Time.timeScale;
            Time.timeScale = 0f;
            CoopUI.Toast("AUDIT STOPPED");
            bool direct = CoopUI.OverlayToastsForTest().Contains("AUDIT STOPPED") && !NativeUI.Busy;
            Time.timeScale = scale;
            Check("HUD-MSG", "while the game is stopped the message goes straight to the overlay", direct, "");
            NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest();
            Note("HUD messages: " + NativeUI.NativeShown + " shown in the game's panel, " + NativeUI.OverlayUsed + " on the overlay");
        }

        static bool Inside(RectTransform inner, RectTransform outer, float slack)
        {
            var a = new Vector3[4]; var b = new Vector3[4];
            inner.GetWorldCorners(a); outer.GetWorldCorners(b);
            float minX = Mathf.Min(b[0].x, b[2].x) - slack, maxX = Mathf.Max(b[0].x, b[2].x) + slack, minY = Mathf.Min(b[0].y, b[2].y) - slack, maxY = Mathf.Max(b[0].y, b[2].y) + slack;
            foreach (var p in a) if (p.x < minX || p.x > maxX || p.y < minY || p.y > maxY) return false;
            return true;
        }

        static string Ids(List<CoopMenu.Row> rows) { var l = new List<string>(); foreach (var r in rows) l.Add(r.Id); return string.Join(",", l.ToArray()); }

        // The CO-OP menu: built into the game's menus, reached from the pause menu, its lines follow the state and do what they say, Back and cancel
        // return to the pause menu, lines that need typing leave the menus and open their window.
        IEnumerator TestCoopMenu()
        {
            // the lines for every state (pure)
            string a, b;
            var s = new CoopMenu.State();
            bool offline = Ids(CoopMenu.RowsFor(s, out a, out b)) == "back" && a.Contains("STEAM");
            s.Online = true;
            bool lobbyless = Ids(CoopMenu.RowsFor(s, out a, out b)) == "host_friends,host_public,host_accept,host_private,join,display,back";
            s.Busy = true;
            bool busy = Ids(CoopMenu.RowsFor(s, out a, out b)) == "display,back";
            s.Busy = false;
            s.InLobby = true; s.Code = "ABCDE12345FGH"; s.InWorld = true; s.HostConnected = true;
            var inRoom = CoopMenu.RowsFor(s, out a, out b);
            bool guest = Ids(inRoom) == "friends,copy,chat,rejoin,leave,display,back" && a.Contains("ABCDE12345FGH");
            s.IsHost = true;
            bool host = Ids(CoopMenu.RowsFor(s, out a, out b)) == "friends,copy,chat,leave,display,back";
            s.Pending = "A VERY LONG PLAYER NAME";      // only a host gets join requests
            bool pend = Ids(CoopMenu.RowsFor(s, out a, out b)) == "let_in,refuse,friends,copy,chat,leave,back";
            s.IsHost = false;
            var crowded = CoopMenu.RowsFor(s, out a, out b);
            pend = pend && crowded.Count <= CoopMenu.MaxRows && crowded[crowded.Count - 1].Id == "back";
            s.Players = "ANA, BOB, CARL +4"; s.Status = new string('X', 200);
            CoopMenu.RowsFor(s, out a, out b);
            bool shortLines = a.Length <= 34 && b.Length <= 44;
            Check("COOP-MENU", "the menu offers the right lines in every state (offline, no party, guest, host, someone waiting), at most 7, BACK last, status lines short",
                offline && lobbyless && busy && guest && host && pend && shortLines, "offline=" + offline + " noParty=" + lobbyless + " connecting=" + busy + " guest=" + guest + " host=" + host + " waiting=" + pend + " short=" + shortLines);

            var cb = GetComponent<CoopBehaviour>();
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            GUIMenu pause = gm == null ? null : CoopMenu.Find(gm, "PauseMenu");
            var menu = CoopMenu.Menu;
            var handler = menu == null ? null : menu.GetComponent<CoopMenuHandler>();
            bool built = gm != null && pause != null && menu != null && gm.m_Menus.Contains(menu) && handler != null && menu.GetComponents<GUIHandler>().Length == 1 && CoopMenu.Entry != null;
            Transform settings = pause == null ? null : pause.transform.Find("AspectKeeper/MenuGroupPanel/ButtonCamera");
            bool placed = built && settings != null && CoopMenu.Entry.transform.parent == settings.parent && CoopMenu.Entry.transform.GetSiblingIndex() == settings.GetSiblingIndex() + 1
                && CoopMenu.Entry.transform.Find("ButText").GetComponent<UnityEngine.UI.Text>().text == "CO-OP";
            Check("COOP-MENU", "a CO-OP menu is made from the game's own Settings menu, and a CO-OP line sits right under Settings in the pause menu", built && placed,
                "built=" + built + " placed=" + placed + " builds=" + CoopMenu.Builds + (CoopMenu.LastProblem.Length > 0 ? " problem: " + CoopMenu.LastProblem : ""));
            if (!built || !placed || cb == null) yield break;

            bool pauseOpened = false;
            try { gm.ActivateMenu("PauseMenu"); pauseOpened = true; }
            catch (Exception e) { Check("COOP-MENU", "the pause menu opens", false, Describe(e)); }
            if (!pauseOpened) yield break;
            yield return null;
            bool entryOk = CoopMenu.Entry.gameObject.activeInHierarchy && CoopMenu.Entry.interactable;
            CoopMenu.Entry.onClick.Invoke();
            yield return null; yield return null;
            string expect1, expect2;
            var expected = CoopMenu.RowsFor(cb.CoopMenuState(), out expect1, out expect2);
            var shown = handler.RowIdsForTest;
            bool open = menu.gameObject.activeInHierarchy && !pause.gameObject.activeInHierarchy && gm.AnyMenuActive;
            bool rowsOk = string.Join(",", shown.ToArray()) == Ids(expected);
            string layout = RowsLayout(handler, expected, menu);
            bool selected = MenuSelected(handler, menu);
            Check("COOP-MENU", "pressing CO-OP in the pause menu opens the co-op menu with the lines for my state, labelled, inside the menu panel, the first line selected (keyboard and controller work)",
                entryOk && open && rowsOk && layout.Length == 0 && selected,
                "entry=" + entryOk + " open=" + open + " rows " + string.Join(",", shown.ToArray()) + " (want " + Ids(expected) + ") layout " + (layout.Length == 0 ? "ok" : layout) + " selected=" + selected + "; title \"" + (handler.Title != null ? handler.Title.text : "") + "\" says \"" + expect1 + "\" / \"" + expect2 + "\"");

            // CO-OP DISPLAY switches the overlay both ways and the line follows
            var display = handler.ButtonFor("display");
            if (display != null)
            {
                bool was = CoopUI.Hidden;
                display.onClick.Invoke(); yield return null;
                display = handler.ButtonFor("display");
                bool flipped = display != null && CoopUI.Hidden != was && display.transform.Find("ButText").GetComponent<UnityEngine.UI.Text>().text == (CoopUI.Hidden ? "CO-OP DISPLAY: OFF" : "CO-OP DISPLAY: ON");
                if (display != null) display.onClick.Invoke();
                yield return null;
                Check("COOP-MENU", "the CO-OP DISPLAY line switches the co-op overlay off and on again, and its label follows", flipped && CoopUI.Hidden == was, "flipped=" + flipped + " back=" + (CoopUI.Hidden == was));
            }
            else Check("COOP-MENU", "the CO-OP DISPLAY line is there when Steam is running", !cb.CoopMenuState().Online, "online=" + cb.CoopMenuState().Online);

            // a line that just changed what it does ignores a press for a moment (a press meant for its old meaning must not let somebody in)
            yield return StartCoroutine(WaitReal(CoopMenuHandler.SettleTime + 0.1f));

            // BACK and the cancel key return to the pause menu
            var back = handler.ButtonFor("back");
            if (back == null) { Check("COOP-MENU", "the menu has a BACK line", false, "rows " + string.Join(",", handler.RowIdsForTest.ToArray())); gm.CloseAllMenus(); yield break; }
            back.onClick.Invoke();
            yield return null;
            bool backOk = pause.gameObject.activeInHierarchy && !menu.gameObject.activeInHierarchy;
            CoopMenu.Entry.onClick.Invoke(); yield return null;
            bool reopened = menu.gameObject.activeInHierarchy;
            handler.OnMenuCancel(); yield return null;
            bool cancelOk = pause.gameObject.activeInHierarchy && !menu.gameObject.activeInHierarchy;
            Check("COOP-MENU", "BACK and the cancel key (Esc / B) go back to the pause menu, and the co-op menu opens again after that", backOk && reopened && cancelOk, "back=" + backOk + " reopened=" + reopened + " cancel=" + cancelOk);

            // JOIN A PARTY leaves the menus and opens the join window (typing needs it)
            if (cb.CoopMenuState().Online && !cb.CoopMenuState().InLobby)
            {
                CoopMenu.Entry.onClick.Invoke(); yield return null;
                yield return StartCoroutine(WaitReal(CoopMenuHandler.SettleTime + 0.1f));
                var jb = handler.ButtonFor("join");
                if (jb != null) jb.onClick.Invoke();
                float t0 = Time.realtimeSinceStartup;
                while (!cb.JoinOpenForTest && Time.realtimeSinceStartup - t0 < 2f) yield return null;
                bool joined = cb.JoinOpenForTest && !gm.AnyMenuActive;
                cb.CloseJoinForTest();
                yield return null;
                Check("COOP-MENU", "JOIN A PARTY closes the menus and opens the join window (code and party list), and the game gets the keys back when it closes", jb != null && joined && !cb.JoinOpenForTest && !gm.AnyMenuActive, "line=" + (jb != null) + " opened=" + joined);
            }
            else Check("COOP-MENU", "JOIN A PARTY is offered when Steam is running and I am not in a party", !cb.CoopMenuState().Online || cb.CoopMenuState().InLobby, "");
            if (gm.AnyMenuActive) gm.CloseAllMenus();
        }

        // Game time stands still on the start screen, so waits there use real time.
        static IEnumerator WaitReal(float seconds) { float end = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < end) yield return null; }

        // Every line of the menu as the player sees it: the expected label, a font, inside the menu panel, the panel on screen, lines not on top of each other,
        // a label that fits its line, and member lines that can never be selected. Returns "" when all is well, else what is wrong.
        static string RowsLayout(CoopMenuHandler handler, List<CoopMenu.Row> expected, GUIMenu menu)
        {
            var problems = new List<string>();
            var rows = handler.RowObjectsForTest; var texts = handler.RowTextsForTest; var inert = handler.RowInertForTest;
            var group = handler.Group as RectTransform;
            if (rows.Count != expected.Count) problems.Add(rows.Count + " lines, want " + expected.Count);
            if (group != null && !Inside(group, (RectTransform)menu.transform, 2f)) problems.Add("panel off screen");
            float lastBottom = float.MaxValue;
            for (int i = 0; i < rows.Count && i < expected.Count; i++)
            {
                var go = rows[i];
                if (!go.activeInHierarchy) { problems.Add(i + " hidden"); continue; }
                var t = texts[i];
                if (t == null || t.text != expected[i].Label || t.font == null) problems.Add(i + " label \"" + (t == null ? "" : t.text) + "\"");
                else if (t.preferredWidth > ((RectTransform)t.transform).rect.width + 2f) problems.Add(i + " label too wide (" + t.preferredWidth.ToString("F0") + " > " + ((RectTransform)t.transform).rect.width.ToString("F0") + ")");
                if (group != null && !Inside((RectTransform)go.transform, group, 2f)) problems.Add(i + " outside the panel");
                if (inert[i] != expected[i].Inert) problems.Add(i + " inert=" + inert[i]);
                if (inert[i] && go.GetComponent<UnityEngine.UI.Selectable>() != null) problems.Add(i + " member line selectable");
                var c = new Vector3[4]; ((RectTransform)go.transform).GetWorldCorners(c);
                float top = Mathf.Max(c[0].y, c[1].y), bottom = Mathf.Min(c[0].y, c[1].y);
                if (top > lastBottom + 0.01f * Mathf.Abs(top - bottom) + 0.001f) problems.Add(i + " overlaps the line above");
                lastBottom = bottom;
            }
            return string.Join("; ", problems.ToArray());
        }

        // the first line the player can press is the menu's default, and the selection is on a line of this menu
        static bool MenuSelected(CoopMenuHandler handler, GUIMenu menu)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            UnityEngine.UI.Button first = null;
            var rows = handler.RowObjectsForTest; var inert = handler.RowInertForTest;
            for (int i = 0; i < rows.Count; i++) if (!inert[i]) { first = rows[i].GetComponent<UnityEngine.UI.Button>(); break; }
            return es != null && first != null && ((UnityGUIMenu)menu).m_defaultSelection == first.gameObject
                && es.currentSelectedGameObject != null && es.currentSelectedGameObject.activeInHierarchy && es.currentSelectedGameObject.transform.IsChildOf(menu.transform)
                && es.currentSelectedGameObject.GetComponent<UnityEngine.UI.Selectable>() != null;
        }

        static List<Party.Member> TestMembers(int count)
        {
            var names = new[] { "TRICKER", "POINT", "A VERY LONG STEAM NAME", "EVE", "BOB", "ANA", "CARL", "DAN" };
            var list = new List<Party.Member>();
            for (int i = 0; i < count; i++) list.Add(new Party.Member { Id = 100UL + (ulong)i, Name = names[i % names.Length], Leader = i == 0, Me = i == 0, Ready = i == 1 || i == 4, Waiting = i == 2 });
            return list;
        }

        static List<Party.Friend> TestFriends(int count)
        {
            var list = new List<Party.Friend>();
            for (int i = 0; i < count; i++) list.Add(new Party.Friend { Id = 500UL + (ulong)i, Name = "FRIEND " + (char)('A' + i), InGame = i % 3 == 0, Invited = i == 1 });
            return list;
        }

        static bool AllCapped(List<CoopMenu.Row> rows)
        {
            int actions = 0;
            foreach (var r in rows) if (!r.Inert) actions++;
            return actions <= CoopMenu.MaxRows && rows.Count > 0 && rows[rows.Count - 1].Id == "back";
        }

        // The party rules and the party menu's lines for every state (pure).
        class CaptureTransport : ITransport
        {
            public readonly List<byte[]> Sent = new List<byte[]>();
            public int MaxSize;
            public void Send(byte[] data) { }
            public byte[] Receive() { return null; }
            public void SendReliable(byte[] data) { Sent.Add(data); if (data.Length > MaxSize) MaxSize = data.Length; }
            public byte[] ReceiveReliable() { return null; }
            public string Diagnostics() { return "capture"; }
            public void Close() { }
        }

        static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // A grown world is bigger than one Steam message: it goes as packed parts, and only the leader's world counts.
        void CheckLeaderSave()
        {
            // a 1.8 MB world, half of it hard to pack, as big as a real grown one
            var rnd = new System.Random(7);
            var world = new byte[1800000];
            for (int i = 0; i < world.Length; i++) world[i] = i < 900000 ? (byte)rnd.Next(256) : (byte)(i % 97);
            var parts = WorldTransfer.Split(world, 42);
            int largest = 0; foreach (var p in parts) largest = Math.Max(largest, p.Length);
            byte[] back = null;
            var asm = new WorldTransfer.Assembly();
            for (int i = parts.Count - 1; i >= 0; i--) { var w = asm.Add(parts[i], 0f); if (w != null) back = w; }     // out of order
            Check("WORLD-PARTS", "a world bigger than one Steam message (1.8 MB) goes in packed parts under 1 MB each and comes back exactly, in any order",
                parts.Count > 1 && largest < 1000000 && Equal(back, world), "parts=" + parts.Count + " largest=" + largest + " back=" + (back != null));

            var again = new WorldTransfer.Assembly(); byte[] twice = null;
            again.Add(parts[0], 0f); again.Add(parts[0], 0f);
            for (int i = 1; i < parts.Count; i++) { var w = again.Add(parts[i], 0f); if (w != null) twice = w; }
            var newer = WorldTransfer.Split(world, 43);
            var mixed = new WorldTransfer.Assembly(); byte[] fromNewer = null;
            mixed.Add(parts[0], 0f);
            foreach (var p in newer) { var w = mixed.Add(p, 0f); if (w != null) fromNewer = w; }
            Check("WORLD-PARTS", "a part that comes twice counts once, and a newer sending of the world starts over cleanly", Equal(twice, world) && Equal(fromNewer, world), "");

            int refused = 0, cases = 0;
            var bad = new List<byte[]>();
            var flipped = (byte[])parts[parts.Count - 1].Clone(); flipped[flipped.Length - 1] ^= 0x5A; bad.Add(flipped);       // damaged data
            foreach (int field in new[] { 5, 7, 9, 17 })                                                                         // part number, part count, sizes
                foreach (int v in new[] { -1, 0, 65535, int.MaxValue })
                {
                    if (field == 5 && v == 0) continue;   // that is the real number of the first part
                    var m = (byte[])parts[0].Clone();
                    if (field == 5 || field == 7) { var b2 = BitConverter.GetBytes((ushort)v); m[field] = b2[0]; m[field + 1] = b2[1]; }
                    else { var b4 = BitConverter.GetBytes(v); Buffer.BlockCopy(b4, 0, m, field, 4); }
                    bad.Add(m);
                }
            bad.Add(new byte[] { WorldTransfer.MsgPart, 1, 2 });
            var cut = new byte[parts[0].Length - 100]; Buffer.BlockCopy(parts[0], 0, cut, 0, cut.Length); bad.Add(cut);
            foreach (var m in bad)
            {
                cases++;
                var a = new WorldTransfer.Assembly();
                try
                {
                    byte[] w = null;
                    if (m == flipped) { for (int i = 0; i < parts.Count - 1; i++) a.Add(parts[i], 0f); }
                    w = a.Add(m, 0f);
                    if (m != flipped && w == null && parts.Count > 1) { foreach (var p in parts) if (p != parts[0]) { var x = a.Add(p, 0f); if (x != null) w = x; } }
                    if (w == null || !Equal(w, world)) refused++;
                }
                catch (Exception) { refused++; }
            }
            Check("WORLD-PARTS", "damaged parts are refused with an ordinary error: wrong numbers, absurd sizes, cut off, changed data (checksum)", refused == cases, refused + "/" + cases);

            Check("WORLD-PARTS", "world parts are only taken from the leader, like a whole world",
                MessageGate.Classify(parts[0], false) == Route.SnapshotNotFromHost && MessageGate.Classify(parts[0], true) == Route.World, "");

            Check("LEADER-SAVE", "the leader of a party uses a player's world changes only once that player plays in the leader's world; without a party nothing changes",
                !Authority.MayApply(true, true, false) && Authority.MayApply(true, true, true) && Authority.MayApply(false, true, false) && Authority.MayApply(true, false, false), "");
            Check("LEADER-SAVE", "a guest sends none of its own world until it plays in the leader's world (its blank world never reaches the leader)",
                !Authority.MayShare(true, false) && Authority.MayShare(true, true) && Authority.MayShare(false, false), "");
            Check("LEADER-SAVE", "the I-am-in-your-world note (with the number of the leader's world) is understood and nothing else is taken for it",
                MessageGate.Classify(new byte[] { Authority.MsgHaveWorld, 1, 2, 3, 4 }, false) == Route.HaveWorld && MessageGate.Classify(new byte[] { Authority.MsgHaveWorld }, false) == Route.Ignore, "");
            Check("LEADER-SAVE", "only the leader can say which world it plays", MessageGate.Classify(new byte[] { Authority.MsgEpoch, 1, 2, 3, 4 }, true) == Route.Epoch && MessageGate.Classify(new byte[] { Authority.MsgEpoch, 1, 2, 3, 4 }, false) == Route.Ignore, "");
            Check("LEADER-SAVE", "a guest is in the leader's world only while the world it loaded is the one the leader plays now (not an old copy, not another leader's, not none)",
                Authority.InLeaderWorld(true, 5, 5) && !Authority.InLeaderWorld(true, 5, 6) && !Authority.InLeaderWorld(true, 0, 0) && !Authority.InLeaderWorld(false, 5, 5), "");
            Check("LEADER-SAVE", "a member following the leader waits up to " + CoopBehaviour.PreloadWait + " s for a big world (the follow never starts a world of its own when it does not come)", CoopBehaviour.PreloadWait >= 60f, "wait=" + CoopBehaviour.PreloadWait);
            var ep = WorldTransfer.Split(new byte[5000], 1, 777); var epa = new WorldTransfer.Assembly(); byte[] epw = null; foreach (var p in ep) { var w = epa.Add(p, 0f); if (w != null) epw = w; }
            Check("WORLD-PARTS", "the number of the leader's world travels with it", epw != null && epa.Epoch == 777, "epoch=" + epa.Epoch);

            // any message bigger than one Steam message goes in pieces and is whole again on the other side
            var big = new byte[2500000]; for (int i = 0; i < big.Length; i++) big[i] = (byte)rnd.Next(256);
            var pieces = PeerLink.Pieces(big);
            var link = new PeerLink(null, 1UL);
            byte[] whole = null; int biggest = 0;
            if (pieces != null) for (int i = pieces.Count - 1; i >= 0; i--) { biggest = Math.Max(biggest, pieces[i].Length); var w = link.AddPiece(pieces[i]); if (w != null) whole = w; }
            var small = PeerLink.Pieces(new byte[1000]);
            Check("WORLD-PARTS", "a big plant or world message (2.5 MB) goes in pieces under 1 MB and comes back exactly; a small one goes as it is",
                pieces != null && pieces.Count > 1 && biggest < 1000000 && Equal(whole, big) && small != null && small.Count == 1 && small[0].Length == 1000, "pieces=" + (pieces != null ? pieces.Count : -1) + " biggest=" + biggest);
            var badLink = new PeerLink(null, 2UL); int badOk = 0;
            if (pieces != null)
            {
                var p0 = (byte[])pieces[0].Clone(); p0[7] = 200; if (badLink.AddPiece(p0) == null) badOk++;
                var p1 = (byte[])pieces[0].Clone(); Buffer.BlockCopy(BitConverter.GetBytes(int.MaxValue), 0, p1, 9, 4); if (badLink.AddPiece(p1) == null) badOk++;
                var p2 = new byte[pieces[0].Length - 7]; Buffer.BlockCopy(pieces[0], 0, p2, 0, p2.Length); if (badLink.AddPiece(p2) == null) badOk++;
            }
            if (badLink.AddPiece(new byte[] { PeerLink.MsgPiece, 1 }) == null) badOk++;
            Check("WORLD-PARTS", "pieces with wrong numbers, absurd sizes or cut off are dropped", badOk == 4, badOk + "/4");
            Check("WORLD-PARTS", "a message too big even for pieces is not sent", PeerLink.Pieces(new byte[PeerLink.PieceSize * PeerLink.MaxPieces + 1]) == null, "");
        }

        void CheckParty()
        {
            bool follow = Party.ShouldFollow(true, false, true, false, "3", null) && Party.ShouldFollow(true, false, true, false, "4", "3")
                && !Party.ShouldFollow(true, false, true, false, "3", "3") && !Party.ShouldFollow(false, false, true, false, "3", null)
                && !Party.ShouldFollow(true, true, true, false, "3", null) && !Party.ShouldFollow(true, false, false, false, "3", null)
                && !Party.ShouldFollow(true, false, true, true, "3", null) && !Party.ShouldFollow(true, false, true, false, "", null) && !Party.ShouldFollow(true, false, true, false, null, null);
            bool play = Party.PlayFor(true, false, "", 3) == "4" && Party.PlayFor(true, false, "4", 9) == "4" && Party.PlayFor(false, true, "4", 4) == "" && Party.PlayFor(false, false, "4", 4) == "4"
                && Party.PlayFor(true, true, null, 0) == "1" && Party.PlayFor(true, false, "", -7) == "1" && Party.ParseRound("x") == 0 && Party.ParseRound("-5") == 0 && Party.ParseRound("12") == 12 && Party.ParseRound(null) == 0;
            Check("PARTY", "members follow the leader in only when the leader starts a game they have not seen started, once let in, never the leader, never twice; the leader's game number goes up each start and is cleared on the start screen",
                follow && play, "follow=" + follow + " play=" + play);

            var members = TestMembers(4);
            var shuffled = new List<Party.Member> { members[2], members[0], members[3], members[1] };
            var ordered = Party.Ordered(shuffled);
            bool roster = ordered[0].Leader && ordered.Count == 4 && Party.RosterLabel(members[0]) == "TRICKER (YOU)  LEADER" && Party.RosterLabel(members[1]) == "POINT  READY"
                && Party.RosterLabel(members[2]) == "A VERY LONG ST  WAITING" && Party.RosterLabel(members[3]) == "EVE" && Party.ReadyCount(members) == 1 && Party.ReadyCount(null) == 0
                && Party.RosterLabel(new Party.Member { Name = null }) == "PLAYER";
            var sorted = Party.SortFriends(new List<Party.Friend> { new Party.Friend { Name = "zed" }, new Party.Friend { Name = "Bea", InGame = true }, new Party.Friend { Name = "amy" }, new Party.Friend { Name = "Cy", InGame = true } });
            bool friends = sorted[0].Name == "Bea" && sorted[1].Name == "Cy" && sorted[2].Name == "amy" && sorted[3].Name == "zed" && Party.SortFriends(null).Count == 0
                && Party.Pages(0) == 1 && Party.Pages(4) == 1 && Party.Pages(5) == 2 && Party.Pages(9) == 3;
            Check("PARTY", "the member list puts the leader first and shows who is me, ready or waiting, long names cut short; friends playing Grow Home come first, by name; friends come in pages of 4",
                roster && friends, "roster=" + roster + " friends=" + friends + " labels: " + Party.RosterLabel(members[0]) + " / " + Party.RosterLabel(members[2]));

            string a, b;
            var problems = new List<string>();
            var s = new CoopMenu.State { AtStart = true };
            var r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "back" || !a.Contains("STEAM")) problems.Add("offline " + Ids(r));
            s.Online = true;
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "host_friends,host_public,join,back") problems.Add("no party " + Ids(r));
            s.Invite = "EVE";
            r = CoopMenu.RowsFor(s, out a, out b);
            if (r[0].Id != "accept_invite" || r[0].Label != "JOIN EVE'S PARTY") problems.Add("invite " + Ids(r));
            s.Invite = ""; s.Busy = true;
            if (Ids(CoopMenu.RowsFor(s, out a, out b)) != "back") problems.Add("connecting");
            s.Busy = false; s.InLobby = true; s.IsHost = true; s.Code = "ABCD-EFGH-JKLMN"; s.Leader = "TRICKER"; s.Members = TestMembers(3); s.Pending = "EVE";
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "member,member,member,let_in,refuse,start,friends,copy,leave,back" || !r[0].Inert || r[3].Inert) problems.Add("leader " + Ids(r));
            if (r.Count > 5 && r[5].Label != "START GAME  1/2 READY") problems.Add("start label " + r[5].Label);
            if (!b.Contains("3 PLAYERS") || !a.Contains("ABCD-EFGH-JKLMN")) problems.Add("leader lines " + a + " / " + b);
            s.Members = TestMembers(1); s.Pending = "";
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "member,start,friends,copy,leave,back" || r[1].Label != "START GAME") problems.Add("alone " + Ids(r));
            s.Members = TestMembers(7);
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "member,member,member,more,start,friends,copy,leave,back" || r[3].Label != "+4 MORE" || !r[3].Inert) problems.Add("big party " + Ids(r));
            s.IsHost = false; s.Members = TestMembers(3);
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "member,member,member,ready,friends,copy,leave,back" || r[3].Label != "READY: NO") problems.Add("guest " + Ids(r));
            s.Ready = true;
            if (CoopMenu.RowsFor(s, out a, out b)[3].Label != "READY: YES") problems.Add("ready yes");
            s.HostPlaying = true;
            if (Ids(CoopMenu.RowsFor(s, out a, out b)) != "member,member,member,follow,friends,copy,leave,back") problems.Add("leader playing");
            s.Waiting = true;
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "member,member,member,friends,copy,leave,back" || !b.Contains("LET YOU IN")) problems.Add("waiting " + Ids(r) + " / " + b);
            s.Waiting = false; s.Page = "friends"; s.Friends = TestFriends(9);
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "invite:500,invite:501,invite:502,invite:503,friends_more,back" || r[1].Label != "INVITED FRIEND B" || r[0].Label != "INVITE FRIEND A" || !b.Contains("PAGE 1 OF 3")) problems.Add("friends " + Ids(r) + " / " + b);
            s.FriendPage = 2;
            if (Ids(CoopMenu.RowsFor(s, out a, out b)) != "invite:508,friends_more,back") problems.Add("friends page 3");
            s.FriendPage = 99;
            if (Ids(CoopMenu.RowsFor(s, out a, out b)) != "invite:508,friends_more,back") problems.Add("friends page past the end");
            s.Friends = new List<Party.Friend>();
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "back" || !b.Contains("NO FRIENDS")) problems.Add("no friends " + Ids(r));
            s.Page = ""; s.Following = true;
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "back" || !a.StartsWith("JOINING TRICKER")) problems.Add("following " + Ids(r) + " / " + a);
            s.Following = false; s.AtStart = false; s.InWorld = true; s.IsHost = true; s.Members = TestMembers(3);
            r = CoopMenu.RowsFor(s, out a, out b);
            if (Ids(r) != "friends,copy,chat,leave,display,back") problems.Add("in the game " + Ids(r));
            // every state: at most MaxRows lines that do something, BACK last, short status lines
            foreach (bool start in new[] { true, false })
                foreach (bool host in new[] { true, false })
                    foreach (int n in new[] { 0, 1, 4, 5, 16 })
                    {
                        var t = new CoopMenu.State { Online = true, AtStart = start, InLobby = n > 0, IsHost = host, InWorld = !start, HostConnected = true, Members = TestMembers(n), Pending = n > 1 ? "SOMEBODY WITH A LONG NAME" : "", Leader = new string('L', 40), Code = "ABCD-EFGH-JKLMN", Invite = new string('I', 40), Status = new string('S', 90) };
                        var rows = CoopMenu.RowsFor(t, out a, out b);
                        int lines = 0; foreach (var x in rows) if (x.Inert) lines++;
                        if (!AllCapped(rows) || a.Length > 34 || b.Length > 44 || lines > Party.MaxShown) problems.Add("state start=" + start + " host=" + host + " n=" + n + ": " + Ids(rows));
                    }
            Check("PARTY", "the party menu offers the right lines in every state (no party, an invite, leader, member, ready, leader already playing, waiting to be let in, friends pages, following), member lines first, at most 7 lines that do something, BACK last",
                problems.Count == 0, problems.Count == 0 ? "" : string.Join("; ", problems.ToArray()));
        }

        // The party lobby on the start screen, before the audit starts the game: the CO-OP PARTY line, the party menu with the lines for my state, a full
        // party laid out inside the panel, the friends page and cancel, JOIN from the start screen, my own saves back after a party, players dropped on leaving.
        // a world file as the game writes it (not compressed, version 91) whose header claims `length` bytes, followed by `body`
        static byte[] FakeWorld(int length, byte[] body)
        {
            var b = new List<byte>();
            b.Add(0); b.AddRange(BitConverter.GetBytes(91)); b.AddRange(BitConverter.GetBytes(length));
            if (body != null) b.AddRange(body);
            return b.ToArray();
        }

        // my own world file (or the game's built-in one) passes the same check a world from the leader goes through
        static bool OwnWorldReads(string levels)
        {
            try
            {
                string f = Path.Combine(levels, Application.loadedLevelName + ".sav");
                if (File.Exists(f)) return WorldSync.LooksLikeWorld(File.ReadAllBytes(f));
                var res = Resources.Load("SaveFiles/LevelFiles/" + Application.loadedLevelName) as TextAsset;
                return res != null && WorldSync.LooksLikeWorld(res.bytes);
            }
            catch (Exception) { return false; }
        }

        IEnumerator TestPartyMenu()
        {
            var cb = GetComponent<CoopBehaviour>();
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            var start = gm == null ? null : CoopMenu.Find(gm, "StartMenu");
            var menu = CoopMenu.Menu;
            var handler = menu == null ? null : menu.GetComponent<CoopMenuHandler>();
            var entry = CoopMenu.StartEntry;
            Transform settings = start == null ? null : start.transform.Find("Panel/ButtonPanel/SettingsButton");
            bool placed = entry != null && settings != null && entry.transform.parent == settings.parent && entry.transform.GetSiblingIndex() + 1 == settings.GetSiblingIndex()
                && entry.transform.Find("ButText").GetComponent<UnityEngine.UI.Text>().text == "CO-OP PARTY";
            bool onScreen = placed && start.gameObject.activeInHierarchy && entry.gameObject.activeInHierarchy && entry.interactable && Inside((RectTransform)entry.transform, (RectTransform)start.transform, 2f);
            Check("PARTY-MENU", "the start menu has a CO-OP PARTY line right above Settings, on screen and pressable", placed && onScreen && handler != null && cb != null,
                "placed=" + placed + " onScreen=" + onScreen + " menu=" + (handler != null) + (CoopMenu.LastProblem.Length > 0 ? " problem: " + CoopMenu.LastProblem : ""));
            if (!placed || !onScreen || handler == null || cb == null) yield break;

            // the party menu for my real state
            entry.onClick.Invoke();
            yield return null; yield return null;
            string l1, l2;
            var state = cb.CoopMenuState();
            var expected = CoopMenu.RowsFor(state, out l1, out l2);
            bool open = menu.gameObject.activeInHierarchy && !start.gameObject.activeInHierarchy && state.AtStart;
            bool titled = handler.Title != null && handler.Title.text == "PARTY";
            bool rowsOk = string.Join(",", handler.RowIdsForTest.ToArray()) == Ids(expected);
            string layout = RowsLayout(handler, expected, menu);
            bool selected = MenuSelected(handler, menu);
            Check("PARTY-MENU", "pressing CO-OP PARTY opens the party menu (titled PARTY) with the lines for my state, inside the panel, the first line selected",
                open && titled && rowsOk && layout.Length == 0 && selected,
                "open=" + open + " titled=" + titled + " rows " + string.Join(",", handler.RowIdsForTest.ToArray()) + " (want " + Ids(expected) + ") layout " + (layout.Length == 0 ? "ok" : layout) + " selected=" + selected + "; says \"" + l1 + "\" / \"" + l2 + "\"");

            // a full party: member lines, someone waiting, the leader's lines; then a member's lines and the friends page
            var full = new CoopMenu.State { Online = true, AtStart = true, InLobby = true, IsHost = true, Code = "ABCD-EFGH-JKLMN", Leader = "TRICKER", Members = TestMembers(6), Pending = "A VERY LONG STEAM" };
            var views = new List<string>();
            foreach (int k in new[] { 0, 1, 2 })
            {
                var t = full;
                if (k == 1) { t.IsHost = false; t.Pending = ""; t.HostPlaying = true; t.Members = TestMembers(2); }
                if (k == 2) { t.Page = "friends"; t.Friends = TestFriends(6); }
                handler.SetStateForTest(t);
                yield return null; yield return null;
                var want = CoopMenu.RowsFor(t, out l1, out l2);
                string lay = RowsLayout(handler, want, menu);
                bool same = string.Join(",", handler.RowIdsForTest.ToArray()) == Ids(want);
                bool sel = MenuSelected(handler, menu);
                if (!same || lay.Length > 0 || !sel) views.Add("view " + k + ": rows " + string.Join(",", handler.RowIdsForTest.ToArray()) + " want " + Ids(want) + " layout " + lay + " selected=" + sel);
                else views.Add("view " + k + " ok (lines " + handler.LastActionHeight + " high" + (handler.Grown ? ", panel grown" : "") + ")");
            }
            handler.ClearStateForTest();
            yield return null;
            bool allViews = true; foreach (var v in views) if (!v.Contains(" ok")) allViews = false;
            Check("PARTY-MENU", "a full party (6 members, someone waiting), a member's view and the friends page all fit: member lines cannot be selected, every line inside the panel and on screen, no line on top of another, labels fit their line",
                allViews, string.Join(" | ", views.ToArray()));

            // cancel: the friends page goes back to the party page, then the party menu back to the start menu
            cb.SetMenuPageForTest("friends");
            handler.OnMenuCancel(); yield return null;
            bool pageBack = menu.gameObject.activeInHierarchy && !start.gameObject.activeInHierarchy;
            handler.OnMenuCancel(); yield return null; yield return null;
            bool closed = !menu.gameObject.activeInHierarchy && start.gameObject.activeInHierarchy && CoopMenu.StartMenuShowing();
            Check("PARTY-MENU", "cancel (Esc / B) on the friends page goes back to the party page, and from the party page back to the start menu", pageBack && closed, "page=" + pageBack + " closed=" + closed);

            // JOIN A PARTY on the start screen: the join window over the start menu (its clicks paused), and back to the party menu when it closes
            if (cb.CoopMenuState().Online && !cb.CoopMenuState().InLobby)
            {
                entry.onClick.Invoke(); yield return null; yield return null;
                yield return StartCoroutine(WaitReal(CoopMenuHandler.SettleTime + 0.1f));
                var jb = handler.ButtonFor("join");
                if (jb != null) jb.onClick.Invoke();
                float t0 = Time.realtimeSinceStartup;
                while (!cb.JoinOpenForTest && Time.realtimeSinceStartup - t0 < 2f) yield return null;
                yield return null;
                bool opened = cb.JoinOpenForTest && !menu.gameObject.activeInHierarchy && start.gameObject.activeInHierarchy && cb.MenusFrozenForTest;
                cb.CloseJoinForTest();
                t0 = Time.realtimeSinceStartup;
                while (!menu.gameObject.activeInHierarchy && Time.realtimeSinceStartup - t0 < 3f) yield return null;
                yield return null;
                bool returned = menu.gameObject.activeInHierarchy && !cb.JoinOpenForTest && !cb.MenusFrozenForTest && UnityEngine.EventSystems.EventSystem.current != null;
                handler.OnMenuCancel(); yield return null; yield return null;
                bool startAgain = CoopMenu.StartMenuShowing();
                Check("PARTY-MENU", "JOIN A PARTY on the start screen opens the join window over the start menu (the menu's clicks paused behind it), and the party menu comes back when it closes",
                    jb != null && opened && returned && startAgain, "line=" + (jb != null) + " opened=" + opened + " returned=" + returned + " startMenu=" + startAgain);
            }
            else Check("PARTY-MENU", "JOIN A PARTY is offered on the start screen when Steam is running and I am not in a party", !cb.CoopMenuState().Online || cb.CoopMenuState().InLobby, "");
            if (menu.gameObject.activeInHierarchy) handler.OnMenuCancel();
            yield return null;

            // after a party: back on the start screen my saves are my own again
            string ownPlayer = SaveSystem.m_playerSavePath, ownLevels = SaveSystem.m_levelFilesPath;
            WorldSync.EnterGuestMode();
            bool entered = WorldSync.GuestMode && SaveSystem.m_levelFilesPath != ownLevels && WorldSync.GuestPathsActive();
            bool online = cb.CoopMenuState().Online;      // without Steam the mod's party loop does not run: that is a failed check, not something to do by hand
            float t1 = Time.realtimeSinceStartup;
            while (online && WorldSync.GuestMode && Time.realtimeSinceStartup - t1 < 3f) yield return null;
            bool restored = !WorldSync.GuestMode && SaveSystem.m_playerSavePath == ownPlayer && SaveSystem.m_levelFilesPath == ownLevels && !WorldSync.GuestPathsActive();
            if (WorldSync.GuestMode) WorldSync.LeaveGuestMode();
            Check("PARTY-LEAVE", "on the start screen without a party a guest's saves go back to their own folder by themselves (the guest copy is never written over them)",
                online && entered && restored, "online=" + online + " entered=" + entered + " restored=" + restored + " now " + SaveSystem.m_playerSavePath + " | " + SaveSystem.m_levelFilesPath);
            Check("PARTY-LEAVE", "a world from the network is read once before it is loaded: a damaged one is refused, a real one passes",
                !WorldSync.LooksLikeWorld(new byte[] { 1, 0, 255, 255, 255, 127, 9, 9, 9, 9, 9, 9 }) && !WorldSync.LooksLikeWorld(null) && OwnWorldReads(ownLevels), "");
            Check("PARTY-LEAVE", "a world claiming more data than it carries is refused before the game's reader would make room for it (header or any block)",
                !WorldSync.LooksLikeWorld(FakeWorld(0x7FFFFFFF, null)) && !WorldSync.LooksLikeWorld(FakeWorld(-1, null))
                && !WorldSync.LooksLikeWorld(FakeWorld(12, new byte[] { 255, 255, 255, 127, 0, 0, 0, 0, 0, 0, 0, 0 }))
                && !WorldSync.LooksLikeWorld(FakeWorld(12, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 255, 255, 0, 0 }))
                && WorldSync.LooksLikeWorld(FakeWorld(12, new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 })), "");

            // once a follow has handed over to the game's own start, my saves stay the guest ones until the game is in
            WorldSync.EnterGuestMode();
            WorldSync.BeginStart();
            WorldSync.LeaveGuestMode();
            yield return StartCoroutine(WaitReal(1f));
            bool kept = WorldSync.GuestMode && WorldSync.GuestPathsActive();
            WorldSync.Starting = false;
            WorldSync.LeaveGuestMode();
            bool backAfter = !WorldSync.GuestMode && SaveSystem.m_playerSavePath == ownPlayer && SaveSystem.m_levelFilesPath == ownLevels;
            Check("PARTY-LEAVE", "while the game is starting for a member following the leader, nothing switches the saves back to my own (the leader's world is what loads)", kept && backAfter, "kept=" + kept + " backAfter=" + backAfter);

            // the leader on the start screen answers a world request at once, with the save the game is about to load
            string ownFile = Path.Combine(SaveSystem.m_levelFilesPath, Application.loadedLevelName + ".sav");
            bool hasOwn = File.Exists(ownFile) && !WorldSync.GuestMode;
            var cap = new CaptureTransport();
            WorldSync.Handle(cap, new byte[] { 1 });
            WorldSync.Forget(cap);     // never left waiting for an answer
            byte[] answered = null; string why = "";
            try { var asm = new WorldTransfer.Assembly(); foreach (var m in cap.Sent) { var w = asm.Add(m, 0f); if (w != null) answered = w; } }
            catch (Exception e) { why = e.Message; }
            bool sameFile = answered != null && hasOwn && Equal(answered, File.ReadAllBytes(ownFile));
            Check("LEADER-SAVE", "the leader still on the start screen sends their saved world straight away (no waiting until they are in the game), in parts that each fit one Steam message, exactly the file the game loads",
                hasOwn && sameFile && cap.Sent.Count > 0 && cap.MaxSize < 1000000, "own=" + hasOwn + " parts=" + cap.Sent.Count + " largest=" + cap.MaxSize + " same=" + sameFile + " " + why);

            // a copy of the leader's world is kept before a party plays in it
            string testBackups = Path.Combine(Path.GetTempPath(), "growhome-coop-audit-backups");
            string backup = WorldSync.BackupOwnWorld("audit", true, testBackups);     // a folder of its own: the real backups are never pushed out by audits
            bool backedUp = backup != null && File.Exists(backup) && Equal(File.ReadAllBytes(backup), File.ReadAllBytes(ownFile));
            try { Directory.Delete(testBackups, true); } catch (Exception) { }
            Check("LEADER-SAVE", "before other players come into the leader's world a copy of the leader's save is kept (the newest 10), so a bad session can be undone", backedUp, backup ?? "no backup");

            // leaving a party drops the players I only had because of it
            if (cb.CoopMenuState().Online)
            {
                cb.AddRoomPeerForTest(Friend);
                bool added = cb.HasPeerForTest(Friend) && cb.RoomPeerCountForTest == 1;
                cb.LeavePartyForTest();
                bool dropped = !cb.HasPeerForTest(Friend) && cb.RoomPeerCountForTest == 0;
                Check("PARTY-LEAVE", "leaving a party drops the players I only had because of it (their copy in my world, their connection and their link)", added && dropped, "added=" + added + " dropped=" + dropped);
            }
            else Check("PARTY-LEAVE", "leaving a party drops its players (needs Steam; Steam is not running)", false, "steam offline");
            if (!CoopMenu.StartMenuShowing() && menu.gameObject.activeInHierarchy) handler.OnMenuCancel();
        }

        // The watchdog: a game that keeps running frames writes nothing; one that stops is reported after the stall time, and again when it recovers.
        IEnumerator TestWatchdog()
        {
            string file = Path.Combine(Path.GetTempPath(), "coop-watchdog-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
            var dog = new Watchdog(file, 1000); dog.Start();
            for (int i = 0; i < 12; i++) { dog.Beat(); yield return new WaitForSeconds(0.1f); }
            string quiet = File.Exists(file) ? File.ReadAllText(file) : "";
            Check("WATCHDOG", "a game that keeps running frames writes nothing", quiet.Length == 0, quiet.Trim());
            yield return new WaitForSeconds(2.2f);                      // no beats: the test's own "game" has frozen
            string frozen = File.Exists(file) ? File.ReadAllText(file) : "";
            Check("WATCHDOG", "a game that stops running frames is reported", frozen.Contains("has not run a frame"), frozen.Trim());
            dog.Beat(); yield return new WaitForSeconds(0.8f);
            string back = File.Exists(file) ? File.ReadAllText(file) : "";
            Check("WATCHDOG", "the recovery is reported too", back.Contains("running again"), back.Trim());
            dog.Stop();
            try { File.Delete(file); } catch (Exception) { }
        }

        // A remote player's copy must never grab anything in my world (its hands would pull on my plant and lock my walkster to the branch), and a friend's
        // held-object packet must never move a plant part: the plant is synced by its own updates only.
        IEnumerator TestRemoteCopy()
        {
            PlantBranch branch = null;
            foreach (PlantRoot r in UnityEngine.Object.FindObjectsOfType(typeof(PlantRoot)))
            {
                foreach (var b in r.m_Branches) if (b != null) { branch = b; break; }
                if (branch != null) break;
            }
            var host = UnityEngine.Object.FindObjectOfType(typeof(CoopBehaviour)) as CoopBehaviour;
            if (branch == null || host == null) { Check("REMOTE-NO-GRAB", "a plant branch and the mod exist to test with", false, "branch=" + (branch != null) + " host=" + (host != null)); yield break; }
            var peer = new Peer { Id = 7777UL, Number = 3 };
            host.TestSpawnCopy(peer, branch.transform.position + Vector3.right * 1.5f);
            if (peer.Clone == null || peer.Driver == null) { Check("REMOTE-NO-GRAB", "a copy of another player can be made for the test", false, "no copy"); yield break; }
            peer.Driver.useKeyboard = false;
            peer.Driver.input.leftGrab = true; peer.Driver.input.rightGrab = true;
            var hands = peer.Driver.GetComponent<BipedHandsScript>();
            bool grabbed = false;
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSeconds(0.1f);
                if (hands == null) break;
                for (int h = 0; h < 2; h++) if (hands.hand[h].grabbing || hands.hand[h].grabbedObject != null) grabbed = true;
            }
            Check("REMOTE-NO-GRAB", "a copy told to grab never grabs anything in my world", hands != null && !grabbed, hands == null ? "no hands on the copy" : (grabbed ? "a hand grabbed something" : "no hand grabbed"));
            Check("CLONE-NO-JETPACK", "a copy of another player has no jetpack of its own (it would talk to my fuel meter)", peer.Clone.GetComponentsInChildren<JetPack>(true).Length == 0, peer.Clone.GetComponentsInChildren<JetPack>(true).Length + " found");
            peer.Driver.input.leftGrab = false; peer.Driver.input.rightGrab = false;
            Destroy(peer.Clone);
            peer.ClearClone();

            Rigidbody part = null;
            foreach (Rigidbody rb in UnityEngine.Object.FindObjectsOfType(typeof(Rigidbody)))
                if (rb.GetComponentInParent<PlantRoot>() != null && (part == null || (part.isKinematic && !rb.isKinematic))) part = rb;
            if (part == null) { Check("PLANT-PART-STEER", "a plant part exists to test with", false, "none"); yield break; }
            Vector3 start = part.position;
            ObjectSync.SetRemoteHeld(7778UL, new PlayerState.Held[] { new PlayerState.Held { id = ObjectSync.KeyOf(part), pos = start + Vector3.right * 6f, rot = part.rotation, vel = Vector3.zero } });
            for (int i = 0; i < 5; i++) ObjectSync.FixedTick();
            yield return new WaitForFixedUpdate();
            float moved = (part.position - start).magnitude;
            Check("PLANT-PART-STEER", "a friend's held-object packet never moves a plant part (the plant is synced by its own updates only)", moved < 0.05f, "moved " + moved.ToString("F2") + " m");
        }

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
            GC.Collect();   // start the timing from a clean heap: a garbage collection that falls inside one message would show up as a slow message (it still counts if it happens)
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
