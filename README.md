# Grow Home Co-op (unofficial, experimental)

A fan-made co-op mod for the Steam game **Grow Home** (Steam build 857617). Up to 8 players see each other, share crystals and the big plant, and join with a short invite code.

**Download:** get `GrowHomeCoop.exe` from the [latest release](../../releases/latest) and double-click it. (A single-file `GrowHomeCoop-Setup.bat` is there too.)

- It makes a **separate patched copy** of Grow Home in `%USERPROFILE%\GrowHomeCoop`. Your normal game and save are never touched.
- You must own Grow Home on Steam. Nothing from the game is distributed here.
- Every time you start it, it checks `version.json` in this repository for a newer `CoopMod.dll`, verifies its SHA-256 and swaps it in.
- In game: **F2** makes an invite code, **F3** types one, **F1** hides the mod's display.

Windows may warn about an unsigned download ("More info", then "Run anyway"). Everything the installer does is in `installer-src/Program.cs`; the mod is in `mod-src/`.

This project is not affiliated with Ubisoft. Source is MIT licensed (see LICENSE). Building the mod needs `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll` from your own game copied next to `CoopMod.csproj` as `Assembly-CSharp.ref.dll` and `Assembly-CSharp-firstpass.ref.dll`; never commit those.
