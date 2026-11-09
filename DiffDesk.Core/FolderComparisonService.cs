using System.IO.Enumeration;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace DiffDesk.Core;

public sealed class FolderComparisonService
{
    public Task<ComparisonSnapshot> CompareAsync(
        string leftRoot,
        string rightRoot,
        ComparisonOptions options,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightRoot);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() => CompareCore(leftRoot, rightRoot, options, progress, cancellationToken), cancellationToken);
    }

    private static ComparisonSnapshot CompareCore(
        string leftRoot,
        string rightRoot,
        ComparisonOptions options,
        IProgress<ComparisonProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var left = NormalizeRoot(leftRoot);
        var right = NormalizeRoot(rightRoot);
        if (!Directory.Exists(left) && !Directory.Exists(right))
            throw new DirectoryNotFoundException($"هیچ‌کدام از پوشه‌های انتخاب‌شده در دسترس نیستند: {left} | {right}");

        var comparer = options.IgnoreNameCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var issues = new List<string>();
        var patterns = ParsePatterns(options.ExcludePatterns);
        var leftFiles = Scan(left, "چپ", options, patterns, comparer, issues, out var leftDirectories, progress, cancellationToken);
        var rightFiles = Scan(right, "راست", options, patterns, comparer, issues, out var rightDirectories, progress, cancellationToken);

        var allPaths = new HashSet<string>(leftFiles.Keys, comparer);
        allPaths.UnionWith(rightFiles.Keys);
        allPaths.UnionWith(leftDirectories.Keys);
        allPaths.UnionWith(rightDirectories.Keys);
        var pathsWithChildren = new HashSet<string>(comparer);
        foreach (var path in allPaths)
        {
            var separator = path.LastIndexOf('/');
            while (separator >= 0)
            {
                pathsWithChildren.Add(path[..separator]);
                separator = path.LastIndexOf('/', separator - 1);
            }
        }
        var orderedPaths = allPaths.OrderBy(path => path, new HierarchicalPathComparer(comparer)).ToArray();
        var fileKinds = new Dictionary<string, DifferenceKind>(comparer);
        var changedDirectories = new HashSet<string>(comparer);

        for (var index = 0; index < orderedPaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = orderedPaths[index];
            var hasLeftDirectory = leftDirectories.ContainsKey(relative);
            var hasRightDirectory = rightDirectories.ContainsKey(relative);
            if (hasLeftDirectory || hasRightDirectory)
            {
                if (hasLeftDirectory != hasRightDirectory) AddAncestorDirectories(relative, changedDirectories);
            }
            else
            {
                leftFiles.TryGetValue(relative, out var leftFile);
                rightFiles.TryGetValue(relative, out var rightFile);
                var kind = Classify(leftFile, rightFile, options, cancellationToken);
                fileKinds[relative] = kind;
                if (kind != DifferenceKind.Identical) AddAncestorDirectories(relative, changedDirectories);
            }

            if (index % 16 == 0 || index == orderedPaths.Length - 1)
                progress?.Report(new ComparisonProgress(index + 1, orderedPaths.Length, relative, "مقایسهٔ محتوا"));
        }

        var rows = new List<FileComparisonRow>(orderedPaths.Length);

        for (var index = 0; index < orderedPaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = orderedPaths[index];
            if (leftDirectories.TryGetValue(relative, out var leftDirectory) || rightDirectories.TryGetValue(relative, out var rightDirectory))
            {
                leftDirectories.TryGetValue(relative, out leftDirectory);
                rightDirectories.TryGetValue(relative, out rightDirectory);
                var kind = leftDirectory is null ? DifferenceKind.RightOnly : rightDirectory is null ? DifferenceKind.LeftOnly :
                    changedDirectories.Contains(relative) ? DifferenceKind.Changed : DifferenceKind.Identical;
                rows.Add(new FileComparisonRow(relative, leftDirectory, rightDirectory, null, null,
                    leftDirectory is null ? null : Directory.GetLastWriteTimeUtc(leftDirectory),
                    rightDirectory is null ? null : Directory.GetLastWriteTimeUtc(rightDirectory), kind,
                    IsDirectory: true, Depth: relative.Count(character => character == '/'), HasChildren: pathsWithChildren.Contains(relative)));
            }
            else
            {
                leftFiles.TryGetValue(relative, out var leftFile);
                rightFiles.TryGetValue(relative, out var rightFile);
                var kind = fileKinds[relative];
                rows.Add(new FileComparisonRow(relative, leftFile?.FullPath, rightFile?.FullPath,
                    leftFile?.Length, rightFile?.Length, leftFile?.Modified, rightFile?.Modified, kind,
                    Depth: relative.Count(character => character == '/')));
            }
        }

        return new ComparisonSnapshot(rows, issues, DateTime.UtcNow - started);
    }

    private static void AddAncestorDirectories(string relativePath, HashSet<string> changedDirectories)
    {
        var separator = relativePath.LastIndexOf('/');
        while (separator >= 0)
        {
            changedDirectories.Add(relativePath[..separator]);
            separator = relativePath.LastIndexOf('/', separator - 1);
        }
    }

    private static Dictionary<string, FileRecord> Scan(
        string root,
        string side,
        ComparisonOptions options,
        IReadOnlyList<string> patterns,
        StringComparer comparer,
        List<string> issues,
        out Dictionary<string, string> directories,
        IProgress<ComparisonProgress>? progress,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, FileRecord>(comparer);
        directories = new Dictionary<string, string>(comparer);
        if (!Directory.Exists(root)) return records;
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryPath = pending.Pop();
            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(directoryPath);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                issues.Add($"{side}: {directoryPath} — {ex.Message}");
                continue;
            }

            FileInfo[] files;
            try { files = directory.GetFiles(); }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                issues.Add($"{side}: {directoryPath} — {ex.Message}");
                files = [];
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExcluded(file.Name, patterns, options.IgnoreNameCase)) continue;
                try
                {
                    var relative = Path.GetRelativePath(root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                    records[relative] = new FileRecord(file.FullName, file.Length, file.LastWriteTimeUtc);
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    issues.Add($"{side}: {file.FullName} — {ex.Message}");
                }
            }

            DirectoryInfo[] subdirectories;
            try { subdirectories = directory.GetDirectories(); }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                issues.Add($"{side}: {directoryPath} — {ex.Message}");
                subdirectories = [];
            }

            foreach (var child in subdirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExcluded(child.Name, patterns, options.IgnoreNameCase)) continue;
                try
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var relative = Path.GetRelativePath(root, child.FullName).Replace(Path.DirectorySeparatorChar, '/');
                    directories[relative] = child.FullName;
                    if (options.IncludeSubdirectories) pending.Push(child.FullName);
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    issues.Add($"{side}: {child.FullName} — {ex.Message}");
                }
            }

            visited++;
            if (visited % 32 == 0)
                progress?.Report(new ComparisonProgress(visited, 0, Path.GetRelativePath(root, directoryPath), $"اسکن {side}"));
        }

        return records;
    }

    private sealed class HierarchicalPathComparer(StringComparer segmentComparer) : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftSegments = left.Split('/');
            var rightSegments = right.Split('/');
            var commonLength = Math.Min(leftSegments.Length, rightSegments.Length);
            for (var index = 0; index < commonLength; index++)
            {
                var segmentComparison = segmentComparer.Compare(leftSegments[index], rightSegments[index]);
                if (segmentComparison != 0) return segmentComparison;
            }
            return leftSegments.Length.CompareTo(rightSegments.Length);
        }
    }

    private static DifferenceKind Classify(FileRecord? left, FileRecord? right, ComparisonOptions options, CancellationToken cancellationToken)
    {
        if (left is null) return DifferenceKind.RightOnly;
        if (right is null) return DifferenceKind.LeftOnly;
        if (left.Length != right.Length) return DifferenceKind.Changed;
        if (options.CompareModifiedTime && left.Modified != right.Modified) return DifferenceKind.Changed;
        if (options.CompareContent && !FilesEqual(left.FullPath, right.FullPath, cancellationToken)) return DifferenceKind.Changed;
        return DifferenceKind.Identical;
    }

    internal static bool FilesEqual(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        const int bufferSize = 128 * 1024;
        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        if (left.Length != right.Length) return false;
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readLeft = left.Read(leftBuffer, 0, leftBuffer.Length);
            var readRight = right.Read(rightBuffer, 0, rightBuffer.Length);
            if (readLeft != readRight) return false;
            if (readLeft == 0) return true;
            if (!leftBuffer.AsSpan(0, readLeft).SequenceEqual(rightBuffer.AsSpan(0, readRight))) return false;
        }
    }

    private static string NormalizeRoot(string path) => Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string[] ParsePatterns(string? raw) => (raw ?? string.Empty)
        .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsExcluded(string name, IReadOnlyList<string> patterns, bool ignoreCase)
    {
        foreach (var pattern in patterns)
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase)) return true;
        return false;
    }

    private static bool IsFileSystemException(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private sealed record FileRecord(string FullPath, long Length, DateTime Modified);
}
