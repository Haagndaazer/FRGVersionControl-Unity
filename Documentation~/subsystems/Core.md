# Core Module

## Overview

The Core module contains the foundational components of the FRGVersionControl system, including lock management, SVN operations, and data structures.

## Components

### FileLockManager.cs

The central singleton manager that coordinates all file locking operations.

**Responsibilities:**
- Lock acquisition and release
- Lock state caching and refresh (5-minute periodic via `FileLockSyncService` + on-focus refresh + on-WC-state-change refresh)
- Identity bootstrap (reads SVN username from per-WC auth cache via `SvnAuthCacheReader`)
- Admin permission checks
- Configuration loading (admin emails, lockable file types)
- Lock validation and conflict detection

**Key Methods:**
- `Initialize()` - Sets up the manager with user identity from SVN's auth cache + configs
- `LockFile(assetPath)` - Attempts to acquire a lock on a file
- `UnlockFile(assetPath, force)` - Releases a lock
- `RevertAndUnlockFile(assetPath, alsoRevertMeta)` - Reverts local changes then unlocks
- `RefreshLockCache()` - Syncs lock state from SVN via `svn status -u --xml`
- `GetFileLock(assetPath)` - Returns the current `FileLock` for a path (cache lookup)
- `IsLockedByMe(assetPath)` / `IsLockedByOther(assetPath)` - Ownership checks (case-insensitive)
- `IsAdmin()` - Checks if current user is in `adminEmails`
- `IsLockableFileType(assetPath)` - Validates extension against `lockable-file-types.json`

### SvnCommandRunner.cs

Low-level SVN command execution wrapper.

**Responsibilities:**
- Execute `svn` / `svnlook` commands with proper error handling
- Centralize cert-trust flags (`--non-interactive --trust-server-cert-failures unknown-ca,cn-mismatch`)
- Centralize per-WC config dir routing (`--config-dir <ProjectRoot>/.svn-tooling-config`) so two Unity instances on the same machine each have their own auth cache
- Parse SVN's XML status output via `XDocument`
- Resolve the `svn` binary across TortoiseSVN/VisualSVN install paths (Windows)

**Key Methods:**
- `Run(arguments, workingDirectory, timeoutSeconds)` - Spawns `svn.exe` and returns `SvnResult`
- `Lock(paths[], message)` / `LockForce(paths[], message)` / `Unlock(paths[], force)`
- `Status(path)` / `StatusUpstream(path)` / `StatusUpstreamAll(workingCopyRoot)` - WC-only and server-side
- `Info(path)` - Returns repo URL, revision, lock metadata
- `Commit(paths[], message)` - Throws `ArgumentException` for null/empty paths (Phase 1 Addendum Test 2 — bare `svn commit` is unsafe)
- `Revert(paths[], alsoRevertMeta)` - Discards local mods. NOTE: read-only flag restoration is conditional on cached lock_token state (Phase 2 closure Test 2 boundary); callers in the override path re-apply `attrib +R` explicitly.
- `AddToChangelist(paths[], changelistName)` / `RemoveFromChangelist(paths[])`
- `Cleanup(workingCopyRoot)` / `VacuumPristines(workingCopyRoot, timeoutSeconds)`
- `HashSha256(path)` - Working-tree content hash for override clobber detection

### Svn/SvnAuthCacheReader.cs

Reads SVN's on-disk auth cache to derive the current SVN username for the repo, without storing identity strings in any committed file.

**Format:** `<config-dir>/auth/svn.simple/<hash>` files in SVN's K/V record format. `username`, `passtype`, `svn:realmstring` are plain text; `password` may be wincrypt-encrypted on Windows but isn't needed here.

**Realm matching:** server-scoped (e.g., `<https://localhost:443> VisualSVN Server`), not repo-scoped. Match by host:port substring, not full equality.

**Bootstrap (once per WC, from a real terminal):**
```
svn info <repo-url> --config-dir <ProjectRoot>/.svn-tooling-config \
    --username <user> --trust-server-cert-failures unknown-ca,cn-mismatch
```

### FileLockData.cs

Data structures for lock state.

**Classes:**
- `FileLocks` - Root container for all locks (in-memory cache)
- `FileLock` - Individual lock record with metadata + `lockState` field (K | O | B | T | "") for B/T overlay rendering
- `AdminConfig` - Admin user configuration loaded from `admin-config.json`
- `LockableFileTypesConfig` - File-extension config loaded from `lockable-file-types.json`

**Lock Metadata:**
- File path (relative to project root, forward-slash normalized — SVN emits Windows backslashes via `--xml`)
- Owner SVN username (case-insensitive comparison; SVN preserves the case the user authenticated with)
- Display name + email (UI)
- Machine name (set only when self holds the lock; SVN doesn't track machine in lock metadata)
- Lock timestamp
- Lock state (K = held by me, O = held by other, B = broken by admin, T = stolen)

### Override/

The Ignore-on-Commit workflow (locked-but-locally-editable). Folder + class names retain `Override` for repo continuity; the artist-facing terminology is "Ignore on Commit", aligned with SVN's `ignore-on-commit` changelist. Lives in `Editor/Core/Override/`:
- `OverrideEntry.cs` — schema for one sidecar entry (`pre_ignore_on_commit_revision`, `post_edit_content_sha256`, `lock_holder_at_start`)
- `OverrideRegistryStore.cs` — atomic-write sidecar at `.svn/ignore-on-commit-registry.json`
- `FileLockOverrideManager.cs` — `SetIgnoreOnCommit` / `ClearIgnoreOnCommit` (Stash/Pop deleted; `svn shelve` unavailable in 1.14.5)
- `OverrideConversion.cs` — Mode B happy path: lock + commit + drop sidecar (`ConvertIgnoreOnCommitToLock`)
- `OverrideReconciler.cs` — Mode A (upstream changed) + Mode B (lock released) detection

## Configuration System

### Admin Configuration
Location: `Assets/FRGVersionControl/admin-config.json`
```json
{
  "adminEmails": ["admin@example.com"],
  "fileLockingEnabled": true,
  "currentDisplayName": "Display name",
  "currentEmail": "history@example.com"
}
```

The `currentSvnUsername` field used in Phase 2 has been removed — username now comes from SVN's auth cache via `SvnAuthCacheReader`.

### Lockable File Types Configuration
Location: `Assets/FRGVersionControl/lockable-file-types.json`
```json
{
  "lockableExtensions": [".unity", ".prefab", ".mat", ...]
}
```

The same list is duplicated in the server-side pre-commit hook (`hooks/pre-commit.ps1`); keep in sync.

## Integration

The Core module is used by:
- **Hooks** - For lock validation during asset save/move (`FileLockAssetModificationProcessor`)
- **Services** - Background sync (`FileLockSyncService`), wc.db mtime + focus-gain detection (`FileLockSvnDetectionService`)
- **UI** - Project window overlay (lock-state icons), Lock Manager window, context menu
- **Tools** - SetupWizard (5 read-only preflight checks), Maintenance (`svn cleanup --vacuum-pristines`)

## SVN Lock Mechanics

The toolset uses native SVN locking:
- `svn:needs-lock` property makes lockable files read-only on disk
- `svn lock` makes them writable + registers a server-side authoritative lock
- `svn unlock` releases the lock + restores read-only
- `svn commit` of a modified `svn:needs-lock` file requires the lock (server-side enforcement; client cannot bypass)

Locks auto-release on commit by default (Phase 1 finding: `[miscellany] no-unlock = no` is the default).

## Per-WC SVN Config Isolation

`SvnCommandRunner` always passes `--config-dir <ProjectRoot>/.svn-tooling-config` so each Unity instance's SVN operations route through a per-WC config + auth cache. This lets two Unity instances on the same machine operate as distinct SVN identities, which the global `%APPDATA%/Subversion/` cache cannot do (it's realm-keyed, not user-keyed).

The `.svn-tooling-config/` directory is svn:ignored at the repo root.

## Async migration + main-thread marshaling (Phase 4 Step 0)

`SvnCommandRunner` exposes `Task<T> XxxAsync(...)` wrappers for every method that delegates to `Run()`. Use these from any UI / panel / editor-window code path that should NOT block the main thread.

**Capturing Unity's main SynchronizationContext:** `SvnCommandRunner.s_UnityMainSync` is captured at static init (which runs on Unity's main thread). Use it to marshal continuations back from background threads:

```csharp
var result = await SvnCommandRunner.CommitAsync(paths, msg, ct);
SvnCommandRunner.s_UnityMainSync.Post(_ =>
{
    // safe to touch EditorWindow / AssetDatabase / UnityEngine APIs here
    Repaint();
}, null);
```

Equivalent: `EditorApplication.delayCall += () => { ... };` — pick one per file and stay consistent. Direct `UnityEngine` API calls from background continuations crash on platform-API checks.

**wc.db SQLite contention retry:** every `Run()` invocation auto-retries up to 3 times with exponential backoff (100ms / 200ms / 400ms) on `E155004` (working copy locked). After 3 failures, the error surfaces to caller normally. This handles the case where the panel's background async refresh races with the modification processor or another in-flight svn op.

**Cancellation:** every `XxxAsync` accepts `CancellationToken ct = default`. Pre-cancellation throws on `await`; mid-execution cancellation completes the underlying svn process but raises on the continuation. Use a `CancellationTokenSource` per long-running panel operation (Commit, Update, Checkout) and cancel on window-close or user-explicit-cancel.

**Progress callbacks:** unbounded operations (`StatusUpstreamAllAsync`, `GetAllFileStatusesAsync`, `CommitAsync`, `UpdateAsync`, `CheckoutAsync`) accept `IProgress<string> progress = null` for inline status messages.

## Silence / temporary-disable toggles (Phase 4 Step 0)

`Services/FileLockSilenceService` exposes counter-based scoping for tools that should NOT trigger user-facing prompts or modification-processor handling:

- `RequestSilence()` / `ClearSilence()` — suppress prompts (lock dialogs, override modals, folder-add prompts). Used by test runners, asset processors, build pipelines.
- `RequestTemporaryDisable()` / `ClearTemporaryDisable()` — suspend the entire modification-processor pipeline. Used for sweeping batch file changes that would otherwise trigger thousands of dialogs.

Always pair Request/Clear in `try/finally`. Counters clamp at 0 if mismatched (with a warning log). Both counters are independent.
