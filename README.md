# AoIP-RX
Over the Internet Audio Receiver — a portable Windows receiver for live MMS and Shoutcast/Icecast radio, with auto-reconnect, accurate stereo meters, and automatic balance correction.

Developed by Greg Vincent. © 2026 Mantraix Software. All rights reserved.

## What it does
- Plays live audio from `mms://` (Windows Media) and `http(s)://` Shoutcast/Icecast streams
- Infinite auto-reconnect (every 4s) when a stream drops, with MMS-over-HTTP fallback
- Fast start (~1-2s) with Fast/Stable buffering toggle
- Accurate stereo L/R level meters + automatic balance correction (toggleable)
- Stream favorites, live logs with export, output device picker with USB hot-plug support
- Sample rate selection (44100/48000/96000 Hz), tray mode, start-on-boot, update checker

## Download & run (portable, no install)
1. Go to [Releases](https://github.com/gregvinz23-bit/AoIP-RX/releases), download `AoIP-RX-v1.0-portable.zip`
2. Extract anywhere (even USB) and run `AoIP-RX.exe`
3. Paste a stream URL, press STREAM

No admin rights needed. Settings (`settings.json`, `streams.json`) and logs stay next to the exe.
Verify the download with the SHA-256 hash posted on the release page.

## Build from source
- .NET 8 SDK, Windows: `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=false`

## Notes
- First startup can take ~30-60s (one-time native extraction + OS scan); afterwards it starts in seconds.
- A new unsigned exe may show a Windows SmartScreen "Unknown publisher" prompt on first run — click More info > Run anyway.
