# Ongeki Discord Rich Presence

A BepInEx 5 (Mono) plugin that shows what you're doing in **ONGEKI** (SDDT) as
Discord Rich Presence. Dependency-free — it talks to the local Discord client
directly over its IPC named pipe; no external DLLs, no internet calls from the plugin.

## Install

1. Copy `OngekiDiscordRP.dll` into `Data/BepInEx/plugins/`
   (the build does this automatically via the `DeployToPlugins` target).
2. Launch the game once so the config is created, **or** use the pre-made
   `Data/BepInEx/config/dev.dyrox.ongeki.discordrp.cfg`.
3. Set `ClientId` in that config to your Discord Application (Client) ID from
   <https://discord.com/developers/applications>. Restart the game.
4. (Optional) Under your Discord app → **Rich Presence → Art Assets**, upload an
   image with key `ongeki` (matches `LargeImageKey`) so a logo appears.

## What it shows

| Game scene (`MU3.Scenes`) | Presence |
|---|---|
| `33_PlayMusic` | `♪ <title>` + `MASTER 13+  ·  <artist>` |
| `37_Result` | `Result: <title>` + difficulty |
| `32_PrePlayMusic` | Selecting a song |
| `20_Advertise` / `00_Launcher` | At the title screen |
| `25_Login` | Logging in |
| `40_SettingRoom` | In the setting room |
| `41/42_CardCustom*` | Customizing cards |
| `31_Story` / `st_*` | Watching a story |
| everything else | In the menus |

Elapsed timer counts from game launch (total session time).

## How it works

- No Harmony patching — every value is publicly readable, so a throttled
  `Update()` poll (default every 3 s) is enough and can't destabilize the game.
- Song/difficulty come from the global carrier
  `MU3.Sequence.PlayInfo.instance.sessionInfo` →
  `.musicData` (`name`, `artistName`, `fumenData`) and `.musicLevel`.
- Level string: `fumenData[difficulty].fumenConst` floored, plus `+` when
  `isFumenConstPlus`.
- State detection: `SceneManager.GetActiveScene().name` matched against the
  `MU3.Scenes.Names` table.
- `DiscordIpcClient` implements the minimal Discord IPC handshake + `SET_ACTIVITY`
  on a background thread (`\\.\pipe\discord-ipc-N`), reconnecting automatically
  if Discord isn't running yet.

## Build

Requires the .NET SDK. Targets `net35` to match the game's Mono runtime.

```bash
dotnet build -c Release
```

Override the game location if it differs:

```bash
dotnet build -c Release -p:GameRoot="D:\Games\SDDT_1.55\SDDT_1.55"
```

## Config reference

`Data/BepInEx/config/dev.dyrox.ongeki.discordrp.cfg`

- `Discord.ClientId` — **required** Discord Application ID.
- `Discord.UpdateIntervalSeconds` — refresh cadence (min 1).
- `Display.ShowArtist` — append artist while playing.
- `Display.ShowSessionTimer` — show the elapsed session timer.
- `Display.LargeImageKey` / `LargeImageText` — art asset key + hover tooltip.
