# Changelog

All notable changes to the FRG Version Control package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] — 2026-05-12

### Added

- Initial extraction of the FRG Version Control toolset from its SVN-tracked test project at `https://localhost/svn/unity-locktest/trunk/locktest-admin-unity/Assets/FRGVersionControl/` to a standalone UPM git-installable package.
- Auto-scaffold of `admin-config.json` and `lockable-file-types.json` into the consuming project's `Assets/FRGVersionControl/` on first launch (defaults shipped at `Editor/DefaultConfig~/`).
- `FRG.VersionControl.Editor` assembly definition (Editor-only, `autoReferenced: false`).
- `FRG.VersionControl.Editor.Tests` assembly definition (gated by `UNITY_INCLUDE_TESTS` define constraint; 31 NUnit tests).

### Inherited from prior SVN development (Phases 1-4, 2026-05-06 → 2026-05-12)

The pre-extraction history is preserved at the original VisualSVN repo and documented in:

- `docs/InitialPhasesDocumentation/PHASE1_FINDINGS.md` — SVN locking, override workflow, auto-props validation (2026-05-06)
- `docs/InitialPhasesDocumentation/PHASE2_FINDINGS.md` — Unity tooling Phase 2 + closure (2026-05-07 → 2026-05-08)
- `docs/InitialPhasesDocumentation/PHASE3_FINDINGS.md` — Multi-user setup, per-WC config-dir, lock state parser (2026-05-08)
- `docs/InitialPhasesDocumentation/PHASE3_CLOSURE_FINDINGS.md` — B/K/O/T/None lock-state parser regression net
- `docs/InitialPhasesDocumentation/TOOLING_NOTES.md` — Phase 4 polish per-feature dev notes (Steps 0-6: async migration, status cache, pending-changes window, conflict resolver, ModProc extensions, telemetry, docs)
- `docs/InitialPhasesDocumentation/ARTIST_QUICKSTART.md` — one-page user guide

Notable prior milestones (cognition graph + SVN log):
- **2026-05-11** — Window consolidation: `FileLockPendingChangesWindow` + `FileLockWindow` merged into `FrgVersionControlWindow` (single window, 2-tab Changes/Locks + persistent footer).
- **2026-05-11** — FileLocking → FRGVersionControl rebrand: folder, namespaces, log prefix, menus consolidated under `Window → FRG Version Control` and `Assets → FRG Version Control → *`. Lock History UI removed; service retained.
- **2026-05-08** — Phase 3 closure: lock-state parser handles K/O/B/T/None correctly; regression net at `SvnCommandRunner.ParseStatusXmlForTest` + `ParseLockInfoFromXmlForTest`.
- **2026-05-07** — Phase 2 closure: override workflow primitives validated end-to-end; `.svn/pristine/` vacuum-pristines made mandatory.
- **2026-05-06** — Phase 1: validated SVN file-locking workflow + override + auto-props.

## [0.2.8] — 2026-05-15

### Changed

- **Terminology rename: "Begin/Clear Override" → "Set/Clear Ignore on Commit"** across all user-facing surfaces (menus, dialogs, status banners, tooltips, the row badge — `OVR` → `IGN`), aligned with SVN's `ignore-on-commit` changelist nomenclature. Public API methods renamed to match: `BeginOverride` → `SetIgnoreOnCommit`, `EndOverride` → `ClearIgnoreOnCommit`, `ConvertOverrideToLock` → `ConvertIgnoreOnCommitToLock`. Namespace, class names, file names, and the `Core/Override/` folder are retained for repo continuity.
- **Persisted state path change.** Sidecar moved from `.svn/lock-overrides.json` to `.svn/ignore-on-commit-registry.json`. JSON field `pre_override_revision` renamed to `pre_ignore_on_commit_revision`. EditorPrefs key `FRGVersionControl.Override.SuppressModeAModal` and SessionState key prefixes renamed under `FRGVersionControl.IgnoreOnCommit.*`. No migration shim ships — any pre-existing sidecar would be orphaned; the only known sidecar was verified empty before the cutover.

## Unreleased

- Auto-bootstrap of per-WC SVN auth cache from `%APPDATA%\Subversion\auth\` (v0.2 candidate).
- Unreal Engine plugin (separate `Plugins/Unreal/` deliverable).
