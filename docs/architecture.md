# SwagMod architecture — initial iteration

## Project boundaries
- **Domain:** framework-independent pack models and schema checks.
- **Infrastructure:** public GitHub raw manifests; verified package downloads; ZIP extraction guards.
- **Valheim adapter:** Steam library detection, Windows loader bootstrap and BepInEx deployment.
- **Application:** orchestration and launch through Steam.
- **Desktop:** Avalonia UI; never directly manages game files.

## Initial deployment behavior
A pack with a pinned loader is assembled into a fresh candidate under `%LOCALAPPDATA%/SwagMod/profiles/valheim/<pack>/`. All archive hashes are verified before the game folder changes. Loader files are recognized by expected BepInEx paths; mod files go under unique package IDs in `BepInEx/plugins`.

**Isolated:** the profile retains `BepInEx`; Windows `mklink /J` exposes it at the game root, with Doorstop bootstrap files copied to the game root.

**Direct:** the assembled `BepInEx` directory is copied to a temporary directory under the game root, then promoted. The same loader bootstrap files are copied.

Both modes reject any unmanaged BepInEx directory or root bootstrap. Updates require ownership hashes to match. Other pack/profile and mode changes are intentionally refused in Alpha 0.1.

## Next engineering work
1. Test compile/build on GitHub Actions and fix SDK/API changes.
2. Add reversible activation/deactivation and robust durable transaction/recovery journals.
3. Validate a real BepInEx package layout against a pinned, reviewed archive.
4. Improve config-preservation rules, dependency solver and package mapping.
5. Build a modpack publisher and multi-game adapter interfaces.

## Threat model
Remote pack manifests are untrusted until curated. Only HTTPS download URLs from the allowlist are accepted, redirect hosts are revalidated, and archive paths must remain under disposable staging. SHA-256 checks determine integrity against a release lockfile but do not authenticate the publisher. Future: signed lockfiles and policy-based publisher trust.
