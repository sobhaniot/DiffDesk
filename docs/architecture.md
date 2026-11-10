# DiffDesk architecture

## Project boundaries

```text
                 ┌─────────────────────┐
                 │ DiffDesk.Desktop    │  WPF, Windows-only
                 └──────────┬──────────┘
                            │ project reference
                 ┌──────────▼──────────┐
                 │ DiffDesk.Core       │  comparison and file services
                 └──────────▲──────────┘
                            │ project reference
         ┌──────────────────┴──────────────────┐
         │                                     │
┌────────▼─────────┐                ┌──────────▼─────────┐
│ DiffDesk.McpServer│                │ DiffDesk.SmokeTests│
│ stdio JSON-RPC    │                │ executable checks  │
└──────────────────┘                └────────────────────┘
```

## Core services

- `FolderComparisonService` scans each side, normalizes relative paths, applies include/exclude and comparison options, classifies items, and reports progress and access errors.
- `TextDiffService` reads supported text encodings and produces line and inline differences; binary input is reported as binary.
- `FileOperationService` builds explicit plans for copy, move, delete, and rename. Directory selection includes its subtree; selecting a child does not implicitly select its parent. Existing targets are backed up before replacement or deletion when possible.
- `FileSelectionPolicy` normalizes selection so a directory and its descendants are not both operated on redundantly.
- `FileChangeHistory` stores content fingerprints used to detect changes on both sides since the last saved baseline.
- `SessionStore` persists recent comparisons and the last folder pair in local application data.

## Desktop application

`DiffDesk.Desktop` is a WPF application targeting `net10.0-windows`. `MainWindow.xaml` defines the two-pane folder view and text-diff presentation; `MainWindow.xaml.cs` coordinates UI actions with core services. The app enforces single-instance behavior and stores settings separately from the source tree. For portable use, a `Data` directory beside the executable is used when present; otherwise settings go to the user's local application data.

The user interface is Persian-first and right-to-left. The comparison panes remain left-to-right to match conventional folder-diff tools. Status colors distinguish missing, changed, selected, and newer versions; text labels and file metadata remain available alongside color.

## MCP server

`DiffDesk.McpServer` communicates over stdio using newline-delimited JSON-RPC and invokes the same core services as the desktop application. It supports folder comparison, text diff inspection, and preview/confirm flows for copy and delete. Protocol output goes to stdout; diagnostic output goes to stderr.

## Data and safety boundaries

- Comparison is read-only until the user selects an operation.
- Copy/move/delete/rename operations are represented as plans before execution.
- MCP copy and delete require a second, explicit confirmation call after preview.
- Paths are resolved beneath the chosen destination root; attempts to escape that root are rejected.
- Reparse points are skipped during recursive folder scans to avoid accidental traversal through junctions or symlinks.

These are current implementation behaviors, not a guarantee against every filesystem failure. Permission errors and unavailable paths can still cause individual operations to fail and are reported to the caller.

## Build targets

- Desktop: `net10.0-windows`, WPF.
- Core, MCP server, and smoke checks: `net10.0`.
- Portable desktop publish: `win-x64`, self-contained, single file. Generated binaries are not committed to the source repository.
