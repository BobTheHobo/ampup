<p align="center">
  <img src="Assets/icon/ampup-256.png" width="120" alt="Amp Up Logo" />
</p>

<h1 align="center">Amp Up</h1>

<p align="center">
  <strong>🎚️ The modern Windows app for the original Turn Up USB volume mixer.</strong><br/>
  Per-app audio, profiles, RGB lighting, room sync, SignalRGB bridge control, tray mixing, macros, and optional N3 stream-controller workflows.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-1.1-00BFEF" alt="Version" />
  <img src="https://img.shields.io/badge/Windows%2010%2F11-0078D6?logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License" />
</p>

---

## 🎚️ What Is Amp Up?

Amp Up is a Windows-only open-source replacement and upgrade path for the **original Turn Up USB volume mixer**. The Turn Up mixer is the core supported hardware: 5 physical knobs, 5 buttons, and 15 RGB LEDs mapped to modern Windows audio, apps, lighting, profiles, macros, and automations.

It keeps the simple idea that made the Turn Up hardware great, then adds the things power users kept wishing existed: per-app audio control, smart profiles, RGB effects, room lighting, a fast tray mixer, button gestures, and a modern dark UI.

Amp Up also includes beta support for the **TreasLin / VSDinside N3** as an optional companion controller, but the original Turn Up mixer remains the main experience.

## 📥 Install

1. Download **`AmpUp-Setup-1.1.exe`** from [Releases](https://github.com/audioslayer/ampup/releases).
2. Run the installer.
3. Launch Amp Up, then connect your **Turn Up mixer**.

Close the official Turn Up app first. Only one app can hold the Turn Up serial port at a time.

### 🪟 Platform Support

Amp Up supports **Windows 10/11**.

The experimental macOS alpha has been discontinued and removed from the repository due to lack of user interest and the maintenance cost of keeping a separate Core Audio/Avalonia port healthy. Older macOS alpha builds are unsupported.

## 🧩 Supported Hardware

| Device | Support | What Works |
|-|-|-|
| **🎚️ Turn Up USB Volume Mixer** | **Stable / Primary** | 5 physical knobs, 5 buttons, 15 RGB LEDs, per-app volume, profiles, app groups, RGB effects, button gestures, macros, tray mixer, SignalRGB bridge |
| **🌈 SignalRGB** | Beta bridge for Turn Up LEDs | Free local UDP bridge, Turn Up LED layout, effect actions, effect cycling, blackout/restore, profile sync |
| **🎛️ TreasLin / VSDinside N3 Stream Controller** | Beta companion device | 6 LCD keys, 3 tap buttons, 3 rotary encoders with press, pages, spaces, icons, actions, sleep/wake |

### 🎚️ Original Turn Up Mixer

Amp Up speaks the Turn Up serial protocol directly over the CH343 USB-to-serial chip at 115200 baud. No cloud service, no helper app, no official Turn Up software required.

- 🎚️ **5 knobs** for master volume, microphone input, app volume, app groups, output/input devices, active-window audio, monitor brightness, and more.
- 🖱️ **5 buttons** with tap, double-press, and hold gestures for media controls, mutes, macros, profiles, app launches, device switching, power actions, Quick Wheel, and integrations.
- 🌈 **15 RGB LEDs** with per-knob effects, global scenes, audio-reactive lighting, mute-aware states, app-status effects, palettes, brightness, speed, and gamma calibration.
- 🔊 **Per-app Windows audio** for Spotify, Discord, Chrome, browsers, games, UWP apps, and custom process/app groups.
- ⚡ **Daily-driver tooling** including tray mixer, profiles, OSD, Quick Wheel, auto-ducking, auto-profile switching, and update checks.
- 🧩 **Integrations** for SignalRGB, Govee, Corsair iCUE, OBS, VoiceMeeter, Home Assistant, Spotify, and Discord tester actions.

### 🎛️ Optional N3 Companion

Amp Up includes native beta support for the [TreasLin / VSDinside N3 stream controller](https://www.amazon.com/dp/B0FM3NP9ZB?ref=ppx_yo2ov_dt_b_fed_asin_title&th=1). The N3 has **6 visual LCD buttons, 3 tap buttons, and 3 rotary buttons**, making it a great compact companion surface for audio, macros, media, apps, and stream controls.

N3 support is designed to be self-contained, with no long-term dependency on VSD Craft for day-to-day use. It is still beta and does not replace the primary Turn Up mixer workflow.

### 🌈 SignalRGB

Amp Up can bridge SignalRGB into the Turn Up LEDs without letting SignalRGB steal the Turn Up serial port. Enable the bridge in **Settings -> Integrations -> SignalRGB**, install/update the bundled SignalRGB plugin, then let SignalRGB render effects into Amp Up over localhost UDP.

SignalRGB actions are available for Turn Up buttons and N3 keys, including apply effect, cycle effects, blackout, restore, and profile-to-effect/layout sync.

## ✨ Highlights

### 🎚️ Turn Up Mixer

- Per-app volume control for Spotify, Discord, Chrome, games, browsers, UWP apps, and app groups.
- Master output, microphone input, specific output/input devices, and monitor brightness targets.
- Linear, logarithmic, exponential, and custom range behavior per knob.
- Live VU meters and peak activity in the main mixer and tray popup.
- Smart app matching, including display-name matching for apps whose process name is not user friendly.
- Hardware-first behavior that keeps the physical Turn Up knobs, buttons, and LEDs feeling immediate and reliable.

### 🖱️ Turn Up Buttons And Actions

- 3 gestures per Turn Up button: tap, double-press, and hold.
- Media controls, mute controls, app launch/close, device switching, keyboard macros, power actions, profiles, URLs, text snippets, screenshots, multi-actions, and toggles.
- Quick Wheel overlay for profile switching and output-device switching.
- Stream-controller actions for pages, spaces, folders, and navigation.

### 🌈 Turn Up Lights

- 60+ Turn Up RGB effects with animated previews.
- Per-knob and global lighting modes for all 15 physical LEDs.
- Premium palettes, gradient editor, brightness, speed, gamma calibration, and hardware hover preview.
- Audio-reactive, mute-aware, device-aware, position-fill, rainbow, fire, comet, plasma, heartbeat, scanner, meteor, matrix, aurora, and more.
- 15 room-bright ambient scenes plus automatic color recommendations for effect presets.
- App Status lighting can use separate effects for unmuted, muted, not-running, and activity-flash states.
- **Dev+Pos** mode combines active output-device colors with knob position fill.

### 🎛️ N3 Stream Controller Beta

- Optional companion surface for users who also have a TreasLin / VSDinside N3.
- Visual designer for 6 LCD keys with title, icon, colors, glow, and display type.
- Spaces and pages for organizing actions.
- Side buttons and encoder presses as first-class controls.
- Encoder rotation actions for page/space cycling and knob-style assignments.
- Hardware monitoring tiles for CPU, GPU, RAM, VRAM, temperature, usage, and fan metrics.
- Gauge displays with max overrides, color-by-value, label overrides, and dimmed tracks for low values.
- Native display rendering for device JPEGs plus crisp in-app previews.
- Smooth auto-scrolling for long titles and wraparound text on both previews and device displays.
- Device sleep/wake controls and disconnect visibility.

### 🏠 Room Lighting

- Govee LAN sync and Govee Cloud controls.
- Corsair iCUE room effects, device sync, and fan/pump controls.
- Room layout canvas with device placement.
- Music Reactive, VU Fill, Screen Sync, and Game Mode.
- Temperature mode for normal white room lighting, with warm amber presets through crisp daylight/cool options.
- Stronger RGBIC segment handling for Govee wall lights, rope lights, and Flow Plus light bars.
- Per-device sync toggles so global actions respect devices you intentionally excluded.

### 🔊 Tray Mixer

- Left or right click opens the unified mixer popup.
- Adjust master volume, app volumes, output/input devices, and assignments without opening the full window.
- Live app activity bars, app filtering, pinned apps, and quick assignment.
- DPI-aware placement for bottom, top, left, and right taskbars.

### 🧼 1.0 Beta Polish

- Lower idle CPU and memory pressure from reduced polling and smarter timers.
- Safer shutdown and update flow.
- More resilient Govee, Corsair, SignalRGB, Home Assistant, audio-session, OSD, and N3 disconnect handling.
- Start with Windows is enabled by default for new installs and can be changed in Settings.
- Manual installer packaging and GitHub release notes are ready for beta distribution.

### 🚀 1.1 Highlights

- Discord RPC actions now support tester-gated authorization, token refresh, persistent auth across profile switches, and direct mute/deafen/voice controls. Because official Discord approval is not available yet, users need to DM Tyson on Discord so he can add them as a friend/tester before they can use these actions.
- Settings backup and restore make it easier to protect or move a tuned setup.
- N3 stream-controller tiles gained scrolling titles, cleaner icon clearing, hardware metric displays, gauge styling, and more polished metric options.
- Lighting gained state-specific App Status effects, automatic effect colors, a renamed Scenes category, 15 room-bright ambient effects, and a compact state-effect chip grid.
- Performance work reduced hot-path overhead across engines, UI, and I/O.

## 🛠️ Build From Source

```bash
git clone https://github.com/audioslayer/ampup.git
cd ampup
dotnet build
```

To build the Windows installer locally:

```powershell
.\build-installer.bat
```

Amp Up is a .NET 8 WPF application using WPF-UI, NAudio, Newtonsoft.Json, System.IO.Ports, HidSharp, and Inno Setup.

## 🗺️ Roadmap

- [x] Turn Up mixer support
- [x] Per-app Windows audio control
- [x] Profiles, buttons, actions, macros, and Quick Wheel
- [x] Turn Up RGB effects and audio-reactive lighting
- [x] Govee, Corsair iCUE, SignalRGB, OBS, VoiceMeeter, and Home Assistant integrations
- [x] TreasLin / VSDinside N3 native beta support
- [x] Tray mixer and release-ready Windows installer
- [x] Retire unsupported macOS alpha port

## 📝 Changelog Highlights

| Version | Highlights |
|-|-|
| **v1.1** | Discord tester-gated RPC/actions, settings backup/restore, N3 scrolling titles and hardware metric gauges, room-bright scenes, automatic effect colors, state-specific App Status effects, and performance optimizations. Discord actions require DMing Tyson so he can add users as friend/testers until official Discord support is available. |
| **v1.0.9.8** | Hardware monitoring improvements including VRAM fixes, HWiNFO fallbacks, gauge max overrides, color-by-value, and compact state-effect chips. |
| **v1.0.9.7** | Automatic effect colors, Scenes naming, 15 room-bright ambient LED effects, and an App Status dead-zone fix. |
| **v1.0.9.5** | App Status lighting now supports separate effects for unmuted, muted, and not-running states, including Off. |
| **v1.0.9.3** | Direct Discord RPC button actions for mute/deafen, explicit voice state controls, leave voice, and noise suppression. |
| **v1.0.9.2** | SignalRGB bridge/actions/profile sync, expanded N3 integration actions, Home Assistant action fixes, Room Temperature mode, warmer Govee temperature presets, and RGBIC segment reliability fixes. |
| **v1.0.6-beta** | SignalRGB beta work, N3 sensitivity refinements, and integration polish. |
| **v1.0.5-beta** | Stream-controller and integration reliability pass. |
| **v1.0.4-beta** | Govee hotfix: added Restore Removed for hidden devices and clarified scan status when removed devices are filtered out. |
| **v1.0.3-beta** | Reliability patch: retired the unsupported macOS alpha, hardened Govee startup power handling, and added recovery/telemetry for stalled Turn Up and N3 hardware input. |
| **v1.0.2-beta** | Quick lighting patch: added **Dev+Pos** / `DevicePositionFill` mode and fixed position-fill LEDs briefly showing 100% on startup when a saved knob position is zero. |
| **v1.0.1-beta** | App-group discoverability fixes in the Mixer target picker and Audio Sessions list. |
| **v1.0.0-beta** | Production-readiness beta with N3 stream-controller support, major Room tab upgrades, Govee/Corsair/iCUE sync fixes, tray mixer polish, lower idle CPU/RAM pressure, release workflow, safer updates, and many crash fixes. |
| **v0.9.8** | Animated effect previews, Phosphor icon polish, card layout pass, and refined visual hierarchy. |
| **v0.9.7** | Room Effect redesign, Favorites, gradient palettes, VU Fill modes, Music Reactive sensitivity, and many lighting fixes. |
| **v0.9.6** | Unified Room tab, Corsair controls, Govee menus, groups, settings footer, and OSD fixes. |
| **v0.9.x** | Tray mixer, audio sessions, profile overview, Quick Wheel, DreamView sync, and smart automations. |
| **v0.5.x** | Auto-ducking, auto-profile switching, app groups, profile import/export, and first major UI polish. |

See [CHANGELOG.md](CHANGELOG.md) for the full release notes.

## 📄 License

MIT. See [LICENSE](LICENSE) for details.

---

<p align="center">
  Built by <a href="https://github.com/audioslayer">Tyson Wolf</a><br/>
  <a href="https://www.buymeacoffee.com/audioslayer">Buy me a coffee</a>
</p>
