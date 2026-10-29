# Services Module

## Overview

The Services module provides background services that enhance the FRGVersionControl system with automatic operations, history tracking, and synchronization capabilities.

## Components

### FileLockAutoUnlockService.cs

Automatically releases locks on idle files to prevent forgotten locks.

**Responsibilities:**
- Monitor file modification times
- Track when locks have been idle (no modifications)
- Automatically unlock files after 30 minutes of inactivity
- Skip auto-unlock for currently open/selected assets

**Configuration:**
- `AUTO_UNLOCK_DELAY_MINUTES = 30` - Time before auto-unlock
- `CHECK_INTERVAL_SECONDS = 60` - How often to check for idle locks

**Lifecycle:**
- Starts automatically via `InitializeOnLoad`
- Runs periodic checks using `EditorApplication.update`
- Stops when Unity Editor closes

**Safety Features:**
- Never unlocks currently selected assets
- Compares file modification time vs lock time
- Only unlocks if no changes detected in 30+ minutes
- Logs all auto-unlock operations

**Usage:**
Service runs transparently in the background. No manual intervention needed.

### FileLockHistoryService.cs

Tracks and persists the history of all lock operations.

**Responsibilities:**
- Record lock/unlock operations with timestamps
- Store history persistently in JSON file
- Provide API for querying history
- Maintain fixed-size history buffer (100 entries)

**History Entry Data:**
- Action type (Locked/Unlocked)
- File path
- User name and email
- Machine name
- Timestamp
- Optional additional details

**Storage:**
- Location: `ProjectSettings/FileLockHistory.json`
- Format: JSON array of history entries
- Persistence: Saved after each operation

**Key Methods:**
- `RecordLock(assetPath, userName, userEmail, machineName)` - Log lock acquisition
- `RecordUnlock(assetPath, userName, userEmail)` - Log lock release
- `GetHistory()` - Retrieve all history entries
- `GetHistoryForFile(assetPath)` - Get file-specific history

**Integration:**
Used by `FileLockHistoryWindow` to display operation timeline.

### FileLockSyncService.cs

Synchronizes lock state between Git remote and local repository.

**Responsibilities:**
- Push local lock changes to remote
- Pull remote lock updates to local
- Handle merge conflicts in lock file
- Provide sync status feedback

**Sync Operations:**

#### Push
- Commits local lock changes
- Pushes `file-locks` branch to remote
- Handles push conflicts

#### Pull
- Fetches remote `file-locks` branch
- Merges changes into local worktree
- Refreshes lock cache after merge

**Key Methods:**
- `PushLockChanges()` - Push local locks to remote
- `PullLockChanges()` - Fetch and merge remote locks
- `SyncLocks()` - Perform full two-way sync

**Error Handling:**
- Returns success/failure status
- Logs detailed error messages
- Gracefully handles network failures
- Uses custom merge driver for conflicts

**Usage:**
Can be triggered manually from UI or automatically on lock operations.

## Service Initialization

All services use Unity's `[InitializeOnLoad]` attribute to start automatically when the Editor loads. They register callbacks with `EditorApplication` for periodic operations.

## Integration with Core

Services depend on `FileLockManager` for:
- Accessing current lock state
- Performing lock/unlock operations
- Refreshing cached data

## Best Practices

- **Auto-unlock** prevents forgotten locks but respects active work
- **History** provides audit trail for debugging conflicts
- **Sync** keeps team in sync but requires network connectivity

Configure sync frequency based on team size and workflow.
