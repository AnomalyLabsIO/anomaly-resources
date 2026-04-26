# manifest-analyzer (vendored)

Vendored copy of the `ManifestAnalyzer` .NET CLI used by `.github/workflows/manifest-tracker.yml`
to keep `builds/scpsl_builds.json` in sync with Steam.

This directory exists so the workflow can build the analyzer in-tree without depending
on a separate repo. The canonical source is maintained alongside the `Anomaly` launcher
codebase; updates land here via copy.

## What it does

The CLI has two modes:

- **CSV mode** (`--csv <path>`): the original behavior, used locally to backfill a SteamDB
  patch-history CSV with resolved game versions.
- **Tracker mode** (`--tracker <path>`): the mode the workflow uses. Runs an authenticated
  Steam PICS query for SCP:SL (app `700330`, depot `700331`), appends any new manifest IDs
  to the JSON, and resolves their `gameVersion` by downloading
  `GameAssembly.dll` + `global-metadata.dat` and extracting the bytes of
  `GameCore.Version$$.cctor`.

## Build

```sh
dotnet build tools/manifest-analyzer/ManifestAnalyzer.csproj -c Release \
  /p:Il2CppDumperDir=$(Anomaly checkout)/Anomaly.InstallerCore/Il2CppDumper
```

`Il2CppDumperDir` must point at a directory that contains
`Il2CppDumper.dll` + `Mono.Cecil*.dll`. The workflow sparse-checks-out the public
`AnomalyLabsIO/Anomaly` repo to obtain it.

## Authentication (workflow context)

In order of preference:

1. `STEAM_REFRESH_TOKEN_JSON` — `{"AccountName":"...","RefreshToken":"..."}` for silent reuse.
2. `STEAM_USERNAME` + `STEAM_PASSWORD` (+ optional `STEAM_SHARED_SECRET` base64 TOTP seed).
3. Stored DPAPI session under `%APPDATA%\Anomaly\steam` (developer machines only).
4. QR fallback (interactive only — never fires in CI).

Auth failures exit with code `78` so the workflow can distinguish them from data errors.

## Themida-protected builds

Recent SCP:SL builds ship Themida/WinLicense-packed `GameAssembly.dll`. The analyzer
detects this and shells out to `unlicense.exe`. The workflow points it at the in-repo
`patching/unlicense.exe` via `ANOMALY_UNLICENSE_PATH`. This is why the workflow runs on
`windows-latest` — `unlicense.exe` is Windows-only.
