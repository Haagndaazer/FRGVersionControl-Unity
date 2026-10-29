# UI Module

## Overview

The UI module provides visual interfaces for interacting with the FRGVersionControl system, including windows, overlays, and context menus.

## Components

### FileLockWindow.cs

Main window for viewing and managing all file locks in the project.

**Access:** `Tools > FRG Version Control > Lock Manager`

**Features:**
- List view of all locked files
- Filter by current user's locks
- Display lock metadata (user, email, machine, timestamp)
- Refresh lock state from Git
- Unlock owned files
- Force unlock for admins
- Visual status indicators (green = yours, yellow = others)

**Layout:**
- Header with refresh and filter controls
- Scrollable list of locks with details
- Action buttons per lock (unlock/force unlock)
- Status messages for operations

**Key Methods:**
- `ShowWindow()` - Opens the window
- `RefreshLocks()` - Updates lock list from Git
- `DrawLockItem(lock)` - Renders individual lock entry
- `HandleUnlock(assetPath)` - Processes unlock action

### FileLockHistoryWindow.cs

Window displaying chronological history of all lock operations.

**Access:** `Tools > FRG Version Control > Lock History`

**Features:**
- Timeline view of lock/unlock events
- Filterable by action type or user
- Search by file path
- Shows operation details and timestamps
- Export history to CSV
- Clear history option

**Display Information:**
- Action icon (lock/unlock)
- Timestamp with relative time
- File path
- User and machine information
- Action-specific details

**Key Methods:**
- `ShowWindow()` - Opens the history window
- `RefreshHistory()` - Loads history from service
- `DrawHistoryEntry(entry)` - Renders individual event
- `ExportToCSV()` - Saves history to file

### FileLockProjectWindowOverlay.cs

Overlay that displays lock icons in Unity's Project window.

**Features:**
- Shows lock status icons next to assets
- Color-coded indicators:
  - **Green lock** - Locked by you
  - **Red lock** - Locked by someone else
  - **No icon** - Not locked
- Works in both list and grid view
- 16px icons for clarity
- Positioned on the right side of asset names

**Implementation:**
- Uses Unity's `ProjectWindowCallback` attribute
- Hooks into `OnProjectWindowGUI()`
- Calculates icon position based on view mode
- Queries `FileLockManager` for lock status

**Performance:**
- Caches lock state to minimize Git queries
- Updates on lock operations
- Lightweight icon rendering

### FileLockContextMenu.cs

Context menu items for right-click operations in Project window.

**Menu Items:**

#### `Assets/FRG Version Control/Lock File`
- Lock the selected asset
- Disabled if already locked by you
- Shows error if locked by someone else

#### `Assets/FRG Version Control/Unlock File`
- Unlock the selected asset
- Only available if you own the lock
- Option to revert changes on unlock

#### `Assets/FRG Version Control/Force Unlock` (Admins only)
- Admin-only force unlock
- Warns before unlocking others' files
- Requires admin email in config

#### `Assets/FRG Version Control/View Lock Info`
- Shows detailed lock information
- Displays owner and timestamp
- Available for any locked file

**Validation:**
- Menu items auto-enable/disable based on state
- Admin permissions checked dynamically
- Clear feedback on operation results

## Visual Design

### Color Scheme
- **Green (#4CAF50)** - Your locks (safe to edit)
- **Red (#F44336)** - Others' locks (blocked)
- **Yellow (#FFC107)** - Warnings/stale locks
- **Gray (#9E9E9E)** - Disabled states

### Icons
- Lock icons are 16x16 pixels for clarity
- Semi-transparent background for readability
- Positioned consistently across views

## User Workflows

### Locking a File
1. Right-click asset in Project window
2. Select `FRG Version Control > Lock File`
3. File is locked, green icon appears

### Viewing Lock Status
1. Look for colored lock icon in Project window
2. Or open Lock Manager window for full list
3. Or check context menu for detailed info

### Managing Locks
1. Open Lock Manager (`Tools > FRG Version Control > Lock Manager`)
2. See all locks with filters
3. Unlock your files or force unlock as admin
4. Check history for audit trail

## Integration Points

All UI components depend on:
- **FileLockManager** - Core lock operations
- **FileLockHistoryService** - History data
- **FileLockSyncService** - Sync operations

UI triggers Core operations and displays results to users.
