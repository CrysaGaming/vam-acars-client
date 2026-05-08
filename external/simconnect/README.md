# SimConnect SDK vendor directory

This directory holds the two SimConnect dlls that `VamAcarsClient.Core`
needs at compile-time. They're vendored here so the GitHub Actions
release workflow (`.github/workflows/release.yml`) can build the project
on a runner that has no MSFS 2024 SDK installed.

## Required layout

Mirror the `lib/` subtree from your local MSFS 2024 SDK exactly:

```
external/simconnect/
└── lib/
    ├── SimConnect.dll                                       (native, ~250 KB)
    └── managed/
        └── Microsoft.FlightSimulator.SimConnect.dll         (managed, ~50 KB)
```

The `Core.csproj` resolves both via `$(MSFS2024SDKPath)\lib\...`. Local
dev defaults `$(MSFS2024SDKPath)` to `C:\MSFS 2024 SDK\SimConnect SDK\`,
so you don't notice this directory at all when building from a machine
that has the full SDK. CI sets the property to point here instead.

## Sourcing the dlls

If you have MSFS 2024 + the SDK installed at the canonical path, copy
both files over (one-time, run from a PowerShell with the repo as cwd):

```powershell
$src = "C:\MSFS 2024 SDK\SimConnect SDK\lib"
New-Item -ItemType Directory -Force -Path "external\simconnect\lib\managed" | Out-Null
Copy-Item "$src\SimConnect.dll"                                  "external\simconnect\lib\SimConnect.dll"
Copy-Item "$src\managed\Microsoft.FlightSimulator.SimConnect.dll" "external\simconnect\lib\managed\Microsoft.FlightSimulator.SimConnect.dll"
git add external/simconnect/lib
git commit -m "chore(release): vendor SimConnect dlls for CI builds"
```

Then the next `git tag v0.1.2 && git push --tags` triggers the workflow
which finds them at the expected paths and proceeds straight to publish.

## Why vendoring is OK here

- These two files are part of the SimConnect SDK that Microsoft
  distributes specifically for third-party developers building
  apps that talk to MSFS. Redistribution is the documented use case.
- Both files end up bundled inside every Setup.exe / nupkg that the
  release pipeline produces — they're already shipped to every user
  who installs the client. Putting them in source control is a
  smaller redistribution step than the release artefacts already are.
- Combined size is ~300 KB. Won't bloat the repo.

## Updating after an SDK upgrade

If Asobo ships a new SimConnect SDK, repeat the copy-and-commit step
above. The workflow has no version pinning beyond "whatever's checked
in here right now."

## What if I don't want to vendor?

Two alternatives, neither of which is implemented today:

1. **Self-hosted runner with the SDK pre-installed.** Replaces
   `runs-on: windows-latest` with `runs-on: self-hosted`, drops the
   "Verify vendored dlls" step, drops the `MSFS2024SDKPath` env var.
   Costs: maintaining a runner box, security implications of self-hosting.

2. **Download dlls at workflow-time from a private artefact source.**
   Could host them on a private GitHub repo + use a PAT secret to
   download. More moving parts than vendoring; same redistribution
   footprint. Implement this if vendoring genuinely isn't an option
   for your situation.
