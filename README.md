<p align="center">
  <img src="Assets/icon/ampup-256.png" width="120" alt="Amp Up Logo" />
</p>

<h1 align="center">Amp Up</h1>

<p align="center">
  <strong>A modern Windows control center for Turn Up mixers and the TreasLin / VSDinside N3 stream controller.</strong><br/>
  Per-app audio, profiles, RGB lighting, room sync, tray mixing, macros, and stream-controller workflows.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-1.0.5--beta-00BFEF" alt="Version" />
  <img src="https://img.shields.io/badge/Windows%2010%2F11-0078D6?logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License" />
</p>

---

## What Is Amp Up?

Amp Up is a Windows-only open-source replacement and upgrade path for the original Turn Up USB volume mixer app. It keeps the simple idea that made the hardware great, then adds the things power users kept wishing existed: per-app audio control, smart profiles, RGB effects, room lighting, Stream Deck-style actions, a fast tray mixer, and a modern dark UI.

The 1.0 beta also adds native support for the **TreasLin / VSDinside N3** stream controller, turning its LCD keys, side buttons, and rotary encoders into a second control surface inside Amp Up.

## Install

1. Download **`AmpUp-Setup-1.0.5-beta.exe`** from [Releases](https://github.com/audioslayer/ampup/releases).
2. Run the installer.
3. Launch Amp Up, then connect your Turn Up mixer or N3 controller.

Close the official Turn Up app first. Only one app can hold the Turn Up serial port at a time.

### Platform Support

Amp Up supports **Windows 10/11**.

The experimental macOS alpha has been discontinued and removed from the repository due to lack of user interest and the maintenance cost of keeping a separate Core Audio/Avalonia port healthy. Older macOS alpha builds are unsupported.

## Supported Hardware

| Device | Support | What Works |
|-|-|-|
| **Turn Up USB Volume Mixer** | Stable | 5 knobs, 5 buttons, 15 RGB LEDs, profiles, per-app volume, lighting, macros, tray mixer |
| **TreasLin / VSDinside N3 Stream Controller** | Beta | 6 LCD keys, 3 tap buttons, 3 rotary encoders with press, pages, spaces, icons, actions, sleep/wake |

### Turn Up Mixer

Amp Up speaks the Turn Up serial protocol directly over the CH343 USB-to-serial chip at 115200 baud. Knobs can control master volume, input gain, individual apps, app groups, device volume, active-window audio, monitor brightness, and more. Buttons support tap, double-press, and hold gestures.

### TreasLin / VSDinside N3

Amp Up includes native beta support for the [TreasLin / VSDinside N3 stream controller](https://www.amazon.com/dp/B0FM3NP9ZB?ref=ppx_yo2ov_dt_b_fed_asin_title&th=1). The N3 has **6 visual LCD buttons, 3 tap buttons, and 3 rotary buttons**, making it a great compact companion surface for audio, macros, media, apps, and stream controls.

N3 support is designed to be self-contained, with no long-term dependency on VSD Craft for day-to-day use.

## Highlights

### Mixer

- Per-app volume control for Spotify, Discord, Chrome, games, browsers, UWP apps, and app groups.
- Master output, microphone input, specific output/input devices, and monitor brightness targets.
- Linear, logarithmic, exponential, and custom range behavior per knob.
- Live VU meters and peak activity in the main mixer and tray popup.
- Smart app matching, including display-name matching for apps whose process name is not user friendly.

### Buttons And Actions

- 3 gestures per Turn Up button: tap, double-press, and hold.
- Media controls, mute controls, app launch/close, device switching, keyboard macros, power actions, profiles, URLs, text snippets, screenshots, multi-actions, and toggles.
- Quick Wheel overlay for profile switching and output-device switching.
- Stream-controller actions for pages, spaces, folders, and navigation.

### N3 Stream Controller

- Visual designer for 6 LCD keys with title, icon, colors, glow, and display type.
- Spaces and pages for organizing actions.
- Side buttons and encoder presses as first-class controls.
- Encoder rotation actions for page/space cycling and knob-style assignments.
- Native display rendering for device JPEGs plus crisp in-app previews.
- Device sleep/wake controls and disconnect visibility.

### Lights

- 60+ Turn Up RGB effects with animated previews.
- Per-knob and global lighting modes.
- Premium palettes, gradient editor, brightness, speed, gamma calibration, and hardware hover preview.
- Audio-reactive, mute-aware, device-aware, position-fill, rainbow, fire, comet, plasma, heartbeat, scanner, meteor, matrix, aurora, and more.
- **Dev+Pos** mode combines active output-device colors with knob position fill.

### Room Lighting

- Govee LAN sync and Govee Cloud controls.
- Corsair iCUE room effects, device sync, and fan/pump controls.
- Room layout canvas with device placement.
- Music Reactive, VU Fill, Screen Sync, and Game Mode.
- Per-device sync toggles so global actions respect devices you intentionally excluded.

### Tray Mixer

- Left or right click opens the unified mixer popup.
- Adjust master volume, app volumes, output/input devices, and assignments without opening the full window.
- Live app activity bars, app filtering, pinned apps, and quick assignment.
- DPI-aware placement for bottom, top, left, and right taskbars.

### 1.0 Beta Polish

- Lower idle CPU and memory pressure from reduced polling and smarter timers.
- Safer shutdown and update flow.
- More resilient Govee, Corsair, audio-session, OSD, and N3 disconnect handling.
- Start with Windows is enabled by default for new installs and can be changed in Settings.
- Manual installer packaging and GitHub release notes are ready for beta distribution.

## Build From Source

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

## Roadmap

- [x] Turn Up mixer support
- [x] Per-app Windows audio control
- [x] Profiles, buttons, actions, macros, and Quick Wheel
- [x] Turn Up RGB effects and audio-reactive lighting
- [x] Govee, Corsair iCUE, OBS, VoiceMeeter, and Home Assistant integrations
- [x] TreasLin / VSDinside N3 native beta support
- [x] Tray mixer and release-ready Windows installer
- [x] Retire unsupported macOS alpha port
- [ ] Broader N3 field testing
- [ ] Multiple Turn Up units
- [ ] More stream-controller plugin-style actions
- [ ] Razer Chroma sync
- [ ] Mobile companion app

## Changelog Highlights

| Version | Highlights |
|-|-|
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

## License

MIT. See [LICENSE](LICENSE) for details.

---

<p align="center">
  Built by <a href="https://github.com/audioslayer">Tyson Wolf</a><br/>
  <a href="https://www.buymeacoffee.com/audioslayer">Buy me a coffee</a>
</p>
