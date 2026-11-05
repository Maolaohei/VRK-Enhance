# Install / restore

Everything here is reversible by name: the installer records what it did in `state/manifest.json`, backs up anything it overwrites into `state/backup/`, and `-Mode restore` undoes exactly those steps.

> 中文版：[zh-CN/INSTALL.md](zh-CN/INSTALL.md)

---

## 0. Before you start

| Requirement | Note |
| --- | --- |
| VR Kanojo installed | Steam version, game folder containing `VR_Kanojo.exe` |
| BepInEx 5.4.x | Ships with the game folder in most distributions. If `BepInEx/` is missing, install BepInEx 5 x64 first |
| Windows PowerShell 5.1 | Built into Windows 10/11 — nothing to install |
| .NET 8 SDK | Only if you build the plugins yourself |

Close the game before installing. The installer will refuse to overwrite a DLL that is locked by a running process.

## 1. The graphical panel (easiest)

Run `安装面板.cmd` (installer panel) from the package folder.

```
[ VRK Enhance - installer / restore ]
 Tick a component, press Install selected. Undo with Restore selected.
 First time: Recommended.

 Components ([on] = currently active)
   [x] No-VR core (white screen / camera / keys / clicking / crosshair)   [on]
   [x] FSR1 upscaling (EASU + RCAS)
   ...
   [ ] Launcher (SHA-1 crash fix + SteamVR preflight + two entries)

 [All] [None] [Install selected] [Restore selected] [Recommended] [Pick folder...] [Close]
 [ ] Dry run (DryRun: show what would change, touch nothing)

 log ...
```

- The status bar shows the auto-detected game folder; if detection fails it turns red and *Pick folder...* lets you choose one for this session.
- *Dry run* prints the exact file operations and touches nothing.
- The panel only collects a selection and forwards it to `tools/setup.ps1` — backup/restore bookkeeping lives in one place.

## 2. One-click scripts

| Script | Meaning |
| --- | --- |
| `安装面板.cmd` | Graphical installer |
| `配置面板.cmd` | Graphical config editor for the six `vrk.*.cfg` files |
| `一键安装.cmd` | Interactive menu in the console |
| `安装推荐.cmd` | core + upscale + perf + prewarm + textures + launcher |
| `安装全部.cmd` | Every component |
| `一键还原.cmd` | Restore everything that is recorded, with a confirmation |
| `还原全部.cmd` | Same, no questions |

## 3. Command line

```powershell
# what is available / what is installed
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -List

# preview
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components all -DryRun

# install
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components flatfps,upscale -Yes

# uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Mode restore -Yes
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Mode restore -Components upscale -Yes
```

| Switch | Effect |
| --- | --- |
| `-Components a,b,c` | Component ids or titles; `all` means everything |
| `-DryRun` | Print the plan, change nothing |
| `-Yes` | No confirmation prompt |
| `-GameDir <path>` | Force the game folder |
| `-NoShortcut` | Do not create desktop shortcuts |
| `-ShortcutDir <path>` | Put the shortcuts somewhere else |
| `-Lang en\|zh\|auto` | Interface language (default `auto`: `VRK_LANG`, else the Windows UI language) |
| `-List` / `-ListFile <path>` | Dump the component table as JSON / as tab separated lines |
| `-EchoGameDir` | Print only the detected game folder |

### How the game folder is found

1. `-GameDir` if given
2. The package folder itself (if `VR_Kanojo.exe` is next to it)
3. Up to four parent folders
4. Steam: `SteamPath` / `InstallPath` from `HKCU|HKLM\Software\Valve\Steam`, then every `steamapps/libraryfolders.vdf`
5. Common layouts on every fixed drive: `SteamLibrary\steamapps\common\VR_Kanojo`, `Steam\steamapps\common\VR_Kanojo`, `Games\VR_Kanojo`, `VR_Kanojo`

If nothing matches it stops with two options: put the package inside the game root, or pass `-GameDir`.

## 4. Components and what they touch

| id | Files written | Config |
| --- | --- | --- |
| `flatfps` | `BepInEx/plugins/VRK_FlatFPS.dll` | `BepInEx/config/vrk.flatfps.cfg` |
| `upscale` | `BepInEx/plugins/VRK_UpScale.dll`, `VR_Kanojo_Data/Plugins/vrkup.dll` | `vrk.upscale.cfg` |
| `perf` | `BepInEx/plugins/VRK_Perf.dll` | `vrk.perf.cfg` |
| `prewarm` | `BepInEx/plugins/VRK_PreWarm.dll` | `vrk.prewarm.cfg` |
| `texplus` | `BepInEx/plugins/VRK_TexPlus.dll` | `vrk.texplus.cfg` |
| `unlock` | `BepInEx/plugins/VRK_Unlock.dll` | `vrk.unlock.cfg` |
| `launcher` | `VR_Kanojo_fix.exe`, `启动_免VR模式.bat`, `启动_VR模式.bat` (+ two desktop shortcuts) | — |
| `openvr` | renames `VR_Kanojo_Data/Plugins/openvr_api.dll` → `.disabled` | — |
| `devtools` | renames `ConfigurationManager.dll`, `RuntimeUnityEditor/RuntimeUnityEditor.Bepin5.dll`, `ScriptLoader.dll`, `VRK_PlayWithoutVR.dll` → `.disabled` | — |

Config files are only written when they do not exist yet (`OnlyIfMissing`) — your tuned values survive reinstalls. Delete the file (or restore first) if you want the shipped defaults back.

Nothing ever deletes a game file: the two rename components only add a `.disabled` suffix, and restore renames them back.

## 5. Restore semantics

`-Mode restore` walks `state/manifest.json` **backwards** and, per entry:

| Entry kind | Action |
| --- | --- |
| `copy` | Delete the installed file if it is still there; if a backup exists, copy it back |
| `rename` | Rename `<path>.disabled` back to `<path>` (skipped if it is already gone) |
| `shortcut` | Delete the `.lnk` (absolute path recorded) |

`state/backup/` mirrors the original relative paths, so you can always roll back by hand.

## 6. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Still a white screen after entering | `flatfps` not installed, or a leftover mod overrides the fade panel. Check `BepInEx/LogOutput.log` for `cleared opaque fade panel at Awake` |
| Still ~100 s of "not responding" before anything appears | The game was started outside the provided entries, so `openvr_api.dll` was active. Use `启动_免VR模式.bat`, or install the `openvr` component |
| Upscaling looks unchanged | The native layer is off by default (`NativePass = false`). Set `Mode = RenderScale` and `Scale = 0.85`, then check the log for `render-scale mode: 2176x1224 -> 2560x1440` |
| Why does Windows say "not responding" during loads? | Unity 5.4 loads scenes synchronously; anything over 5 s without pumping messages is flagged. The audio keeps playing — the game is working |
| Unlock did nothing | It writes on the title screen. Install `unlock`, start the game, wait for the title, then quit. Check for `unlock: ALLClearData() applied` |
| The panel cannot find the game | Pass `-GameDir`, or put the package into the game root |
| `Permission denied` while installing | The game is still running — close it and retry |

Logs: `BepInEx/LogOutput.log`. Installer history: `state/manifest.json`, `state/gui-last.log`.
