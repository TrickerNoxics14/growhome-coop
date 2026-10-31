# Grow Home Co-op: critical checks

One command runs everything, on this PC, with nobody playing:

```
powershell -ExecutionPolicy Bypass -File audit\run-audit.ps1
```

It builds the mod and the launcher, starts the real game in its own copy (the game presses CONTINUE by itself), runs every check below
inside it, closes it, puts your saves and settings back exactly as they were, and prints PASS / FAIL for each check.
Exit code 0 means every critical check passed in a full run; 1 means something did not; 3 means a partial or quick run (not a release gate); 2 means Grow Home was running and nothing was touched. The newest report is `audit\latest-report.md`.

Options: `-Quick` (shorter, for development, never a release gate), `-Quick -Groups clone,fx` (QUICK AUDIT of only those groups - world, plants, party, net, clone, fx, ui, speedrun, session, movement, story, creatures, launcher - see `audit\check-groups.txt`; it prints "QUICK AUDIT (not a release audit)" and never writes `last-full-pass.md`), `-SkipGame`, `-SkipExe`, `-KeepBuild`, `-ForceClose`
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
| LAUNCH-VERIFIED | script | the launcher window starts the co-op copy, confirms the mod loaded within 60 s and closes by itself | the audit opens the window with auto-play, exactly like a friend pressing Play; the launcher log shows steam_appid.txt, the mod pin and the Steam gate before the start, in that order |
| LAUNCH-VERIFY | exe | the launcher tells apart: mod loaded, regular game started instead, nothing started, game closed again, mod missing | stand-in game processes and a log file the launcher has to judge |
| RUNNING-GAME | exe | a regular Grow Home that is already running is told apart from the co-op copy | stand-in processes running from different folders; the launcher compares real program paths |
| BORROWER-DETECT | exe | a Steam account that only borrows the game (Family Sharing) is recognised, so it is started through Steam | install manifests with the owner's and other accounts, plus this PC's real manifest |
| STEAM-LAUNCH-OPTION | exe | Steam's launch option that makes Play start the co-op copy is edited safely | edits in memory only: exact read-back, exact undo, damaged files refused, Windows line endings kept, this PC's real settings files |
| INSTALL-FAST | exe | the install links the big data files instead of copying: seconds, no extra disk; never links what the game or patcher writes; deleting the copy never touches the original | a fake 130 MB game folder, hashes before and after, free disk space before and after; a read-only original that the copy links to stays read-only after the copy is deleted |
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
| SR-UI | game | the timer panel draws every state natively and fits every screen | sample runs laid out on the real uGUI HUD canvas (fonts, screen corners, texts), and the pure panel layout at 800x450, 1280x720 and 4K |
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
| MOUSE-FREE | game | the mouse works on the native co-op pages opened in the world | the real mod frees the cursor against a game that locks it |
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
| HOTKEYS | game | F2 and F3 open the room pages as pages of the game's own menu, also over the pause menu | the real key handler, the real GUIManager menu stack and input sets |
| MAKE-ROOM-MENU | game | making a room is a native menu page, not a window drawn over the game | the copied Settings menu driven through the input module's submit event; Steam replaced by a sink |
| JOIN-ROOM-MENU | game | joining by code or from the room list is a native menu page with a real text field | keyboard events fed to the uGUI InputField; listed rooms and Steam replaced by test data and a sink |
| TEXT-ROW | game | the code line looks and acts like the game's own menu line | a raycast through the real EventSystem at the line's centre |
| JOIN-REQUEST-HUD | game | a join request appears in the game's HUD style, not as an overlay | the mod's HUD canvas built from the real AbilityHUD; the answer goes to a sink |
| CODE-NOTICE | game | the new invite code is told by M.O.M, not on a drawn card | the real NativeUI message queue, the clipboard and my-invite-code.txt |
| NO-IMGUI | game | nothing of the mod is drawn over the game any more: every screen piece is the game's own menu or uGUI on the game's GUI camera | reflection over every script of the mod for OnGUI, every call in the mod's compiled IL resolved and checked against GUI, GUILayout, GUIStyle, GUIUtility and GUISkin (Event.PopEvent as a control), and a watch on the game's own GUIUtility.s_OriginalID at the end of every frame of the whole audit: only the game's OnGUI ever ran, at least 120 frames each on the start screen, in the world, in the pause, with the chat open, with the join page open and (with speedrun) during a run; the game's own passes seen there prove the watch works |
| HUD-CANVAS | game | the co-op HUD is part of the game's own GUI, not an overlay | the live canvas under the real GUIManager: camera, sorting, raycasters, the font of every text, every piece on it |
| LINK-LINE | game | the old status panel is a single native line that shows only when there is something to report, and the keys are in the CO-OP menu | the pure line for solo, host, peers, connecting, notices; the live uGUI texts with a test state; a real menu opened over it; the pure CO-OP rows |
| MOM-MIRROR | game | messages the game's M.O.M panel cannot take still reach the player, natively | the live HUD texts and art after a fallback message; the real co-op menu's subtitle; F1; the 6.5 s life in real time |
| CHAT-HUD | game | chat is typed into a native uGUI field that never lets keys reach the game, and always closes | T through the real key handler; key events fed to the real InputField; the real input sets and event system selection; the live chat log texts |
| NAME-TAGS | game | the tags over other players are native and follow the player | a test player in front of and behind the real game camera; the tag's screen corners against WorldToScreenPoint; a real menu |
| F1-HIDE | game | F1 still hides the whole co-op display | every live piece with test content, F1 on and off |
| NATIVE-FONT | game | the co-op pages and HUD read like the game: its own font everywhere, times in its fixed-width digits, no missing letters | every Text under the co-op menu (all pages, pooled lines too) and the co-op HUD compared with AbilityHUD's font object; the speedrun preview's times in block_fixed; HasCharacter for every letter shown; a typed q7 arrives as Q7 |
| MENU-NAV | game | the co-op pages work with the keyboard arrows, the stick and the d-pad exactly like the game's menus | the CO-OP, MAKE A ROOM and JOIN A ROOM (5 rooms) pages walked down and back up with the game's move events (ExecuteEvents.moveHandler, as its input module sends them): the selected line ids in order, member lines skipped, no wrap, the selection inside the menu; every line's animator, transition and navigation equal the game's template line |
| PAUSE-OVER-COOP | game | pausing and resuming with a co-op page open never leaves the game stuck (keys muted, mouse held, a menu left over) | the game's own pause (PauseManager's focus-loss path) and resume (ResumeRequested) over the join page while typing, while not typing, and with the page stacked on the pause menu: menus, typing, InputManager stack depth, WantMouse, the cursor tracker, the page and timeScale all back |
| HUD-LAYOUT | game | no co-op piece covers the game's HUD or another co-op piece, and none runs off the screen, on a small and a large window | each piece alone with long test content (link line, chat log, SAY> line, M.O.M messages, join request, timer and race strip, result card, countdown, name tags far to the side): every shown graphic's glyph box in screen pixels inside the screen, clear of the game's M.O.M side panel (forced up), altitude, islands, jetpack, seeds, upgrades, data bank, controls, warnings and crystal finder panels and of the earlier pieces; the countdown clear of the pause menu's title and lines; at 800x450 and 1920x1080 (the window resized and put back), pure rects at 4:3, 16:10, 16:9, 21:9 and 4K |
| MENU-NOTICE | game | a host sitting on the start menu still sees who wants to join, the new room's code and a failed join | the real start menu and its copied notice line, the join request through the real Y key, the invite code through the real M.O.M routing, a refused join through the lobby's own handler |
| MESSAGES-KEPT | game | no message from co-op or another player is lost while a game menu is up | the real pause menu's notice line, the real Settings menu held over 15 s, then the live HUD texts of the M.O.M panel and the chat log |
| PAGE-ESC | game | Esc closes a co-op page instead of pausing the game, and BACK returns to the screen the page was opened from | the page opened by its real key, the handler's own Esc path and the game's input stack, PauseManager and the menu stack read afterwards |
| TYPING-GUARDS | game | typing a code or a chat line can never be taken over by the mouse, a cutscene or the menu's sounds, and T never breaks the pause menu's keys | the real code line and EventSystem selection, the real cancel-sound flag of the copied menu, a cutscene input set pushed on the game's own InputManager, T through the real key handler |
| LINK-FIT | game | the room code and world notices on the B.U.D. LINK line never vanish because many players are linked | the pure layout function the HUD draws with, 7 long names and a long notice at two widths |
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
| HANG-GUARD | game | a frozen game never leaves a black screen: mouse back, minimized, ended once after the limit; loads get longer; sleep is no freeze; the game runs windowed | fake-clock watchdogs (no frames, frames then freeze, a 5 min clock jump), the live watchdog's limits, the screen rule cases and the live window |
| QUIT-GUARD | game | Save & Quit comes back when the block counters leak; real blocks are kept | a leaked block with the pause menu open and closed, a test reason that must be kept, a real growing trunk removed; a pause menu that closes by itself during the wait is noted and opened again, never read as a failed repair |
| PAUSE-ROWS | game | the pause menu always has a working Save & Quit and every row fits | the live pause menu with and without an active run |
| LEADER-LEFT | game | guests go back to the start menu when the leader leaves its world (says so, leaves the party, back to the start screen, or its heartbeat stops advancing) | 13 pure cases of the leader rules, the message gate, and a live guest stay ended by a fake leader's leave message; a leave message that arrives as the guest is out of the world for a frame (death) is kept and kicks once it is back |
| MOVE-WIRE | game | the pose packet says when it was sent, from which run of the game, and whether the player is gone, climbing, in the air or just teleported, without disturbing old versions | round trip with flower, held object and event; byte-for-byte old layout around the block; every cut-off; short, empty-session and contradictory blocks; newer, longer and foreign blocks; the gone heartbeat; the sender's teleport rule (run, fall, hitch, 40 m jump, respawn, first packet) |
| MOVE-BUFFER | game | a friend's packets give a smooth path whatever order they come in | out-of-order and repeated packets, the 32-packet limit, the clock difference from the fastest packets, the delay on a steady and a jittery link, blending between packets, the 250 ms guess-ahead cap and one jump per teleport mark; a guessed fall stops at the ground under the copy when packets stop at a landing, and falls on over a deep drop |
| MOVE-SESSION | game | a friend who restarts the game is not ignored (the old bug: their copy stood still forever) | a new session with packet number 1 after number 500, the buffer reset, old-version ordering |
| MOVE-SIM | game | copies move smoothly over bad links | 7 scripted paths (run, circle, start/stop, jump arcs, 30 m fall, climb, respawn) over 6 links (perfect, 150+-50 ms jitter, 5% loss, 300 ms bursts, reorder + duplicates, 30 fps) plus a 2 s gap and a restart, through the real packet code; each frame: error against the truth (avg < 0.3 m, worst < 1.5 m), frame step (at most 2 x speed x frame + 0.1 m), stand-stills, unmarked jumps, delay |
| MOVE-FAR | game | the "teleporting" bug: a friend far from my camera is no longer frozen by the game and jumped 14 m at a time | a live copy 40 m behind the camera fed a 6 m/s run for 4 s: never kinematic, never still, at least 85% of the distance, no jump, error avg < 0.6 m |
| MOVE-NEAR | game | a friend next to me follows smoothly with one pull | a live copy 4 m in front fed a 2 m/s walk: never frozen, no jump, error avg < 1 m, worst < 3 m |
| PLANT-ONPLANT-BODY | game | a guest riding a branch is protected from leader updates (the old check measured from the Walkster object at the level start, so it never was) | the measuring point against the body, a body next to the farthest stem and one 5 km away, a branch marked as ridden |
| PLANT-GROW-LEASE | game | guests can press E on a nub and grow a vine: the leader takes it instead of refusing it as "built on an older copy", and still never lets a guest cut a leader vine off | the real plant through Handle: a lease, the leader's own copy changing during it, the guest's plant on an old number, nothing sent meanwhile, the end and the pass-on (newer number, echo to the grower); a copy older than the leader's newest vine; a second grower |
| PLANT-LEASE-NOREVERT | game | the "pressed E, it grew and snapped back" bug | as a guest: a popped bud, the leader's older copy arriving mid-ride (length, parts, number unchanged), the final plant then the end, a revoke by the leader and one by somebody else |
| PLANT-LEASE-ISLAND | game | a guest connecting an island counts in the leader's world and save | a guest plant with an island attached that the leader's copy has not: attached afterwards, counter +1 through the game's own connect step |
| PLANT-LEASE-TIMEOUT | game | a guest that drops mid-ride cannot keep the leader's plant frozen | a lease aged past 45 s: cleared, the leader's copy sent, an old guest copy refused again |
| PLANT-GUEST-QUIET | game | guests no longer send their trunk creeping on (the 135-part rebuilds on the leader) | a guest copy changed without a vine, 4 s of ticks |
| PLANT-COLLIDERS | game | plants always keep collision after the mod updates them | the real plant updated in place, rebuilt (injected failure) and pruned (one stem left out): colliders within 40 m on after one guard tick, the same layer/kinematic kinds as before, no dead links in branches or in the game's collider queue |
| LAUNCH-WINDOWED | exe | the game always starts in a window | the argument builder, Steam's launch option text and the hang note |
| STEAM-READY | exe | the game starts only on a ready Steam | the readiness rule with stale pids, no web helper and a fresh Steam |
| LAUNCH-HANDOFF | exe | a regular game Steam starts in place of the co-op copy is closed again | a stand-in regular game appearing after a direct start |
| SINGLE-INSTANCE | exe | one launcher at a time | the lock taken from two threads |
| NO-FOCUS-STEAL | exe | the launcher never covers the game | the window decision for every case |
| HANG-NO-FALSE-KILL | exe | a healthy game is never killed as "frozen" | the 10-sample rule, a left-over hang note with no frozen game, the safe default answer; in the window Escape, N and Enter on the frozen-game question leave the game running (only Y or "Close it" closes it); after a frozen co-op copy is closed no question about a regular game follows |
| UPDATE-ATOMIC | exe | the mod swap is all or nothing, never while the game runs | failures injected half way, a stand-in co-op game while swapping, staging and reinstalling |
| INSTALL-IN-USE | exe | a co-op copy in use is never half deleted | a folder with a file held open |
| CS-WIRE | game | a broken or hostile story message can do nothing | round trip, cut-off, padded, unknown op, oversized; the gate's route |
| CS-FLAG-LIST | game | a copied flag never says "already seen" before the cutscene played, and personal flags stay personal | the never-copy list, personal flags, story flags; the game's private cutscene, text, nutrient and lever fields |
| CS-FLAG | game | "if someone got a story flag, everyone gets it", only from the leader | as a guest: a personal flag refused, a guest's flag ignored, the leader's added once (the game's event fires once), no echo, my own flag sent once |
| CS-RELAY | game | a guest's story progress reaches the leader's save and the other guests | as the leader: a guest's flag taken and passed on once with the guest as origin; a player not in my world and a guest's skip ignored |
| CS-SR | game | speedrun splits and races are not disturbed | a friend's flag through the run tap: no split (my own: one); a racer holding sends nothing |
| CS-CENSUS | game | the cutscenes are found by name | every CutsceneController logged with its done-flag, length, animation; done-flags unique |
| CS-MIRROR | game | "everyone gets the cutscene, not just that player" | a real cutscene's flag cleared, the leader's start applied: it runs here, marked as a friend's, nothing sent back |
| CS-SKIP-AUTH | game | "if the leader skips, it skips for everyone"; a guest's skip stays local | a guest's skip: still running; the leader's: ended within 8 s and marked seen |
| CS-BLOCKS | game | no Save & Quit / pause leak from synced cutscenes | quit block, pause block, input stack, time scale and the player's frozen state equal to before |
| CS-LOCAL | game | my cutscene and (as leader) my skip reach everybody | the game's own SignalTrigger: one start out; the skip Jump does: one skip out; everything given back |
| CS-ORDER | game | late or out-of-order messages leave nothing stuck | a start that cannot run waits; the leader's skip first cancels it (seen, not played); a start that never can is marked seen after the wait |
| CS-SCROLL | game | the leader's skip of the computer's text skips it for everyone | a guest's text skip ignored; the leader's waits for the text, applies when skippable (the computer's own scroller, restored after); my own skip told once |
| CS-OUTRO | game | the computer endings play for everybody | an unknown ending refused; a lever pull told once as OutroStage picks it; an already seen ending changes nothing |
| CS-NUTRIENT | game | the first island cutscene plays for everybody, once | seen already: nothing replays or freezes; not seen: waits while it cannot show |
| CREATURE-WIRE | game | a broken or hostile creature message can do nothing, and older versions are not disturbed | pose, list, death, trap round trips; cut-off, padded, wrong version/count, NaN, id 0, long names; the gate (leader only, exact sizes); an old reader sees a pose as nothing; no sync in a race or outside the leader's world |
| CREATURE-BUDGET | game | creature sync never floods a guest's connection | 200 creatures near each of 3 guests for 10 s: bytes per second under the 8 KB/s cap (and under a lowered 2 KB/s cap), packets at most 24 creatures / 1200 bytes, every creature sent |
| CREATURE-INTEREST | game | guests get the creatures that matter to them | 30 m: about 10 per second; 120 m: about 1 per second; 200 m: never |
| CREATURE-CENSUS | game | the game fields the sync relies on exist in this game build | freeze inhibit, respawn queue, venus trap end, flying sheep grab count |
| CREATURE-LEADER | game | "sheep in the same place for everybody": the leader sends what it runs | a fake guest next to a real creature: the list has it, about 10 poses a second, the last pose where it is, nothing beyond 150 m, packets under 1200 bytes, nothing to a bot |
| CREATURE-AWAKE | game | a creature next to a guest but far from the leader is not frozen | the walker furthest from my camera, kept awake for 2.5 s with a guest next to it |
| CREATURE-FLYSHEEP | game | achievements stay each player's own | a friend's copy's grab put into the flying sheep counter is gone again within a frame |
| CREATURE-CLAIM | game | a guest can carry a sheep; nothing a stranger sends moves the leader's creatures | a stranger and a far guest refused; a guest's held pose followed with the brain off; brain back after letting go |
| CREATURE-PUPPET | game | "guest AI stays off": guests show the leader's creatures, not their own | the leader's list mapped, every brain off, a spare switched off, a 1.5 m/s walk followed (average error under 1.5 m); a stranger's or another world's list and poses refused |
| CREATURE-DEATH | game | a creature eaten or destroyed on the leader disappears on the guest, once | the leader: death sent, list says dead, poses stop; the guest: its own game's chunking blocked, the leader's death chunks it, a late pose ignored, the replacement shown where the leader says with its brain off |
| CREATURE-CLAIM-GUEST | game | the guest holding a creature moves it and tells the leader | not steered while held; poses to the leader marked held |
| CREATURE-TRAP | game | venus traps destroyed by the plant are gone for everybody | the leader tells a trap's end; the guest runs the game's own trap end; a stranger's word ignored |
| CREATURE-RELEASE | game | races and leaving a party give the game back its own creatures | brains on, spares back, no mod script left, the leader's poses ignored |
| STEAM-APPID | exe | steam_appid.txt (323320) is in the co-op game folder before every start | a throw-away folder: missing file created, wrong value repaired, right value left alone |
| LAUNCH-ONLY-COOP | exe | the launcher never starts the regular GrowHome.exe next to the co-op copy | the allowed-exe rule over the co-op, regular and stray paths; a regular game that comes up next to ours is closed; a refused start; Start() and Install() on a co-op folder that is the regular game's write nothing there |
| HANG-MINIDUMP | exe | a hung game leaves a minidump in the logs folder before it is closed; only the last 2 are kept | a real dump of a fake hung process (MiniDumpWriteDump), then pruning to 2 |
| MOD-PIN | exe | a mismatched or old CoopMod.dll is replaced, never used | the pin verdict over every case; a wrong dll in a throw-away game folder replaced by the bundled one with a .bak, a newer pinned update kept; a missing CoopMod.dll is put back by the start (the flow and the Play button do not reinstall), and the co-op save survives |
| STEAM-STALE | exe | Steam whose client is up and signed in while its active process stays unset or dead is never used for a start; the user is told to exit Steam fully and offered a restart; a Steam that is still starting is waited for | the pure state rule over fake pids, ages, web helpers and sign-in (never the real registry); the gate on a fake clock: cancel, restart, still stale, ready, not running, updating for 40 s, a crash's old pid, not signed in, a 2 s blip; the restart plan is -shutdown then start |
| START-GATES | exe | the real start checks steam_appid.txt, the mod pin and a stale Steam, in that order, before anything is launched | Start() itself in a throw-away folder: an unwritable steam_appid.txt stops it before the mod pin and Steam are touched; a stale fake Steam (Cancel) stops it after both ran, in log order, with nothing launched |
| LEADER-BEAT | game | leader gone is decided from a heartbeat only the leader advances, not packet silence | the beat wire and its order, 4 guest-watch cases (pings without beats count as gone), a live beat sender, the receive path with pings, replays, beats and poses, and the live guest stay with a frozen fake beat; the beats the real send loop sends to a real (not bot) stand-in player are counted and in order; the guest watch reads the real player's counter in the peer list |
| LEADER-LEFT-SAVE | game | a guest kicked because the leader left keeps its own save byte for byte | file hashes of the guest's own world before entering the guest stay and after the kick |
| QUIT-DOOMED | game | a plant part destroyed this frame stops blocking Save & Quit at once and is never referenced by the guard | a real growing trunk doomed: holds drop by one, no second give-back, no static branch field (reflection), cleared the next frame |
| QUIT-GUARD-LOG | game | the quit guard's counters are in the log about once a minute, never spam | number of counter lines against the run time, closest two lines at least 55 s apart |
| HOLLOW-PASS | game | a test that stops early or relies on a sender that never advanced FAILS | every section ran through a guard (BEGIN/END, aborted ones stay open), required ids per section, SENDER lines, the scenario end |
| ONLY-COOP-EXE | script | only the co-op GrowHome.exe ever runs during the launcher flow and the game stage | every GrowHome process path sampled 5 times a second while the launcher runs, then through the game stage; the launcher log of the run must not name a regular game it closed (one that lived between two samples) |
| REAL-DATA-UNCHANGED | script | the owner's real save, save-keep and the game's registry values are unchanged by the whole run | hashes before the run and at its end; the registry values are backed up first and put back after the game |
| RESULT-COMPLETE | script | the mod's result file is whole, never a hollow pass | the scorer first fails 8 made-up hollow files (missing, cut off, open or aborted section, stuck or missing sender, wrong count, early exit) and accepts a whole one, then checks the real file |
| THREAD-SAFE | exe | a crash on a launcher thread can never end it with a Windows dialog on the player's screen | a thread and a pool job that throw on purpose are caught and logged, fake games start right after their copy, the error mode (no fault box) and the unhandled-exception exit are on, no thread faulted in the whole launcher audit; an exception in a posted action on the window's thread reaches the launcher's own handler (no WinForms dialog) and a frame that cannot be drawn is skipped |
| NO-CRASH-EVENTS | script | neither the launcher nor the game crashed during the run | the Application event log since the start of the run: no Application Error, .NET Runtime, Application Hang or WER event naming GrowHomeCoop.exe or GrowHome.exe |
