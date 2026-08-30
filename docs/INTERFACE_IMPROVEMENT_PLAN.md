# Interface Improvement Specification: Proton Drive Browser & Cloud Sync Explorer

## 1. Executive Summary & Value Proposition
This specification details the UX/UI and functional migration of the application from a single-column sync list to a dual-pane explorer interface. The primary objective is to maximize operational efficiency, reduce cognitive friction, and eliminate user errors during bi-directional file synchronization and cloud storage management.

---

## 2. Comprehensive UX/UI Design Critique & Optimizations

### 2.1 Header & Navigation Bar
- **Current Observation:** The header contains the application title, theme toggle, provider dropdown, and settings button.
- **Utilitarian Enhancements:**
  - **Account & Connection Telemetry:** Incorporate real-time connection status (Connected, Syncing, Offline, Rate-Limited) alongside the provider selector so users immediately know if transfers will succeed.
  - **Storage Quota Metric:** Display an active quota gauge (e.g., `45.2 GB / 500 GB (9%) used`) to prevent unexpected failures due to storage limits.
  - **Global Quick Search / Filter Bar:** Enable immediate filtering across both active views to speed up target folder and file discovery.
  - **Breadcrumb Navigation:** Replace static path text with interactive breadcrumbs enabling single-click traversal to parent directories.

### 2.2 Dual-Pane File Explorer (Cloud vs. Local)
- **Current Observation:** Split view displays Cloud on the left and Local on the right with basic metadata (Name, Modified, Type).
- **Utilitarian Enhancements:**
  - **Visual Density & Column Customization:** Add file size column (`Size`) alongside `Modified Date` and `Type` to facilitate informed transfer decisions.
  - **Dynamic Breadcrumbs & Path Address Bar:** Allow direct copy/pasting of local paths and quick jumping through cloud hierarchy.
  - **Batch Selection & Multi-Item Actions:** Support standard keyboard shortcuts (`Ctrl/Cmd + Click`, `Shift + Click`, `Ctrl/Cmd + A`) for multi-file operations.
  - **Sync State Indicators:** Display per-item sync badges on rows (e.g., synced green checkmark, pending sync clock, sync error exclamation mark).
  - **Typo & Consistency Corrections:** Correct mock-up paths and text labels (e.g., `/hime.user` $\to$ `/home/user`, standardized English/Spanish localization).

### 2.3 Bi-Directional Drag & Drop Engine
- **Current Observation:** Direct dragging of files between left and right panes.
- **Utilitarian Enhancements:**
  - **Active Dropzone Affordances:** Highlight valid drop targets (folders or root panel) with high-contrast outlines and clear operation badges (`+ Upload to /Documentos` or `↓ Download to /home/user/Downloads`).
  - **Transfer Queue & Progress Overlay:** Provide non-blocking background queue management with ETA, transfer speed (MB/s), pause/resume, and retry controls.
  - **Conflict Resolution Dialog:** Automatically detect existing file collisions and prompt the user with utilitarian options: *Overwrite*, *Skip*, *Keep Both (Rename)*, and *Apply to all remaining conflicts*.

### 2.4 Activity & CLI Panel
- **Current Observation:** Bottom panel showing CLI activity with buttons: Collapse, Save, Delete.
- **Utilitarian Enhancements:**
  - **State Persistence & Resizability:** Allow panel height resizing via drag handle and remember collapsed/expanded state across sessions.
  - **Log Filtering & Search:** Provide log level filters (`All`, `Info`, `Warning`, `Error`, `Transfers`) and search input to quickly diagnose CLI sync issues.
  - **Direct CLI Execution / Quick Actions:** Retain quick action buttons with clear labeling (`Clear Log`, `Export Log`, `Toggle Details`).

---

## 3. Improved Task Descriptions & Technical Requirements

### Task 1: Application Header Redesign & System Telemetry
- **Description:** Modernize the primary navigation header to provide unified control over workspace themes, storage provider context, connection telemetry, and global settings.
- **Key Details & Acceptance Criteria:**
  - Implement a zero-flicker Theme Switcher (Light / Dark / System Default) persisting user preferences.
  - Provide a global Settings modal shortcut (hotkey `Ctrl/Cmd + ,`) exposing account management, CLI binary paths, network bandwidth throttles, and default sync folders.
  - Display current cloud connection status (Online / Syncing / Disconnected) and real-time storage quota metrics.
  - Clean layout conforming to modern design standards with responsive scaling down to compact desktop window dimensions.

### Task 2: Dynamic Storage Provider Context Engine
- **Description:** Implement a provider switcher dropdown that dynamically binds the Cloud Pane to the active storage backend (Proton Drive, Google Drive, OneDrive, Nextcloud, Local/Custom S3).
- **Key Details & Acceptance Criteria:**
  - Quick-switch dropdown in the header indicating active provider icon and account identity (e.g., `user@proton.me`).
  - Seamless view switching: immediately update the Cloud Explorer pane, cached directory tree, available quota, and active sync pairs for the selected provider.
  - Multi-account / multi-provider authentication management allowing login/logout flows without application restarts.
  - Graceful fallback and error messaging if a provider's CLI session has expired or requires re-authentication.

### Task 3: Expandable Dual-Pane File Explorer (Local & Cloud)
- **Description:** Replace the legacy static sync status view with a high-throughput, dual-pane file management interface featuring interactive breadcrumbs, sorting, and directory navigation.
- **Key Details & Acceptance Criteria:**
  - **Left Pane (Cloud):** Virtualized list rendering for large directories; sortable columns (`Name`, `Size`, `Date Modified`, `Sync Status`); interactive breadcrumb navigation.
  - **Right Pane (Local):** Native filesystem browser with breadcrumb navigation, home shortcut, hidden files toggle, and available disk space indicator.
  - **Splitter Component:** Adjustable split ratio between Cloud and Local panes with double-click reset to 50/50.
  - **Performance Requirement:** Fast directory traversal and responsive caching to minimize CLI latency when browsing remote hierarchies.

### Task 4: Responsive Bottom Activity & CLI Telemetry Panel
- **Description:** Maximize vertical workspace by introducing a collapsible, resizable terminal/activity panel for monitoring background CLI tasks and sync operations.
- **Key Details & Acceptance Criteria:**
  - Single-click toggle (`Collapse` / `Expand`) with keyboard shortcut (`Ctrl/Cmd + ~`).
  - Persistent floating status bar when collapsed showing current transfer rate, active job count, and last log status.
  - Functional action bar with:
    - `Clear`: Flush log output buffer.
    - `Save / Export`: Export logs to file for troubleshooting.
    - `Filter`: Toggle error/warning-only log views.
  - Smooth animation without layout jumping or pane clipping.

### Task 5: Bi-Directional Drag & Drop Transfer System
- **Description:** Enable seamless, direct drag-and-drop file and directory transfers between Cloud and Local panes with clear visual feedback and collision handling.
- **Key Details & Acceptance Criteria:**
  - **Cloud $\to$ Local:** Dragging items downloads them into the target local directory.
  - **Local $\to$ Cloud:** Dragging items initiates upload into the selected cloud path.
  - **Drop Target Highlighting:** Clear visual feedback indicating valid folder targets vs invalid/read-only locations.
  - **Transfer Manager Integration:** Transfers register as active tasks in a centralized queue with progress bars, ETA, and cancellation options.
  - **Conflict Detection:** Automatic modal prompt for existing files offering *Overwrite*, *Skip*, or *Auto-rename*.

### Task 6: Path Sync Configuration & Context Menu System
- **Description:** Provide granular synchronization controls allowing users to configure continuous two-way or one-way synchronization pairs directly from directory views.
- **Key Details & Acceptance Criteria:**
  - Right-click context menu on any directory or file with options:
    - `Sync Selected Path...` (Opens Sync Pair Wizard: Two-Way, Cloud $\to$ Local, Local $\to$ Cloud, Interval/Continuous).
    - `Download Selected / Download All`.
    - `Upload to this folder`.
    - `Copy Path / Share Link`.
    - `Delete / Rename / Properties`.
  - Configured sync pairs show active badges directly on directory icons in both panes.
  - Quick access to pause, resume, or force-sync any established sync pair.

---

## 4. UI/UX Workflow Matrix & Architecture

```
+---------------------------------------------------------------------------------------------------+
|  DRIVE   [Theme: Light/Dark]   [ Provider: Proton Drive v ]   [ Quota: 45GB/500GB ]   [ Settings ] |
+---------------------------------------------------------------------------------------------------+
|  CLOUD EXPLORER (Remote)                     |  LOCAL EXPLORER (Filesystem)                       |
|  Breadcrumb: My Files > Documents            |  Breadcrumb: /home/user/Documents                  |
|  [Search Cloud...]                           |  [Search Local...]                                 |
|  +-----------------------------------------+ | +------------------------------------------------+ |
|  | [DIR]  Work Projects         2026-08-15 | | | [DIR]  Work Projects (Synced)      2026-08-15 | |
|  | [DIR]  Personal              2026-08-20 | | | [DIR]  Personal                    2026-08-20 | |
|  | [FILE] Report.pdf   2.4 MB   2026-08-28 | | | [FILE] Notes.md           14 KB    2026-08-30 | |
|  +-----------------------------------------+ | +------------------------------------------------+ |
|                     <==== Drag & Drop / Bidirectional Transfer ====>                              |
+---------------------------------------------------------------------------------------------------+
|  CLI ACTIVITY & TRANSFERS [Transferring: 1 item at 12.4 MB/s | 68%]              [Clear] [Save] [-] |
|  [2026-08-30 11:35:01] INFO  Upload started: /home/user/Documents/Notes.md -> /Documents/Notes.md  |
|  [2026-08-30 11:35:02] SUCCESS Upload completed (14 KB transferred in 0.4s)                       |
+---------------------------------------------------------------------------------------------------+
```
