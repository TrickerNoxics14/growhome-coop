# Grow Home Co-op: security notes

What the program does on your PC, who it trusts, and what it does about it. Every point below is covered by a check in `audit/required-checks.txt`.

## Who can change what you run

- **Updates are signed.** The launcher only installs a new mod if the update description (version, SHA-256 fingerprint, download address) carries a valid RSA-3072 signature made with the author's private key. The matching public key is built into `GrowHomeCoop.exe`. The private key lives only on the author's PC (`%USERPROFILE%\.growhome-coop\signing-key.xml`), never in the repository. Breaking into the GitHub account is therefore not enough to push code to players. (`UPDATE-SIGNED`)
- A signature cannot be reused for another file, another address or a higher version number (all three are signed), and an older signed update never replaces a newer mod. (`UPDATE-SIGNED`, `UPDATE-NO-DOWNGRADE`)
- Downloads come only from GitHub over https, are not followed to other hosts, and are limited in size. (`UPDATE-SIGNED`)
- A downloaded update is first stored next to the game, checked again against its fingerprint when it is installed, and the old mod is kept as `.bak`. (`UPDATE-STAGED-APPLY`, `UPDATE-STAGE-TAMPER`)
- Update checks can be switched off completely (`coop-noupdate.txt`). (`UPDATE-BACKGROUND`)

## Who can talk to your game

- Only people in your Steam lobby (they know the invite code) can connect; anyone else's connection request is ignored.
- Everything another player sends is checked before it is used: world snapshots are accepted only from the host and only up to 2 MB, plant, island and crystal events are validated (checksum, sizes, sane values), pose packets with impossible numbers are dropped, and none of it can crash or stall the game. (`GATE-ROUTING`, `MSG-FUZZ`, `NET-SANITY`)
- A guest's own save is never touched: in someone else's world every save goes to a throwaway folder. (`GUEST-ISOLATION`)
- The mod's log cannot be used to fill your disk. (`NET-SANITY`)

## What it never touches

- Your normal Grow Home install, your real Steam saves and Steam Cloud: the mod runs in a separate copy with its own saves. (`ORIGINAL-UNTOUCHED`, `SAVE-PATHS`)
- Steam's own settings are changed in one place only (the Grow Home launch option, with a backup and only after asking), and the edit is checked to change nothing else. (`STEAM-LAUNCH-OPTION`)

## What is trusted on purpose

- **The host.** The player who makes the invite code decides the world everyone else plays in. Only join codes from people you trust. Guests are protected from losing their own save, but a hostile host could still send odd world data to the game's own loader.
- **Anyone who has the invite code** can join until the lobby is full. Do not post the code publicly.
- **Your own PC.** Files next to the program (`coop-*.txt`) are settings; someone who can write to your user folder can already run programs as you.

## Keeping the signing key safe

- Do not copy `signing-key.xml` to a USB stick, chat or the repository. If it is lost, no new update can be published to launchers that carry the old public key: publish a new `GrowHomeCoop.exe` with a new key and ask players to download it again.
- If it is ever leaked: generate a new key, publish a new `GrowHomeCoop.exe` containing the new public key, tell players to download it.

## Reporting a problem

Open an issue on the repository, or message the author privately for anything that could harm players.

## Rooms, join requests, chat (v4)
- A room's data (mode, name, accepted and refused players) is written by the room's owner and read as untrusted text: lists are length- and count-capped, numbers parsed strictly, names cleaned like player names; anything unknown counts as private.
- In a private or "you accept" room nobody is connected to until the host says yes (Y/N). Only the player who owned the room when you joined is the host: if Steam hands the room to somebody else, the room closes for you, so a waiting player can never accept themselves.
- Chat shows the sender's real connection name, plain text, 100 characters, 5 messages per 6 seconds.
- Joining somebody else's public room means playing in their world: a world snapshot is accepted only from the room's host and is size- and checksum-checked; join only rooms you trust.

## Hardening (v5 review)
- Host: only the room owner I joined counts as host (recorded when I joined or made the room). If Steam hands the room to someone else, nobody new becomes host. Without a room, the configured players are used, as before.
- Who is heard: once I have joined or made a room, only players the room currently admits are heard (chat, positions, plants, islands, world data). A player who left or was refused is heard by nobody.
- Credit: plant and island updates are credited to the connection they arrived on, so a player cannot claim someone else's name inside a message.
- Steam Cloud: a cloud file name that could leave the save folder (.., rooted, drive letters, back-slashes) is refused.
- Known limits, not changed: the LAN fast path (coop-lan.txt, opt-in) is plain UDP and unauthenticated; only use it on a network you trust. Local test files (coop-selftest.txt and similar) work only for someone who can already write to the game folder.
