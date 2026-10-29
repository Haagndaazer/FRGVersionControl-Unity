# FRGVersionControl Editor System

## Overview

The FRGVersionControl system is a Unity Editor extension that provides Git-based file locking functionality to prevent merge conflicts on binary and critical asset files. It implements a distributed locking mechanism using Git worktrees and a dedicated file-locks branch.

## Architecture

The system is organized into several key modules:

- **Core/** - Core lock management, Git operations, and data models
- **Hooks/** - Unity Editor lifecycle integration and asset modification hooks
- **Services/** - Background services for auto-unlock, history tracking, and sync
- **UI/** - User interface components including windows, overlays, and context menus
- **Tools/** - Utility tools for setup and Git merge conflict resolution
- **Tests/** - Integration tests for the locking system

## Key Features

- **Automatic Lock Detection** - Automatically locks configured file types when modified
- **Visual Indicators** - Shows lock status in Project window with colored icons
- **Lock Management** - UI for viewing, acquiring, and releasing locks
- **Admin Permissions** - Admin users can force unlock fresh locks
- **Auto-Unlock** - Automatically releases locks on idle files after 30 minutes
- **Lock History** - Tracks lock operations with detailed history
- **Conflict Resolution** - Custom Git merge driver for lock file conflicts
- **Configuration** - Centralized config for lockable file types and admin users

## Configuration Files

- **ProjectSettings/FileLocks.json** - Current lock state (Git-managed)
- **Assets/FRGVersionControl/admin-config.json** - Admin user email list
- **Assets/FRGVersionControl/lockable-file-types.json** - File extensions to lock

## Getting Started

1. Run the Setup Wizard: `Tools > FRG Version Control > Setup Wizard`
2. Configure admin users in `admin-config.json`
3. Customize lockable file types in `lockable-file-types.json`
4. Locks are automatically acquired when modifying configured file types

## Integration Points

- **AssetModificationProcessor** - Intercepts Unity asset modifications
- **EditorApplication callbacks** - Monitors Editor state changes
- **Project Window Overlay** - Displays lock icons
- **Context Menus** - Provides right-click lock operations

## Git Worktree Structure

The system uses a Git worktree at `.file-locks-worktree/` to manage the `file-locks` branch separately from the main working directory, preventing workflow disruption.
