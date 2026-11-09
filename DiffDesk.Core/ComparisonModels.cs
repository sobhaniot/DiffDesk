namespace DiffDesk.Core;

public enum ComparisonMode
{
    Folders,
    Files
}

public enum DifferenceKind
{
    Identical,
    Changed,
    LeftOnly,
    RightOnly,
    Error
}

public sealed record ComparisonOptions
{
    public bool CompareContent { get; init; } = true;
    public bool CompareModifiedTime { get; init; }
    public bool IncludeSubdirectories { get; init; } = true;
    public bool IgnoreNameCase { get; init; } = true;
    public string ExcludePatterns { get; init; } = "bin;obj;.git;*.tmp";
}

public sealed record FileComparisonRow(
    string RelativePath,
    string? LeftPath,
    string? RightPath,
    long? LeftSize,
    long? RightSize,
    DateTime? LeftModified,
    DateTime? RightModified,
    DifferenceKind Kind,
    string? Error = null,
    bool IsDirectory = false,
    int Depth = 0,
    bool HasChildren = false)
{
    public bool IsExpanded { get; set; }
    public bool BothVersionsChanged { get; init; }
    public bool ContentHistoryChecked { get; init; }
    public FileSide? ChangedContentSide { get; init; }
    public const string BothVersionsChangedMessage = "هر دو نسخه از زمان ثبت مبنا تغییر کرده‌اند";
    public string DisplayName => $"{(BothVersionsChanged ? "⚠" : IsDirectory ? "▰" : "▪")}  {System.IO.Path.GetFileName(RelativePath)}";
    public string LeftDisplayName => LeftPath is null ? string.Empty : DisplayName;
    public string RightDisplayName => RightPath is null ? string.Empty : DisplayName;
    public FileSide? NewerSide
    {
        get
        {
            if (Kind != DifferenceKind.Changed || IsDirectory || BothVersionsChanged || LeftModified is null || RightModified is null || LeftModified == RightModified) return null;
            return LeftModified.Value > RightModified.Value ? FileSide.Left : FileSide.Right;
        }
    }
    public bool IsLeftNewer => NewerSide == FileSide.Left;
    public bool IsRightNewer => NewerSide == FileSide.Right;
    public bool HighlightLeftVersion => (ContentHistoryChecked ? ChangedContentSide : NewerSide) == FileSide.Left;
    public bool HighlightRightVersion => (ContentHistoryChecked ? ChangedContentSide : NewerSide) == FileSide.Right;
    public string LeftVersionHint => VersionHint(FileSide.Left);
    public string RightVersionHint => VersionHint(FileSide.Right);

    private string VersionHint(FileSide side)
    {
        if (BothVersionsChanged) return BothVersionsChangedMessage;
        if (ContentHistoryChecked && Kind == DifferenceKind.Changed)
        {
            if (ChangedContentSide is null) return $"نسخهٔ تغییرکرده مشخص نیست · {RelativePath}";
            return ChangedContentSide == side ? $"محتوا از زمان ثبت مبنا تغییر کرده است · {RelativePath}" : RelativePath;
        }
        return NewerSide is null ? RelativePath : NewerSide == side ? $"نسخهٔ جدیدتر · {RelativePath}" : $"نسخهٔ قدیمی‌تر · {RelativePath}";
    }
    public string StatusText => Kind switch
    {
        DifferenceKind.Identical => "یکسان",
        DifferenceKind.Changed => "متفاوت",
        DifferenceKind.LeftOnly => "فقط چپ",
        DifferenceKind.RightOnly => "فقط راست",
        DifferenceKind.Error => "خطا",
        _ => "نامشخص"
    };
}

public sealed record ComparisonProgress(int Completed, int Total, string CurrentPath, string Stage)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

public sealed record ComparisonSnapshot(
    IReadOnlyList<FileComparisonRow> Rows,
    IReadOnlyList<string> Issues,
    TimeSpan Duration)
{
    public int DifferentCount => Rows.Count(row => row.Kind is DifferenceKind.Changed or DifferenceKind.LeftOnly or DifferenceKind.RightOnly or DifferenceKind.Error);
    public int IdenticalCount => Rows.Count(row => row.Kind == DifferenceKind.Identical);
}

public enum DiffLineKind
{
    Equal,
    Changed,
    LeftOnly,
    RightOnly
}

public sealed record DiffLine(int? LeftNumber, string? LeftText, int? RightNumber, string? RightText, DiffLineKind Kind)
{
    public bool IsChanged => Kind == DiffLineKind.Changed;
}

public sealed record TextFileComparison(
    bool IsBinary,
    bool IsIdentical,
    long LeftLength,
    long RightLength,
    IReadOnlyList<DiffLine> Lines,
    string? Message = null);

public sealed record RecentSession(
    ComparisonMode Mode,
    string LeftPath,
    string RightPath,
    DateTime SavedAt);

public sealed record LastFolderPair(string LeftPath, string RightPath);

public enum FileOperationKind
{
    Copy,
    Move,
    Delete,
    Rename
}

public sealed record FileOperationItem(string SourcePath, string? DestinationPath, bool DestinationExists, string RelativePath, bool IsDirectory = false);

public sealed record FileOperationPlan(FileOperationKind Kind, IReadOnlyList<FileOperationItem> Items, string Description)
{
    public int OverwriteCount => Items.Count(item => item.DestinationExists);
}

public sealed record FileOperationReport(int Succeeded, int Failed, IReadOnlyList<string> Errors, string? BackupDirectory);
