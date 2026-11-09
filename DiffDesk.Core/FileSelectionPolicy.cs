namespace DiffDesk.Core;

public static class FileSelectionPolicy
{
    public static IReadOnlyList<FileComparisonRow> Normalize(
        IEnumerable<FileComparisonRow> selectedRows,
        IEnumerable<FileComparisonRow> newlySelectedRows)
    {
        var selected = selectedRows
            .DistinctBy(row => NormalizePath(row.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var newlySelected = newlySelectedRows
            .Select(row => NormalizePath(row.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in selected.Where(row => row.IsDirectory))
        foreach (var child in selected.Where(row => IsDescendant(NormalizePath(row.RelativePath), NormalizePath(folder.RelativePath))))
        {
            var folderPath = NormalizePath(folder.RelativePath);
            var childPath = NormalizePath(child.RelativePath);
            var folderWasAdded = newlySelected.Contains(folderPath);
            var childWasAdded = newlySelected.Contains(childPath);

            if (folderWasAdded && !childWasAdded) suppressed.Add(childPath);
            else if (childWasAdded) suppressed.Add(folderPath);
            else suppressed.Add(childPath);
        }

        return selected.Where(row => !suppressed.Contains(NormalizePath(row.RelativePath))).ToArray();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    private static bool IsDescendant(string path, string parent) =>
        path.Length > parent.Length && path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}
