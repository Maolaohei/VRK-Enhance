# Key map (no-VR mode)

The game ships without a mouse/keyboard scheme: camera and touch are bound to a VR headset. `VRK_FlatFPS` re-wires the game's own controller, so these keys drive the original code paths rather than a parallel camera.

> 中文版：[zh-CN/KEYS.md](zh-CN/KEYS.md)

---

## Camera (third person — the original `IllusionCamera`)

| Key | Action |
| --- | --- |
| Mouse move | Look around (no button needed) |
| `W A S D` | Walk forward / left / back / right (was the arrow keys) |
| `PageUp` / `PageDown` | Rise / lower |
| `6` / `4` | Yaw left / right (was the numpad) |
| `8` / `2` | Pitch up / down |
| `5` | Level the pitch |
| `End` / `Home` | Pull in / push out |
| `.` / `\` | Roll (`Shift` = snap 45°) |
| `/` | Reset roll |
| `R` | Reset view (roll to zero; also recenters the game's own VR pose) |
| `[` / `=` | Narrow / widen field of view |
| `;` | Reset FOV |
| `=` / `-` | Key sensitivity up / down |
| Mouse wheel | Mouse sensitivity |
| Left drag | Rotate |
| Left + right drag | Pan |
| Right drag | Distance |
| `Shift` / `Ctrl` | ×3 / ×0.25 while moving |

## First person (`F` to switch)

| Key | Action |
| --- | --- |
| `F` | Cycle first person → third person → free camera |
| Mouse move | Look (yaw turns the player rig, pitch stays on the camera) |
| `W A S D` | Walk (2 m/s, `Shift` = ×3) |
| `PageUp` / `PageDown` | Eye height ±5 cm (live, shows a toast) |

Free camera: same mouse + WASD, `PageUp`/`PageDown` for altitude, `Shift` to sprint. It is a flight camera — the game's idea of "where you stand" does not follow it.

## Interaction

| Key | Action |
| --- | --- |
| `E` | Interact: hand/gaze touch, the same call the VR hand makes |
| Left click | Decide / act / click menus / grab |
| Right click | Cancel / end |
| Middle click | Next |
| Aim | The gaze ring snaps to whatever you look at; corner brackets + `E` appear over interactables |

## Overlays

| Key | Action |
| --- | --- |
| `F7` | Show / hide the key reference (auto-shown 5 s after a scene loads) |
| `F9` | Upscale status dump |
| `F10` | Dump cameras / canvases / UI to the log |
| Hold `Alt` | Release the mouse pointer (release the key to lock it again) |

## VR mode

Nothing above applies. The launcher's VR entry starts the game with every plugin disabled — controls, camera and interaction are the game's own.
