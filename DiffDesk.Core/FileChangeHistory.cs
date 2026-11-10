using System.Text.Json;

namespace DiffDesk.Core;

public sealed class FileChangeHistory(string dataDirectory)
{
    private sealed record Fingerprints(string First, string Second);

    private readonly string _historyPath = Path.Combine(dataDirectory, "file-change-history.json");

    public async Task<ComparisonSnapshot> TrackAsync(
        ComparisonSnapshot snapshot,
        CancellationToken cancellationToken = default,
        IProgress<ComparisonProgress>? progress = null,
        bool preferLatestTimestamp = false)
    {
        var history = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var rows = snapshot.Rows.ToArray();
        var issues = snapshot.Issues.ToList();
        var updated = false;
        var trackableCount = rows.Count(IsTrackable);
        var completed = 0;
        for (var index = 0; index < rows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            if (row.IsDirectory || row.Kind == DifferenceKind.Error || row.LeftPath is null || row.RightPath is null) continue;

            var key = PairKey(row.LeftPath, row.RightPath, out var reversed);
            history.TryGetValue(key, out var baseline);
            try
            {
                var left = QuickFingerprint(row.LeftPath);
                var right = QuickFingerprint(row.RightPath);
                if (baseline is not null && (!IsQuickFingerprint(baseline.First) || !IsQuickFingerprint(baseline.Second)))
                    baseline = null; // Rebase legacy content hashes to Beyond Compare-style quick metadata checks.
                var current = Ordered(left, right, reversed);
                if (baseline is null || (row.Kind == DifferenceKind.Identical && current != baseline))
                {
                    history[key] = current;
                    updated = true;
                }
                if (row.Kind == DifferenceKind.Changed)
                {
                    var leftChanged = baseline is not null && left != (reversed ? baseline.Second : baseline.First);
                    var rightChanged = baseline is not null && right != (reversed ? baseline.First : baseline.Second);
                    rows[index] = row with
                    {
                        ContentHistoryChecked = true,
                        BothVersionsChanged = leftChanged && rightChanged,
                        ChangedContentSide = preferLatestTimestamp || leftChanged == rightChanged ? null : leftChanged ? FileSide.Left : FileSide.Right
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

            completed++;
            if (completed % 16 == 0 || completed == trackableCount)
                progress?.Report(new ComparisonProgress(completed, trackableCount, row.RelativePath, "بررسی سابقهٔ فایل‌ها"));
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

    private static bool IsTrackable(FileComparisonRow row) =>
        !row.IsDirectory && row.Kind != DifferenceKind.Error && row.LeftPath is not null && row.RightPath is not null;

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
            var sourceFingerprint = QuickFingerprint(source);
            if (sourceFingerprint == (reversed ? baseline.Second : baseline.First)) return;
            var destinationFingerprint = QuickFingerprint(destination);
            if (sourceFingerprint != destinationFingerprint && destinationFingerprint != (reversed ? baseline.First : baseline.Second)) conflicts.Add(relative);
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

    private static string QuickFingerprint(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return $"quick-v1:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private static bool IsQuickFingerprint(string value) => value.StartsWith("quick-v1:", StringComparison.Ordinal);
}
