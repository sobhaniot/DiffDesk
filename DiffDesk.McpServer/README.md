# DiffDesk MCP Server

Local MCP server for DiffDesk. It uses the same comparison and file-operation services as the desktop app and communicates over standard input/output using newline-delimited JSON-RPC. It supports the current stateless `2026-07-28` protocol and falls back to the legacy `initialize` handshake used by older clients.

## Tools

- `compare_folders`: compare two folder trees and return differences, file metadata, and the newer side for changed files.
- `get_file_diff`: compare two files and return an aligned text diff.
- `copy_items`: preview selected relative paths, then copy after a second call with `confirm: true`. Existing destinations are backed up.
- `delete_items`: preview selected relative paths, then delete after a second call with `confirm: true`. Deleted files and folders are backed up first.

Copy and delete calls are previews by default. Review the returned paths before explicitly confirming an operation. Directory selections include the directory's complete contents; a child selection does not expand to its parent.

## Build

```powershell
dotnet build DiffDesk.McpServer\DiffDesk.McpServer.csproj -c Release
```

## Client configuration

Build the solution or the server project first. Configure your MCP client to launch the built `DiffDesk.McpServer.dll` with the .NET runtime. For clients that accept an `mcpServers` JSON object:

```json
{
  "mcpServers": {
    "diffdesk": {
      "command": "dotnet",
      "args": [
        "C:\\path\\to\\DiffDesk\\DiffDesk.McpServer\\bin\\Release\\net10.0\\DiffDesk.McpServer.dll"
      ],
      "env": {
        "DIFFDESK_DATA_DIR": "C:\\path\\to\\DiffDeskData"
      }
    }
  }
}
```

Replace the sample paths with the actual paths on your computer. `DIFFDESK_DATA_DIR` is optional; when omitted, the server uses the platform's local application-data directory. The server only writes protocol messages to stdout; diagnostics are sent to stderr.
