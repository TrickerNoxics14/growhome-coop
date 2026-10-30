# Grow Home Co-op: critical checks

One command runs everything, on this PC, with nobody playing:

```
powershell -ExecutionPolicy Bypass -File audit\run-audit.ps1
```

It builds the mod and the launcher, starts the real game in its own copy (the game presses CONTINUE by itself), runs every check below
inside it, closes it, puts your saves and settings back exactly as they were, and prints PASS / FAIL for each check.
Exit code 0 means every critical check passed in a full run; 1 means something did not; 3 means a partial or quick run (not a release gate); 2 means Grow Home was running and nothing was touched. The newest report is `audit\latest-report.md`.

Options: `-Quick` (shorter, for development, never a release gate), `-Quick -Groups clone,fx` (QUICK AUDIT of only those groups - world, plants, party, net, clone, fx, ui, launcher - see `audit\check-groups.txt`; it prints "QUICK AUDIT (not a release audit)" and never writes `last-full-pass.md`), `-SkipGame`, `-SkipExe`, `-KeepBuild`, `-ForceClose`
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
| PLANT-DEFER | game | while my walkster stands on a plant, a friend update waits and lands once I step off | runs an update with the walkster on the plant, then off it |
| REMOTE-NO-GRAB | game | a remote player copy told to grab never grabs anything in my world | tells the copy to grab next to a branch and watches its hands |
| CLONE-NO-JETPACK | game | a remote player copy has no jetpack of its own, so it cannot disturb my fuel meter | counts the jetpacks on a spawned copy |
| WATCHDOG | game | a frozen game is reported in the log (and its recovery); a running game writes nothing | runs a watchdog with a 1 s limit, beats, stops beating, beats again |
| PLANT-THROTTLE | game | a big plant is re-sent less often than a small one (3 s up to 30 s) | asks the resend interval for small and huge updates |
| STEAM-ENV | game | the mod sets SteamAppId and SteamGameId itself at start-up | clears them, runs the setter, reads them back |
| PLANT-PART-STEER | game | a friend held-object packet never moves a plant part | sends a held-object packet for a plant part and steps physics |
| PLANT-NOLEAK | game | sending plants does not pile up the game's after-load callbacks | counts the game's queued callbacks before and after many sends |
| PLANT-OWNER | game | a received plant is never echoed back; a plant a friend is growing is not overwritten; sending resumes afterwards | simulates a friend's update, then grows locally and watches what is sent |
| ISLAND-ATTACH | game | an island a friend connected shows as connected here | applies a friend's plant update on a copy that does not know the island |
| ISLAND-COUNTER | game | my connection is announced once; a friend's raises my island counter and the HUD "n/6"; nothing is echoed; silly values are refused | connects an island the way the game does, plays in a friend's message and out-of-range ones, reads the HUD text |
| CRYSTAL-SYNC | game | my crystal is announced; a friend's disappears here and counts for the team; a made-up one does nothing | reports a real crystal as collected, plays a friend's message in |
| MSG-FUZZ | game | hundreds of broken and hostile messages change no plant, escape nowhere, and are never slow | random bytes, truncations of a real update, lying headers, damaged updates |
| UPDATE-OFFLINE-FAST | exe | a dead or hanging update server delays the start by seconds at most and touches nothing | a server that never answers, and a refused connection |
| UPDATE-BUTTON | exe | the home page's Check for updates button checks now, stages a newer mod, and reports an unreachable server | runs the button against a throw-away update server and checks each answer |
| LAUNCH-PARENT | exe | a program started under Steam has steam.exe as its parent | starts a harmless program that way and reads its parent process |
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
| SR-TIMER | game | the speedrun timer is real time (RTA) and exact | an injected clock (start, stop, 30 h), the format, and the real clock while the game is stopped |
| SR-SPLITS-ANY | game | Any% / NG+ split in order and end only on the computer switch | synthetic event streams: skips, repeats, a fade or a seed that must not end it |
| SR-SPLITS-ASS | game | All Star Seeds splits per seed and ends only on the final fade | synthetic event streams, and an absurd seed total kept to 16 splits |
| SR-SPLITS-100 | game | 100% ends on the last seed after everything else, an early last seed cannot finish | synthetic event streams in any order |
| SR-COMPARE | game | +/- against the PB, gold segments, sum of best, the timer colour | a run against a made-up PB |
| SR-RECORDS | game | PB and best segments survive, invalid runs are kept out, a damaged file falls back to its backup | round trips in a temp folder, a slower run, garbage text |
| SR-MENU-ROWS | game | every SPEEDRUN page fits the menu rules | the pure line builder for every page |
| SR-VALIDITY | game | Any% skin rules, assists, early last seed | the pure rules and a run fed an early seed |
| SR-LIVESPLIT | game | LiveSplit gets the commands in order and never slows the game | a local TCP listener, then nobody listening |
| SR-MENU | game | SPEEDRUN is reached from the game's own start menu | the real start menu line and the copied Settings menu |
| SR-START-FRAME | game | the timer starts on the NEW GAME frame, never without the slot | the real START RUN line, the game's own new-game flag, a fault injected |
| SR-CLOUD-OFF | game | no speedrun while Steam Cloud saving is on | the cloud flag forced on for one call, then the save files compared |
| SR-NEWGAME-FRESH | game | the slot game is a fresh new game with no skins for Any% | the real slot game |
| SR-EVENTS-LIVE | game | the game's own events reach the run in the same frame | real AddAccomplishment, AddShard and databank scans |
| SR-BACKUP-GUARD | game | no slot world is backed up over my real backups | the real backup call in the slot |
| SR-SKIN-GUARD | game | my co-op skin file is not used or changed by a speedrun | coop-skin.txt set to Ninja for a slot run |
| SR-PERF | game | the speedrun tick is cheap | 300 ticks in a running slot game, time and heap |
| SR-UI | game | the timer panel draws every state and fits every screen | OnGUI with sample runs, and the panel layout at 800x450, 1280x720 and 4K |
| SR-RESET-RESTORE | game | RESET RUN ends cleanly and gives my own save back | the real pause-menu action, runs.log, the records file and the save paths |
| SR-SLOT-ISOLATION | game | a speedrun never changes my own save | hashes of every save file and the backups before and after two slot runs with forced saves |
| SR-LEVER-END | game | Any% ends on the computer switch | the real lever's UnityEvent fired after the first seed |
| SR-100-END | game | 100% ends on the last seed after everything else | real crystals, databank scans, endings and AddSeed in the slot game |
| SR-ASS-END | game | All Star Seeds ends when the final fade is black | the real final sequence started by the game after the last seed |
| SR-RECORDS-LIVE | game | a finished run becomes the PB on disk | the records file and runs.log after the run |
| SR-GATE | game | race and co-op run messages are only taken at their exact size, the leader-only ones only from the leader, and older mods ignore them | every type at its size and one byte off, hostile fields, a stale run, a non-leader ARM, a PROGRESS flood, the old world path |
| SR-RACE-FSM | game | a race arms, waits for READY (15 s at most), counts down, notices silent racers and ranks the results | the race rules with made-up players: timeouts, version mismatch, stale messages, OFFLINE and DNF, ties |
| SR-RACE-LOOP | game | a whole race works: everybody gets their own slot, starts on the same GO, sees the others live, and gets the results | five fake players (one busy, one on an old mod, one going quiet) through the real gate and the real race code, with a fake clock |
| SR-COOP-TIMER | game | a co-op run has one timer (the leader's) that every member sees; members' splits count only from the leader's world | a fake leader and member: extrapolated time, events in and out of the world, duplicates, HOST LOST, the final time |
| SR-RACE-ISOLATION | game | during a speedrun nothing of anybody else's world reaches mine, and mine is never sent | plant, crystal, island, teleporter, pickup, world request, snapshot, epoch and have-world messages from a player through the real message routing in a slot game |

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
| UI-DUMP | game | the game's menus and HUD layout are logged, so the native co-op menu can be built from the real level data | the real GUIManager and HUD of the level |
| PARTY | game | the party on the start screen behaves like a party in other games: the leader starts, the members follow once, nobody is pulled back in after leaving | the rules the mod uses, and the menu lines for every party state |
| PARTY-MENU | game | the party lobby is a real menu of the game, reached from the start menu, and every party view fits on screen | the real start menu and the copied Settings menu, driven through their buttons, with full-party states |
| WORLD-PARTS | game | a friend joining a grown world really gets it (one Steam message holds only about 1 MB) | the mod's packer and part assembly, with damaged and foreign parts |
| LEADER-SAVE | game | everybody plays in the leader's save and nothing a guest does before that can damage it | the real start screen answering a request, the real backup, the leader/guest rules |
| PLANT-LEADER | game | a guest climbing an out-of-date copy of the beanstalk can never cut the leader's newer growth off | the real trunk in the real game, applied as if from a guest and as if from the leader |
| PARTY-LEAVE | game | a party never leaves a player stuck on somebody else's save, and leaving it really disconnects | the real save paths and the mod's player list |
| COOP-MENU | game | co-op is reached from the game's own pause menu, through a real game menu | the real pause menu and the copied Settings menu, driven through their buttons |
| HUD-MSG | game | co-op messages use the game's own M.O.M panel, falling back to the overlay when it cannot show them | the real HUD queue: shown, ordered, pushed-aside game message restored, busy panel, stopped game |
| NET-FX | game | the jetpack and flower/leaf parts of a pose packet survive the trip exactly, and cut-off or silly ones are refused | round trips with a flower, a leaf and held objects; every cut-off; both flags at once, NaN, far away, too many petals, a flag without its data; an old-layout packet |
| CLONE-FX | game | a friend's jetpack flame and flower/leaf show on their copy without any of the game's scripts, and never touch my fuel, flight lock or inventory | spawns a copy, switches the flame and flower/leaf on and off and lets the link go quiet; counts scripts, colliders and rigidbodies; compares my fuel, flight time, lock and inventory |
| PICKUP-SYNC | game | a flower or leaf a friend takes disappears here and grows back, but never goes into my inventory; mine is announced once, a dropped leaf never | wrong lengths at the gate, a made-up spot, bad types; the game's own pickup report; a friend's message for a real pickup of the save (a Note when the save has none) |
| CLONE-SCRIPTS | game | the friend's copy (a copy of MY walkster) runs nothing that reaches my BUD, HUD, save, input, camera or voice | lists every script on a fresh copy against a deny list (and any singleton); checks its guard; removes it and looks for its skin callback in the game's load queue |
| CLONE-VOICE | game | a friend jumping never makes MY BUD say the jump line; their copy says it | a real guarded jump of the copy with both voice sources silent first; reads which sound event each source plays one step later |
| CLONE-VOICE-RESTORE | game | the one physics step in which a copy jumps changes nothing of mine for good | compares the game's voice field and its leaf-jump and achievement hooks before and after; inside a real window adds the leaf achievement and plays the leaf foley on my body, then checks the achievement is gone unheard, the sound follows the copy and the rumble is cut (a Note when the save already has the achievement) |
| CLONE-IMMORTAL | game | a friend's copy bumping hard into something does not cut my voice or count a death of mine | reads the copy's immortal flag, clears it and waits for the driver to set it again, feeds the game's collision handler a 30 m/s bump and compares my death counters and voice |
| CLONE-NO-CHUNK | game | a venus trap, a ram or water cannot leave a friend's copy broken in my world | calls the game's chunker on a copy and checks it returns nothing and the copy is back, with driver and walker, 2 frames later |
| NET-FX-EVENT | game | a friend's voice lines, hard landings and deaths show on their copy, never on my BUD, and old players are not disturbed by the new packet part | round trips, every cut-off, wrong bank/names/lengths/counts/normals/distances/causes, prefix equality with the old layout, repeat and wrap-around handling; a relayed line, a landing on the ground under the copy (a Note when there is none), a death and a respawn on a spawned copy; my own voice line and three landing strengths through the real announcer |
