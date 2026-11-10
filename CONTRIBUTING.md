# Contributing to DiffDesk

Thanks for your interest in improving DiffDesk. The project is a Windows-first WPF application with a reusable .NET core library and a local MCP server.

## Development setup

1. Install Git and the .NET 10 SDK on Windows.
2. Clone the repository and open `DiffDesk.sln` in Visual Studio, or use the `dotnet` CLI.
3. Build with `dotnet build DiffDesk.sln -c Release`.
4. Run the existing core smoke checks with `dotnet run --project DiffDesk.SmokeTests\DiffDesk.SmokeTests.csproj -c Release`.

The desktop project uses WPF and cannot run on non-Windows systems. `DiffDesk.Core` is platform-independent and is the best place for comparison or file-operation logic that does not require a UI.

## Before opening a pull request

- Open an issue first for large behavior changes so the scope can be agreed upon.
- Keep changes focused and preserve the separation between UI code and core services.
- Do not commit `bin/`, `obj/`, `artifacts/`, local settings, backups, credentials, or machine-specific absolute paths.
- For file operations, preserve clear source/destination semantics, path containment checks, confirmation, and cancellation where applicable.
- For MCP operations, preserve the preview-before-confirm flow for destructive or overwriting actions. Keep stdout reserved for protocol messages.
- Describe user-visible behavior changes and include screenshots for UI changes when practical.
- Run the build and relevant existing smoke checks, and include the results in the pull request.

## Pull request checklist

- [ ] The change has a clear purpose and a concise description.
- [ ] Build succeeds on Windows with the .NET 10 SDK.
- [ ] Relevant existing smoke checks pass.
- [ ] No generated output, local data, credentials, or personal paths are included.
- [ ] Documentation is updated when behavior or setup changes.

## Language and UI

The product UI is Persian-first and right-to-left, while code identifiers and technical documentation are primarily English. Keep RTL layout behavior intact and make status understandable through text or icons as well as color.

## License status

The project has not selected an open-source license yet. Please open an issue before proposing a license change or adding third-party code with licensing requirements.
