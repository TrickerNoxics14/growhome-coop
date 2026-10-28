# Grow Home Co-op: critical checks

One command runs everything, on this PC, with nobody playing:

```
powershell -ExecutionPolicy Bypass -File audit\run-audit.ps1
```

It builds the mod and the launcher, starts the real game in its own copy (the game presses CONTINUE by itself), runs every check below
inside it, closes it, puts your saves and settings back exactly as they were, and prints PASS / FAIL for each check.
Exit code 0 means every critical check passed in a full run; 1 means something did not; 3 means a partial or quick run (not a release gate); 2 means Grow Home was running and nothing was touched. The newest report is `audit\latest-report.md`.

Options: `-Quick` (shorter, for development, never a release gate), `-SkipGame`, `-SkipExe`, `-KeepBuild`, `-ForceClose`
(close a running Grow Home first; without it the audit refuses to start, so it never kills a game you are playing).

**A release is allowed only when one full run (no -Quick, nothing skipped) shows every check PASS.**
A check that did not report at all is shown as `NOT RUN` and counts as a failure, so a check that crashes or is skipped cannot hide.

## The checks

Where they run: **game** = inside the real game, using the real send/receive code; **exe** = the launcher against a throw-away web server and folder;
**script** = the runner itself.

| Check | Where | What it proves | How |
|---|---|---|---|
| BUILD-OK | script | mod and launcher build clean; the audited mod is the one installed for the run | `dotnet build`, hash of the built dll |
| LZ-ROUNDTRIP | game | the compressor gives back exactly what went in | empty / tiny / zeros / noise / patterns / floats, and the real trunk data after growing |
| LZ-HOSTILE | game | garbage, absurd claimed sizes (up to 2 billion bytes) and length bombs only cause ordinary errors | 400 random inputs, 4 absurd sizes, one bomb; must be refused fast without a crash or giant allocation |
| GATE-ROUTING | game | network messages follow the rules: only the host's world is accepted, oversized data is dropped, events are accepted from anyone | the gate function is called with every message type, from host and non-host, normal and oversized |
| SAVE-PATHS | game | all saves go to `coop-saves`, Steam Cloud saving is off | reads the game's save paths and Steam settings |
| GUEST-ISOLATION | game | as a guest, saving (even forced) never changes my own save files | hashes every file in `coop-saves`, enters guest mode, forces a full save, hashes again |
| PLANT-SEND | game | a growing plant is sent, at most once per 3 s, and the settled state is sent last | grows the trunk like a player pulling on it and records what would go on the wire |
| PLANT-SIZE | game | one update stays under 100 KB and takes under 60 ms to make | measures the largest update and the time to serialise + compress it |
| PLANT-CONVERGE | game | a copy that only has an older plant ends up identical (parts, branches, length, islands counter) | rewinds the plant to its old state, plays the updates in, compares |
| PLANT-REBUILD | game | when updating in place fails, the fallback rebuild still gives the identical plant | injects one failure into the in-place path |
| PLANT-NOLEAK | game | sending plants does not pile up the game's after-load callbacks | counts the game's queued callbacks before and after many sends |
| PLANT-OWNER | game | a received plant is never echoed back; a plant a friend is growing is not overwritten; sending resumes afterwards | simulates a friend's update, then grows locally and watches what is sent |
| ISLAND-ATTACH | game | an island a friend connected shows as connected here | applies a friend's plant update on a copy that does not know the island |
| ISLAND-COUNTER | game | my connection is announced once; a friend's raises my island counter and the HUD "n/6"; nothing is echoed; silly values are refused | connects an island the way the game does, plays in a friend's message and out-of-range ones, reads the HUD text |
| CRYSTAL-SYNC | game | my crystal is announced; a friend's disappears here and counts for the team; a made-up one does nothing | reports a real crystal as collected, plays a friend's message in |
| MSG-FUZZ | game | hundreds of broken and hostile messages change no plant, escape nowhere, and are never slow | random bytes, truncations of a real update, lying headers, damaged updates |
| UPDATE-OFFLINE-FAST | exe | a dead or hanging update server delays the start by seconds at most and touches nothing | a server that never answers, and a refused connection |
| UPDATE-STAGED-APPLY | exe | a newer mod is downloaded first, installed at the next start, the old one kept | full stage / apply cycle |
| UPDATE-TAMPER-REJECT | exe | a download whose SHA-256 does not match is never staged or installed | server hands out other bytes than the manifest promises |
| UPDATE-NO-DOWNGRADE | exe | the same or an older version changes nothing | manifests with versions 2, 1, 0 while 2 is installed |
| UPDATE-BAD-MANIFEST | exe | broken manifests and non-https download addresses are ignored without a crash | 9 broken manifests; address rules |
| UPDATE-STAGE-TAMPER | exe | a staged file changed after download, or not newer, is refused and removed | edits the staged file, stages an old version |
| PATCH-CORRECT | exe | the patcher adds exactly one call to the mod at the start of `SteamManager.Awake`, to the supported Steam build | patches a copy of the original and reads the result back |
| INSTALL-PATCHED | exe | the installed game really calls the mod | reads the installed game assembly |
| STARTUP-FAST | game | the start menu is reached within 30 s because the intro logo screens are skipped | reads the time the game takes to reach the menu and the skip setting |
| LAUNCH-VERIFIED | script | the launcher window starts the co-op copy, confirms the mod loaded within 60 s and closes by itself | the audit opens the window with auto-play, exactly like a friend pressing Play |
| LAUNCH-VERIFY | exe | the launcher tells apart: mod loaded, regular game started instead, nothing started, game closed again, mod missing | stand-in game processes and a log file the launcher has to judge |
| RUNNING-GAME | exe | a regular Grow Home that is already running is told apart from the co-op copy | stand-in processes running from different folders; the launcher compares real program paths |
| BORROWER-DETECT | exe | a Steam account that only borrows the game (Family Sharing) is recognised, so it is started through Steam | install manifests with the owner's and other accounts, plus this PC's real manifest |
| STEAM-LAUNCH-OPTION | exe | Steam's launch option that makes Play start the co-op copy is edited safely | edits in memory only: exact read-back, exact undo, damaged files refused, Windows line endings kept, this PC's real settings files |
| INSTALL-FAST | exe | the install links the big data files instead of copying: seconds, no extra disk; never links what the game or patcher writes; deleting the copy never touches the original | a fake 130 MB game folder, hashes before and after, free disk space before and after |
| UPDATE-BACKGROUND | exe | on a very slow network the short check gives up harmlessly; the background check is patient; `coop-noupdate.txt` switches checking off completely (no request at all); the launcher leaves the game a note | a web server that answers after 6 s, a request counter |
| UPDATE-NOTICE | game | the game shows "MOD UPDATE n DOWNLOADED: RESTART THE GAME TO USE IT" once and ignores damaged notes | the note file is written and read with good and damaged contents |
| GUI-FONT | exe | the window uses the game's own menu font, read from the player's own Grow Home at run time (no game files are in the download), with a safe fallback | cuts the font out of this PC's game data, then tries with no game to read from |
| GUI-RENDER | exe | every page is drawn like the game's menus: blue sky, pink selection, framed status panel, red progress boxes lighting up | the pages are drawn into pictures and measured; the pictures are kept in the report folder (`window\`) |
| GUI-LAYOUT | exe | menu lines fit their column, long paths wrap inside the panel without losing a letter, the window draws at any size | text measured against the space it has; four window sizes |
| GUI-INPUT | exe | arrow keys, Enter, Escape, Y/N and the mouse work like the game's menus; a question asked by the worker thread is answered from the window; settings switch both ways | simulated key presses and clicks, a real second thread asking |
| UPDATE-SIGNED | exe | only the author can update the mod: every update must carry a valid signature (not missing, not from another key, not reusable for another file, address or version number), come from GitHub over https, not be redirected elsewhere, and not be oversized | signed and tampered manifests, look-alike hosts, a redirecting server, a 9 MB download |
| NET-SANITY | game | pose packets with impossible numbers (NaN, a million metres, absurd speeds) or cut off are thrown away; bad visibility messages are refused; the log cannot be flooded | thousands of damaged packets and messages |
| PLAYER-NAMES | game | players are shown by their Steam names in the status bar, the player list, the name tags and the messages; odd names are cleaned (no emoji or markup, 16 letters at most, duplicates numbered) | cleaning rules on 20 odd names, plus the real Steam name read in the running game |
| CHAT | game | in-game chat (T to open): text only, 100 characters, shown under the name of the sender's connection, flood-limited, damaged messages refused, routed by the network gate | cleaning rules, hostile messages, a 20-message flood, the real mod sending and receiving |
| LAUNCH-DIRECT-FIRST | exe | every account (also one that only borrows the game) starts the co-op copy directly; Steam is only the fallback when a direct start fails; the old --via-steam option is ignored | the start decision for every option combination, and the audit's own launch uses the old option |
| GAME-STARTS-MODDED | script | the modded game starts, loads the mod, reaches the world alone, no crash | log lines, process state, crash folders |
| NO-UNEXPECTED-ERRORS | script | the mod log and the Unity log have no errors or exceptions from the mod | scans both logs (only lines tagged `[test]` are allowed: the injected fault) |
| ORIGINAL-UNTOUCHED | script | the real Steam save files and the original Grow Home assembly are exactly as before | hashes before and after |
| AUDIT-RESTORED | script | the test copy's saves, logs and settings are put back exactly | hashes before and after |

## Real bugs this audit has already caught (and that are now fixed)

1. The compression added for plant updates could not run in this game's Mono (no native zlib): every plant send failed silently. Replaced by `Lz.cs`.
2. `InvalidDataException` does not exist in the game's runtime: the code that used it would have crashed on first use. Replaced.
3. Saving a plant makes the game queue "after the next load" callbacks for every branch; they piled up with every send and later ran against deleted branches (crash on the next update). Now dropped before each load and restored after each save.
4. The "rebuild my copy of the plant" fallback also deleted the crown the level designers placed on top of the trunk, which cannot be re-created (later island and HUD code crashed). It now only removes what grew at runtime.
5. A damaged or hostile island message could set the island counter to any number. Now limited to a sane range.
6. On the laptop the "Grow Home Co-op" shortcut asked Steam to start Grow Home, but Steam's launch option for the game was empty, so Steam started the REGULAR game. The launcher now sets the option (after asking), notices a regular game that is already running, and confirms the mod really loaded.

## Adding a check

1. Add a line to `required-checks.txt`: `ID | game/exe/script | what it proves`.
2. Report it from the code: in the mod `Check("ID", "what", ok, "detail")` (SelfTest.cs), in the launcher `Check("ID", ok, "what", "detail")` (Audit.cs).
3. Run the audit. A required check that never reports shows as NOT RUN and fails the run.

## Releasing

`audit\deploy.ps1 [-Laptop]` installs the build on this PC (and the laptop over ssh) but only if `audit\last-full-pass.md` (written by a full passing run) names exactly the mod that is built now.
It never publishes anything: pushing a new version to GitHub for friends is a separate, deliberate step.
| ROOMS | game | public rooms (listed, anyone joins) and private rooms (code + host accepts each player with Y/N) | pure rule checks with hostile data |
| MOUSE-FREE | game | the mouse works in the code box and room menus | the real mod frees the cursor against a game that locks it |
| GHOST-LEFT | game | a player who left does not stay standing in the world | the 6-second rule and the real removal of the copy |
| TELEPORTER-SYNC | game | a teleporter one player switches on is unlocked for everybody | the real teleporters of the world, both directions, echo and bad messages |
| ROOM-TALK | game | only players my room admits may talk to me | the rule the mod uses, checked on every run |
| ROOM-HOST | game | the host is the room owner I joined, not whoever Steam hands the room to | the rule the mod uses |
| SENDER-ID | game | plant and island updates are credited to their connection, not a name inside the message | the rule the mod uses |
| CLOUD-NAMES | game | cloud file names cannot escape the save folder | the rule the mod uses, with hostile names |
| SETTINGS | exe | every settings option switches the mod and the launcher as shown | the settings page driven with keys, files checked each way |
| ROOM-DEFAULT | game | the F2 menu Enter key picks the room chosen in the settings | the rule the mod uses |
