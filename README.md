# Adoxic Sound
A portable Windows studio for live internet radio: receive MMS and Shoutcast/Icecast streams with auto-reconnect, or broadcast your microphone to any Icecast/Shoutcast server. Accurate meters, stream health stats, and silent-failure alarms included.

Crafted by Greg Vincent for Mantraix Software Solutions. © 2026 Mantraix Software Solutions. All rights reserved — see LICENSE.txt.

## Install (portable, no setup)
1. Download the latest `AdoxicSound-vX.X-portable.zip` from [Releases](https://github.com/gregvinz23-bit/AdoxicSound/releases)
2. Extract anywhere and run `AdoxicSound.exe` — that's it
3. Your stations, settings and logs live next to the exe, so the folder roams beautifully on USB

First launch takes a little while (Windows checks new files once), then it opens in seconds.

## Everyday use
- **Receive tab** — paste a stream address, press STREAM. Save favorites with + Add.
- **Send tab** — enter your Icecast/Shoutcast details once, GO LIVE to broadcast your mic.
- **Record tab** — capture your input to MP3/AAC/WAV with hourly filing.
- **Logs** — every connect, drop, retry and alarm, exportable for overnight checks.

The full tour lives in `Instruction.txt` (ships in the project for now — ask us and we'll bundle it).

## Build from source
Windows + .NET 8 SDK:
```
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=false
```

## Support
Something wrong? Open the Logs window, press Export, and send us the file with a line about what happened. Thank you for trying Adoxic Sound!
