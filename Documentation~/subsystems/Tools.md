# Tools Module

## Overview

The Tools module provides utility tools for system setup, configuration, and specialized operations like Git merge conflict resolution.

## Components

### FileLockSetupWizard.cs

Interactive setup wizard for first-time configuration of the FRGVersionControl system.

**Access:** `Tools > FRG Version Control > Setup Wizard`

**Purpose:**
Guides users through initial setup to ensure the system is properly configured before first use.

**Setup Steps:**

#### 1. Git Repository Check
- Verifies project is in a Git repository
- Shows error if not initialized
- Provides instructions for git init

#### 2. User Configuration
- Checks for Git user.name and user.email
- Prompts to configure if missing
- Validates email format

#### 3. File-Locks Branch
- Creates `file-locks` orphan branch if needed
- Initializes empty FileLocks.json
- Sets up proper Git tracking

#### 4. Worktree Setup
- Creates `.file-locks-worktree/` directory
- Configures worktree to point to file-locks branch
- Adds to .gitignore if needed

#### 5. Merge Driver Installation
- Registers custom merge driver in `.git/config`
- Sets up .gitattributes for FileLocks.json
- Ensures conflict-free merging

#### 6. Configuration Files
- Creates admin-config.json template
- Creates lockable-file-types.json with defaults
- Prompts user to customize configs

**UI Features:**
- Step-by-step progress indicator
- Clear error messages with solutions
- Success confirmation
- Option to re-run setup

**Key Methods:**
- `ShowWindow()` - Opens the wizard
- `CheckGitRepository()` - Validates Git setup
- `CreateFileLocksBranch()` - Initializes lock branch
- `SetupMergeDriver()` - Configures Git merge settings
- `CreateConfigFiles()` - Generates default configs

**When to Use:**
- First time installing FRGVersionControl system
- After cloning project without lock setup
- When setup is broken or incomplete
- To reset configuration

### FileLockMergeDriver.cs

Custom Git merge driver for resolving conflicts in FileLocks.json.

**Purpose:**
Handle merge conflicts in the lock file automatically using a "last-write-wins" strategy per file.

**How It Works:**

Git calls this tool when FileLocks.json has a conflict:
```
git merge-file -L LOCAL -L BASE -L REMOTE local.json base.json remote.json
```

The driver:
1. Parses all three versions (base, local, remote)
2. Merges lock entries by file path
3. Uses newest timestamp for each file
4. Writes merged result
5. Returns success/failure

**Merge Strategy:**

For each locked file:
- If only in local → Keep local
- If only in remote → Keep remote
- If in both → Keep newest by timestamp
- If unlocked in one → Keep lock (assume unlock failed to sync)

**Command Line Usage:**
```bash
FileLockMergeDriver.exe <base> <local> <remote> <output>
```

**Git Configuration:**

In `.git/config`:
```ini
[merge "file-locks"]
    name = File Locks Merge Driver
    driver = FileLockMergeDriver.exe %O %A %B %A
```

In `.gitattributes`:
```
ProjectSettings/FileLocks.json merge=file-locks
```

**Exit Codes:**
- `0` - Merge successful
- `1` - Merge failed
- `2` - Parse error

**Integration:**
Automatically configured by Setup Wizard. Git invokes it during merge operations.

**Limitations:**
- Assumes UTC timestamps
- Cannot resolve complex conflicts (e.g., same file locked on same machine at same millisecond)
- Requires valid JSON in all versions

## Setup Workflow

### Initial Project Setup
1. Run Setup Wizard
2. Follow all prompts
3. Configure admin-config.json
4. Customize lockable-file-types.json
5. Test by locking a file

### Adding Collaborators
1. Have them run Setup Wizard
2. Pull file-locks branch
3. Add their email to admin-config.json (if admin)
4. They're ready to collaborate

### Troubleshooting
- **Locks not working** → Re-run Setup Wizard
- **Merge conflicts** → Verify merge driver is configured
- **Wrong user** → Check Git user.name and user.email
- **Missing icons** → Check FileLockInitializer ran

## Best Practices

- Run Setup Wizard on every new clone
- Keep admin-config.json in version control
- Keep lockable-file-types.json in version control
- Don't manually edit ProjectSettings/FileLocks.json
- Commit merge driver configuration to repo
