using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DiffDesk.Core;
using Microsoft.Win32;

namespace DiffDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly FolderComparisonService _folderComparison = new();
    private readonly TextDiffService _textDiff = new();
    private readonly FileOperationService _fileOperations = new();
    private readonly SessionStore _sessionStore = new(App.GetDataDirectory());
    private readonly FileChangeHistory _fileChangeHistory = new(App.GetDataDirectory());
    private readonly ObservableCollection<DiffLine> _diffLines = [];
    private List<FileComparisonRow> _allRows = [];
    private Dictionary<string, FileComparisonRow> _rowsByPath = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _diffCancellation;
    private ComparisonMode _mode = ComparisonMode.Folders;
    private string? _lastDiffLeft;
    private string? _lastDiffRight;
    private bool _isBusy;
    private bool _restoringLastFolders;
    private bool _syncingDiffPanes;
    private bool _syncingFolderPanes;
    private (FileComparisonRow Row, FileSide Side, int Time, Point Position)? _lastFolderClick;
    private (DataGrid Grid, FileComparisonRow Row)? _folderSelectionAnchor;

    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDoubleClickTime();
    private string _sortColumn = "Name";
    private bool _sortAscending = true;
    private FileSide _selectedFolderSide = FileSide.Left;
    private ScrollViewer? _leftDiffScrollViewer;
    private ScrollViewer? _rightDiffScrollViewer;
    private ScrollViewer? _leftFolderScrollViewer;
    private ScrollViewer? _rightFolderScrollViewer;

    public MainWindow()
    {
        InitializeComponent();
        DiffLeftList.ItemsSource = _diffLines;
        DiffRightList.ItemsSource = _diffLines;
        RightResultsGrid.ItemsSource = Array.Empty<FileComparisonRow>();
        SetMode(_mode);
        RestoreLastFolderPair();
        RefreshRecentSessions();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _leftDiffScrollViewer = FindVisualChild<ScrollViewer>(DiffLeftList);
        _rightDiffScrollViewer = FindVisualChild<ScrollViewer>(DiffRightList);
        _leftFolderScrollViewer = FindVisualChild<ScrollViewer>(ResultsGrid);
        _rightFolderScrollViewer = FindVisualChild<ScrollViewer>(RightResultsGrid);
        if (_leftDiffScrollViewer is not null) _leftDiffScrollViewer.ScrollChanged += DiffScrollViewer_ScrollChanged;
        if (_rightDiffScrollViewer is not null) _rightDiffScrollViewer.ScrollChanged += DiffScrollViewer_ScrollChanged;
        if (_leftFolderScrollViewer is not null) _leftFolderScrollViewer.ScrollChanged += FolderScrollViewer_ScrollChanged;
        if (_rightFolderScrollViewer is not null) _rightFolderScrollViewer.ScrollChanged += FolderScrollViewer_ScrollChanged;
        UpdateThemeButton();
        UpdateSortHeaderCaptions();
        LeftPathBox.Focus();
    }

    public void OpenArguments(string[] args)
    {
        var paths = args.Select(argument => argument.Trim().Trim('"')).Where(argument => !string.IsNullOrWhiteSpace(argument)).Take(2).ToArray();
        if (paths.Length == 0)
        {
            ActivateWindow();
            return;
        }
        if (paths.Length == 1)
        {
            if (Directory.Exists(paths[0]))
            {
                SetMode(ComparisonMode.Folders);
                LeftPathBox.Text = paths[0];
            }
            else if (File.Exists(paths[0]))
            {
                SetMode(ComparisonMode.Files);
                LeftPathBox.Text = paths[0];
            }
        }
        else
        {
            SetMode(File.Exists(paths[0]) || File.Exists(paths[1]) ? ComparisonMode.Files : ComparisonMode.Folders);
            LeftPathBox.Text = paths[0];
            RightPathBox.Text = paths[1];
            SaveLastFolderPair();
            _ = CompareAsync();
        }

        if (_mode == ComparisonMode.Folders) SaveLastFolderPair();
        ActivateWindow();
    }

    private void RestoreLastFolderPair()
    {
        var saved = _sessionStore.LoadLastFolderPair();
        if (saved is null) return;
        _restoringLastFolders = true;
        try
        {
            LeftPathBox.Text = saved.LeftPath;
            RightPathBox.Text = saved.RightPath;
        }
        finally { _restoringLastFolders = false; }
        SaveLastFolderPair();
    }

    private void SaveLastFolderPair()
    {
        if (_restoringLastFolders || _mode != ComparisonMode.Folders) return;
        try { _sessionStore.SaveLastFolderPair(LeftPathBox.Text.Trim(), RightPathBox.Text.Trim()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "مسیرها ذخیره نشدند";
        }
    }

    private void FolderPath_LostFocus(object sender, RoutedEventArgs e) => SaveLastFolderPair();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => SaveLastFolderPair();

    private void SetMode(ComparisonMode mode)
    {
        _mode = mode;
        var isFolder = mode == ComparisonMode.Folders;
        ModeBadge.Text = isFolder ? "مقایسهٔ پوشه" : "مقایسهٔ فایل";
        IncludeSubdirectoriesCheck.IsEnabled = isFolder;
        CompareTimeCheck.IsEnabled = isFolder;
        SetModeButtonVisual(FolderModeButton, isFolder);
        SetModeButtonVisual(FileModeButton, !isFolder);
        LeftPathHint.Text = isFolder ? "پوشهٔ مبدا را انتخاب کنید" : "فایل مبدا را انتخاب کنید";
        RightPathHint.Text = isFolder ? "پوشهٔ مقصد را انتخاب کنید" : "فایل مقصد را انتخاب کنید";
        StatusText.Text = isFolder ? "حالت پوشه آماده است" : "حالت فایل آماده است";
    }

    private static void SetModeButtonVisual(Button button, bool selected)
    {
        button.Background = selected ? (Brush)Application.Current.Resources["AccentSoftBrush"] : Brushes.Transparent;
        button.BorderBrush = selected ? (Brush)Application.Current.Resources["AccentBrush"] : Brushes.Transparent;
        button.Foreground = selected ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["MutedTextBrush"];
    }

    private void UpdateThemeButton()
    {
        ThemeButton.Content = Application.Current is App app && app.IsLightTheme ? "تم تاریک" : "تم روشن";
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ToggleTheme();
        UpdateThemeButton();
    }

    private void FolderModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetMode(ComparisonMode.Folders);
        LeftPathBox.Focus();
    }

    private void FileModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetMode(ComparisonMode.Files);
        LeftPathBox.Focus();
    }

    private void NavigateCompareButton_Click(object sender, RoutedEventArgs e)
    {
        SetMode(ComparisonMode.Folders);
        LeftPathBox.Focus();
    }

    private void BrowseLeftButton_Click(object sender, RoutedEventArgs e) => BrowseForPath(LeftPathBox);
    private void BrowseRightButton_Click(object sender, RoutedEventArgs e) => BrowseForPath(RightPathBox);

    private void BrowseForPath(TextBox target)
    {
        if (_mode == ComparisonMode.Folders)
        {
            var picker = new OpenFolderDialog
            {
                Title = target == LeftPathBox ? "انتخاب پوشهٔ سمت چپ" : "انتخاب پوشهٔ سمت راست",
                InitialDirectory = Directory.Exists(target.Text) ? target.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Multiselect = false
            };
            if (picker.ShowDialog(this) == true)
            {
                target.Text = picker.FolderName;
                SaveLastFolderPair();
            }
        }
        else
        {
            var picker = new OpenFileDialog
            {
                Title = target == LeftPathBox ? "انتخاب فایل سمت چپ" : "انتخاب فایل سمت راست",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = File.Exists(target.Text) ? Path.GetDirectoryName(target.Text) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Filter = "همهٔ فایل‌ها|*.*"
            };
            if (picker.ShowDialog(this) == true) target.Text = picker.FileName;
        }
    }

    private void SwapButton_Click(object sender, RoutedEventArgs e)
    {
        (LeftPathBox.Text, RightPathBox.Text) = (RightPathBox.Text, LeftPathBox.Text);
        (LeftPathHint.Text, RightPathHint.Text) = (RightPathHint.Text, LeftPathHint.Text);
        SaveLastFolderPair();
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e) => await CompareAsync();

    private async Task CompareAsync()
    {
        if (_isBusy) return;
        var left = LeftPathBox.Text.Trim().Trim('"');
        var right = RightPathBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            MessageBox.Show(this, "هر دو مسیر را وارد یا انتخاب کنید.", "مسیر ناقص است", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "مسیر سمت چپ و راست یکسان است. دو مسیر متفاوت انتخاب کنید.", "مسیر تکراری", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, "در حال آماده‌سازی مقایسه…");
        var progress = new Progress<ComparisonProgress>(UpdateProgress);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ComparisonSnapshot snapshot;
            if (_mode == ComparisonMode.Folders)
            {
                var options = new ComparisonOptions
                {
                    CompareContent = false,
                    CompareModifiedTime = true,
                    VerifyTimestampDifferencesByContent = true,
                    IncludeSubdirectories = IncludeSubdirectoriesCheck.IsChecked == true,
                    ExcludePatterns = ExcludeBox.Text,
                    IgnoreNameCase = true
                };
                snapshot = await _folderComparison.CompareAsync(left, right, options, progress, _operationCancellation.Token);
            }
            else
            {
                var comparison = await _textDiff.CompareFilesAsync(left, right, _operationCancellation.Token);
                var row = new FileComparisonRow(Path.GetFileName(left), left, right, comparison.LeftLength, comparison.RightLength,
                    File.GetLastWriteTimeUtc(left), File.GetLastWriteTimeUtc(right), comparison.IsIdentical ? DifferenceKind.Identical : DifferenceKind.Changed);
                snapshot = new ComparisonSnapshot([row], [], stopwatch.Elapsed);
                _lastDiffLeft = null;
                _lastDiffRight = null;
                if (comparison.IsBinary)
                {
                    SetDiffLines([]);
                    DiffSummaryText.Text = comparison.Message ?? "فایل باینری";
                    DiffEmptyText.Text = comparison.Message ?? "مقایسهٔ باینری انجام شد.";
                    DiffEmptyText.Visibility = Visibility.Visible;
                    SelectedLeftFileText.Text = $"چپ: {Path.GetFileName(left)}";
                    SelectedRightFileText.Text = $"راست: {Path.GetFileName(right)}";
                }
                else
                {
                    SetDiffLines(comparison.Lines);
                    DiffSummaryText.Text = comparison.IsIdentical ? "محتوا یکسان است" : $"{comparison.Lines.Count(line => line.Kind != DiffLineKind.Equal):N0} ردیف متفاوت";
                    DiffEmptyText.Visibility = comparison.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    SelectedLeftFileText.Text = $"چپ: {Path.GetFileName(left)}";
                    SelectedRightFileText.Text = $"راست: {Path.GetFileName(right)}";
                }
            }

            StatusText.Text = "در حال بررسی سابقهٔ تغییرات فایل‌ها…";
            snapshot = await _fileChangeHistory.TrackAsync(snapshot, _operationCancellation.Token, progress,
                preferLatestTimestamp: _mode == ComparisonMode.Folders);
            snapshot = ResolveDirectoryNewerSides(snapshot);
            stopwatch.Stop();
            _allRows = snapshot.Rows.ToList();
            _rowsByPath = _allRows.ToDictionary(row => row.RelativePath, StringComparer.OrdinalIgnoreCase);
            ApplyFilters();
            EmptyResultsPanel.Visibility = _allRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountsText.Text = $"{snapshot.Rows.Count:N0} مورد  ·  {snapshot.DifferentCount:N0} تفاوت  ·  {snapshot.IdenticalCount:N0} یکسان";
            var conflictCount = snapshot.Rows.Count(row => row.BothVersionsChanged);
            if (conflictCount > 0) CountsText.Text += $"  ·  ⚠ {conflictCount:N0} مورد با تغییر در هر دو نسخه";
            if (snapshot.Issues.Count > 0) CountsText.Text += $"  ·  ⚠ {snapshot.Issues.Count:N0} مورد برای بررسی";
            DurationText.Text = $"{stopwatch.Elapsed.TotalSeconds:0.00} ثانیه";
            StatusText.Text = snapshot.Issues.Count == 0 ? "مقایسه کامل شد" : $"مقایسه کامل شد · {snapshot.Issues.Count:N0} مورد نیازمند بررسی";
            StatusText.ToolTip = snapshot.Issues.Count == 0
                ? null
                : string.Join(Environment.NewLine, snapshot.Issues.Take(12));
            SaveRecentSession();

            if (_mode == ComparisonMode.Files && ResultsGrid.Items.Count > 0)
            {
                ResultsGrid.SelectedIndex = 0;
                ResultsGrid.ScrollIntoView(ResultsGrid.SelectedItem);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "مقایسه متوقف شد";
        }
        catch (Exception exception)
        {
            StatusText.Text = "مقایسه انجام نشد";
            MessageBox.Show(this, exception.Message, "خطا در مقایسه", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private static ComparisonSnapshot ResolveDirectoryNewerSides(ComparisonSnapshot snapshot)
    {
        var rows = snapshot.Rows.ToArray();
        var votes = new Dictionary<string, (int Left, int Right, DateTime? LeftLatest, DateTime? RightLatest)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.Kind == DifferenceKind.Identical || row.Kind == DifferenceKind.Error) continue;
            if (row.IsDirectory && row.Kind == DifferenceKind.Changed) continue;
            var side = row.Kind switch
            {
                DifferenceKind.LeftOnly => FileSide.Left,
                DifferenceKind.RightOnly => FileSide.Right,
                _ => row.NewerSide
            };
            if (side is null) continue;

            var modified = side == FileSide.Left ? row.LeftModified : row.RightModified;
            var separator = row.RelativePath.LastIndexOf('/');
            while (separator >= 0)
            {
                var parent = row.RelativePath[..separator];
                votes.TryGetValue(parent, out var vote);
                votes[parent] = side == FileSide.Left
                    ? (vote.Left + 1, vote.Right, Later(vote.LeftLatest, modified), vote.RightLatest)
                    : (vote.Left, vote.Right + 1, vote.LeftLatest, Later(vote.RightLatest, modified));
                separator = row.RelativePath.LastIndexOf('/', separator - 1);
            }
        }

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (!row.IsDirectory || row.Kind != DifferenceKind.Changed) continue;
            votes.TryGetValue(row.RelativePath, out var vote);

            FileSide? newerSide = vote.Left > vote.Right ? FileSide.Left
                : vote.Right > vote.Left ? FileSide.Right
                : vote.LeftLatest > vote.RightLatest ? FileSide.Left
                : vote.RightLatest > vote.LeftLatest ? FileSide.Right
                : row.LeftModified > row.RightModified ? FileSide.Left
                : row.RightModified > row.LeftModified ? FileSide.Right
                : row.ChangedContentSide;

            if (newerSide is not null) rows[index] = row with { ChangedContentSide = newerSide };
        }

        return snapshot with { Rows = rows };

        static DateTime? Later(DateTime? left, DateTime? right) => left is null || right > left ? right : left;
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        CompareButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.IsIndeterminate = busy;
        if (!busy) ProgressBar.Value = 0;
        StatusText.Text = status;
        UpdateCopyDirectionButton();
    }

    private void UpdateProgress(ComparisonProgress progress)
    {
        if (!Dispatcher.CheckAccess()) return;
        if (progress.Total > 0)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = progress.Percent;
            StatusText.Text = $"{progress.Stage} · {progress.Completed:N0} از {progress.Total:N0} · {progress.CurrentPath}";
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
            StatusText.Text = $"{progress.Stage} · {progress.CurrentPath}";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        _diffCancellation?.Cancel();
        StatusText.Text = "درخواست توقف ارسال شد…";
    }

    private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not FileComparisonRow row) return;
        if (row.LeftPath is null || row.RightPath is null)
        {
            _diffCancellation?.Cancel();
            _lastDiffLeft = null;
            _lastDiffRight = null;
            SetDiffLines([]);
            DiffSummaryText.Text = "این فایل فقط در یک سمت وجود دارد";
            DiffEmptyText.Text = "برای این ردیف، فایلی در سمت دیگر وجود ندارد.";
            DiffEmptyText.Visibility = Visibility.Visible;
            SelectedLeftFileText.Text = row.LeftPath is null ? "چپ: —" : $"چپ: {Path.GetFileName(row.LeftPath)}";
            SelectedRightFileText.Text = row.RightPath is null ? "راست: —" : $"راست: {Path.GetFileName(row.RightPath)}";
            return;
        }
        if (row.LeftPath == _lastDiffLeft && row.RightPath == _lastDiffRight) return;

        _diffCancellation?.Cancel();
        _diffCancellation?.Dispose();
        _diffCancellation = new CancellationTokenSource();
        var token = _diffCancellation.Token;
        _lastDiffLeft = row.LeftPath;
        _lastDiffRight = row.RightPath;
        SelectedLeftFileText.Text = $"چپ: {row.RelativePath}";
        SelectedRightFileText.Text = $"راست: {row.RelativePath}";
        DiffSummaryText.Text = "در حال خواندن فایل…";
        DiffEmptyText.Text = "در حال آماده‌سازی نمای متن…";
        DiffEmptyText.Visibility = Visibility.Visible;
        try
        {
            var comparison = await _textDiff.CompareFilesAsync(row.LeftPath, row.RightPath, token);
            if (token.IsCancellationRequested) return;
            if (comparison.IsBinary)
            {
                SetDiffLines([]);
                DiffSummaryText.Text = comparison.Message ?? "فایل باینری";
                DiffEmptyText.Text = comparison.Message ?? "نمای متن برای فایل باینری موجود نیست.";
                DiffEmptyText.Visibility = Visibility.Visible;
            }
            else
            {
                SetDiffLines(comparison.Lines);
                DiffSummaryText.Text = comparison.IsIdentical ? "محتوا یکسان است" : $"{comparison.Lines.Count(line => line.Kind != DiffLineKind.Equal):N0} ردیف متفاوت";
                DiffEmptyText.Text = comparison.IsIdentical ? "محتوای این دو فایل یکسان است." : "";
                DiffEmptyText.Visibility = comparison.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            DiffSummaryText.Text = "نمایش فایل ممکن نشد";
            DiffEmptyText.Text = exception.Message;
            DiffEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void SetDiffLines(IEnumerable<DiffLine> lines)
    {
        _syncingDiffPanes = true;
        DiffLeftList.SelectedIndex = -1;
        DiffRightList.SelectedIndex = -1;
        _diffLines.Clear();
        foreach (var line in lines) _diffLines.Add(line);
        _syncingDiffPanes = false;
        DiffLeftList.UpdateLayout();
        DiffRightList.UpdateLayout();
        _leftDiffScrollViewer?.ScrollToTop();
        _rightDiffScrollViewer?.ScrollToTop();
    }

    private void DiffPane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDiffPanes) return;
        _syncingDiffPanes = true;
        if (ReferenceEquals(sender, DiffLeftList)) DiffRightList.SelectedIndex = DiffLeftList.SelectedIndex;
        else DiffLeftList.SelectedIndex = DiffRightList.SelectedIndex;
        _syncingDiffPanes = false;
    }

    private void DiffScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingDiffPanes || e.VerticalChange == 0) return;
        var source = sender as ScrollViewer;
        var target = ReferenceEquals(source, _leftDiffScrollViewer) ? _rightDiffScrollViewer : _leftDiffScrollViewer;
        if (source is null || target is null) return;

        _syncingDiffPanes = true;
        target.ScrollToVerticalOffset(source.VerticalOffset);
        _syncingDiffPanes = false;
    }

    private void FolderScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingFolderPanes || e.VerticalChange == 0) return;
        var source = sender as ScrollViewer;
        var target = ReferenceEquals(source, _leftFolderScrollViewer) ? _rightFolderScrollViewer : _leftFolderScrollViewer;
        if (source is null || target is null) return;

        _syncingFolderPanes = true;
        target.ScrollToVerticalOffset(source.VerticalOffset);
        _syncingFolderPanes = false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var sourceGrid = sender as DataGrid;
        if (sourceGrid is null) return;
        if (sourceGrid.SelectedItem is not FileComparisonRow row) return;
        if (row.IsDirectory) return;
        if (row.LeftPath is not null && row.RightPath is not null) _ = OpenFileComparisonAsync(row);
    }

    private void LeftFolderExpander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileComparisonRow row) ToggleFolderExpansion(row, FileSide.Left);
    }

    private void RightFolderExpander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileComparisonRow row) ToggleFolderExpansion(row, FileSide.Right);
    }

    private void ToggleFolderExpansion(FileComparisonRow row, FileSide side)
    {
        if (!row.IsDirectory || !row.HasChildren) return;
        row.IsExpanded = !row.IsExpanded;
        _syncingFolderPanes = true;
        ResultsGrid.UnselectAll();
        RightResultsGrid.UnselectAll();
        _selectedFolderSide = side;
        ApplyFilters();
        var grid = side == FileSide.Left ? ResultsGrid : RightResultsGrid;
        grid.ScrollIntoView(row);
    }

    private async Task OpenFileComparisonAsync(FileComparisonRow row)
    {
        if (row.LeftPath is null || row.RightPath is null) return;
        await ShowFileDiffAsync(row);
    }

    private async Task ShowFileDiffAsync(FileComparisonRow row)
    {
        _lastDiffLeft = row.LeftPath;
        _lastDiffRight = row.RightPath;
        var comparison = await _textDiff.CompareFilesAsync(row.LeftPath!, row.RightPath!);
        SetDiffLines(comparison.Lines);
        DiffSummaryText.Text = comparison.IsBinary ? comparison.Message ?? "فایل باینری" : comparison.IsIdentical ? "محتوا یکسان است" : $"{comparison.Lines.Count(line => line.Kind != DiffLineKind.Equal):N0} ردیف متفاوت";
        DiffEmptyText.Text = comparison.Message ?? "";
        DiffEmptyText.Visibility = comparison.IsBinary || comparison.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedLeftFileText.Text = $"چپ: {row.RelativePath}";
        SelectedRightFileText.Text = $"راست: {row.RelativePath}";
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();
    private void StatusFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void DifferenceVisibilityBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void ExcludeBox_TextChanged(object sender, TextChangedEventArgs e) { }

    private void ApplyFilters()
    {
        if (ResultsGrid is null) return;
        var search = FilterBox.Text.Trim();
        var tag = (StatusFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var differenceTag = (DifferenceVisibilityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var matches = _allRows.Where(row =>
        {
            if (search.Length > 0 && row.RelativePath.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
            if (differenceTag == "different" && row.Kind is not (DifferenceKind.Changed or DifferenceKind.LeftOnly or DifferenceKind.RightOnly)) return false;
            return tag switch
            {
                "different" => row.Kind is DifferenceKind.Changed or DifferenceKind.LeftOnly or DifferenceKind.RightOnly or DifferenceKind.Error,
                "same" => row.Kind == DifferenceKind.Identical,
                "left" => row.Kind == DifferenceKind.LeftOnly,
                "right" => row.Kind == DifferenceKind.RightOnly,
                "error" => row.Kind == DifferenceKind.Error,
                _ => true
            };
        }).Select(row => row.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (search.Length > 0 || tag != "all")
        {
            foreach (var path in matches.ToArray())
            {
                var separator = path.LastIndexOf('/');
                while (separator >= 0)
                {
                    var parentPath = path[..separator];
                    if (_rowsByPath.TryGetValue(parentPath, out var parent) && parent.IsDirectory)
                    {
                        parent.IsExpanded = true;
                        matches.Add(parentPath);
                    }
                    separator = parentPath.LastIndexOf('/');
                }
            }
        }
        var filtered = SortRows(_allRows.Where(row => matches.Contains(row.RelativePath) && IsVisibleInExpandedTree(row))).ToList();
        _folderSelectionAnchor = null;
        _syncingFolderPanes = true;
        ResultsGrid.ItemsSource = filtered;
        RightResultsGrid.ItemsSource = filtered;
        _syncingFolderPanes = false;
        UpdateCopyDirectionButton();
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string column }) return;
        if (string.Equals(_sortColumn, column, StringComparison.Ordinal)) _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }
        UpdateSortHeaderCaptions();
        ApplyFilters();
    }

    private void UpdateSortHeaderCaptions()
    {
        if (LeftNameSortHeader is null) return;
        var marker = _sortAscending ? "  ▲" : "  ▼";
        LeftNameSortHeader.Content = RightNameSortHeader.Content = "Name" + (_sortColumn == "Name" ? marker : string.Empty);
        LeftSizeSortHeader.Content = RightSizeSortHeader.Content = "Size" + (_sortColumn == "Size" ? marker : string.Empty);
        LeftModifiedSortHeader.Content = RightModifiedSortHeader.Content = "Modified" + (_sortColumn == "Modified" ? marker : string.Empty);
    }

    private IEnumerable<FileComparisonRow> SortRows(IEnumerable<FileComparisonRow> rows)
    {
        var childrenByParent = rows
            .GroupBy(row => ParentPath(row.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<FileComparisonRow>();
        AppendChildren(string.Empty);
        return ordered;

        void AppendChildren(string parentPath)
        {
            if (!childrenByParent.TryGetValue(parentPath, out var siblings)) return;
            siblings.Sort(CompareSiblings);
            foreach (var row in siblings)
            {
                ordered.Add(row);
                if (row.IsDirectory) AppendChildren(row.RelativePath);
            }
        }

        int CompareSiblings(FileComparisonRow left, FileComparisonRow right)
        {
            if (left.IsDirectory != right.IsDirectory) return left.IsDirectory ? -1 : 1;
            var comparison = _sortColumn switch
            {
                "Size" => Math.Max(left.LeftSize ?? 0, left.RightSize ?? 0).CompareTo(Math.Max(right.LeftSize ?? 0, right.RightSize ?? 0)),
                "Modified" => LatestModified(left).CompareTo(LatestModified(right)),
                _ => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(left.RelativePath), Path.GetFileName(right.RelativePath))
            };
            if (!_sortAscending) comparison = -comparison;
            return comparison != 0
                ? comparison
                : StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(left.RelativePath), Path.GetFileName(right.RelativePath));
        }

        static string ParentPath(string path)
        {
            var separator = path.LastIndexOf('/');
            return separator < 0 ? string.Empty : path[..separator];
        }

        static DateTime LatestModified(FileComparisonRow row)
        {
            var left = row.LeftModified ?? DateTime.MinValue;
            var right = row.RightModified ?? DateTime.MinValue;
            return left > right ? left : right;
        }
    }

    private bool IsVisibleInExpandedTree(FileComparisonRow row)
    {
        var parentPath = row.RelativePath;
        var separator = parentPath.LastIndexOf('/');
        while (separator >= 0)
        {
            parentPath = parentPath[..separator];
            if (_rowsByPath.TryGetValue(parentPath, out var parent) && parent.IsDirectory && !parent.IsExpanded) return false;
            separator = parentPath.LastIndexOf('/');
        }
        return true;
    }

    private void LeftPane_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncFolderPaneSelection(FileSide.Left, ResultsGrid, e);
    private void RightPane_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncFolderPaneSelection(FileSide.Right, RightResultsGrid, e);

    private void SyncFolderPaneSelection(FileSide selectedSide, DataGrid source, SelectionChangedEventArgs e)
    {
        if (_syncingFolderPanes) return;
        _selectedFolderSide = selectedSide;
        _syncingFolderPanes = true;
        RemoveOverlappingFolderSelections(source, e);
        var target = selectedSide == FileSide.Left ? RightResultsGrid : ResultsGrid;
        target.UnselectAll();
        _syncingFolderPanes = false;
        UpdateCopyDirectionButton();
    }

    private static void RemoveOverlappingFolderSelections(DataGrid grid, SelectionChangedEventArgs e)
    {
        var selected = grid.SelectedItems.OfType<FileComparisonRow>().ToArray();
        var normalized = FileSelectionPolicy.Normalize(selected, e.AddedItems.OfType<FileComparisonRow>());
        var keep = normalized.Select(row => row.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in selected.Where(row => !keep.Contains(row.RelativePath)).ToArray())
            grid.SelectedItems.Remove(row);
    }

    private void FolderGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.ChangedButton != MouseButton.Left) return;
        var origin = e.OriginalSource as DependencyObject;
        var row = FindVisualAncestor<DataGridRow>(origin);
        if (row is not null && !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid)) row = null;
        var side = ReferenceEquals(grid, ResultsGrid) ? FileSide.Left : FileSide.Right;
        var disclosureButton = FindVisualAncestor<Button>(origin);
        var isDisclosureClick = disclosureButton is not null &&
            AutomationProperties.GetName(disclosureButton) == "باز یا بسته کردن زیرشاخه";
        var previousFolderClick = _lastFolderClick;
        _lastFolderClick = null;
        if (row?.Item is FileComparisonRow clickedRow && !clickedRow.IsVisibleOnSide(side) && !isDisclosureClick)
        {
            e.Handled = true;
            return;
        }
        if (row?.Item is FileComparisonRow clickedFolder && clickedFolder.IsDirectory && !isDisclosureClick &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None)
        {
            var position = e.GetPosition(grid);
            if (previousFolderClick is { } first && ReferenceEquals(first.Row, clickedFolder) && first.Side == side &&
                unchecked((uint)(e.Timestamp - first.Time)) <= GetDoubleClickTime() &&
                Math.Abs(position.X - first.Position.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - first.Position.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                e.Handled = true;
                ToggleFolderExpansion(clickedFolder, side);
                return;
            }
            _lastFolderClick = (clickedFolder, side, e.Timestamp, position);
        }
        var otherGrid = ReferenceEquals(grid, ResultsGrid) ? RightResultsGrid : ResultsGrid;
        var modifiers = Keyboard.Modifiers;
        if (row?.Item is FileComparisonRow item && !isDisclosureClick)
        {
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                SelectFolderRange(grid, otherGrid, side, item, modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                return;
            }
            _folderSelectionAnchor = (grid, item);
            if (modifiers.HasFlag(ModifierKeys.Control)) return;
        }
        else
        {
            _folderSelectionAnchor = null;
        }

        if (row is null && grid.SelectedItems.Count == 0 && otherGrid.SelectedItems.Count == 0) return;
        _selectedFolderSide = side;
        _syncingFolderPanes = true;
        grid.UnselectAll();
        otherGrid.UnselectAll();
        if (row is not null && !isDisclosureClick)
        {
            grid.CurrentCell = new DataGridCellInfo(row.Item, grid.Columns[0]);
            grid.Focus();
            grid.SelectedItem = row.Item;
            e.Handled = true;
        }
        _syncingFolderPanes = false;
        if (isDisclosureClick && row?.Item is FileComparisonRow folder)
        {
            e.Handled = true;
            ToggleFolderExpansion(folder, side);
            return;
        }
        UpdateCopyDirectionButton();
    }

    private void SelectFolderRange(DataGrid grid, DataGrid otherGrid, FileSide side, FileComparisonRow endRow, bool extend)
    {
        var endIndex = grid.Items.IndexOf(endRow);
        if (endIndex < 0) return;
        var startIndex = _folderSelectionAnchor is { } anchor && ReferenceEquals(anchor.Grid, grid)
            ? grid.Items.IndexOf(anchor.Row)
            : -1;
        if (startIndex < 0)
        {
            startIndex = endIndex;
            _folderSelectionAnchor = (grid, endRow);
        }

        var range = grid.Items.Cast<FileComparisonRow>()
            .Skip(Math.Min(startIndex, endIndex))
            .Take(Math.Abs(endIndex - startIndex) + 1)
            .Where(row => row.IsVisibleOnSide(side))
            .ToArray();
        var selected = extend ? grid.SelectedItems.OfType<FileComparisonRow>().Concat(range) : range;
        var normalized = FileSelectionPolicy.Normalize(selected, range);

        _selectedFolderSide = side;
        _syncingFolderPanes = true;
        try
        {
            otherGrid.UnselectAll();
            grid.CurrentCell = new DataGridCellInfo(endRow, grid.Columns[0]);
            grid.Focus();
            grid.UnselectAll();
            foreach (var item in normalized) grid.SelectedItems.Add(item);
        }
        finally
        {
            _syncingFolderPanes = false;
        }
        UpdateCopyDirectionButton();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void UpdateCopyDirectionButton()
    {
        var sourceGrid = _selectedFolderSide == FileSide.Left ? ResultsGrid : RightResultsGrid;
        var hasSource = sourceGrid.SelectedItems.OfType<FileComparisonRow>().Any(row =>
            (_selectedFolderSide == FileSide.Left ? row.LeftPath : row.RightPath) is not null);
        var direction = _selectedFolderSide == FileSide.Left ? "→" : "←";
        var side = _selectedFolderSide == FileSide.Left ? "چپ به راست" : "راست به چپ";
        CopyDirectionButton.Content = direction;
        CopyDirectionButton.ToolTip = hasSource ? $"کپی انتخاب از {side}" : "یک مورد موجود در این سمت را انتخاب کنید";
        CopyDirectionButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, $"کپی انتخاب از {side}");
        CopyDirectionButton.IsEnabled = hasSource && !_isBusy;
    }

    private void CopyDirectionButton_Click(object sender, RoutedEventArgs e) => _ = RunCopyAsync(_selectedFolderSide);

    private async Task RunCopyAsync(FileSide sourceSide, bool move = false)
    {
        var selected = GetSelectedRows();
        if (selected.Length == 0)
        {
            GlassDialog.Show(this, "انتخابی انجام نشده", "برای کپی، ابتدا یک یا چند مورد را در پنل موردنظر انتخاب کنید.");
            return;
        }
        var targetRoot = sourceSide == FileSide.Left ? RightPathBox.Text : LeftPathBox.Text;
        if (!Directory.Exists(targetRoot))
        {
            GlassDialog.Show(this, "پوشهٔ مقصد در دسترس نیست", $"مسیر مقصد وجود ندارد یا قابل دسترسی نیست:\n{targetRoot}", GlassDialogTone.Error);
            return;
        }

        FileOperationPlan plan;
        try { plan = move ? _fileOperations.CreateMovePlan(selected, sourceSide, targetRoot) : _fileOperations.CreateCopyPlan(selected, sourceSide, targetRoot); }
        catch (Exception exception)
        {
            GlassDialog.Show(this, "آماده‌سازی کپی ناموفق بود", exception.Message, GlassDialogTone.Error);
            return;
        }
        await ExecutePlanWithPreviewAsync(plan);
    }

    private async Task RunDeleteAsync(FileSide side)
    {
        var selected = GetSelectedRows();
        if (selected.Length == 0)
        {
            GlassDialog.Show(this, "انتخابی انجام نشده", "ابتدا فایل یا پوشه‌ای را در پنلی که می‌خواهید از آن حذف شود انتخاب کنید.");
            return;
        }
        var plan = _fileOperations.CreateDeletePlan(selected, side);
        await ExecutePlanWithPreviewAsync(plan);
    }

    private void FolderGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is not DataGridRow container || container.Item is not FileComparisonRow row)
            return;

        var side = ReferenceEquals(grid, RightResultsGrid) ? FileSide.Right : FileSide.Left;
        if (!container.IsSelected)
        {
            grid.UnselectAll();
            container.IsSelected = true;
        }
        _selectedFolderSide = side;
        var otherGrid = side == FileSide.Left ? RightResultsGrid : ResultsGrid;
        _syncingFolderPanes = true;
        otherGrid.UnselectAll();
        _syncingFolderPanes = false;
        UpdateCopyDirectionButton();

        var itemExists = (side == FileSide.Left ? row.LeftPath : row.RightPath) is not null;
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft };
        var sideLabel = side == FileSide.Left ? "چپ" : "راست";
        var deleteLabel = row.IsDirectory
            ? $"حذف پوشه و محتویات از سمت {sideLabel}…"
            : $"حذف فایل از سمت {sideLabel}…";
        AddMenuItem(menu, deleteLabel, (_, _) => _ = RunDeleteAsync(side), itemExists);
        menu.PlacementTarget = container;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async Task RenameSelectedAsync(FileSide side)
    {
        if (ResultsGrid.SelectedItem is not FileComparisonRow row)
        {
            MessageBox.Show(this, "یک فایل را انتخاب کنید.", "فایلی انتخاب نشده", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = side == FileSide.Left ? row.LeftPath : row.RightPath;
        if (path is null) return;
        var dialog = new TextEntryDialog("تغییر نام فایل", "نام جدید را وارد کنید:", Path.GetFileName(path)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var plan = _fileOperations.CreateRenamePlan(row, side, dialog.Value);
            await ExecutePlanWithPreviewAsync(plan);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "نام فایل معتبر نیست", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExecutePlanWithPreviewAsync(FileOperationPlan plan)
    {
        if (plan.Items.Count == 0)
        {
            GlassDialog.Show(this, "موردی برای عملیات پیدا نشد", "مورد انتخاب‌شده در این سمت وجود ندارد.");
            return;
        }
        var preview = string.Join(Environment.NewLine, plan.Items.Take(6).Select(item => plan.Kind == FileOperationKind.Delete
            ? item.IsDirectory ? $"• حذف پوشه و همهٔ محتویات: {item.SourcePath}" : $"• حذف فایل: {item.SourcePath}"
            : $"• {item.SourcePath}  →  {item.DestinationPath}"));
        if (plan.Items.Count > 6) preview += Environment.NewLine + $"… و {plan.Items.Count - 6:N0} مورد دیگر";
        var overwriteNote = plan.OverwriteCount > 0
            ? $"{Environment.NewLine}{Environment.NewLine}{plan.OverwriteCount:N0} مورد مقصد جایگزین می‌شود."
            : string.Empty;
        var deleteNote = plan.Kind == FileOperationKind.Delete
            ? $"{Environment.NewLine}{Environment.NewLine}این حذف دائمی است."
            : string.Empty;
        var operationTitle = plan.Kind switch
        {
            FileOperationKind.Delete => "تأیید حذف",
            FileOperationKind.Move => "تأیید انتقال",
            FileOperationKind.Rename => "تأیید تغییر نام",
            _ => "تأیید کپی"
        };
        if (_isBusy) return;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        SetBusy(true, "در حال بررسی سابقهٔ فایل‌های مقصد…");
        try
        {
            var conflicts = await _fileChangeHistory.FindOverwriteConflictsAsync(plan, cancellationToken);
            var conflictNote = string.Empty;
            if (conflicts.Count > 0)
            {
                operationTitle = "هشدار تغییر در هر دو نسخه";
                var names = string.Join(Environment.NewLine, conflicts.Take(6).Select(path => $"• {path}"));
                if (conflicts.Count > 6) names += Environment.NewLine + $"… و {conflicts.Count - 6:N0} مورد دیگر";
                conflictNote = $"{FileComparisonRow.BothVersionsChangedMessage}:{Environment.NewLine}{names}{Environment.NewLine}{Environment.NewLine}";
            }
            cancellationToken.ThrowIfCancellationRequested();
            StatusText.Text = plan.Description;
            var answer = GlassDialog.Confirm(this, operationTitle, $"{conflictNote}{plan.Description}{Environment.NewLine}{Environment.NewLine}{preview}{overwriteNote}{deleteNote}{Environment.NewLine}{Environment.NewLine}ادامه می‌دهید؟");
            if (!answer) return;

            var result = await _fileOperations.ExecuteAsync(plan, new Progress<ComparisonProgress>(UpdateProgress), cancellationToken);
            var detail = result.Errors.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Take(5));
            var tone = result.Failed == 0 ? GlassDialogTone.Success : result.Succeeded > 0 ? GlassDialogTone.Warning : GlassDialogTone.Error;
            var operationName = plan.Kind switch
            {
                FileOperationKind.Delete => "حذف",
                FileOperationKind.Move => "انتقال",
                FileOperationKind.Rename => "تغییر نام",
                _ => "کپی"
            };
            var title = result.Failed == 0 ? $"{operationName} کامل شد" : result.Succeeded > 0 ? $"{operationName} با خطا تمام شد" : $"{operationName} انجام نشد";
            var explanation = result.Errors.Any(error => error.Contains("دسترسی", StringComparison.Ordinal))
                ? $"{Environment.NewLine}{Environment.NewLine}اگر مورد در برنامه‌ای باز است آن را ببندید و مجوز دسترسی به مسیر را بررسی کنید."
                : string.Empty;
            if (result.Succeeded > 0)
            {
                SetBusy(false, "عملیات پایان یافت؛ در حال به‌روزرسانی مقایسه…");
                await CompareAsync();
            }
            GlassDialog.Show(this, title, $"موفق: {result.Succeeded:N0}    ناموفق: {result.Failed:N0}{detail}{explanation}", tone);
        }
        catch (OperationCanceledException) { StatusText.Text = "عملیات متوقف شد"; }
        catch (Exception exception) { GlassDialog.Show(this, "عملیات فایل ناموفق بود", exception.Message, GlassDialogTone.Error); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private FileComparisonRow[] GetSelectedRows()
    {
        var grid = _selectedFolderSide == FileSide.Left ? ResultsGrid : RightResultsGrid;
        return grid.SelectedItems.OfType<FileComparisonRow>().ToArray();
    }

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft };
        AddMenuItem(menu, "انتقال به چپ  ·  Alt+Left", (_, _) => _ = RunCopyAsync(FileSide.Right, move: true));
        AddMenuItem(menu, "انتقال به راست  ·  Alt+Right", (_, _) => _ = RunCopyAsync(FileSide.Left, move: true));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "تغییر نام سمت چپ  ·  F2", (_, _) => _ = RenameSelectedAsync(FileSide.Left));
        AddMenuItem(menu, "تغییر نام سمت راست", (_, _) => _ = RenameSelectedAsync(FileSide.Right));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "حذف از سمت چپ…", (_, _) => _ = RunDeleteAsync(FileSide.Left));
        AddMenuItem(menu, "حذف از سمت راست…", (_, _) => _ = RunDeleteAsync(FileSide.Right));
        menu.PlacementTarget = MoreActionsButton;
        menu.IsOpen = true;
    }

    private static void AddMenuItem(ItemsControl menu, string label, RoutedEventHandler action, bool isEnabled = true)
    {
        var item = new MenuItem { Header = label, Padding = new Thickness(12, 8, 18, 8), IsEnabled = isEnabled };
        item.SetResourceReference(MenuItem.HeaderTemplateProperty, "ContextMenuLabelTemplate");
        item.Click += action;
        menu.Items.Add(item);
    }

    private void SaveRecentSession()
    {
        try
        {
            if (_mode == ComparisonMode.Folders) SaveLastFolderPair();
            _sessionStore.SaveRecent(new RecentSession(_mode, LeftPathBox.Text.Trim(), RightPathBox.Text.Trim(), DateTime.Now));
            RefreshRecentSessions();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "مقایسه انجام شد، اما ذخیرهٔ جلسه ممکن نبود";
        }
    }

    private void RefreshRecentSessions()
    {
        var sessions = _sessionStore.LoadRecent();
        RecentSessionsList.ItemsSource = sessions;
        RecentCountText.Text = sessions.Count.ToString(CultureInfo.CurrentCulture);
    }

    private void RecentSessionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentSessionsList.SelectedItem is not RecentSession session) return;
        SetMode(session.Mode);
        LeftPathBox.Text = session.LeftPath;
        RightPathBox.Text = session.RightPath;
        _ = CompareAsync();
    }

    private void ShortcutsButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Ctrl + Enter   شروع مقایسه\nF5   مقایسهٔ دوباره\nCtrl + F   رفتن به فیلتر\nCtrl + L   تمرکز روی مسیر چپ\nCtrl + R   تمرکز روی مسیر راست\nAlt + ←   کپی انتخاب‌ها به چپ\nAlt + →   کپی انتخاب‌ها به راست\nDelete   حذف از پنل فعال\nF2   تغییر نام فایل سمت چپ\nEsc   توقف عملیات جاری\nF1   نمایش این راهنما",
            "میانبرهای صفحه‌کلید", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.Enter) { _ = CompareAsync(); e.Handled = true; }
        else if (modifiers == ModifierKeys.None && e.Key == Key.F5) { _ = CompareAsync(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.F) { FilterBox.Focus(); FilterBox.SelectAll(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.L) { LeftPathBox.Focus(); LeftPathBox.SelectAll(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.R) { RightPathBox.Focus(); RightPathBox.SelectAll(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Left) { _ = RunCopyAsync(FileSide.Right); e.Handled = true; }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Right) { _ = RunCopyAsync(FileSide.Left); e.Handled = true; }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Delete && (ResultsGrid.IsKeyboardFocusWithin || RightResultsGrid.IsKeyboardFocusWithin)) { _ = RunDeleteAsync(_selectedFolderSide); e.Handled = true; }
        else if (modifiers == ModifierKeys.None && e.Key == Key.F2) { _ = RenameSelectedAsync(FileSide.Left); e.Handled = true; }
        else if (modifiers == ModifierKeys.None && e.Key == Key.F1) { ShortcutsButton_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Escape) { _operationCancellation?.Cancel(); _diffCancellation?.Cancel(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveRecentSession(); e.Handled = true; }
    }

    private void ActivateWindow()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}

internal sealed class TextEntryDialog : Window
{
    private readonly TextBox _input;
    public string Value => _input.Text;

    public TextEntryDialog(string title, string prompt, string initialValue)
    {
        Title = title;
        Width = 480;
        Height = 205;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["SurfaceBrush"];
        Foreground = (Brush)Application.Current.Resources["TextBrush"];
        FlowDirection = FlowDirection.RightToLeft;
        ShowInTaskbar = false;

        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = prompt, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(label, 0);
        layout.Children.Add(label);

        _input = new TextBox { Text = initialValue, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Left };
        Grid.SetRow(_input, 1);
        layout.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) };
        var save = new Button { Content = "ادامه", MinWidth = 92, Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "انصراف", MinWidth = 82, Style = (Style)Application.Current.Resources["BaseButton"], IsCancel = true };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        layout.Children.Add(buttons);
        Content = layout;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
}
