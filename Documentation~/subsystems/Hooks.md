# Hooks Module

## Overview

The Hooks module integrates the FRGVersionControl system into Unity's Editor lifecycle, intercepting asset operations and automatically managing locks when files are modified.

## Components

### FileLockAssetModificationProcessor.cs

Unity's `AssetModificationProcessor` subclass that intercepts asset operations.

**Responsibilities:**
- Intercept asset save/move/delete operations
- Automatically acquire locks on lockable file types
- Block operations if lock acquisition fails
- Show user dialogs for lock conflicts
- Track which assets are currently being saved to prevent recursion

**Key Callbacks:**

#### `OnWillSaveAssets(string[] paths)`
Called before Unity saves assets.
- Filters to lockable file types using `FileLockManager.IsLockableFileType()`
- Attempts to lock each file via `FileLockManager.TryLockFile()`
- Shows conflict dialog if file is locked by another user
- Returns null to allow save, or throws to block

#### `OnWillMoveAsset(string sourcePath, string destinationPath)`
Called before asset is moved/renamed.
- Checks if source file is locked by another user
- Returns failure message to block operation if locked

#### `OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)`
Called before asset is deleted.
- Checks if file is locked by another user
- Returns failure status to block deletion if locked

**Lock Conflict UI:**
- Shows dialog with locker's name, email, machine, and time
- Displays how long ago the lock was acquired
- Options: Cancel operation or continue without lock

### FileLockInitializer.cs

Editor initialization class using `InitializeOnLoad` attribute.

**Responsibilities:**
- Initialize `FileLockManager` when Unity Editor starts
- Ensure system is ready before any asset operations occur
- Handle initialization errors gracefully

**Lifecycle:**
- Static constructor runs on Editor load
- Calls `FileLockManager.Instance.Initialize()`
- Logs initialization status

## Integration Flow

1. **Editor Startup** → `FileLockInitializer` initializes the system
2. **User Modifies Asset** → Unity triggers `OnWillSaveAssets()`
3. **Lock Check** → System checks if file type is lockable
4. **Lock Acquisition** → Attempts to acquire lock via Git
5. **Success** → Save proceeds normally
6. **Conflict** → Shows dialog, blocks save if canceled

## File Type Filtering

The hooks use centralized configuration from `FileLockManager.IsLockableFileType()` which reads from `lockable-file-types.json`. Only configured extensions are locked:
- `.unity`, `.prefab`, `.mat` (Unity assets)
- `.fbx`, `.psd`, `.png` (Binary assets)
- `.wav`, `.mp4` (Media files)

Source code files (`.cs`, `.js`, `.shader`) are intentionally NOT locked to allow normal text-based merging.

## User Experience

- **Transparent** - Locks are acquired automatically, no manual action needed
- **Non-blocking** - Only blocks if file is locked by someone else
- **Informative** - Clear error messages with lock details
- **Graceful** - Falls back to warning on errors, doesn't break workflow
