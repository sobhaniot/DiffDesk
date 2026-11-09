using System.Security;

namespace DiffDesk.Core;

public enum FileSide
{
    Left,
    Right
}

public sealed class FileOperationService
{
    public FileOperationPlan CreateCopyPlan(IEnumerable<FileComparisonRow> rows, FileSide sourceSide, string destinationRoot)
    {
        var root = Path.GetFullPath(destinationRoot);
        var items = CollapseDirectorySelections(rows).Select(row =>
        {
            var source = sourceSide == FileSide.Left ? row.LeftPath : row.RightPath;
            if (source is null) return null;
            var destination = Path.GetFullPath(Path.Combine(root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("مسیر مقصد از پوشهٔ انتخاب‌شده خارج می‌شود.");
            return new FileOperationItem(source, destination, row.IsDirectory ? Directory.Exists(destination) : File.Exists(destination), row.RelativePath, row.IsDirectory);
        }).Where(item => item is not null).Cast<FileOperationItem>().ToArray();
        var target = sourceSide == FileSide.Left ? "راست" : "چپ";
        return new FileOperationPlan(FileOperationKind.Copy, items, $"کپی {items.Length:N0} مورد به سمت {target}");
    }

    public FileOperationPlan CreateDeletePlan(IEnumerable<FileComparisonRow> rows, FileSide side)
    {
        var items = CollapseDirectorySelections(rows).Select(row =>
        {
            var source = side == FileSide.Left ? row.LeftPath : row.RightPath;
            return source is null ? null : new FileOperationItem(source, null, false, row.RelativePath, row.IsDirectory);
        }).Where(item => item is not null).Cast<FileOperationItem>().ToArray();
        return new FileOperationPlan(FileOperationKind.Delete, items, $"حذف {items.Length:N0} مورد از سمت {(side == FileSide.Left ? "چپ" : "راست")}");
    }

    private static FileComparisonRow[] CollapseDirectorySelections(IEnumerable<FileComparisonRow> rows)
    {
        var selected = rows.DistinctBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedDirectories = selected.Where(row => row.IsDirectory).Select(row => row.RelativePath).ToArray();
        return selected.Where(row => !selectedDirectories.Any(directory =>
            !string.Equals(directory, row.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            row.RelativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public FileOperationPlan CreateMovePlan(IEnumerable<FileComparisonRow> rows, FileSide sourceSide, string destinationRoot)
    {
        var copyPlan = CreateCopyPlan(rows, sourceSide, destinationRoot);
        return copyPlan with { Kind = FileOperationKind.Move, Description = copyPlan.Description.Replace("کپی", "انتقال", StringComparison.Ordinal) };
    }

    public FileOperationPlan CreateRenamePlan(FileComparisonRow row, FileSide side, string newName)
    {
        var source = side == FileSide.Left ? row.LeftPath : row.RightPath;
        if (source is null) throw new InvalidOperationException("فایلی در این سمت وجود ندارد.");
        var cleanName = Path.GetFileName(newName.Trim());
        if (string.IsNullOrWhiteSpace(cleanName) || cleanName is "." or "..") throw new ArgumentException("نام فایل معتبر نیست.", nameof(newName));
        if (!string.Equals(cleanName, newName.Trim(), StringComparison.Ordinal)) throw new ArgumentException("نام فایل نباید مسیر پوشه داشته باشد.", nameof(newName));
        var destination = Path.Combine(Path.GetDirectoryName(source)!, cleanName);
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("نام جدید با نام فعلی تفاوتی ندارد.");
        return new FileOperationPlan(FileOperationKind.Rename,
            [new FileOperationItem(source, destination, File.Exists(destination), row.RelativePath)],
            $"تغییر نام {Path.GetFileName(source)} به {cleanName}");
    }

    public Task<FileOperationReport> ExecuteAsync(FileOperationPlan plan, string backupRoot, IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => ExecuteCore(plan, backupRoot, progress, cancellationToken), cancellationToken);
    }

    private static FileOperationReport ExecuteCore(FileOperationPlan plan, string backupRoot, IProgress<ComparisonProgress>? progress, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var completed = 0;
        string? backupDirectory = null;

        for (var index = 0; index < plan.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = plan.Items[index];
            try
            {
                switch (plan.Kind)
                {
                    case FileOperationKind.Copy:
                        if (item.DestinationPath is null) throw new InvalidOperationException("مقصد کپی مشخص نشده است.");
                        EnsureBackup(item.DestinationPath, item.RelativePath, backupRoot, ref backupDirectory);
                        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
                        if (item.IsDirectory) CopyDirectory(item.SourcePath, item.DestinationPath, backupRoot, item.RelativePath, ref backupDirectory, cancellationToken);
                        else CopyFileOverwriting(item.SourcePath, item.DestinationPath);
                        break;
                    case FileOperationKind.Move:
                        if (item.DestinationPath is null) throw new InvalidOperationException("مقصد انتقال مشخص نشده است.");
                        EnsureBackup(item.DestinationPath, item.RelativePath, backupRoot, ref backupDirectory);
                        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
                        if (item.IsDirectory)
                        {
                            CopyDirectory(item.SourcePath, item.DestinationPath, backupRoot, item.RelativePath, ref backupDirectory, cancellationToken);
                            Directory.Delete(item.SourcePath, recursive: true);
                        }
                        else
                        {
                            CopyFileOverwriting(item.SourcePath, item.DestinationPath);
                            EnsureBackup(item.SourcePath, $"move-source/{item.RelativePath}", backupRoot, ref backupDirectory);
                            ClearReadOnly(item.SourcePath);
                            File.Delete(item.SourcePath);
                        }
                        break;
                    case FileOperationKind.Delete:
                        EnsureBackup(item.SourcePath, item.RelativePath, backupRoot, ref backupDirectory);
                        if (item.IsDirectory)
                        {
                            foreach (var file in Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories)) ClearReadOnly(file);
                            foreach (var directory in Directory.EnumerateDirectories(item.SourcePath, "*", SearchOption.AllDirectories)) ClearReadOnly(directory);
                            ClearReadOnly(item.SourcePath);
                            Directory.Delete(item.SourcePath, recursive: true);
                        }
                        else { ClearReadOnly(item.SourcePath); File.Delete(item.SourcePath); }
                        break;
                    case FileOperationKind.Rename:
                        if (item.DestinationPath is null) throw new InvalidOperationException("نام مقصد مشخص نشده است.");
                        EnsureBackup(item.DestinationPath, item.RelativePath, backupRoot, ref backupDirectory);
                        if (item.DestinationExists) { ClearReadOnly(item.DestinationPath); File.Delete(item.DestinationPath); }
                        File.Move(item.SourcePath, item.DestinationPath);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(plan.Kind));
                }
                completed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or InvalidOperationException)
            {
                errors.Add(FormatOperationError(item, exception));
            }

            progress?.Report(new ComparisonProgress(index + 1, plan.Items.Count, item.RelativePath, "اجرای عملیات"));
        }

        return new FileOperationReport(completed, errors.Count, errors, backupDirectory);
    }

    private static void CopyDirectory(string source, string destination, string backupRoot, string relativePath,
        ref string? backupDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            EnsureBackup(target, Path.Combine(relativePath, relative), backupRoot, ref backupDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFileOverwriting(file, target);
        }
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
    }

    private static void EnsureBackup(string path, string relativePath, string backupRoot, ref string? backupDirectory)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        backupDirectory ??= Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        var safeRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var backupPath = Path.Combine(backupDirectory, safeRelative);
        if (Directory.Exists(path))
        {
            BackupDirectory(path, backupPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(path, backupPath, overwrite: true);
        }
    }

    private static void BackupDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var backupFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.Copy(file, backupFile, overwrite: true);
        }
    }

    private static void CopyFileOverwriting(string source, string destination)
    {
        var sourceIsReadOnly = (File.GetAttributes(source) & FileAttributes.ReadOnly) != 0;
        if (File.Exists(destination)) ClearReadOnly(destination);
        File.Copy(source, destination, overwrite: true);
        var destinationAttributes = File.GetAttributes(destination);
        if (sourceIsReadOnly) File.SetAttributes(destination, destinationAttributes | FileAttributes.ReadOnly);
        else if ((destinationAttributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(destination, destinationAttributes & ~FileAttributes.ReadOnly);
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static string FormatOperationError(FileOperationItem item, Exception exception)
    {
        if (exception is UnauthorizedAccessException or SecurityException)
        {
            var path = item.DestinationPath ?? item.SourcePath;
            var advice = item.DestinationPath is not null && File.Exists(item.DestinationPath)
                ? "ویندوز اجازهٔ جایگزین‌کردن فایل مقصد را نداد. اگر فایل در برنامه‌ای باز است آن را ببندید؛ اگر خطا ادامه داشت دسترسی تغییر فایل در پوشهٔ مقصد را بررسی کنید."
                : "ویندوز اجازهٔ نوشتن در این مسیر را نداد. دسترسی تغییر فایل در پوشه را بررسی کنید.";
            return $"{item.RelativePath}: {advice}\nمسیر: {path}\nجزئیات ویندوز: {exception.Message}";
        }
        return $"{item.RelativePath}: {exception.Message}";
    }
}
