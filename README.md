# Beam

A tiny, free, fully local **cast receiver** for Android phones, tablets and Android TV.
Cast video, music or photos to it from another device on the same Wi-Fi and it plays them full-screen.
No cloud, no account, no ads. Framework APIs only (no dependencies), so the APK stays small and RAM use stays low.

## What works

**DLNA / UPnP MediaRenderer** (discovery via SSDP, control via SOAP, state via events):

- Works with BubbleUPnP, VLC, Kodi, Windows "Cast to device" and phone gallery/music apps that have a DLNA cast button.
- Plays what Android's own player can: MP4, MKV, WebM, 3GP, HLS, MP3, AAC, FLAC, OGG, WAV, and shows JPEG/PNG/WebP/GIF pictures.
- Play, pause, stop, seek, volume, mute; the sender sees the real state.
- Remote / D-pad: OK = play/pause, left/right = seek 10 s, Back = stop.

**Not supported yet:** Chrome tab/screen casting. Chrome's Cast menu talks Google's own protocol and official receivers are
licensed by Google, so this needs separate research. DASH streams are not supported either (Android's MediaPlayer can't play them).

## Use

1. Open Beam, tap **Turn on**. A small notification keeps the receiver alive (with a **Stop** button).
2. Optional: tap **Allow auto-open** so the player opens by itself when a cast starts (otherwise tap the notification).
3. In the sender app, pick the device named in Beam.

The receiver only listens on your local network while it is on. Nothing is sent to the internet.

## Low RAM and storage

- No libraries. One foreground service, a few small threads, one MediaPlayer at a time, pictures capped at 2400 px.
- The protocol engine (`Upnp`, `UpnpDevice`, `MiniHttp`, `SsdpMsg`, `SsdpServer`) is plain Java with no Android classes and is
  tested on a normal JVM.

## Build

GitHub Actions builds an APK on every push (artifact `Beam-debug-apk`). Locally: `./gradlew :app:assembleDebug`.
Min Android 10 (API 29).

## Releasing

Same as Fliq: create a keystore, add the secrets `KEYSTORE_B64`, `KEYSTORE_PASSWORD`, `KEY_ALIAS`, `KEY_PASSWORD`, then push a tag
such as `v0.1.0`.

## Beam for Windows

Same idea on a PC: a single `windows/` project, built to one **Beam.exe** (about 120 KB). It targets .NET Framework 4.8, which is already part of Windows 10/11, so there is nothing to install.

- Download the latest build from the **latest-windows** pre-release, run `Beam.exe`.
- Home screen says **Ready to cast** with the steps; **Settings** has the receiver name, picture mode, full-screen and start-with-Windows. Closing the window keeps Beam in the tray.
- Picture modes while playing (press **V**, or **1 2 3 4**): **Fit**, **Fill**, **16:9**, **Crop**.
- Keys: Space pause, ← → jump 10 s, ↑ ↓ volume, M mute, F full screen, Esc stop.
- Playback uses Windows' built-in media stack, so H.264 MP4 is the safe format. MKV/HEVC/etc. depend on codecs installed on the PC.
- The first run asks Windows Firewall for permission: allow **Private networks**.
- Not code-signed, so SmartScreen may warn once: *More info → Run anyway*.
- Phones need a DLNA sender app (VLC, BubbleUPnP, LocalCast, Web Video Caster…). YouTube/Chrome use Google Cast, which Beam does not speak.
