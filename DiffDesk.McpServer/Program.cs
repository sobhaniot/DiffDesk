using System.Text.Json;
using System.Text.Json.Serialization;
using DiffDesk.Core;

await new DiffDeskMcpServer().RunAsync();

internal sealed class DiffDeskMcpServer
{
    private const string ModernProtocolVersion = "2026-07-28";
    private static readonly string[] LegacyProtocolVersions = ["2025-11-25", "2025-06-18"];
    private const int MaxLineLength = 4 * 1024 * 1024;
    private readonly FolderComparisonService _folders = new();
    private readonly FileOperationService _operations = new();
    private readonly TextDiffService _textDiff = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private string? _legacyProtocolVersion;

    public async Task RunAsync()
    {
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (line.Length > MaxLineLength)
            {
                await WriteErrorAsync(null, -32700, "Request exceeds the maximum message size.");
                continue;
            }

            JsonDocument request;
            try { request = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                await WriteErrorAsync(null, -32700, "Parse error.");
                continue;
            }

            using (request)
            {
                var root = request.RootElement;
                var id = root.TryGetProperty("id", out var requestId) ? requestId.Clone() : (JsonElement?)null;
                var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
                if (method is null)
                {
                    if (id is not null) await WriteErrorAsync(id, -32600, "Invalid request.");
                    continue;
                }

                if (method.StartsWith("notifications/", StringComparison.Ordinal)) continue;
                if (id is null) continue;

                try
                {
                    var parameters = root.TryGetProperty("params", out var p) ? p : default;
                    if (method == "initialize")
                    {
                        var initialize = InitializeResult(parameters);
                        await WriteResultAsync(id, initialize);
                        continue;
                    }
                    if (method == "server/discover")
                    {
                        EnsureModernMetadata(parameters);
                        await WriteResultAsync(id, DiscoverResult());
                        continue;
                    }

                    var isModern = _legacyProtocolVersion is null;
                    if (isModern) EnsureModernMetadata(parameters);
                    var result = method switch
                    {
                        "ping" => isModern ? Complete(new { }) : new { },
                        "tools/list" => ToolsListResult(isModern),
                        "tools/call" => await CallToolAsync(parameters),
                        _ => throw new RpcMethodNotFoundException(method)
                    };
                    await WriteResultAsync(id, result);
                }
                catch (UnsupportedVersionException exception)
                {
                    await WriteErrorAsync(id, -32022, exception.Message, new { supported = new[] { ModernProtocolVersion }, requested = exception.Requested });
                }
                catch (RpcInvalidParamsException exception)
                {
                    await WriteErrorAsync(id, -32602, exception.Message);
                }
                catch (RpcMethodNotFoundException exception)
                {
                    await WriteErrorAsync(id, -32601, exception.Message);
                }
                catch (Exception exception)
                {
                    await WriteToolErrorAsync(id, exception.Message);
                    Console.Error.WriteLine(exception);
                }
            }
        }
    }

    private object InitializeResult(JsonElement parameters)
    {
        var requestedVersion = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var version)
            ? version.GetString()
            : null;
        _legacyProtocolVersion = requestedVersion is not null && LegacyProtocolVersions.Contains(requestedVersion, StringComparer.Ordinal)
            ? requestedVersion
            : LegacyProtocolVersions[0];
        return new
        {
            protocolVersion = _legacyProtocolVersion,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "diffdesk", version = "1.0.0" },
            instructions = "Compare folders and files with DiffDesk. Copy and delete tools return a preview first; set confirm=true to execute the operation."
        };
    }

    private static object DiscoverResult() => new
    {
        resultType = "complete",
        supportedVersions = new[] { ModernProtocolVersion },
        capabilities = new { tools = new { listChanged = false } },
        _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = new { name = "diffdesk", version = "1.0.0" } },
        instructions = "Compare folders and files with DiffDesk. Copy and delete tools return a preview first; set confirm=true to execute the operation.",
        ttlMs = 3_600_000,
        cacheScope = "public"
    };

    private static object ToolsListResult(bool modern) => new
    {
        resultType = modern ? "complete" : null,
        _meta = modern ? ServerMeta() : null,
        tools = new object[]
        {
            new
            {
                name = "compare_folders",
                description = "Compare two folder trees and return file/folder differences, sizes, timestamps, and which side has the newer changed file.",
                inputSchema = Schema(
                    new { leftPath = StringProperty("Absolute path to the left folder."), rightPath = StringProperty("Absolute path to the right folder."), compareContent = BoolProperty("Compare file contents."), compareModifiedTime = BoolProperty("Treat different modification times as changes."), includeSubdirectories = BoolProperty("Include nested folders."), ignoreNameCase = BoolProperty("Match names without case sensitivity."), excludePatterns = StringProperty("Semicolon-separated file and folder patterns to exclude."), maxRows = IntegerProperty("Maximum number of rows to return (1-5000).") },
                    "leftPath", "rightPath")
            },
            new
            {
                name = "get_file_diff",
                description = "Compare the text or binary contents of two files and return a line-aligned diff.",
                inputSchema = Schema(new { leftPath = StringProperty("Absolute path to the left file."), rightPath = StringProperty("Absolute path to the right file."), maxLines = IntegerProperty("Maximum diff lines to return (1-2000).") }, "leftPath", "rightPath")
            },
            new
            {
                name = "copy_items",
                description = "Copy selected relative paths from one folder tree to another. Returns a preview unless confirm=true. Directories include all nested files and folders.",
                inputSchema = Schema(new { sourceRoot = StringProperty("Absolute source folder path."), destinationRoot = StringProperty("Absolute destination folder path."), relativePaths = StringArrayProperty("Relative paths of selected files or folders."), confirm = BoolProperty("Set true only after reviewing the preview to execute the copy."), backupRoot = StringProperty("Optional absolute path for backups of overwritten destination items.") }, "sourceRoot", "destinationRoot", "relativePaths")
            },
            new
            {
                name = "delete_items",
                description = "Delete selected relative paths from a folder tree after creating recoverable backups. Returns a preview unless confirm=true. Deleting a directory includes its complete contents.",
                inputSchema = Schema(new { rootPath = StringProperty("Absolute folder path containing the selected items."), relativePaths = StringArrayProperty("Relative paths of selected files or folders."), confirm = BoolProperty("Set true only after reviewing the preview to execute deletion."), backupRoot = StringProperty("Optional absolute backup directory. Defaults to DiffDesk's local backup folder.") }, "rootPath", "relativePaths")
            }
        }
    };

    private async Task<object> CallToolAsync(JsonElement parameters)
    {
        var name = RequiredString(parameters, "name");
        var args = parameters.TryGetProperty("arguments", out var arguments) ? arguments : JsonDocument.Parse("{}").RootElement.Clone();
        var result = name switch
        {
            "compare_folders" => await CompareFoldersAsync(args),
            "get_file_diff" => await GetFileDiffAsync(args),
            "copy_items" => await CopyItemsAsync(args),
            "delete_items" => await DeleteItemsAsync(args),
            _ => throw new ArgumentException($"Unknown DiffDesk tool: {name}")
        };
        return result;
    }

    private async Task<object> CompareFoldersAsync(JsonElement args)
    {
        var leftPath = FullPath(RequiredString(args, "leftPath"));
        var rightPath = FullPath(RequiredString(args, "rightPath"));
        var options = new ComparisonOptions
        {
            CompareContent = OptionalBool(args, "compareContent", true),
            CompareModifiedTime = OptionalBool(args, "compareModifiedTime", false),
            IncludeSubdirectories = OptionalBool(args, "includeSubdirectories", true),
            IgnoreNameCase = OptionalBool(args, "ignoreNameCase", true),
            ExcludePatterns = OptionalString(args, "excludePatterns") ?? "bin;obj;.git;*.tmp"
        };
        var snapshot = await _folders.CompareAsync(leftPath, rightPath, options);
        var maxRows = Math.Clamp(OptionalInt(args, "maxRows", 500), 1, 5000);
        var returned = snapshot.Rows.Take(maxRows).Select(row => new
        {
            row.RelativePath,
            kind = row.Kind.ToString(),
            row.IsDirectory,
            leftPath = row.LeftPath,
            rightPath = row.RightPath,
            row.LeftSize,
            row.RightSize,
            leftModified = row.LeftModified,
            rightModified = row.RightModified,
            newerSide = row.NewerSide?.ToString(),
            row.HasChildren,
            row.Error
        }).ToArray();

        return ToolResult(new
        {
            leftPath,
            rightPath,
            totalRows = snapshot.Rows.Count,
            returnedRows = returned.Length,
            omittedRows = Math.Max(0, snapshot.Rows.Count - returned.Length),
            snapshot.DifferentCount,
            snapshot.IdenticalCount,
            durationMilliseconds = (long)snapshot.Duration.TotalMilliseconds,
            issues = snapshot.Issues,
            rows = returned
        });
    }

    private async Task<object> GetFileDiffAsync(JsonElement args)
    {
        var leftPath = FullPath(RequiredString(args, "leftPath"));
        var rightPath = FullPath(RequiredString(args, "rightPath"));
        if (!File.Exists(leftPath) || !File.Exists(rightPath)) throw new FileNotFoundException("هر دو مسیر فایل باید وجود داشته باشند.");
        var comparison = await _textDiff.CompareFilesAsync(leftPath, rightPath);
        var maxLines = Math.Clamp(OptionalInt(args, "maxLines", 500), 1, 2000);
        return ToolResult(new
        {
            leftPath,
            rightPath,
            comparison.IsBinary,
            comparison.IsIdentical,
            comparison.LeftLength,
            comparison.RightLength,
            comparison.Message,
            totalLines = comparison.Lines.Count,
            omittedLines = Math.Max(0, comparison.Lines.Count - maxLines),
            lines = comparison.Lines.Take(maxLines).Select(line => new
            {
                leftLine = line.LeftNumber,
                line.LeftText,
                rightLine = line.RightNumber,
                line.RightText,
                kind = line.Kind.ToString()
            })
        });
    }

    private async Task<object> CopyItemsAsync(JsonElement args)
    {
        var sourceRoot = FullPath(RequiredString(args, "sourceRoot"));
        var destinationRoot = FullPath(RequiredString(args, "destinationRoot"));
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"پوشهٔ مبدا وجود ندارد: {sourceRoot}");
        if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException($"پوشهٔ مقصد وجود ندارد: {destinationRoot}");
        var rows = BuildSelectedRows(sourceRoot, RequiredStringArray(args, "relativePaths"));
        var plan = _operations.CreateCopyPlan(rows, FileSide.Left, destinationRoot);
        if (!OptionalBool(args, "confirm", false))
            return ToolResult(new { state = "preview", plan.Description, items = plan.Items.Select(item => new { item.RelativePath, item.IsDirectory, item.DestinationExists, source = item.SourcePath, destination = item.DestinationPath }) });

        var backupRoot = ResolveBackupRoot(OptionalString(args, "backupRoot"));
        var report = await _operations.ExecuteAsync(plan, backupRoot);
        return ToolResult(new { state = "completed", plan.Description, report.Succeeded, report.Failed, report.Errors, report.BackupDirectory });
    }

    private async Task<object> DeleteItemsAsync(JsonElement args)
    {
        var rootPath = FullPath(RequiredString(args, "rootPath"));
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException($"پوشهٔ مبدا وجود ندارد: {rootPath}");
        var rows = BuildSelectedRows(rootPath, RequiredStringArray(args, "relativePaths"));
        var plan = _operations.CreateDeletePlan(rows, FileSide.Left);
        if (!OptionalBool(args, "confirm", false))
            return ToolResult(new { state = "preview", plan.Description, backupRoot = ResolveBackupRoot(OptionalString(args, "backupRoot")), items = plan.Items.Select(item => new { item.RelativePath, item.IsDirectory, source = item.SourcePath, includesContents = item.IsDirectory }) });

        var backupRoot = ResolveBackupRoot(OptionalString(args, "backupRoot"));
        var report = await _operations.ExecuteAsync(plan, backupRoot);
        return ToolResult(new { state = "completed", plan.Description, report.Succeeded, report.Failed, report.Errors, report.BackupDirectory });
    }

    private static FileComparisonRow[] BuildSelectedRows(string root, IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0) throw new ArgumentException("حداقل یک مسیر نسبی لازم است.");
        return relativePaths.Select(relative =>
        {
            var clean = NormalizeRelativePath(relative);
            var full = Path.GetFullPath(Path.Combine(root, clean.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"مسیر از پوشهٔ مبدا خارج می‌شود: {relative}");
            var isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full)) throw new FileNotFoundException($"مورد انتخاب‌شده وجود ندارد: {relative}", full);
            var kind = isDirectory ? DifferenceKind.Changed : DifferenceKind.LeftOnly;
            return new FileComparisonRow(clean, full, null, isDirectory ? null : new FileInfo(full).Length, null,
                isDirectory ? Directory.GetLastWriteTimeUtc(full) : File.GetLastWriteTimeUtc(full), null, kind,
                IsDirectory: isDirectory, Depth: clean.Count(character => character == '/'));
        }).ToArray();
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("مسیر نسبی نمی‌تواند خالی باشد.");
        var clean = value.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(clean) || clean.StartsWith('/') || clean.Split('/').Any(segment => segment is "" or "." or "..") || clean.Contains(':'))
            throw new ArgumentException($"مسیر نسبی معتبر نیست: {value}");
        return clean;
    }

    private static string ResolveBackupRoot(string? requested)
    {
        var dataRoot = Environment.GetEnvironmentVariable("DIFFDESK_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffDesk");
        return FullPath(requested ?? Path.Combine(dataRoot, "Backups"));
    }

    private static string FullPath(string path) => Path.GetFullPath(path.Trim().Trim('"'));

    private static string RequiredString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"پارامتر متنی '{name}' الزامی است.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool OptionalBool(JsonElement args, string name, bool fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static int OptionalInt(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static string[] RequiredStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"پارامتر '{name}' باید آرایه‌ای از مسیرها باشد.");
        var items = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ArgumentException($"تمام عناصر '{name}' باید متن باشند.")).ToArray();
        if (items.Length == 0) throw new ArgumentException($"آرایهٔ '{name}' نمی‌تواند خالی باشد.");
        return items;
    }

    private static object Schema(object properties, params string[] required)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(properties));
        return new { type = "object", properties = document.RootElement.Clone(), required, additionalProperties = false };
    }

    private static object StringProperty(string description) => new { type = "string", description };
    private static object BoolProperty(string description) => new { type = "boolean", description };
    private static object IntegerProperty(string description) => new { type = "integer", description };
    private static object StringArrayProperty(string description) => new { type = "array", items = new { type = "string" }, description };

    private static void EnsureModernMetadata(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("_meta", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            throw new RpcInvalidParamsException("Modern MCP requests must include params._meta.");
        if (!metadata.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
            throw new RpcInvalidParamsException("Modern MCP requests must include io.modelcontextprotocol/protocolVersion.");
        if (!metadata.TryGetProperty("io.modelcontextprotocol/clientCapabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            throw new RpcInvalidParamsException("Modern MCP requests must include io.modelcontextprotocol/clientCapabilities.");
        var version = versionElement.GetString();
        if (!string.Equals(version, ModernProtocolVersion, StringComparison.Ordinal))
            throw new UnsupportedVersionException(version ?? "");
    }

    private static object Complete(object value) => new { resultType = "complete", _meta = ServerMeta() };

    private async Task WriteResultAsync(JsonElement? id, object result)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, _jsonOptions));
        await Console.Out.FlushAsync();
    }

    private async Task WriteToolErrorAsync(JsonElement? id, string message)
    {
        var result = new
        {
            resultType = _legacyProtocolVersion is null ? "complete" : null,
            _meta = _legacyProtocolVersion is null ? ServerMeta() : null,
            isError = true,
            content = new[] { new { type = "text", text = message } }
        };
        await WriteResultAsync(id, result);
    }

    private async Task WriteErrorAsync(JsonElement? id, int code, string message, object? data = null)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message, data } }, _jsonOptions));
        await Console.Out.FlushAsync();
    }

    private object ToolResult(object value) => new
    {
        resultType = _legacyProtocolVersion is null ? "complete" : null,
        _meta = _legacyProtocolVersion is null ? ServerMeta() : null,
        isError = false,
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } },
        structuredContent = value
    };

    private static Dictionary<string, object> ServerMeta() => new() { ["io.modelcontextprotocol/serverInfo"] = new { name = "diffdesk", version = "1.0.0" } };

    private sealed class RpcMethodNotFoundException(string method) : Exception($"Method not found: {method}");
    private sealed class RpcInvalidParamsException(string message) : Exception(message);
    private sealed class UnsupportedVersionException(string requested) : Exception($"Unsupported protocol version: {requested}")
    {
        public string Requested { get; } = requested;
    }
}
