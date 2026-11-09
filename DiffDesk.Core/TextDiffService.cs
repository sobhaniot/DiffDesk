using System.Text;

namespace DiffDesk.Core;

public sealed class TextDiffService
{
    private const long MaxLcsCells = 4_000_000;
    private const int MaxDisplayedLines = 20_000;

    public async Task<TextFileComparison> CompareFilesAsync(string leftPath, string rightPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightPath);
        return await Task.Run(() => CompareFilesCore(leftPath, rightPath, cancellationToken), cancellationToken);
    }

    public static IReadOnlyList<DiffLine> CompareText(string leftText, string rightText)
    {
        var left = NormalizeLines(leftText);
        var right = NormalizeLines(rightText);
        if (left.Length > MaxDisplayedLines || right.Length > MaxDisplayedLines)
            throw new InvalidOperationException($"برای این نسخه، نمایش هم‌زمان بیش از {MaxDisplayedLines:N0} خط در هر سمت پشتیبانی نمی‌شود.");

        if ((long)left.Length * right.Length > MaxLcsCells)
            return BuildIndexAlignedDiff(left, right);

        var lcs = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
                lcs[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        }

        var raw = new List<RawLine>(left.Length + right.Length);
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length || rightIndex < right.Length)
        {
            if (leftIndex < left.Length && rightIndex < right.Length && string.Equals(left[leftIndex], right[rightIndex], StringComparison.Ordinal))
            {
                raw.Add(new RawLine(leftIndex + 1, left[leftIndex], rightIndex + 1, right[rightIndex], DiffLineKind.Equal));
                leftIndex++;
                rightIndex++;
            }
            else if (rightIndex < right.Length && (leftIndex == left.Length || lcs[leftIndex, rightIndex + 1] >= lcs[leftIndex + 1, rightIndex]))
            {
                raw.Add(new RawLine(null, null, rightIndex + 1, right[rightIndex], DiffLineKind.RightOnly));
                rightIndex++;
            }
            else
            {
                raw.Add(new RawLine(leftIndex + 1, left[leftIndex], null, null, DiffLineKind.LeftOnly));
                leftIndex++;
            }
        }

        var aligned = new List<DiffLine>(raw.Count);
        var cursor = 0;
        while (cursor < raw.Count)
        {
            if (raw[cursor].Kind == DiffLineKind.Equal)
            {
                var line = raw[cursor++];
                aligned.Add(new DiffLine(line.LeftNumber, line.LeftText, line.RightNumber, line.RightText, DiffLineKind.Equal));
                continue;
            }

            var removed = new List<RawLine>();
            var added = new List<RawLine>();
            while (cursor < raw.Count && raw[cursor].Kind != DiffLineKind.Equal)
            {
                if (raw[cursor].Kind == DiffLineKind.LeftOnly) removed.Add(raw[cursor]);
                else added.Add(raw[cursor]);
                cursor++;
            }

            var paired = Math.Min(removed.Count, added.Count);
            for (var i = 0; i < paired; i++)
                aligned.Add(new DiffLine(removed[i].LeftNumber, removed[i].LeftText, added[i].RightNumber, added[i].RightText, DiffLineKind.Changed));
            for (var i = paired; i < removed.Count; i++)
                aligned.Add(new DiffLine(removed[i].LeftNumber, removed[i].LeftText, null, null, DiffLineKind.LeftOnly));
            for (var i = paired; i < added.Count; i++)
                aligned.Add(new DiffLine(null, null, added[i].RightNumber, added[i].RightText, DiffLineKind.RightOnly));
        }

        return aligned;
    }

    private static TextFileComparison CompareFilesCore(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(leftPath)) throw new FileNotFoundException("فایل سمت چپ پیدا نشد.", leftPath);
        if (!File.Exists(rightPath)) throw new FileNotFoundException("فایل سمت راست پیدا نشد.", rightPath);
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (IsBinary(leftPath, cancellationToken) || IsBinary(rightPath, cancellationToken))
        {
            var same = leftInfo.Length == rightInfo.Length && FolderComparisonService.FilesEqual(leftPath, rightPath, cancellationToken);
            return new TextFileComparison(true, same, leftInfo.Length, rightInfo.Length, [], same ? "محتوای باینری دو فایل یکسان است." : "فایل باینری است؛ اختلاف دقیق متن نمایش داده نمی‌شود.");
        }

        var leftText = ReadText(leftPath, cancellationToken);
        var rightText = ReadText(rightPath, cancellationToken);
        var lines = CompareText(leftText, rightText);
        return new TextFileComparison(false, string.Equals(leftText, rightText, StringComparison.Ordinal), leftInfo.Length, rightInfo.Length, lines);
    }

    private static string[] NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static IReadOnlyList<DiffLine> BuildIndexAlignedDiff(string[] left, string[] right)
    {
        var result = new List<DiffLine>(Math.Max(left.Length, right.Length));
        var count = Math.Max(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            var hasLeft = i < left.Length;
            var hasRight = i < right.Length;
            if (hasLeft && hasRight)
                result.Add(new DiffLine(i + 1, left[i], i + 1, right[i], left[i] == right[i] ? DiffLineKind.Equal : DiffLineKind.Changed));
            else if (hasLeft)
                result.Add(new DiffLine(i + 1, left[i], null, null, DiffLineKind.LeftOnly));
            else
                result.Add(new DiffLine(null, null, i + 1, right[i], DiffLineKind.RightOnly));
        }
        return result;
    }

    private static bool IsBinary(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[8192];
        var read = stream.Read(bytes, 0, bytes.Length);
        cancellationToken.ThrowIfCancellationRequested();
        if (read >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return false;
        if (read >= 4 && ((bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF) || (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00))) return false;
        return bytes.AsSpan(0, read).Contains((byte)0);
    }

    private static string ReadText(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private sealed record RawLine(int? LeftNumber, string? LeftText, int? RightNumber, string? RightText, DiffLineKind Kind);
}
