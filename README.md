# STRAFTAT x TF2 Rockets

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **STRAFTAT** that turns every round into a Team Fortress 2
rocket-jumping arena:

- Two game modes, picked by the host in the F8 panel:
  - **Rockets**: 1 rocket launcher (TF2 damage), 3 Market Gardener.
  - **Jump & Snipe**: 1 Rocket Jumper (rockets launch you but never hurt anyone), 2 sniper (STRAFTAT's M2000 scope,
    two body shots or one headshot), 3 Market Gardener.
  Switch weapons with 1/2/3 or the scroll wheel. The Market Gardener (left-click) crits for a one-hit kill while you
  are rocket jumping, like in TF2.
- **Final kill cam**: the kill that wins a set plays a TF2 freeze-cam style shot for everyone: slow motion around the
  falling ragdoll, a fly-in to the winner with TF2's freeze cam sound, a freeze frame with the snapshot sound and a
  FINAL BLOW banner. Turn it off or preview it solo in the F8 panel.
- No weapon pickups, infinite ammo, no self-damage.
- Rockets follow TF2's numbers: 1100 u/s, 0.8 s between shots, 146-unit blast, 90 base damage with TF2's
  ramp-up/fall-off and splash.
- Blasts push you with TF2's knockback formula (air and ground scales, the crouch bonus, the 1000 u/s cap), so
  crouch-jumps, ctaps and jump-and-fire rocket jumps work like they do in TF2.
- While a rocket has you airborne you move with TF2's air physics (gravity, air strafing at TF2's 66-tick rate),
  then land with TF2's friction, or keep sliding if you hold crouch.
- STRAFTAT's maps, rounds, lobbies, wall jumps and slides stay; movement speeds are brought into TF2's range so rocket
  PvP stays playable.
- Your launcher shows **The Original**, with its sounds. Press **F8** in game to pick any TF2 rocket launcher model,
  the rocket model your shots fly as, the sounds, your sniper (six TF2 rifles or five STRAFTAT guns), the scope's
  opacity, your Market Gardener's look (any TF2 or STRAFTAT melee weapon), a colour, opacity, an unusual-style
  effect, and to move/turn/scale each weapon in your view. Press **V** to flip your weapon (each press restarts it).

No files from STRAFTAT or Team Fortress 2 are in this repository or its releases. The TF2 models, textures and sounds
are read from **your own TF2 install** each time the game starts.

## What you need

- STRAFTAT on Steam (Windows).
- Team Fortress 2 installed on Steam (free) for the TF2 launchers, rockets and sounds. Without it the mod still plays,
  with STRAFTAT's own launcher look.

## Install

1. Download `StraftatTF2Rockets-<version>.zip` from the [latest release](../../releases/latest) and unzip it anywhere.
2. Double-click `install.bat`. It finds STRAFTAT through Steam, installs BepInEx 5.4.23.5 (downloaded from BepInEx's
   official GitHub release and checksum-verified) if you do not have it yet, and copies the mod in.
3. Start STRAFTAT from Steam.

Manual install: put [BepInEx 5.4.23.5 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) into the STRAFTAT
folder (next to `STRAFTAT.exe`), then copy the release's `BepInEx` folder into the same place.

### Playing with friends

Everyone in the lobby needs **the same version** of the mod. STRAFTAT only matches players whose mods are identical, so
modded players get their own lobbies and never join vanilla ones. The host gives everyone a launcher each round;
damage and knockback are worked out the way STRAFTAT handles its own weapons online.

The F8 look options (model, rocket, sounds, colour, effects, viewmodel) only change **your own** screen. Other players
see The Original and TF2's rocket.

### Uninstall

Delete `BepInEx\plugins\StraftatRockets` from the STRAFTAT folder. To remove BepInEx too, also delete the `BepInEx`
folder, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version`.

## Settings

`BepInEx\config\melty.straftat.tf2rockets.cfg` holds everything the F8 panel saves, plus `Paths.TF2Folder` if TF2 is
somewhere Steam does not know about (point it at TF2's `tf` folder).

## Building from source

The plugin compiles against your own STRAFTAT install (its `Managed` DLLs and BepInEx core), which are never copied
into the output.

```
dotnet build StraftatRockets -c Release -p:StraftatDir="C:\Program Files (x86)\Steam\steamapps\common\STRAFTAT"
```

Requires the .NET SDK (6 or newer); the project targets .NET Framework 4.7.2 through reference assemblies.

The design lives in `sheets/` as JSON (TF2 values, rules, Harmony hooks, models, tuning). `python tools/gen_sheets.py`
turns the sheets into `Sheets.g.cs` and `Models.g.cs`; edit the sheets, not the generated files.

`tools/export_models.py` is a developer tool that exports the same TF2 models to files for offline checks; players do
not need it.

## Credits and notes

- STRAFTAT is made by Lemaitre Bros. Team Fortress 2 is made by Valve. This mod is not affiliated with or endorsed by
  either; all game content stays in the games you own.
- Built with BepInEx and HarmonyX.
- Released under the MIT License (see `LICENSE`). This covers the mod's own code only.
