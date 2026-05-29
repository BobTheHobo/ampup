# Hardware Disconnect Memory

Created while testing the Turn Up + N3 disconnect issue around the v1.0.4-beta hotfix.

## Current Symptoms

- AmpUp remains open and responsive, but hardware controls can appear to stop working.
- This has been seen with both the Turn Up mixer and the TreasLin / VSDinside N3.
- The issue may take hours or a sleep/resume cycle to reproduce.

## Important Log Clues

- The installed v1.0.4-beta log showed both hardware readers still receiving input at times.
- After resume, N3 input arrived but volume control failed repeatedly:
  - `Hardware input received: N3 side button 3 down code=0x31 state=0x01`
  - `SetVolume error for Master: Object reference not set to an instance of an object.`
- Turn Up serial recovered after long sleep/resume stalls:
  - `Serial error: No Turn Up serial data for 34934.4s on COM3; reconnecting`
  - `Connected to COM3 @ 115200 baud`
- N3 had sleep/wake cycles using firmware standby:
  - `N3: sleep (CRT HAN) sent`
  - `N3: wake (CRT DIS + CRT LIG) sent`
- One N3 standby command timed out after overnight sleep:
  - `N3: sleep failed - Operation timed out (500 ms).`

## Current Working Hypothesis

This may be two overlapping failures that feel like one disconnect:

1. Windows audio endpoint/session COM objects can go stale after sleep/resume, so hardware input is received but the action fails when it tries to control audio.
2. The N3 HID stream can stay logically open while device writes/keepalive fail, leaving the app in a false connected state unless failures promote to a disconnect and reconnect.

## Uncommitted Test Patch

Files changed:

- `AudioMixer.cs`
  - `MMDeviceEnumerator` is now resettable instead of readonly.
  - `SetVolume` catches endpoint failures, resets audio device/session handles, refreshes sessions, removes the debounce value, and retries once.
  - Adds `Audio devices reset: ...` logging.

- `AmpUp.Core/Services/N3Controller.cs`
  - Tracks consecutive keepalive failures.
  - Keepalive failures now throw up to `SafeKeepAlive`.
  - Two consecutive keepalive failures call `HandleReadLoopDisconnected("keepalive", ex)`, which lets the existing app reconnect loop recover N3.

- `App.xaml.cs`
  - Automatic N3 idle sleep now uses brightness `0` instead of firmware standby so HID input stays awake.
  - `Sleep Now` and system suspend still use the real firmware standby command.

## Verification So Far

Build command:

```powershell
dotnet build AmpUp.sln --no-restore -p:UseSharedCompilation=false -v:minimal
```

Result: build passed with 0 warnings and 0 errors.

Test run:

- Stopped installed `C:\Program Files\Amp Up\AmpUp.exe`.
- Started patched debug build:
  - `Z:\Projects\ampup\bin\Debug\net8.0-windows\AmpUp.exe`
- Fresh log after patch showed both devices working:
  - `2026-05-17 18:52:00` N3 encoder input received.
  - `2026-05-17 18:52:09` Turn Up knob input received.

## What To Watch During Testing

If controls stop responding again, check `%AppData%\AmpUp\ampup.log` for:

- New `SetVolume error ...` lines.
- New `Audio devices reset: ...` lines.
- `SetVolume recovered ...` behavior after a reset.
- `N3: keepalive failed ...`
- `N3: read loop stopped on keepalive ...`
- `N3: reconnect attempt after disconnected state`
- Any Turn Up `Serial error: No Turn Up serial data ...`

## Next Steps If It Reappears

- If hardware input lines still appear but actions fail, continue hardening audio endpoint recovery.
- If N3 input disappears and keepalive failures appear, tune N3 reconnect timing or force reconnect sooner.
- If Turn Up stops without serial stall logs, add more logging around serial read timeout / info request / RGB write failures.
- If both devices stop with no hardware logs, inspect shared app timers, dispatcher health, power/session events, and any long-running action handlers.
