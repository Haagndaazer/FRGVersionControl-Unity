# FRG Version Control System - User Guide

A complete tutorial for using the file locking system to prevent merge conflicts on Unity assets.

---

## Table of Contents

1. [Getting Started](#1-getting-started)
2. [Daily Workflows](#2-daily-workflows)
3. [Lock Manager Window](#3-lock-manager-window)
4. [Context Menu Operations](#4-context-menu-operations)
5. [Lock History Window](#5-lock-history-window)
6. [Project Window Icons](#6-project-window-icons)
7. [Handling Conflicts](#7-handling-conflicts)
8. [Batch Operations](#8-batch-operations)
9. [Admin Features](#9-admin-features)
10. [Advanced Workflows](#10-advanced-workflows)
11. [Troubleshooting](#11-troubleshooting)
12. [Best Practices](#12-best-practices)
13. [Keyboard Shortcuts](#13-keyboard-shortcuts)
14. [Reference](#14-reference)
15. [FAQ](#15-faq)
16. [Glossary](#16-glossary)

---

## 1. GETTING STARTED

### 1.1 What is FRG Version Control?

File locking prevents multiple team members from editing the same binary asset file simultaneously. When you lock a file, others cannot save changes to it until you unlock it. This prevents painful merge conflicts on scenes, prefabs, and other Unity assets.

**Who should use this:** Artists, designers, and developers working on shared Unity assets in a team environment.

**What you need:** Git installed and configured, Unity 2020.3 or later.

### 1.2 Quick Start (5 Minutes)

**Tutorial: Setting Up FRG Version Control**

1. Open Unity and your project
2. Go to **Tools → FRG Version Control → Setup Wizard**
3. Click the **"Run Setup"** button
4. Wait for all 4 steps to show green checkmarks:
   - ✓ Create file-locks branch
   - ✓ Create worktree
   - ✓ Configure merge driver
   - ✓ Test merge driver
5. See "Setup Complete!" message

**What just happened?**
- A new Git branch (`file-locks`) was created to store lock metadata
- A local worktree (`.file-locks-worktree/`) was created for lock operations
- Git was configured to automatically resolve lock conflicts

### 1.3 Understanding the Basics

**Auto-Lock vs Manual Lock:**
- **Auto-Lock (Recommended)**: Files are automatically locked when you save them (Ctrl+S)
- **Manual Lock**: You explicitly lock files before editing via right-click menu

**What Gets Locked:**
- Scenes (.unity)
- Prefabs (.prefab)
- Materials (.mat)
- ScriptableObjects (.asset)
- And many more binary asset types (see Section 14.1 for full list)

**What Doesn't Get Locked:**
- Source code files (.cs, .js)
- Text configuration files (.json, .xml)
- Anything else you can safely merge in Git

**Visual Indicators:**
- Green padlock icon: Locked by you
- Red padlock icon: Locked by someone else
- These appear in the Project window next to file names

---

## 2. DAILY WORKFLOWS

### 2.1 Auto-Lock Workflow (Recommended)

**Tutorial: Editing a Scene with Auto-Lock**

1. Open a scene file by double-clicking it in Project window
2. Make your changes to the scene
3. Press **Ctrl+S** (or Cmd+S on Mac) to save
4. **What happens:**
   - File is automatically locked to you
   - You see a brief notification in the Console
   - A green padlock icon appears next to the file
5. Continue editing and saving as needed
6. When done, **commit your changes** and **push to Git**
7. **Within 5 minutes**, your lock is automatically removed

**Scenario A: Saving Multiple Files at Once**
- Select multiple files, modify them, save all
- Each file is locked individually
- Console shows result for each file

**Scenario B: Someone Else Has the File Locked**
- You try to save
- Dialog appears: "⚠️ File(s) Locked"
- Shows who has it locked and for how long
- Your save is blocked
- See Section 7 for how to handle this

**Scenario C: Auto-Lock on New Files**
- Create a new prefab/scene
- First save: Auto-lock is skipped (file not in Git yet)
- Commit the file to Git first
- Next save: Auto-lock works normally

### 2.2 Manual Lock Workflow (Optional)

**Tutorial: Manually Locking Files Before Editing**

Use manual locking when you want to "claim" a file before you start working on it.

1. Navigate to the file in Project window
2. Right-click the file
3. Select **FRG Version Control → Lock File(s)**
4. Check the Console for confirmation: `[FRGVersionControl] Locked: Assets/...`
5. A green padlock icon appears
6. Now edit the file (you already own the lock)
7. When done: Right-click → **FRG Version Control → Unlock File(s)**

**Batch Manual Locking:**
- Ctrl+Click multiple files to select them
- Right-click → FRG Version Control → Lock File(s)
- All selected files are locked at once

---

## 3. LOCK MANAGER WINDOW

### 3.1 Opening the Window

- **Menu**: Tools → FRG Version Control → Lock Manager
- **Keyboard**: Ctrl+Shift+L (or Cmd+Shift+L on Mac)

The Lock Manager is your central hub for viewing and managing all file locks.

### 3.2 Toolbar Features

**Refresh Button**
- **What it does**: Fetches the latest locks from Git
- **When to use**: After a team member locks files, or when troubleshooting
- **Background sync**: Happens automatically every 5 minutes

**Search Field**
- **What it does**: Filters locks by file path or name
- **Example**: Type "Scene" to find all locked scenes
- **Case-insensitive**: Works with uppercase or lowercase

**My Locks Only Toggle**
- **ON (default)**: Shows only YOUR locks
- **OFF**: Shows all team members' locks
- **Why default is ON**: Most of the time you only care about your own locks

**Show Stale Toggle**
- **ON (default)**: Shows all locks including old ones
- **OFF**: Hides locks older than 24 hours
- **Stale locks**: Locks over 24 hours old that others can force unlock

**Setup Button**
- **What it does**: Opens the Setup Wizard
- **Always available**: Even if file locking is globally disabled

### 3.3 Selection Bar Features

The selection bar appears below the toolbar and provides powerful batch operations.

**Select All Button**
- **What it does**: Checks all locks that you can unlock
- **Works with filters**: Only selects visible, unlockable locks
- **Disabled locks**: Locks owned by others (non-stale) can't be selected

**Deselect All Button**
- **What it does**: Unchecks all selected locks
- **Use case**: Quick way to clear your selection

**Selection Counter**
- **Format**: "Selected: 3 of 5"
- **Meaning**: 3 locks checked out of 5 unlockable locks

**Unlock Unmodified Button**
- **Format**: "Unlock Unmodified (2)"
- **What it does**: Unlocks ALL your locks that have no uncommitted changes
- **No confirmation**: It's a safe operation (no data loss)
- **Use case**: End-of-day cleanup after committing your work
- **Progress bar**: Shows progress for large batches (>5 locks)
- **Results dialog**: Shows success/failure count

**Tutorial: Using Unlock Unmodified**

1. Finish your work and commit/push all changes to Git
2. Open Lock Manager (Ctrl+Shift+L)
3. Look at "Unlock Unmodified (N)" button
4. If N > 0, click the button
5. Wait for progress bar (if >5 locks)
6. See results dialog confirming unlocks
7. Your clean locks are now released

**Unlock Selected Button**
- **Format**: "Unlock Selected (3)"
- **What it does**: Unlocks all checked locks (batch operation)
- **Confirmation dialog**: Shows detailed breakdown
- **Handles mixed types**: Your locks, stale locks, admin force unlocks
- **Options**:
  - "Unlock All": Keep uncommitted changes
  - "Revert & Unlock All": Discard all changes
  - "Cancel": Abort operation

### 3.4 Lock List Display

**Understanding Lock Entries**

Each lock is displayed as a colored box with information:

**Color Coding:**
- **Bright green background**: YOUR locks (on this machine)
- **White background**: Other users' locks

**Entry Information:**
- **Checkbox**: Check to select for batch unlock (if you can unlock it)
- **File name**: Displayed in bold at the top
- **Full path**: Gray text below name (selectable for copying)
- **Owner**: "Name (email)"
- **Machine**: Computer name where file was locked
- **Lock age**: "5 minutes ago", "2 hours ago", "3 days ago"
- **STALE indicator**: Appears if lock is >24 hours old

**Lock Entry Buttons**

For YOUR locks:
- **"Revert & Unlock"** button: Only shown if file has uncommitted changes. Discards changes and unlocks.
- **"Unlock"** button: Always shown. Unlocks without reverting changes.

For STALE locks (any owner):
- **"Force Unlock"** button: Anyone can force unlock stale locks

For OTHER users' FRESH locks:
- **"Unlock"** button: Only shown for admins
- No buttons for regular users

**Clicking on Lock Entries:**
- Click anywhere on the entry (not on buttons)
- File is selected in Project window
- File is pinged (highlighted briefly)
- Useful for quickly navigating to locked files

### 3.5 Lock Manager Workflows

**Tutorial: Finding and Unlocking Your Locks**

1. Open Lock Manager (Ctrl+Shift+L)
2. Ensure **"My Locks Only"** toggle is ON
3. Review the list of your active locks
4. For each lock you're done with:
   - Click **"Unlock"** button, OR
   - Check the checkbox for batch unlock
5. Alternatively: Click **"Unlock Unmodified"** to unlock all clean files at once

**Tutorial: Checking Who Has a File Locked**

1. Open Lock Manager
2. Turn **OFF** the "My Locks Only" toggle
3. Use the search field to find the file (e.g., type "MainScene")
4. Look at the lock entry to see:
   - Owner name and email
   - Machine name
   - How long ago it was locked
   - Whether it's stale
5. Use this info to contact the owner if needed

**Tutorial: Force Unlocking Stale Locks**

1. Open Lock Manager
2. Ensure **"Show Stale"** is ON
3. Look for locks marked **(STALE)**
4. Click **"Force Unlock"** button
5. Read the confirmation dialog explaining the lock age and owner
6. Click **"Yes, Force Unlock"** to confirm
7. Lock is removed for everyone

---

## 4. CONTEXT MENU OPERATIONS

### 4.1 Right-Click Menu Overview

Right-click any asset in the Project window to access file locking operations.

**Menu Location**: FRG Version Control (submenu with 6 options)

**Options Shown Dynamically**: Menu items appear/disappear based on:
- Whether file is locked
- Who owns the lock
- Whether you're an admin
- Whether file has uncommitted changes

### 4.2 Lock File(s)

**What it does**: Manually locks the selected file(s)

**When shown**:
- File is not currently locked, OR
- File is locked by you (re-lock is a no-op)

**Requirements**:
- File must be tracked in Git (already committed)
- Grayed out for untracked files

**Tutorial: Locking Multiple Files**

1. Ctrl+Click to select multiple prefabs in Project window
2. Right-click on any selected file
3. Choose **FRG Version Control → Lock File(s)**
4. Check Console for results
5. Green padlock icons appear on all successfully locked files

### 4.3 Unlock File(s)

**What it does**: Unlocks the selected file(s)

**Behavior varies by ownership**:
- **YOUR locks (same machine)**: Unlocks immediately
- **YOUR locks (different machine)**: Admin dialog if you're an admin
- **OTHER users' locks**: Admin-only (shows force unlock dialog)
- **Regular users on others' locks**: Error dialog explaining restriction

**Tutorial: Unlocking Your Files**

1. Select a file with a green padlock (your lock)
2. Right-click → **FRG Version Control → Unlock File(s)**
3. Lock is removed immediately
4. Padlock icon disappears

### 4.4 Revert File(s)

**What it does**: Discards uncommitted changes (Git revert)

**When shown**: File has uncommitted changes (blue checkmark icon)

**Warning**: This operation cannot be undone!

**What gets reverted**:
- The asset file itself (.prefab, .unity, etc.)
- The associated .meta file

**Tutorial: Reverting a Broken Prefab**

1. You've made changes to a prefab that broke it
2. Select the prefab in Project window
3. Right-click → **FRG Version Control → Revert File(s)**
4. Read the confirmation dialog listing files to revert
5. Click **"Revert"** to discard changes
6. File returns to its last committed state
7. Unity reloads the asset

### 4.5 Revert and Unlock File(s)

**What it does**: Combines revert + unlock in one operation

**When shown**:
- File is locked by YOU
- File has uncommitted changes

**Use case**: Abandoning work on a file you don't want to keep

**Tutorial: Abandoning Changes on a Scene**

1. You've been experimenting with a scene but don't like the results
2. Select the scene file
3. Right-click → **FRG Version Control → Revert and Unlock File(s)**
4. Confirmation dialog appears
5. Click **"Yes"** to discard changes and unlock
6. Scene reverts to last committed state
7. Lock is removed

### 4.6 View Lock Info

**What it does**: Shows detailed lock information in a dialog

**Availability**: Single file only (not available for batch selections)

**Information shown**:
- Owner name and email
- Machine name
- Lock timestamp (exact time)
- Stale status (if >24 hours)
- If not locked: "This file is not currently locked"

**Tutorial: Checking Lock Details**

1. Select a locked file in Project window
2. Right-click → **FRG Version Control → View Lock Info**
3. Dialog pops up with complete details
4. Use email to contact the owner if needed
5. Use machine name to identify which computer has the lock

### 4.7 Refresh Locks and Git Status

**What it does**: Manually refreshes all caches

**Refreshes**:
- Latest locks from Git remote
- Git status cache (which files are modified)
- Project window icons

**When to use**:
- Icons seem out of sync
- Team member just locked a file
- Troubleshooting visual issues
- After resolving Git conflicts

**How it works**:
- Runs `git fetch` on file-locks branch
- Re-scans working directory for modified files
- Repaints Project window to update icons

---

## 5. LOCK HISTORY WINDOW

### 5.1 Opening History

**Menu**: Tools → FRG Version Control → Lock History

The History window shows an audit trail of all lock and unlock operations.

### 5.2 History Toolbar

**Search Field**
- Filter history entries by file path
- Example: Type "Prefabs/" to see all prefab lock operations

**Refresh Button**
- Reloads history from lock cache
- History updates automatically when locks sync

**Clear History Button**
- Removes all local history entries
- Requires confirmation
- Useful for privacy or cleaning up old data
- Note: Only clears YOUR local history, not team history

### 5.3 Understanding History Entries

**Color Coding**:
- **Bright green**: YOUR lock operations
- **Bright red**: YOUR unlock operations
- **Light green**: Other users' lock operations
- **Light red**: Other users' unlock operations
- **Orange**: Force unlock operations

**Entry Information**:
- **Timestamp**: Local timezone, format "2025-11-12 14:30:45"
- **Action**: LOCK or UNLOCK
- **File path**: Full path to the file
- **User name**: Who performed the operation
- **(YOU)** indicator: Marks your own actions
- **(FORCED)** indicator: Marks force unlock operations
- **Time ago**: Relative time like "5m ago", "2h ago", "3d ago"

### 5.4 History Features

**Clicking on Entries**:
- Click any history entry
- File is selected in Project window (if it still exists)
- File is pinged (highlighted)
- Useful for navigating to recently locked files

**Tutorial: Investigating Lock History**

1. Team member asks "Who locked MainScene yesterday?"
2. Open Lock History window
3. Search for "MainScene" in search field
4. Scroll through results to find LOCK entries
5. Check timestamp and user name
6. See if it was later unlocked (UNLOCK entry)
7. Check for (FORCED) indicator to see if it was force unlocked

---

## 6. PROJECT WINDOW ICONS

### 6.1 Lock Status Icons

**Padlock Icons**:
- **Green padlock**: File is locked by YOU on this machine
- **Red padlock**: File is locked by someone else

**Hover Tooltips**:
- Hover over lock icon to see details
- YOUR locks: "Locked by you\n[timestamp]"
- OTHERS' locks: "Locked by [Name] ([email])\n[Machine Name]\n[timestamp]"
- Stale locks also show "(STALE)" in tooltip

### 6.2 Git Status Icons

**Additional Icons**:
- **Orange plus (+)**: Untracked file (new, not committed to Git yet)
- **Blue checkmark (✓)**: Modified file (has uncommitted changes)

**Combined Display**:
- Files can show both a lock icon AND a Git status icon
- Example: Green padlock + blue checkmark = Locked by you with changes

### 6.3 Icon Positions

**List View**:
- Icons appear on the right side of file name
- Vertically centered with the text
- Order: Git status icon first, then lock icon

**Icon View (Grid)**:
- Icons appear at bottom-right corner of thumbnail
- Side-by-side layout

---

## 7. HANDLING CONFLICTS

### 7.1 When Someone Else Has the File Locked

**Scenario: Trying to Save a Locked File**

You're editing a scene and press Ctrl+S to save...

1. **Dialog appears**: "⚠️ File(s) Locked"
2. **Information shown**:
   - Owner name: "John Smith (john@team.com)"
   - Machine: "DESKTOP-ABC"
   - Locked: "2 hours ago"
3. **Your options**:
   - **OK**: Cancel the save operation
   - **Force Unlock**: Only shown for admins on stale locks

**What to do**:
1. Note the owner's email from the dialog
2. Contact them (Slack, Teams, Email)
3. Ask politely: "Hey, I need to work on MainScene. Can you push your changes?"
4. Wait for them to commit+push (auto-unlock triggers)
5. Alternative: Wait 24 hours for lock to become stale, then force unlock

### 7.2 Same-User, Different-Machine Locks

**Scenario**: You locked a file on your laptop at home, now you're on your desktop at the office.

**What happens**:
- You try to save
- Dialog: "This file is locked by you on machine 'LAPTOP-HOME'"
- Save is blocked

**Solutions**:

**Option A (Regular Users)**:
- Remote into your laptop
- Open Unity and unlock the file
- Or: Commit+push from laptop to trigger auto-unlock

**Option B (Admins Only)**:
1. Right-click file → FRG Version Control → Unlock File(s)
2. Dialog: "Force unlock file from different machine?"
3. Click **"Force Unlock"**
4. File is now unlocked
5. Next save will lock it on your current machine

### 7.3 Stale Locks (>24 Hours Old)

**What Makes a Lock Stale?**
- Lock timestamp is more than 24 hours ago
- Usually means: User forgot to unlock, went on vacation, or computer died

**How to Identify**:
- Lock Manager shows **(STALE)** indicator
- Tooltip says "(STALE)"

**How to Handle**:
1. Open Lock Manager
2. Find the stale lock
3. Click **"Force Unlock"** button (available to everyone, not just admins)
4. Read confirmation dialog:
   - Shows owner and lock age
   - Warns that file may have been actively worked on
5. Click **"Yes, Force Unlock"**
6. Lock is removed for everyone

**Best Practices**:
- Try to contact the owner first (use email from lock info)
- Check team chat/calendar (are they on vacation?)
- Document in team chat: "Force unlocked MainScene (John's stale lock)"

### 7.4 Failed Lock Attempts

**Scenario: Lock Operation Fails**

You try to lock or save a file, and you get an error dialog.

**Common Error Messages**:
- "Lock Failed: File is already locked by [Name]"
- "Lock Failed: Network error (could not push to remote)"
- "Lock Failed: File is untracked (commit it first)"

**What to Do**:

**If "already locked"**:
1. Check Lock Manager to confirm who has it
2. Contact the owner

**If "network error"**:
1. Check your internet connection
2. Verify Git remote is accessible: Open terminal, run `git fetch`
3. Try again in a moment
4. If persistent, contact your Git admin

**If "file is untracked"**:
1. File is new and not committed to Git yet
2. First commit: `git add [file] && git commit -m "Add new file"`
3. Push: `git push`
4. Now locking will work

---

## 8. BATCH OPERATIONS

### 8.1 Batch Unlocking from Lock Manager

**Tutorial: Unlocking Multiple Files at Once**

1. Open Lock Manager (Ctrl+Shift+L)
2. Check the boxes next to files you want to unlock
3. Click **"Unlock Selected (5)"** button
4. **Confirmation dialog appears** with:
   - Summary: "You are about to unlock 5 lock(s):"
   - Breakdown: "• 3 of your locks", "• 2 stale locks"
   - Warning: Lists files with uncommitted changes (if any)
5. **Choose action**:
   - **"Unlock All"**: Keep uncommitted changes in those files
   - **"Revert & Unlock All"**: Discard ALL changes in ALL selected files
   - **"Cancel"**: Abort operation
6. **Progress bar shows** (if >5 locks):
   - "Unlocking MainScene.unity... (3/5)"
   - Cancel button available
7. **Results dialog**:
   - "✓ Successfully unlocked: 4"
   - "✗ Failed to unlock: 1"
   - Lists failed files with reasons

### 8.2 Unlock Unmodified Feature

**What It Does**:
- Automatically finds ALL your locks
- Filters to only "clean" files (no uncommitted changes)
- Unlocks them all with one click
- No confirmation dialog (it's a safe operation)

**When to Use**:
- End of workday after committing everything
- After a big push to clean up locks
- Preparing for time off (vacation, weekend)

**Tutorial: End-of-Day Cleanup**

1. Finish your work
2. Commit all changes: `git add . && git commit -m "Your message"`
3. Push to remote: `git push`
4. Open Lock Manager
5. Look at **"Unlock Unmodified (8)"** button
6. If number > 0, click the button
7. No confirmation needed (you're not losing data)
8. Progress bar shows unlock progress
9. Results dialog shows success count
10. All your clean locks are released

### 8.3 Batch Locking (Context Menu)

**Tutorial: Locking All Prefabs in a Folder**

1. Navigate to a folder in Project window
2. Click inside the folder to see contents
3. Press **Ctrl+A** to select all files
4. Right-click on any selected file
5. Choose **FRG Version Control → Lock File(s)**
6. System attempts to lock each file individually
7. Console shows results:
   - Success: `[FRGVersionControl] Locked: Assets/Prefabs/Enemy.prefab`
   - Skip: `[FRGVersionControl] Skipping untracked file: Assets/Prefabs/New.prefab`
   - Fail: `[FRGVersionControl] Failed to lock: Assets/Prefabs/Boss.prefab (already locked by Jane)`

---

## 9. ADMIN FEATURES

### 9.1 Who is an Admin?

**Admin Definition**:
- Admins are users with elevated permissions
- Can force unlock any file (even fresh locks)
- Can unlock your locks from different machines
- Defined in configuration file

**Check Your Admin Status**:
1. Open: `Assets/FRGVersionControl/admin-config.json`
2. Look at `adminEmails` array
3. Check if YOUR email is listed
4. Your email must match Git config: `git config user.email`

**Example admin-config.json**:
```json
{
  "adminEmails": [
    "lead@team.com",
    "manager@team.com"
  ],
  "fileLockingEnabled": true
}
```

### 9.2 Admin-Only Actions

**Force Unlock Other Users' Fresh Locks**:
- Regular users: Cannot unlock others' fresh locks
- Admins: Can force unlock anyone's lock at any time
- Confirmation dialog warns about disrupting work

**Force Unlock Your Locks from Different Machines**:
- Regular users: Must use the original machine
- Admins: Can unlock from any machine
- Useful when laptop is offline or broken

**Batch Operations on Others' Locks**:
- Admins can check and unlock others' locks
- Confirmation dialog shows admin override warning
- Multiple locks can be force unlocked at once

### 9.3 Admin Responsibilities

**Best Practices**:
- Only force unlock when absolutely necessary
- Try contacting the owner first
- Document reason for force unlock in team chat
- Use power responsibly

**When to Force Unlock**:
- Owner is on vacation and file is needed urgently
- Lock is stale (>24 hours) and no response from owner
- Critical bug fix requires immediate access
- Owner's computer is broken/offline

**Communication Example**:
"@team Force unlocked MainScene.unity (Jane's lock from 3 days ago) for critical bug fix. Jane is on vacation until Monday."

### 9.4 Global System Control

**Emergency Disable**:

Admins can disable the entire file locking system for the whole team.

**How to Disable**:
1. Open: `Assets/FRGVersionControl/admin-config.json`
2. Change: `"fileLockingEnabled": true` to `false`
3. Commit and push this change
4. Notify team to restart Unity
5. Everyone's file locking is now disabled

**When to Disable**:
- System experiencing widespread issues
- Critical deadline requires all hands on deck
- Major refactoring needs multiple people on same files
- Troubleshooting system problems

**What Happens When Disabled**:
- Lock Manager shows: "FILE LOCKING IS CURRENTLY DISABLED"
- All toolbar buttons are grayed out
- Auto-lock on save does not occur
- Lock enforcement is disabled (anyone can save any file)
- Existing locks remain in system but are not enforced
- Context menu shows lock options but they do nothing

**Re-enabling**:
1. Open: `Assets/FRGVersionControl/admin-config.json`
2. Change back to: `"fileLockingEnabled": true`
3. Commit and push
4. Team restarts Unity
5. System is fully operational again

---

## 10. ADVANCED WORKFLOWS

### 10.1 Working with Untracked Files

**What are Untracked Files?**
- New files not yet committed to Git
- Show orange plus (+) icon in Project window
- Cannot be locked until committed

**Workflow**:
1. Create new scene: File → New Scene
2. Save scene: Ctrl+S
3. **First save**: Auto-lock is skipped
   - Console: `[FRGVersionControl] Skipping untracked file: Assets/NewScene.unity`
4. Commit file to Git:
   ```bash
   git add Assets/NewScene.unity Assets/NewScene.unity.meta
   git commit -m "Add new scene"
   git push
   ```
5. **Subsequent saves**: Auto-lock works normally

### 10.2 Rename/Move Operations

**Renaming YOUR Locked File**:
1. Right-click locked file → Rename
2. Type new name, press Enter
3. Unity handles the rename
4. Locking system tracks both operations:
   - Locks the old path (deleted)
   - Locks the new path (added)
5. Your lock transfers to the new path
6. Commit the rename: `git add -A && git commit -m "Rename file"`

**Renaming OTHER User's Locked File**:
- Unity blocks the rename
- Dialog: "Cannot rename - File is locked by [Name]"
- Must wait for owner to unlock
- Or: Admin can force unlock first

### 10.3 Deleting Locked Files

**YOUR Locked Files**:
- Unity allows deletion
- If file is untracked: Lock is auto-removed
- If file is tracked: Lock may remain (file recoverable from Git)
- Best practice: Unlock before deleting

**OTHER User's Locked Files**:
- Unity DOES allow deletion (no lock enforcement on delete)
- Be very careful
- Communicate with team before deleting others' locked files
- File can be recovered from Git if needed

### 10.4 Working Offline

**What Works Offline**:
- Viewing cached lock state in Lock Manager
- Seeing your own locks (from cache)
- Editing files you already have locked
- Using Lock History (local data)

**What Doesn't Work**:
- Creating new locks (requires `git push`)
- Syncing others' new locks (requires `git fetch`)
- Auto-unlock after push (requires remote push)
- Force unlock operations (requires Git)

**Best Practices**:
1. **Before going offline**: Lock all files you might need
2. **Work normally** on pre-locked files
3. **When back online**:
   - Click "Refresh" in Lock Manager
   - Commit and push your changes
   - Wait for auto-unlock to trigger

---

## 11. TROUBLESHOOTING

### 11.1 Common Issues

**Issue: "Git is not available"**

**Cause**: Git is not installed or not in your system PATH

**Solution**:
1. Install Git from: https://git-scm.com/downloads
2. During installation, choose "Git from command line and also from 3rd-party software"
3. Restart Unity
4. Verify Git works: Open terminal and run `git --version`
5. Re-run Setup Wizard if needed

---

**Issue: Lock Icons Not Showing**

**Cause**: Icon cache not refreshed or Project window needs repaint

**Solution**:
- Right-click in Project window → FRG Version Control → Refresh Locks and Git Status
- OR: Close and reopen Project window
- OR: Restart Unity

---

**Issue: Locks Not Syncing**

**Cause**: Network problem, Git remote issue, or sync service not running

**Solution**:
1. Click **"Refresh"** button in Lock Manager
2. Check network connection
3. Test Git remote: Open terminal, run `git fetch origin file-locks`
4. Check Console for error messages
5. If errors persist, contact your Git admin

---

**Issue: Can't Lock Files**

**Cause**: File is untracked, already locked, or Git repository problem

**Solution**:
1. Check if file has orange plus icon (untracked)
   - If yes: Commit file first
2. Check Lock Manager for existing lock
   - If locked: Contact owner or wait for stale period
3. Check Console for specific error message
4. Verify Git status: `git status` in terminal

---

**Issue: Merge Conflicts in FileLocks.json**

**Cause**: Merge driver not properly configured

**Solution**:
1. Open Setup Wizard: Tools → FRG Version Control → Setup Wizard
2. Click "Run Setup" again
3. Merge driver will be reconfigured
4. Try the operation again

---

**Issue: Worktree Errors**

**Cause**: `.file-locks-worktree/` folder corrupted or out of sync

**Solution**:
1. Close Unity
2. Delete folder: `.file-locks-worktree/`
3. Open terminal in project root
4. Run: `git worktree prune`
5. Restart Unity (worktree will be auto-recreated)

### 11.2 Console Messages

**Understanding Output**:
- `[FRGVersionControl] Auto-locked: Assets/Scene.unity` = Success
- `[FRGVersionControl] Skipping untracked file: Assets/New.prefab` = File not in Git yet (normal)
- `[FRGVersionControl] Failed to lock: Assets/Scene.unity (already locked by Jane)` = Someone else has it
- `[FRGVersionControl] Auto-unlocked: Assets/Scene.unity` = Unlocked after push (normal)
- `[FRGVersionControl] Initialized for user: John (john@team.com)` = System started successfully

**Enable Debug Logging**:
- File locking system logs all important operations to Unity Console
- Window → General → Console
- Filter by "FRGVersionControl" to see only lock-related messages

### 11.3 Getting Help

**Self-Diagnosis Steps**:
1. Open Unity Console (Ctrl+Shift+C) and look for error messages
2. Open Lock Manager and click Refresh
3. Review Lock History for recent operations
4. Check Git status in terminal: `git status`
5. Test Git remote: `git fetch origin`

**When Contacting Team Admin**:

Provide the following information:
- Error message from Console (copy exact text)
- Screenshot of Lock Manager window
- File path having the issue
- What you were trying to do
- Your Git email: `git config user.email`

---

## 12. BEST PRACTICES

### 12.1 Daily Habits

**For Individual Contributors**:
- **Commit frequently**: Triggers auto-unlock faster
- **Push regularly**: Locks are removed after push + 5 minutes
- **Check Lock Manager**: Before editing critical shared files
- **Unlock files**: When switching to a different task
- **Don't leave locks overnight**: Others may need the files

**End-of-Day Checklist**:
1. ✓ Commit all work you want to keep
2. ✓ Push to Git: `git push`
3. ✓ Wait 5 minutes (auto-unlock service runs)
4. ✓ OR manually use "Unlock Unmodified" button
5. ✓ Close Unity

### 12.2 Team Communication

**Announce in Team Chat**:
- "Working on MainScene for the next 2 hours"
- "Locking all UI prefabs for redesign today"
- "File locking seems down, investigating..."

**Use Lock Manager for Coordination**:
- Before editing: Check if anyone has the file locked
- See what your teammates are working on
- Identify bottleneck files (frequently locked by multiple people)

**Lock Duration Guidelines**:
- **< 2 hours**: Normal, no announcement needed
- **2-8 hours**: Announce in chat what you're working on
- **> 8 hours**: Consider splitting file or committing partial work
- **Overnight/weekend**: Unlock unless actively working on it

### 12.3 File Organization

**Reduce Lock Conflicts**:
- **Break large scenes into pieces**: Use multiple scenes loaded additively
- **Use prefab variants**: Edit variants instead of base prefabs
- **Separate concerns**: Scene for geometry, scene for lighting, scene for gameplay
- **Organize by ownership**: Designer folders vs Artist folders vs Programmer folders

**Example Structure**:
```
Assets/
  Scenes/
    Gameplay/
      MainGameplay.unity (core gameplay, rarely edited)
      Level1_Art.unity (art pass, artist edits)
      Level1_Design.unity (design pass, designer edits)
  Prefabs/
    Characters/ (owned by character artist)
    Environment/ (owned by environment artist)
    UI/ (owned by UI designer)
```

### 12.4 Performance Tips

**Keep Lock Count Reasonable**:
- Don't lock 100+ files at once
- Unlock files you're not actively editing
- Use "Unlock Unmodified" regularly
- Commit and push frequently (triggers auto-unlock)

**Optimize Sync Performance**:
- Background sync runs every 5 minutes (acceptable)
- Manual Refresh is instant
- Lock History is capped at 500 entries (automatic cleanup)

---

## 13. KEYBOARD SHORTCUTS

### 13.1 Window Shortcuts

- **Ctrl+Shift+L** (Cmd+Shift+L on Mac): Open Lock Manager
- **Ctrl+S** (Cmd+S on Mac): Save file (triggers auto-lock)
- **Ctrl+Shift+C** (Cmd+Shift+C on Mac): Open Console (view lock messages)

### 13.2 Project Window Selection

- **Ctrl+Click** (Cmd+Click on Mac): Select multiple files
- **Ctrl+A** (Cmd+A on Mac): Select all files in current folder
- **Click and drag**: Multi-select files

### 13.3 Workflow Tips

- **Tab**: Move focus to search fields in Lock Manager/History
- **Enter**: Confirm dialogs
- **Escape**: Cancel dialogs

---

## 14. REFERENCE

### 14.1 Lockable File Types

**Unity Binary Assets**:
- `.unity` - Scenes
- `.prefab` - Prefabs
- `.asset` - ScriptableObjects, Custom Assets
- `.mat` - Materials
- `.controller` - Animator Controllers
- `.anim` - Animation Clips
- `.playable` - Timeline Assets
- `.lighting` - Lighting Settings
- `.mixer` - Audio Mixers
- `.physicmaterial` - 3D Physics Materials
- `.physicsmaterial2d` - 2D Physics Materials
- `.shadergraph` - Shader Graph
- `.shadersubgraph` - Shader Sub-Graph
- `.vfx` - Visual Effect Graph
- `.inputactions` - Input System Actions
- `.terrainlayer` - Terrain Layers
- `.brush` - Terrain/Tilemap Brushes
- `.flare` - Lens Flare
- `.mask` - Avatar Masks
- `.preset` - Presets
- `.spriteatlas` - Sprite Atlases
- `.cubemap` - Cubemaps

**3D Models**:
- `.fbx` - Autodesk FBX
- `.gltf`, `.glb` - glTF models
- `.blend` - Blender files
- `.mb`, `.ma` - Maya files
- `.obj` - Wavefront OBJ
- `.dae` - COLLADA
- `.max` - 3ds Max files

**Images**:
- `.psd`, `.psb` - Photoshop
- `.ai` - Adobe Illustrator
- `.xcf` - GIMP
- `.sketch` - Sketch
- `.afdesign` - Affinity Designer
- `.afphoto` - Affinity Photo
- `.png`, `.jpg`, `.jpeg` - Raster images
- `.tga` - Targa images
- `.exr`, `.hdr` - HDR images

**Audio**:
- `.wav` - Wave audio
- `.mp3` - MP3 audio
- `.ogg` - Ogg Vorbis
- `.aiff` - AIFF audio
- `.flac` - FLAC lossless

**Video**:
- `.mp4` - MPEG-4 video
- `.mov` - QuickTime
- `.avi` - AVI video
- `.webm` - WebM video

**NOT Locked (Text/Mergeable)**:
- `.cs`, `.js` - Source code
- `.shader`, `.cginc` - Shader code
- `.json`, `.xml`, `.txt` - Configuration
- `.md` - Markdown documentation
- `.meta` - Unity metadata

### 14.2 Configuration Files

**admin-config.json**

Location: `Assets/FRGVersionControl/admin-config.json`

Purpose: Defines admin users and global enable/disable

Format:
```json
{
  "adminEmails": [
    "lead@team.com",
    "manager@team.com"
  ],
  "fileLockingEnabled": true
}
```

Fields:
- `adminEmails`: Array of email addresses with admin privileges
- `fileLockingEnabled`: Global on/off switch (true or false)

---

**lockable-file-types.json**

Location: `Assets/FRGVersionControl/lockable-file-types.json`

Purpose: Defines which file extensions can be locked

Format:
```json
{
  "lockableExtensions": [
    ".unity",
    ".prefab",
    ".mat",
    ".asset"
  ]
}
```

Fields:
- `lockableExtensions`: Array of file extensions (include the dot)

Modify to customize file locking behavior.

### 14.3 System Architecture

**Git Branches**:
- `main` (or `master`): Your actual work commits
- `file-locks`: Lock metadata only (completely separate history)
- These branches NEVER merge

**Worktree**:
- Folder: `.file-locks-worktree/`
- Purpose: Local checkout of `file-locks` branch
- Not tracked in `.gitignore`
- Auto-created on Unity startup
- Enables lock operations without branch switching

**Background Services**:
- **Sync Service**: Every 5 minutes, runs `git fetch` and updates cache
- **Auto-Unlock Service**: Detects Git pushes, removes your locks automatically
- **History Service**: Records lock/unlock operations for audit trail

**Lock Storage**:
- File: `ProjectSettings/FileLocks.json` (on `file-locks` branch only)
- Contains: Array of all active locks with metadata
- Format: JSON with timestamps, owners, file paths

---

## 15. FAQ

**Q: Can I lock source code files (.cs)?**
A: No. Code files are designed to be merged in Git. Only binary/asset files are locked.

**Q: What happens if my computer crashes while I have files locked?**
A: Locks remain for 24 hours, then become stale. Your team can force unlock them after 24 hours.

**Q: Can multiple people edit different prefabs at the same time?**
A: Yes! Each person locks their own prefab independently. No conflicts occur.

**Q: What if I forget to unlock files before going on vacation?**
A: After 24 hours, your locks become stale and anyone can force unlock them.

**Q: Does this work with Git LFS?**
A: Yes, but this system is designed as an alternative to Git LFS locking. You don't need both.

**Q: Can I see lock history after a file is deleted?**
A: Yes! Lock History shows all operations even for files that no longer exist.

**Q: What happens if two people try to lock the same file at the exact same second?**
A: Git handles this. The earliest timestamp wins. The second person gets a "file locked" error.

**Q: Can I disable auto-lock and only use manual locking?**
A: Yes, but requires code modification. Ask your team's developer to modify or disable the `FileLockAssetModificationProcessor` class.

**Q: Is there a limit to how many files I can lock?**
A: No hard limit, but keep it reasonable (under 50) for performance.

**Q: What if the Git remote goes down?**
A: New lock operations will fail, but you can still view cached locks and work on files you've already locked.

**Q: Will this slow down my Unity editor?**
A: No. Background sync runs only every 5 minutes. Lock checks on save are fast (milliseconds).

**Q: Can I lock files in multiple projects?**
A: Yes! Each Unity project has its own `file-locks` branch. Setup is per-project.

**Q: Do .meta files get locked too?**
A: Yes, when you lock a file, its .meta file is automatically included.

---

## 16. GLOSSARY

**Auto-Lock**: Automatic locking of files when you save them (Ctrl+S)

**Stale Lock**: A lock that is older than 24 hours, can be force unlocked by anyone

**Force Unlock**: Admin action to remove a lock owned by someone else

**Worktree**: A separate Git working directory that stays on the `file-locks` branch

**Lockable File**: A binary asset file that can be locked (scenes, prefabs, etc.)

**Untracked File**: A new file that hasn't been committed to Git yet

**Batch Operation**: An action performed on multiple files at once

**Lock History**: Audit trail showing all lock and unlock operations

**Admin**: A user with elevated permissions (defined in admin-config.json)

**Merge Driver**: Git tool that automatically resolves lock file conflicts

**Uncommitted Changes**: Modifications to a file that haven't been committed to Git

**Lock Cache**: Local storage of lock data for fast access

**Background Sync**: Automatic fetching of lock updates every 5 minutes

**Project Window Overlay**: Visual lock icons shown in Unity's Project window

---

## Need More Help?

- Check the Unity Console for detailed error messages
- Use Lock Manager's Refresh button to sync latest state
- Review Lock History to see recent operations
- Contact your team admin with specific error messages
- Re-run Setup Wizard if configuration seems broken

**Remember**: File locking is designed to help your team avoid conflicts. When in doubt, communicate with your teammates!
