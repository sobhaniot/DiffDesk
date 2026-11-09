# DiffDesk

**DiffDesk** is a Windows desktop application for comparing two folders side by side. It highlights differences, lets you inspect expanded folder trees, and provides explicit copy, move, rename, and delete operations. A separate MCP server exposes the same core comparison and file-operation services to MCP clients.

The project is open for contributions. The UI is inspired by the two-pane comparison workflow used by established tools; DiffDesk has its own implementation and code.

## Features

- Side-by-side folder comparison with recursive scanning and expandable directory rows.
- File comparison by content, with optional modified-time comparison, case handling, and exclusion patterns.
- Clear states for identical, changed, left-only, right-only, and inaccessible items.
- Text-file diff view with line-level and inline highlighting; binary files are identified instead of decoded as text.
- Selection-aware copy direction, plus copy, move, rename, and delete plans. Existing destination data is backed up before overwrite or deletion when possible.
- Detection of files whose two versions have both changed since the saved content baseline; the app warns instead of guessing which side to overwrite.
- Recent folder pairs and saved application settings are stored locally.
- A local MCP server for folder comparison, text diffs, copy previews, and delete previews.
- Single-instance desktop behavior.

## Try the app

DiffDesk targets Windows x64 and .NET 10. For a self-contained portable executable, build from source with the .NET 10 SDK:

```powershell
dotnet publish DiffDesk.Desktop\DiffDesk.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o artifacts\DiffDesk\SingleExe
```

The executable is written to `artifacts\DiffDesk\SingleExe\DiffDesk.exe`. The output is intentionally ignored by Git; build it locally or download it from a project release when one is published. A self-contained build includes its .NET runtime and does not require a separate .NET installation on the target computer.

To run the desktop project during development:

```powershell
dotnet run --project DiffDesk.Desktop\DiffDesk.Desktop.csproj
```

## Build and inspect the source

Requirements:

- Windows 10 or later (WPF desktop application).
- .NET 10 SDK. Visual Studio with the .NET desktop development workload is also suitable.
- Git for source control.

Clone and build:

```powershell
git clone https://github.com/sobhaniot/DiffDesk.git
cd DiffDesk
dotnet build DiffDesk.sln -c Release
```

The solution includes the desktop app, the platform-independent core library, the MCP server, and the smoke-test executable. Run the existing smoke checks with:

```powershell
dotnet run --project DiffDesk.SmokeTests\DiffDesk.SmokeTests.csproj -c Release
```

## Architecture

```text
DiffDesk.Desktop ──┐
                   ├──> DiffDesk.Core
DiffDesk.McpServer ┘

DiffDesk.SmokeTests ──> DiffDesk.Core
```

- **DiffDesk.Core** contains comparison models, folder scanning, text diffing, file operations, selection rules, and session storage. It has no WPF dependency.
- **DiffDesk.Desktop** is the WPF Windows UI. It owns navigation, two-pane selection, dialogs, and presentation.
- **DiffDesk.McpServer** is a standalone stdio MCP server that calls the core services. It writes protocol messages to stdout and diagnostics to stderr.
- **DiffDesk.SmokeTests** is a lightweight executable that checks core behavior without a separate test framework.

For implementation boundaries and safety notes, see [docs/architecture.md](docs/architecture.md). For pull requests, see [CONTRIBUTING.md](CONTRIBUTING.md).

## MCP server

Build the server:

```powershell
dotnet build DiffDesk.McpServer\DiffDesk.McpServer.csproj -c Release
```

MCP clients should launch the built `DiffDesk.McpServer.dll` with `dotnet` and use newline-delimited JSON-RPC over standard input/output. The server README contains the available tools and a client configuration example; replace its sample paths with paths on your machine.

Copy and delete MCP operations use a preview/confirm flow. Review the previewed paths before calling the operation with `confirm: true`.

## Current scope

The folder comparison and basic text comparison workflows are implemented. Three-way merge, advanced binary/image/table viewers, synchronization profiles, and remote/cloud sources are future work; see [ROADMAP.md](ROADMAP.md).

## License

No open-source license has been selected yet. Until a license is added, the source is public for inspection and contribution proposals, but standard copyright restrictions still apply to reuse and redistribution.

---

## فارسی

**DiffDesk** یک برنامهٔ دسکتاپ ویندوز برای مقایسهٔ دو پوشه در دو پنل کنار هم است. تفاوت‌ها را نمایش می‌دهد، امکان باز و بسته‌کردن شاخه‌های پوشه را دارد و عملیات کپی، جابه‌جایی، تغییرنام و حذف را با مقصد مشخص انجام می‌دهد. سرور MCP جداگانه‌ای نیز سرویس‌های هستهٔ مقایسه و عملیات فایل را در اختیار کلاینت‌های MCP قرار می‌دهد.

### ساخت نسخهٔ قابل‌حمل

برای ساخت فایل تک‌اجرایی قابل‌حمل، Windows و .NET 10 SDK لازم است. دستور `dotnet publish` بخش انگلیسی را اجرا کنید؛ خروجی در `artifacts\DiffDesk\SingleExe\DiffDesk.exe` ساخته می‌شود. نسخهٔ self-contained به نصب جداگانهٔ .NET روی سیستم مقصد نیاز ندارد.

### ساختار پروژه

- `DiffDesk.Core`: مقایسهٔ پوشه و متن، مدل‌ها، عملیات فایل، قواعد انتخاب و ذخیرهٔ نشست‌ها.
- `DiffDesk.Desktop`: رابط WPF ویندوز.
- `DiffDesk.McpServer`: سرور MCP محلی بر پایهٔ stdio.
- `DiffDesk.SmokeTests`: بررسی‌های سبک برای رفتارهای هسته.

برای ساخت، دستورهای PowerShell در بخش انگلیسی را اجرا کنید. راهنمای معماری در [docs/architecture.md](docs/architecture.md) و راهنمای مشارکت در [CONTRIBUTING.md](CONTRIBUTING.md) است.

**مجوز:** هنوز مجوز متن‌باز انتخاب نشده است؛ تا زمان افزودن مجوز، استفادهٔ مجدد و بازتوزیع تابع محدودیت‌های عادی حق نشر است.
