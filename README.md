# SwagMod Manager

A GitHub-controlled Valheim modpack manager with an Avalonia desktop GUI and a conservative **hybrid isolated-profile / direct-injection** engine.

> **Alpha 0.1 / experimental.** This is initial source code, not a production-tested installer. Back up any existing BepInEx installation and your Valheim data before testing. The example pack contains no loader or mods and cannot install anything.

## What works in this first source milestone

- Dark-themed Windows desktop window for detecting Steam Valheim and selecting a GitHub pack.
- Load and validate `pack.json` and `pack.lock.json` from a public GitHub repository and optional folder.
- Preview a deployment plan and show installed state.
- Download HTTPS packages from allowed GitHub/Thunderstore hosts, verify exact SHA-256 lockfile checksums, and cache archives.
- Safely extract staged ZIP files with path traversal, symlink, entry count and expanded-size guards.
- Conservative hybrid deployment: **Isolated** profile with Windows directory junction, or **Direct** BepInEx copy.
- Reject unmanaged game-root loaders and externally modified managed files rather than overwriting them.
- Preserve backups during updates; attempt rollback after deployment errors.
- Start Valheim via Steam.
- Windows GitHub Actions CI tests and a self-contained win-x64 publish artifact.

## Not implemented yet

Profile switching, vanilla deactivation, dependable crash-recovery journals, config merging, server deployment, search/browsing mods, in-app pack publishing, full mod dependency solving, automatic self-updates, and strong end-to-end Windows install testing.

## Build

Install the **.NET 10 SDK**, then from the repository root:

```powershell
dotnet test tests/SwagMod.UnitTests/SwagMod.UnitTests.csproj
dotnet run --project src/SwagMod.Desktop/SwagMod.Desktop.csproj
```

For a self-contained Windows build:

```powershell
dotnet publish src/SwagMod.Desktop/SwagMod.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/SwagMod-win-x64
```

GitHub Actions publishes a downloadable ZIP under each successful workflow run's **Artifacts**. It does not automatically create a release.

## Pack repository contract

A pack source is a public GitHub `owner/repo`, optionally containing its pack files in a folder. `pack.json` describes identity/game; `pack.lock.json` pins every package archive with `id`, `version`, `kind` (loader/plugin), `url`, `sha256`, and optional `dependencies` (IDs of other pinned packages). Include **exactly one** loader of kind `loader` for an installable Valheim pack. The loader ZIP must contain `BepInEx/core`, `winhttp.dll`, and `doorstop_config.ini`. Plugin archives are placed in `BepInEx/plugins/{id}`.

Only create hashes from real downloaded, reviewed archives. Never fabricate SHA-256 values. Existing packs from r2modman are not directly compatible with this format without conversion.

The checked-in `examples/swagheim` folder is a *noninstallable* smoke-test manifest to demonstrate GitHub synchronization without modifying anyone's game.

## Safety notes

- Alpha 0.1 targets **Windows + Steam Valheim** only.
- **Do not point the installer at a game directory with mods you want to keep**; unmanaged installs will be refused, and the rollback feature has not been battle-tested.
- A checksum verifies file integrity, not trustworthiness. Mod DLLs are executable code.
- If Valheim is open, installation is blocked.
- Launching from Steam after activating an isolated profile still loads that active BepInEx junction.
- Backups are retained in `%LOCALAPPDATA%/SwagMod/backups`.

## Architecture

`SwagMod.Domain` defines manifests, lockfiles, validation and deployment objects.
`SwagMod.Infrastructure` implements GitHub fetch, verified downloads and secure staging.
`SwagMod.Games.Valheim` handles Steam discovery and BepInEx activation.
`SwagMod.Application` orchestrates the core and launch.
`SwagMod.Desktop` provides a native GUI without platform-specific XAML.
`SwagMod.UnitTests` covers first-pass validation and host restrictions.

See [docs/architecture.md](docs/architecture.md) for the technical plan.
