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
