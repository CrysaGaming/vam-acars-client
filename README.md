# VAM ACARS Client

A Windows desktop ACARS client for Microsoft Flight Simulator 2024 that
connects pilots to the [VAM virtual airline platform](https://vam.kevindrack.de).
Lives in the system tray, reads your sim's telemetry via SimConnect, and
streams flight data to the VAM server in real-time so your flights show
up on the live-map and auto-file as PIREPs when you reach the gate.

> ⚠️ **Pre-1.0 / personal project.** Built primarily for the LEAV Aviation
> virtual airline. Other VAs can pair too if their VAM-server admin enables
> ACARS for their airline; the client itself is generic and ships unbranded.

## Install

1. Download the latest `VamAcarsClient-win-Setup.exe` from
   [Releases](https://github.com/CrysaGaming/vam-acars-client/releases/latest).
2. Double-click. Velopack installs to `%LOCALAPPDATA%\VamAcarsClient\`,
   adds a Start-Menu entry, and launches the tray app.
3. Windows SmartScreen may complain — the installer isn't code-signed
   yet. "More info" → "Run anyway." If you'd rather build from source,
   see _Building from source_ at the bottom.

Auto-update is on by default. The app checks GitHub Releases on launch
and shows a banner in the **EINSTELLUNGEN** card when a newer version
exists. One click to install.

## First-time pairing

The client doesn't know who you are until you pair it with your VAM
account. One-time setup:

1. Open the tray app's status window (left-click the tray icon).
2. Sign in to https://vam.kevindrack.de in your browser, go to
   **Settings → ACARS** and click **Pair Device**. You'll get a
   short pairing code like `X4F-7K2-9PB`. Codes are valid for 15 min.
3. Type the code into the tray app's first-launch dialog (or run
   `VamAcarsClient.Cli pair X4F-7K2-9PB` from a terminal — the CLI
   shares the same token store via DPAPI).
4. The client saves a long-lived API token under DPAPI-encrypted
   `%LOCALAPPDATA%\VamAcarsClient\token.dat`. From here on it
   authenticates automatically. To re-pair (e.g. after a Windows
   reinstall), delete that file and start over.

## Daily flow

1. Start MSFS 2024 and load any flight.
2. Make sure the tray app is running (it auto-starts with Windows if
   you've enabled the option in **EINSTELLUNGEN**).
3. Open the status window. Fill in **Callsign** (e.g. `NGN901`),
   **Network** (Offline / VATSIM / IVAO), and **Departure / Arrival**
   ICAO codes if you'd like the auto-PIREP to file against the right
   route. The form is locked once you click **Verbinden** so you can't
   change mid-flight.
4. Click **Verbinden** (green button). The pill at the top flips
   to green / "Connected", **HEARTBEATS** counters tick up at 1-2 Hz,
   and your aircraft appears on https://vam.kevindrack.de/live.
5. Fly. The client tracks phase transitions automatically (PreFlight
   → Pushback → Taxi → Takeoff → ... → Landing → TaxiIn → BlockOn).
6. After landing, taxi to the gate. Set parking brake + cut engines —
   the server detects BlockOn from the telemetry and auto-files a
   PIREP. Discord broadcast goes out, rank-promotion is evaluated.
7. Click **Trennen** when you're done (or just close the sim — the
   server cleans up stale sessions after ~10 minutes).

## What the tray app shows

| Section | What it tells you |
|---|---|
| **Status pill** | Disconnected (grey) / Connecting (yellow) / Connected (green) / Error (red) |
| **VERBINDUNG** | Server URL + pairing status (gepaart / nicht gepaart) |
| **FLUG-KONTEXT** | Editable callsign, network, dep/arr ICAOs + live aircraft type, registration, phase |
| **HEARTBEATS** | sent / failed / queued counters |
| **EINSTELLUNGEN** | Auto-start with Windows toggle, version, "Auf Updates prüfen" / Update-installieren |

The tray icon's tooltip mirrors the same info in compact form so you
don't need the window open during a flight.

## Logs + bug reports

Logs land at:
```
%LOCALAPPDATA%\VamAcarsClient\logs\acars-tray-YYYYMMDD.log
```

Rolling daily, keeps last 7 days. Serilog format with timestamp +
level + structured payload. When filing a bug, attach the log from
the day the issue happened — most useful is the slice around the
last `Connected` line.

File issues at
[github.com/CrysaGaming/vam-acars-client/issues](https://github.com/CrysaGaming/vam-acars-client/issues).
Please include:
- Client version (visible in **EINSTELLUNGEN**)
- MSFS 2024 build number
- Log excerpt (~50 lines around the bad behavior)
- What you expected vs what happened

## Architecture overview

Three projects in one solution:

```
VamAcarsClient.Core           ─┐ pure logic, no UI
   ├─ Pairing                   token redeem via /api/acars/pairing/redeem
   ├─ TokenStore                DPAPI-encrypted persistence
   ├─ SimConnectClient          telemetry read-loop
   ├─ HeartbeatSender           1-2Hz POST /api/acars/heartbeat
   └─ FlightContext             persisted per-launch user prefs
                                │
VamAcarsClient.Cli              │ headless console; dev/debug tool
   └─ Program.cs ────────────── │ wires Core into a foreground console
                                │
VamAcarsClient.Tray             │ WPF tray + status window
   ├─ App.xaml.cs ───────────── │ TaskbarIcon, lifetime, AssemblyResolve hook
   ├─ MainWindow.xaml           │ status panel (this is what you see when you click the icon)
   ├─ Models/AcarsClientState   │ shared INotifyPropertyChanged state
   └─ Services/UpdateService    │ Velopack auto-update integration
```

The Tray + Cli both consume Core. They share token storage via DPAPI
(per-user) so pairing through one is recognized by the other.
SimConnect is loaded via standard MSBuild Reference handling with a
runtime AssemblyLoadContext fallback resolver (covers edge cases
where probing fails for some unusual reason).

The server contract lives in vam-system's `apps/web/app/api/acars/`
routes — this client is the canonical caller, but the server treats
the API as stable for any future ACARS-compatible client.

## Building from source

Prereqs:
- Windows 10 / 11 x64
- .NET 10 SDK ([dotnet.microsoft.com/download](https://dotnet.microsoft.com/download))
- MSFS 2024 SDK at `C:\MSFS 2024 SDK\` (Asobo's official SDK download).
  The SimConnect dlls under `lib\` are what the build needs.
  If you have it elsewhere, set `$env:MSFS2024SDKPath = "your\path\SimConnect SDK"`
  before running `dotnet build`.

```powershell
git clone https://github.com/CrysaGaming/vam-acars-client.git
cd vam-acars-client
dotnet build VamAcarsClient.Tray\VamAcarsClient.Tray.csproj
# Run the freshly-built tray:
.\VamAcarsClient.Tray\bin\Debug\net10.0-windows\VamAcarsClientTray.exe
```

To produce a Velopack release locally:
```powershell
.\release.ps1            # local pack only, no upload (artefacts in releases/)
.\release.ps1 -Publish   # pack + push to GitHub Releases (needs $env:GITHUB_TOKEN)
```

To trigger an automated release via GitHub Actions:
```powershell
# Bump VamAcarsClient.Tray.csproj <Version>, commit, then:
git tag v0.1.2
git push origin master --tags
# Watch the Actions tab — workflow takes ~3-4 minutes end-to-end.
```

The Actions workflow needs the SimConnect dlls vendored in the repo
under `external/simconnect/` — see
[`external/simconnect/README.md`](external/simconnect/README.md) for the
one-time setup.

## License

Source code: MIT (see [LICENSE](LICENSE) when added).

The bundled SimConnect dlls are Microsoft's, redistributed under the
[Microsoft Flight Simulator SDK license](https://docs.flightsimulator.com/),
which permits inclusion with third-party apps that interact with the sim.
