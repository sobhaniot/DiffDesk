using DiffDesk.Core;

var testRoot = Path.Combine(AppContext.BaseDirectory, "DiffDesk-Smoke-" + Guid.NewGuid().ToString("N"));
var passed = 0;
var failed = 0;

try
{
    Directory.CreateDirectory(testRoot);
    var sessionStore = new SessionStore(Path.Combine(testRoot, "session-store"));
    sessionStore.SaveLastFolderPair("C:\\left", "D:\\right");
    var rememberedFolders = sessionStore.LoadLastFolderPair();
    Assert(rememberedFolders?.LeftPath == "C:\\left" && rememberedFolders.RightPath == "D:\\right", "selected folder paths persist across application launches");
    var legacySessionStore = new SessionStore(Path.Combine(testRoot, "legacy-session-store"));
    legacySessionStore.SaveRecent(new RecentSession(ComparisonMode.Folders, "E:\\old-left", "F:\\old-right", DateTime.Now));
    var migratedFolders = legacySessionStore.LoadLastFolderPair();
    Assert(migratedFolders?.LeftPath == "E:\\old-left" && migratedFolders.RightPath == "F:\\old-right", "last folder pair restores from existing recent sessions on upgrade");

    var leftRoot = Path.Combine(testRoot, "left");
    var rightRoot = Path.Combine(testRoot, "right");
    Directory.CreateDirectory(leftRoot);
    Directory.CreateDirectory(rightRoot);
    Write(leftRoot, "same.txt", "same content");
    Write(rightRoot, "same.txt", "same content");
    Write(leftRoot, "changed.txt", "Cats");
    Write(rightRoot, "changed.txt", "Dogs");
    Write(leftRoot, "only-left.txt", "left");
    Write(rightRoot, "only-right.txt", "right");
    Write(leftRoot, "nested/child.txt", "inside");
    Write(rightRoot, "nested/child.txt", "modified inside");
    Write(rightRoot, "only-right-folder/child.txt", "new folder");
    Write(leftRoot, "bin/ignored.txt", "ignore");
    Write(rightRoot, "bin/ignored.txt", "different");
    Write(leftRoot, "skip.tmp", "ignored");
    Write(rightRoot, "skip.tmp", "different");

    var folderService = new FolderComparisonService();
    var withContent = await folderService.CompareAsync(leftRoot, rightRoot, new ComparisonOptions());
    Assert(withContent.Rows.Count == 8, "folder view lists nested files and folders as a tree and excludes default patterns");
    Assert(Find(withContent, "same.txt").Kind == DifferenceKind.Identical, "identical file classification");
    Assert(Find(withContent, "changed.txt").Kind == DifferenceKind.Changed, "same-size content difference classification");
    var olderStamp = DateTime.UtcNow.AddMinutes(-2);
    var newerStamp = olderStamp.AddMinutes(1);
    var leftNewer = new FileComparisonRow("new-left.txt", "L", "R", 4, 4, newerStamp, olderStamp, DifferenceKind.Changed);
    var rightNewer = leftNewer with { LeftModified = olderStamp, RightModified = newerStamp };
    var equalStamp = leftNewer with { RightModified = newerStamp };
    var changedFolder = leftNewer with { IsDirectory = true };
    Assert(leftNewer.IsLeftNewer && leftNewer.RightVersionHint.Contains("قدیمی‌تر"), "newer left file is identified for directional highlighting");
    Assert(rightNewer.IsRightNewer && rightNewer.LeftVersionHint.Contains("قدیمی‌تر"), "newer right file is identified for directional highlighting");
    Assert(equalStamp.NewerSide is null && changedFolder.NewerSide is null, "equal timestamps and changed folders do not imply a newer file side");
    Assert(Find(withContent, "only-left.txt").Kind == DifferenceKind.LeftOnly, "left-only classification");
    Assert(Find(withContent, "only-right.txt").Kind == DifferenceKind.RightOnly, "right-only classification");
    Assert(Find(withContent, "nested").IsDirectory && Find(withContent, "nested").Kind == DifferenceKind.Changed, "changed nested folder is surfaced as a folder row");
    Assert(Find(withContent, "nested").HasChildren && Find(withContent, "nested/child.txt").Depth == 1, "nested contents are available as indented child rows");
    Assert(Find(withContent, "only-right-folder").Kind == DifferenceKind.RightOnly, "new folder is surfaced on the right");
    Assert(Find(withContent, "only-right-folder/child.txt").Kind == DifferenceKind.RightOnly, "new folder contents are individually listed beneath the folder");

    var metadataOnly = await folderService.CompareAsync(leftRoot, rightRoot, new ComparisonOptions { CompareContent = false });
    Assert(Find(metadataOnly, "changed.txt").Kind == DifferenceKind.Identical, "content comparison can be disabled");
    var topOnly = await folderService.CompareAsync(leftRoot, rightRoot, new ComparisonOptions { IncludeSubdirectories = false });
    Assert(Find(topOnly, "nested").Kind == DifferenceKind.Identical, "disabling recursion leaves folder rows present without descending");
    Assert(topOnly.Rows.Count == 6 && topOnly.Rows.All(row => row.Depth == 0), "disabling recursion keeps the tree collapsed at the root");

    var lineDiff = TextDiffService.CompareText("alpha\nsame\nleft\n", "alpha\nsame\nright\n");
    Assert(lineDiff.Count(line => line.Kind == DiffLineKind.Changed) == 1, "line diff pairs a modified line");
    Assert(lineDiff.Any(line => line.LeftText == "left" && line.RightText == "right" && line.IsChanged), "changed line text alignment");
    var insertDiff = TextDiffService.CompareText("one\nthree", "one\ntwo\nthree");
    Assert(insertDiff.Any(line => line.Kind == DiffLineKind.RightOnly && line.RightText == "two"), "line diff detects inserted lines");

    var binaryLeft = Path.Combine(testRoot, "binary-left.bin");
    var binaryRight = Path.Combine(testRoot, "binary-right.bin");
    await File.WriteAllBytesAsync(binaryLeft, [0, 1, 2, 3]);
    await File.WriteAllBytesAsync(binaryRight, [0, 1, 2, 4]);
    var binaryDiff = await new TextDiffService().CompareFilesAsync(binaryLeft, binaryRight);
    Assert(binaryDiff.IsBinary && !binaryDiff.IsIdentical, "binary detection and comparison");

    var historyDirectory = Path.Combine(testRoot, "change-history");
    var historyLeft = Path.Combine(testRoot, "history-left");
    var historyRight = Path.Combine(testRoot, "history-right");
    Write(historyLeft, "nested/data.bin", "base");
    Write(historyRight, "nested/data.bin", "base");
    var leftHistoryFile = Path.Combine(historyLeft, "nested", "data.bin");
    var rightHistoryFile = Path.Combine(historyRight, "nested", "data.bin");
    var historyOptions = new ComparisonOptions();
    var original = await folderService.CompareAsync(historyLeft, historyRight, historyOptions);
    await new FileChangeHistory(historyDirectory).TrackAsync(original);
    var history = new FileChangeHistory(historyDirectory);
    Write(historyLeft, "nested/data.bin", "left");
    var oneChanged = await history.TrackAsync(await folderService.CompareAsync(historyLeft, historyRight, historyOptions));
    Assert(!Find(oneChanged, "nested/data.bin").BothVersionsChanged, "a change on only one side does not warn");
    Write(historyRight, "nested/data.bin", "rght");
    File.SetLastWriteTimeUtc(leftHistoryFile, olderStamp);
    File.SetLastWriteTimeUtc(rightHistoryFile, olderStamp);
    var bothChanged = await history.TrackAsync(await folderService.CompareAsync(historyLeft, historyRight, historyOptions with { CompareContent = false }));
    var conflictingRow = Find(bothChanged, "nested/data.bin");
    Assert(conflictingRow.BothVersionsChanged && conflictingRow.Kind == DifferenceKind.Changed,
        "both edits are detected after reopening history even with identical size and timestamp");
    var recursiveCopy = new FileOperationService().CreateCopyPlan([Find(bothChanged, "nested")], FileSide.Right, historyLeft);
    Assert((await history.FindOverwriteConflictsAsync(recursiveCopy)).Single().Replace('\\', '/') == "nested/data.bin",
        "reverse-direction folder copy warns for its conflicting child");
    var newHistory = new FileChangeHistory(Path.Combine(testRoot, "new-history"));
    var noBaseline = await newHistory.TrackAsync(await folderService.CompareAsync(historyLeft, historyRight, historyOptions));
    Assert(!Find(noBaseline, "nested/data.bin").BothVersionsChanged, "different first-seen versions do not imply two edits");
    Write(historyRight, "nested/data.bin", "left");
    var converged = await history.TrackAsync(await folderService.CompareAsync(historyLeft, historyRight, historyOptions));
    Assert(!Find(converged, "nested/data.bin").BothVersionsChanged && (await history.FindOverwriteConflictsAsync(recursiveCopy)).Count == 0,
        "identical current contents clear the conflict and update the baseline");
    Write(historyLeft, "nested/data.bin", "next");
    Assert((await history.FindOverwriteConflictsAsync(recursiveCopy)).Count == 0, "only one edit since the updated baseline does not warn");

    Write(historyLeft, "nested/data.bin", "initial left");
    Write(historyRight, "nested/data.bin", "a different initial right");
    var differentHistory = new FileChangeHistory(Path.Combine(testRoot, "initially-different-history"));
    var firstDifferent = await differentHistory.TrackAsync(await folderService.CompareAsync(historyLeft, historyRight, historyOptions));
    var firstDifferentRow = Find(firstDifferent, "nested/data.bin");
    Assert(!firstDifferentRow.BothVersionsChanged && !firstDifferentRow.HighlightLeftVersion && !firstDifferentRow.HighlightRightVersion,
        "first comparison of unequal versions records a baseline without preferring the later timestamp");
    Write(historyLeft, "nested/data.bin", "edited left");
    File.SetLastWriteTimeUtc(leftHistoryFile, olderStamp);
    File.SetLastWriteTimeUtc(rightHistoryFile, newerStamp);
    var oneEditFromDifferent = await differentHistory.TrackAsync(await folderService.CompareAsync(historyLeft, historyRight, historyOptions));
    Assert(Find(oneEditFromDifferent, "nested/data.bin").HighlightLeftVersion && !Find(oneEditFromDifferent, "nested/data.bin").HighlightRightVersion,
        "content edit is highlighted even when the other version has a later timestamp");
    Write(historyRight, "nested/data.bin", "edited right");
    Assert((await differentHistory.FindOverwriteConflictsAsync(recursiveCopy)).Count == 1,
        "copy checks for two edits made since an initially different baseline even before refreshing the comparison");
    var reversedConflict = await differentHistory.TrackAsync(await folderService.CompareAsync(historyRight, historyLeft, historyOptions));
    var reversedConflictRow = Find(reversedConflict, "nested/data.bin");
    Assert(reversedConflictRow.BothVersionsChanged && !reversedConflictRow.HighlightLeftVersion && !reversedConflictRow.HighlightRightVersion,
        "swapping panes still detects two edits without preferring either version");

    var operationRoot = Path.Combine(testRoot, "operations");
    Directory.CreateDirectory(operationRoot);
    var destinationRoot = Path.Combine(operationRoot, "destination");
    Directory.CreateDirectory(destinationRoot);
    var sourceFile = Path.Combine(operationRoot, "source.txt");
    await File.WriteAllTextAsync(sourceFile, "new value");
    var operationRow = new FileComparisonRow("source.txt", sourceFile, null, 9, null, null, null, DifferenceKind.LeftOnly);
    var operations = new FileOperationService();
    var copyPlan = operations.CreateCopyPlan([operationRow], FileSide.Left, destinationRoot);
    var copyResult = await operations.ExecuteAsync(copyPlan, Path.Combine(operationRoot, "backups"));
    Assert(copyResult.Succeeded == 1 && await File.ReadAllTextAsync(Path.Combine(destinationRoot, "source.txt")) == "new value", "copy operation creates destination file");

    var protectedDestination = Path.Combine(destinationRoot, "source.txt");
    await File.WriteAllTextAsync(protectedDestination, "old read-only value");
    File.SetAttributes(protectedDestination, File.GetAttributes(protectedDestination) | FileAttributes.ReadOnly);
    var overwritePlan = operations.CreateCopyPlan([operationRow], FileSide.Left, destinationRoot);
    var overwriteResult = await operations.ExecuteAsync(overwritePlan, Path.Combine(operationRoot, "backups"));
    Assert(overwriteResult.Succeeded == 1 && await File.ReadAllTextAsync(protectedDestination) == "new value", "copy overwrites a read-only destination after backing it up");
    Assert(overwriteResult.BackupDirectory is not null && await File.ReadAllTextAsync(Path.Combine(overwriteResult.BackupDirectory, "source.txt")) == "old read-only value", "copy preserves the previous read-only destination in backup");
    if (overwriteResult.BackupDirectory is not null) File.SetAttributes(Path.Combine(overwriteResult.BackupDirectory, "source.txt"), FileAttributes.Normal);

    var sourceFolder = Path.Combine(operationRoot, "folder-source");
    await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(sourceFolder, "nested")).FullName, "child.txt"), "folder data");
    var folderRow = new FileComparisonRow("folder-source", sourceFolder, null, null, null, null, null, DifferenceKind.LeftOnly, IsDirectory: true);
    var folderCopyPlan = operations.CreateCopyPlan([folderRow], FileSide.Left, destinationRoot);
    var folderCopyResult = await operations.ExecuteAsync(folderCopyPlan, Path.Combine(operationRoot, "backups"));
    Assert(folderCopyResult.Succeeded == 1 && await File.ReadAllTextAsync(Path.Combine(destinationRoot, "folder-source", "nested", "child.txt")) == "folder data", "copy operation includes selected folder contents");

    var folderChildRow = new FileComparisonRow("folder-source/nested/child.txt", Path.Combine(sourceFolder, "nested", "child.txt"), null, 11, null, null, null, DifferenceKind.LeftOnly, Depth: 2);
    var fileSelection = FileSelectionPolicy.Normalize([folderRow, folderChildRow], [folderChildRow]);
    Assert(fileSelection.Count == 1 && fileSelection[0].RelativePath == folderChildRow.RelativePath, "selecting a nested file clears its selected parent folder");
    var folderSelection = FileSelectionPolicy.Normalize([folderRow, folderChildRow], [folderRow]);
    Assert(folderSelection.Count == 1 && folderSelection[0].RelativePath == folderRow.RelativePath, "selecting a folder clears its selected descendants");
    var overlappingCopyPlan = operations.CreateCopyPlan([folderRow, folderChildRow], FileSide.Left, destinationRoot);
    Assert(overlappingCopyPlan.Items.Count == 1 && overlappingCopyPlan.Items[0].IsDirectory, "copying a selected folder subsumes its selected descendants");
    var folderDeletePlan = operations.CreateDeletePlan([folderRow, folderChildRow], FileSide.Left);
    Assert(folderDeletePlan.Items.Count == 1 && folderDeletePlan.Items[0].IsDirectory, "deleting a selected folder subsumes its selected descendants");
    var folderDeleteResult = await operations.ExecuteAsync(folderDeletePlan, Path.Combine(operationRoot, "backups"));
    Assert(folderDeleteResult.Succeeded == 1 && !Directory.Exists(sourceFolder), "delete operation removes the selected folder and its contents");
    Assert(folderDeleteResult.BackupDirectory is not null && await File.ReadAllTextAsync(Path.Combine(folderDeleteResult.BackupDirectory, "folder-source", "nested", "child.txt")) == "folder data", "delete operation backs up folder contents before removal");

    var destinationFile = Path.Combine(destinationRoot, "source.txt");
    var deleteRow = operationRow with { LeftPath = destinationFile, RelativePath = "source.txt" };
    var deletePlan = operations.CreateDeletePlan([deleteRow], FileSide.Left);
    var deleteResult = await operations.ExecuteAsync(deletePlan, Path.Combine(operationRoot, "backups"));
    Assert(deleteResult.Succeeded == 1 && !File.Exists(destinationFile), "delete operation removes the selected file");
    Assert(deleteResult.BackupDirectory is not null && Directory.Exists(deleteResult.BackupDirectory), "delete operation keeps a recoverable backup");

    var renameSource = Path.Combine(operationRoot, "rename-before.txt");
    await File.WriteAllTextAsync(renameSource, "rename me");
    var renameRow = new FileComparisonRow("rename-before.txt", renameSource, null, 9, null, null, null, DifferenceKind.LeftOnly);
    var renamePlan = operations.CreateRenamePlan(renameRow, FileSide.Left, "rename-after.txt");
    var renameResult = await operations.ExecuteAsync(renamePlan, Path.Combine(operationRoot, "backups"));
    Assert(renameResult.Succeeded == 1 && File.Exists(Path.Combine(operationRoot, "rename-after.txt")), "rename operation changes the file name");

    Console.WriteLine($"Smoke checks passed: {passed}; failed: {failed}");
}
finally
{
    try { Directory.Delete(testRoot, recursive: true); }
    catch (IOException) { }
}

return failed == 0 ? 0 : 1;

void Assert(bool condition, string description)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"PASS  {description}");
    }
    else
    {
        failed++;
        Console.WriteLine($"FAIL  {description}");
    }
}

FileComparisonRow Find(ComparisonSnapshot snapshot, string path) => snapshot.Rows.Single(row => row.RelativePath == path);

void Write(string root, string relativePath, string value)
{
    var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, value);
}
