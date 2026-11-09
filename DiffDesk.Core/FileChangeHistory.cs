using System.Security.Cryptography;
using System.Text.Json;

namespace DiffDesk.Core;

public sealed class FileChangeHistory(string dataDirectory)
{
    private sealed record Fingerprints(string First, string Second);

    private readonly string _historyPath = Path.Combine(dataDirectory, "file-change-history.json");

    public async Task<ComparisonSnapshot> TrackAsync(ComparisonSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var history = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var rows = snapshot.Rows.ToArray();
        var issues = snapshot.Issues.ToList();
        var updated = false;
        for (var index = 0; index < rows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            if (row.IsDirectory || row.LeftPath is null || row.RightPath is null) continue;

            var key = PairKey(row.LeftPath, row.RightPath, out var reversed);
            history.TryGetValue(key, out var baseline);
            try
            {
                var left = await FingerprintAsync(row.LeftPath, cancellationToken).ConfigureAwait(false);
                var right = await FingerprintAsync(row.RightPath, cancellationToken).ConfigureAwait(false);
                var current = Ordered(left, right, reversed);
                if (baseline is null || (left == right && current != baseline))
                {
                    history[key] = current;
                    updated = true;
                }
                if (left != right)
                {
                    var leftChanged = baseline is not null && left != (reversed ? baseline.Second : baseline.First);
                    var rightChanged = baseline is not null && right != (reversed ? baseline.First : baseline.Second);
                    rows[index] = row with
                    {
                        Kind = DifferenceKind.Changed,
                        ContentHistoryChecked = true,
                        BothVersionsChanged = leftChanged && rightChanged,
                        ChangedContentSide = leftChanged == rightChanged ? null : leftChanged ? FileSide.Left : FileSide.Right
                    };
                }
                else
                {
                    rows[index] = row with { ContentHistoryChecked = true };
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                rows[index] = row with { ContentHistoryChecked = true };
                issues.Add($"{row.RelativePath} — بررسی سابقهٔ فایل ممکن نبود: {exception.Message}");
            }
        }

        if (updated)
        {
            Directory.CreateDirectory(dataDirectory);
            var temporaryPath = _historyPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, history, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        return snapshot with { Rows = rows, Issues = issues };
    }

    public async Task<IReadOnlyList<string>> FindOverwriteConflictsAsync(FileOperationPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Kind is not (FileOperationKind.Copy or FileOperationKind.Move)) return [];
        var history = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (history.Count == 0) return [];

        var conflicts = new List<string>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.DestinationPath is not { } destination) continue;
            if (!item.IsDirectory)
            {
                await CheckAsync(item.SourcePath, destination, item.RelativePath).ConfigureAwait(false);
            }
            else if (Directory.Exists(destination))
            {
                foreach (var source in Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(item.SourcePath, source);
                    await CheckAsync(source, Path.Combine(destination, relative), Path.Combine(item.RelativePath, relative)).ConfigureAwait(false);
                }
            }
        }
        return conflicts;

        async Task CheckAsync(string source, string destination, string relative)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!history.TryGetValue(PairKey(source, destination, out var reversed), out var baseline) || !File.Exists(destination)) return;
            var sourceHash = await FingerprintAsync(source, cancellationToken).ConfigureAwait(false);
            if (sourceHash == (reversed ? baseline.Second : baseline.First)) return;
            var destinationHash = await FingerprintAsync(destination, cancellationToken).ConfigureAwait(false);
            if (sourceHash != destinationHash && destinationHash != (reversed ? baseline.First : baseline.Second)) conflicts.Add(relative);
        }
    }

    private async Task<Dictionary<string, Fingerprints>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(_historyPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var history = new Dictionary<string, Fingerprints>();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                // Earlier releases stored a single fingerprint shared by both files.
                history[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                    ? new Fingerprints(entry.Value.GetString()!, entry.Value.GetString()!)
                    : entry.Value.Deserialize<Fingerprints>() ?? throw new IOException("فایل سابقهٔ تغییرات معتبر نیست.");
            }
            return history;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static Fingerprints Ordered(string left, string right, bool reversed) =>
        reversed ? new Fingerprints(right, left) : new Fingerprints(left, right);

    private static string PairKey(string left, string right, out bool reversed)
    {
        left = NormalizePath(left);
        right = NormalizePath(right);
        reversed = string.CompareOrdinal(left, right) > 0;
        return reversed ? $"{right}\0{left}" : $"{left}\0{right}";
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
