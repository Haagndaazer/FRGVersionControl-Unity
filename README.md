# FRG Version Control

Editor-only SVN-backed file-locking toolset for Unity teams. Surfaces SVN lock state in the Unity Editor, manages overrides for the `svn:needs-lock` workflow, runs working-copy maintenance, and integrates with TortoiseSVN.

## Install

In Unity's **Package Manager** (`Window → Package Manager`):

1. Click the `+` button → **Add package from git URL...**
2. Paste:
   ```
   https://github.com/Haagndaazer/FRGVersionControl-Unity.git
   ```
3. Click **Add**. Unity downloads, compiles, and the package is ready.

The install URL is the public **release mirror** (`FRGVersionControl-Unity`), which GitHub
Actions refreshes from this repository on every push to `main` — it contains only the Unity
package, so no GitHub sign-in is ever required. The old
`FRGVersionControl.git?path=Plugins/Unity` URL points at a private repository and prompts
every teammate for credentials; replace it in `Packages/manifest.json`.

Pin to a specific release tag (recommended for production):
```
https://github.com/Haagndaazer/FRGVersionControl-Unity.git#0.2.88
```

## Requirements

- **Unity 6 (6000.0)** or newer.
- **TortoiseSVN 1.14 with command-line client tools** (any install folder; svn-core finds it through the TortoiseSVN registry entry). During the installer, check the "command line client tools" box (off by default).
- The Unity project folder must be a versioned folder in an SVN checkout — either the checkout itself (for example `trunk` checked out on its own) or a folder inside it (for example the whole repository checked out, with the project in `trunk/`). Open the project folder itself in Unity Hub.

## Quickstart

After install, the toolset auto-runs its first-launch setup:

- `Assets/FRGVersionControl/admin-config.json` and `lockable-file-types.json` are scaffolded into your project on first launch. Both are user-editable and SVN-committable.
- Open **`Window → FRG Version Control`** for the daily-driver window (Changes / Locks tabs, footer with Setup Wizard and Run Maintenance buttons).
- Right-click any asset → **`Assets → FRG Version Control → ...`** for per-asset actions (Lock, Unlock, Show Log, Edit SVN Properties, Set/Clear Ignore on Commit).

See [Documentation~/USER_GUIDE.md](Documentation~/USER_GUIDE.md) for the full artist guide.

## Features

- **Auto-lock on save** for `svn:needs-lock` file types (scenes, prefabs, materials, FBX, textures, …).
- **Pending Changes panel** that lets artists commit, update, and revert without leaving Unity.
- **Ignore-on-Commit workflow** for editing locally when another user holds the lock (server-side enforcement prevents accidental commit).
- **Inline conflict resolver** (Take Mine / Take Theirs / Open externally) for SVN `update` conflicts.
- **Per-asset history viewer** with paginated SVN log + external diff tool integration.
- **SVN properties editor** for `svn:needs-lock`, `svn:ignore`, custom properties.
- **SceneView banner** when opening a scene locked by another user.
- **Working-copy maintenance**: `svn cleanup --vacuum-pristines` from the footer button.

## How it works

- Editor-only assembly (`FRG.VersionControl.Editor.dll`); no runtime cost.
- All SVN commands are routed through a per-WC config dir (`<ProjectRoot>/.svn-tooling-config`) for multi-identity isolation.
- Cache-first reads + async writes — Unity never freezes on network ops.
- 30 NUnit tests cover the SVN status parser and override workflow primitives.

## Running the tests

The package's NUnit tests are gated by the `UNITY_INCLUDE_TESTS` define constraint (standard UPM pattern — keeps test compilation out of consumer projects that don't want it). To run them via Unity Test Runner, add `"testables"` to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.frg.versioncontrol": "https://github.com/Haagndaazer/FRGVersionControl-Unity.git"
  },
  "testables": [
    "com.frg.versioncontrol"
  ]
}
```

After saving, Unity recompiles and `Window → General → Test Runner → EditMode` shows the `FRG.VersionControl.Editor.Tests` assembly with all 30 tests. Run them via the Test Runner UI or programmatically via `UnityEditor.TestTools.TestRunner.Api`.

If `"testables"` is not present, the tests are silently excluded — by design, so most consumers don't pay the test-compile cost.

## Repo layout

This package lives inside a multi-engine plugin monorepo. Repo structure:

```
FRGVersionControl/
├── Plugins/
│   ├── Unity/         ← this package
│   └── Unreal/        (future)
├── docs/                        ← project docs + InitialPhasesDocumentation/ history (Phase 1-4 findings)
├── .mcp.json
└── CLAUDE.md
```

The Unity package is the `Plugins/Unity/` subfolder, hence `?path=Plugins/Unity` in the install URL.

## Issues / contributions

File issues at https://github.com/Haagndaazer/FRGVersionControl/issues.

## License

See [LICENSE.md](LICENSE.md).
