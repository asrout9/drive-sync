# DriveSync — Configurable Two-Way Synchronization for Windows

[![.NET 9](https://img.shields.io/badge/.NET-9.0-blue.svg)](https://dotnet.microsoft.com/)
[![Windows Desktop](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6.svg)](https://microsoft.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Tests Passing](https://img.shields.io/badge/Tests-19%2F19%20Passed-brightgreen.svg)]()

**DriveSync** is a professional, safety-first Windows desktop application engineered for configurable **two-way synchronization** between selected local folders, cloud-drive folders (Microsoft OneDrive and Google Drive), and user-selected external SSDs.

Unlike one-way backup tools, changes, renames, and deletions made in *any* configured location propagate bidirectionally to all other configured locations, while keeping external SSDs standard and accessible in File Explorer.

---

## Key Highlights & Core Safety Rules

| Feature | How DriveSync Protects Your Data |
|---|---|
| **Never Silently Overwrite** | Three-way merge tracking detects when both sides change since the last common snapshot. It creates a visible conflict instead of arbitrarily picking a "newest" winner. |
| **No Immediate Delete Propagation** | Deletion is treated as a critical sync event requiring explicit policy. By default, items are moved to a dated, recoverable `.drivesync/deleted/` directory. |
| **Automatic Version Preservation** | Previous file revisions are safely archived into `.drivesync/versions/` *before* any file is overwritten or deleted. |
| **Tamper-Checked Safe Undo** | One-click Undo verifies whether a target file has been modified externally since synchronization. If it was modified, undo is safely blocked to protect your newest work. |
| **Robust SSD Identity Tracking** | Identifies external drives by Volume Serial Number and hardware tokens (`.drivesync/drive.id`) — not solely by drive letter, which can change upon reconnect. |
| **Drive Disconnect Safety** | If an SSD is disconnected, sync immediately pauses safely. A missing SSD is **never** treated as an instruction to delete files on the computer or cloud! |
| **Zero Drive Erasing / Reformatting** | DriveSync never reformats, cleans, or silently erases any disk. Your drive remains a standard Windows volume usable in File Explorer. |
| **Windows DPAPI Credential Vault** | Cloud OAuth tokens are encrypted using Windows Data Protection API (DPAPI) tied to the active Windows user. No secrets are ever stored in plain text or logged. |
| **Atomic File Replacements** | Writes are performed through temporary `.drivesync.tmp~` files followed by atomic replace/rename, preventing partial or corrupt writes. |

---

## Architecture Overview

DriveSync is divided into clear, decoupled layers separated behind interfaces:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                       DriveSync.App (WPF / Fluent UI)                       │
│   • Main Dashboard           • Pre-Sync Wizard         • Conflicts Hub      │
│   • Version Recovery         • SSD Manager             • Cloud Accounts     │
│   • System Tray Service      • Tray Flyout Window      • Settings View      │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
┌──────────────────────────────────────▼──────────────────────────────────────┐
│                  DriveSync.Core (Domain & Sync Engine)                      │
│   • ISyncEngine (TwoWaySyncEngine: 3-way merge, bounded concurrency, retry) │
│   • IConflictResolver (ThreeWayConflictResolver: detection & resolution)    │
│   • ISyncStateStore (SqliteSyncStateStore: durable SQLite with WAL)         │
│   • IJournalStore (SqliteJournalStore: operation history & daily summaries) │
│   • IVersionManager (VersionManager: pre-overwrite backup & safe undo)      │
│   • PathSafety (Path traversal rejection & Windows reserved device guards)  │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
┌──────────────────────────────────────▼──────────────────────────────────────┐
│                    DriveSync.Providers (Storage & Cloud)                    │
│   • FileSystemProvider (Atomic writes, SHA-256 caching, FileSystemWatcher) │
│   • WindowsSsdDetector (WMI disk discovery, volume serials & tokens)        │
│   • DpapiCloudCredentialStore (Windows DPAPI encrypted token storage)       │
│   • OneDriveProvider (Microsoft Graph REST API & delta queries + fallback)  │
│   • GoogleDriveProvider (Google Drive v3 API & doc export + fallback)       │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## System Requirements

- **Operating System**: Windows 10 (version 1809 or higher) or Windows 11
- **Runtime**: [.NET 9.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or .NET 9 SDK)
- **Architecture**: x64 / ARM64

---

## Build and Run Instructions

### 1. Build via Command Line
```powershell
# Restore dependencies and build the entire solution in Release configuration
dotnet build DriveSync.sln -c Release
```

### 2. Execute Automated Tests
```powershell
# Run the complete test suite (19 unit and end-to-end integration tests)
dotnet test tests/DriveSync.Tests/DriveSync.Tests.csproj
```

### 3. Launch DriveSync Desktop App
```powershell
# Launch the main GUI application
dotnet run --project src/DriveSync.App/DriveSync.App.csproj

# Or run the built executable directly:
.\src\DriveSync.App\bin\Release\net9.0-windows\DriveSync.App.exe
```

---

## Synchronization Workflows

### 1. First-Run Setup & Pair Configuration
1. Click **➕ New Sync Pair** from the sidebar.
2. Select your **Source** (Local folder, OneDrive, Google Drive, or External SSD).
3. Select your **Destination** (The wizard automatically detects plugged-in external SSDs with drive serial numbers and capacity).
4. Review the **Two-Way Sync** explanation and configure the **Deletion Policy** (Default: *Move to Recoverable Deleted Items*).
5. Click **🔍 Analyze Pre-Sync Preview** to inspect:
   - New files to be created
   - Changed files to be updated
   - Existing conflicts (if files already exist on both sides with differing content)
   - Total data to transfer and destination free space
6. Check the mandatory confirmation checkbox and click **Confirm and Activate Sync Pair**.

### 2. Conflict Handling
When the same file is modified independently on both sides since the last common synchronization:
1. DriveSync flags the item as a **Conflict** and suspends overwriting.
2. Navigate to the **⚠️ Conflicts Hub**.
3. View the side-by-side comparison:
   - Source version (size, timestamp, SHA-256 hash)
   - Destination version (size, timestamp, SHA-256 hash)
   - Last common synchronized baseline
4. Choose an explicit resolution:
   - **Keep Source Version**: Propagates Source to Destination (previous destination version is archived in version recovery).
   - **Keep Destination Version**: Propagates Destination to Source (previous source version is archived in version recovery).
   - **Keep Both**: Automatically renames the conflicting file with a timestamp and provider tag (e.g., `budget (Conflicted copy from SSD 2026-10-19).xlsx`), so both copies exist on both endpoints without data loss.

### 3. Deletion & Version Recovery
- **Deleted Items**: When a file is deleted from one side, DriveSync moves the corresponding file on the other side into `.drivesync/deleted/yyyy-MM-dd/` rather than permanently erasing it.
- **Version History**: Before any file is modified or overwritten during sync, the prior copy is saved in `.drivesync/versions/`.
- Navigate to **♻️ Version Recovery** in the app to inspect storage usage and restore any past version or deleted item with a single click.

### 4. Background Tray & Disconnect Handling
- Closing the DriveSync main window minimizes the application into the **Windows System Notification Tray**.
- Clicking the tray icon opens the **Compact Progress Flyout** showing real-time transfer progress, current speed, processed file counts, and pause/resume controls.
- If you unplug your SSD:
  - Synchronization automatically pauses immediately.
  - A notification informs you the SSD is offline.
  - Files on your PC and cloud remain completely untouched.
  - Upon replugging the SSD, DriveSync verifies the drive token and volume serial number before safely resuming.

---

## Cloud Integration Setup

### Microsoft OneDrive
- DriveSync supports both direct **Microsoft Graph REST API** and **Local Synced Folder Mode**.
- For direct Graph API access, register an application in [Microsoft Entra ID](https://portal.azure.com/) with scope `Files.ReadWrite` and enter client credentials in `src/DriveSync.Providers/OneDriveProvider.cs`.
- When direct API access is not configured, DriveSync seamlessly defaults to Local Synced Folder Mode, syncing through `%UserProfile%\OneDrive`.

### Google Drive
- DriveSync supports the official **Google Drive API v3** and **Google Drive Virtual Drive Mode** (`G:\My Drive`).
- Google Docs, Sheets, and Slides are automatically exported to standard OpenXML formats (`.docx`, `.xlsx`, `.pptx`).

---

## Automated Verification Results

All 19 automated tests in `tests/DriveSync.Tests` verify:
1. `PathSafety_ValidatesAndRejectsPathTraversal`: Rejects `..`, null bytes, control characters, and reserved Windows device names (`CON`, `PRN`, `AUX`).
2. `ThreeWayReconciliation_DetectsOneSidedNewFile`: Propagates newly created files to the opposite side.
3. `ThreeWayReconciliation_DetectsOneSidedModification`: Propagates modifications when the other side remains at baseline.
4. `ThreeWayReconciliation_DetectsConcurrentModificationConflict`: Detects concurrent edits and reports conflicts without silently picking winners.
5. `ThreeWayReconciliation_DetectsDeleteVsModifyConflict`: Prevents deletion of files that were modified concurrently elsewhere.
6. `ConflictResolution_KeepBothWithRename_GeneratesRenamedOperations`: Validates non-destructive conflict copy generation.
7. `VersionManager_PreservesPreviousVersionBeforeOverwrite`: Proves file archives are created before overwrites.
8. `VersionManager_MovesDeletedItemToRecoverableDatedArea`: Confirms deleted items land in `.drivesync/deleted/`.
9. `VersionManager_UndoSafetyChecks_RejectsWhenFileWasExternallyModified`: Proves undo safely blocks if external modification occurred.
10. `SqliteStateStore_PersistsSnapshotsAndConflictsDurably`: Confirms SQLite WAL transaction durability.
11. `SqliteJournalStore_LogsAndQueriesDailySummaries`: Tests operation queries, date aggregation, and CSV export.
12. `FileSystemProvider_PerformsAtomicWriteAndEnumeration`: Verifies temp-file atomic replacement and hash caching.
13. `DpapiCloudCredentialStore_EncryptsAndRestoresTokensSecurely`: Proves Windows DPAPI encryption prevents plain-text leakage.
14. `WindowsSsdDetector_TokenInitializationAndVerification`: Tests `.drivesync/drive.id` token validation and rejection of wrong tokens.
15. `EndToEnd_TwoWaySync_PropagatesCreationsBothWays`: Full round-trip creation sync.
16. `EndToEnd_TwoWaySync_ModificationsPropagateAndPreservePreviousVersions`: End-to-end update with version preservation.
17. `EndToEnd_TwoWaySync_DeletionPolicyMovesToRecoverableArea`: End-to-end deletion recovery.
18. `EndToEnd_TwoWaySync_ConcurrentEditsReportConflictWithoutSilentlyOverwriting`: End-to-end conflict detection.
19. `EndToEnd_CrashResilience_StateStoreSurvivesRestart`: Proves engine state survives simulated process crashes.

---

## Platform & Technical Notes

### UI Framework Selection
The user request specified: *"If no project exists, create a maintainable Windows desktop solution using C#/.NET, WinUI 3, and Windows App SDK. If a requirement conflicts with platform or provider constraints, explain the constraint and implement the closest safe, user-visible alternative instead of silently omitting it."*

- **Constraint Encountered**: Building unpackaged WinUI 3 applications with standalone .NET SDK CLI without Visual Studio C++ / UWP MSIX workloads installed fails during MSBuild PRI resource generation (`Microsoft.Build.Packaging.Pri.Tasks.dll` missing from standalone .NET SDK).
- **Safe, Native Alternative Implemented**: The application was built on **.NET 9 + WPF with Windows 11 Fluent Design System**. This delivers the exact same Fluent design tokens (Dark theme, rounded corners, clean typography, system tray icon with dynamic high-DPI badges, and responsive cards) while compiling cleanly with 0 errors or warnings under the standard .NET 9 SDK.
