using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DivaModManager.UI
{
    public partial class SongManagerWindow : Window
    {
        private readonly ObservableCollection<SongRow> songs = new ObservableCollection<SongRow>();
        private readonly SongEditService editService = new SongEditService();
        private readonly SongRunStatusOverrideStore runStatusOverrideStore = new SongRunStatusOverrideStore();
        private SongArtworkService artworkService;
        private readonly string modsFolder;
        private readonly string selectedModRoot;

        private ICollectionView songsView;
        private CancellationTokenSource scanCancellation;
        private CancellationTokenSource operationCancellation;
        private CancellationTokenSource artworkPreviewCancellation;
        private SongArtworkPreview currentArtworkPreview;
        private SongArtworkKind selectedArtworkKind = SongArtworkKind.Jacket;
        private bool initialized;
        private bool isBusy;
        private bool writeOperationInProgress;
        private bool closeRequested;

        public SongManagerWindow()
            : this(GetConfiguredModsFolder(), null)
        {
        }

        public SongManagerWindow(string selectedModRoot)
            : this(GetConfiguredModsFolder(), selectedModRoot)
        {
        }

        public SongManagerWindow(string modsFolder, string selectedModRoot)
        {
            InitializeComponent();

            this.modsFolder = modsFolder ?? String.Empty;
            this.selectedModRoot = NormalizePath(selectedModRoot);
            artworkService = new SongArtworkService(this.modsFolder, editService);
            SelectedModScopeButton.IsEnabled = !String.IsNullOrWhiteSpace(this.selectedModRoot);
            SelectedModScopeButton.ToolTip = SelectedModScopeButton.IsEnabled
                ? $"只显示 {Path.GetFileName(this.selectedModRoot)} 中的歌曲"
                : "打开窗口时未选择模组";

            if (SelectedModScopeButton.IsEnabled)
                SelectedModScopeButton.IsChecked = true;

            songsView = CollectionViewSource.GetDefaultView(songs);
            songsView.Filter = FilterSong;
            SongGrid.ItemsSource = songsView;
            InitializeFilterOptions();
            JacketArtworkButton.IsChecked = true;
            initialized = true;
            UpdateSelectionDetails(null);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshSongsAsync();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!writeOperationInProgress)
                return;

            closeRequested = true;
            e.Cancel = true;
            const string message = "正在完成文件写入或回滚，完成后将关闭窗口…";
            SetOperationOverlay(true, message);
            StatusText.Text = message;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (writeOperationInProgress)
                return;

            scanCancellation?.Cancel();
            artworkPreviewCancellation?.Cancel();
            artworkPreviewCancellation?.Dispose();
        }

        private async Task RefreshSongsAsync()
        {
            if (isBusy)
                return;

            SetBusyState(true);
            try
            {
                await ReloadSongsAsync();
            }
            finally
            {
                SetBusyState(false);
            }
        }

        private async Task<bool> ReloadSongsAsync()
        {
            CancelArtworkPreview();
            scanCancellation?.Cancel();
            scanCancellation?.Dispose();
            scanCancellation = new CancellationTokenSource();
            var cancellationToken = scanCancellation.Token;
            var selectedKey = GetSelectionKey(SongGrid.SelectedItem as SongRow);

            SetListState(ListState.Loading, "正在扫描歌曲…", "正在读取 MEGA39+ Custom Songs 数据库和关联资源");
            StatusText.Text = "正在扫描歌曲模组…";

            try
            {
                ValidateModsFolder();
                var entries = await ScanCatalogAsync(modsFolder, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                artworkService = new SongArtworkService(modsFolder, editService);

                songs.Clear();
                foreach (var entry in entries.OrderBy(item => item.PvId).ThenBy(item => item.ModName))
                {
                    runStatusOverrideStore.Apply(entry);
                    songs.Add(new SongRow(entry));
                }

                RefreshDifficultyFilterOptions(entries);
                songsView.Refresh();
                RestoreSelection(selectedKey);
                UpdateViewSummary();
                StatusText.Text = $"扫描完成，共识别 {songs.Count} 首歌曲";
                return true;
            }
            catch (OperationCanceledException)
            {
                // Closing the window or starting a newer scan cancels this scan.
                return false;
            }
            catch (Exception exception)
            {
                SetListState(ListState.Error, "无法读取歌曲", UserErrorMessage.From(exception), true);
                StatusText.Text = "歌曲扫描失败";
                Global.logger?.WriteLine($"扫描自定义歌曲失败。({exception})", LoggerType.Error);
                return false;
            }
        }

        private static Task<IReadOnlyList<SongEntry>> ScanCatalogAsync(string folder, CancellationToken cancellationToken)
        {
            return SongCatalogService.ScanModsAsync(folder, cancellationToken);
        }

        private bool FilterSong(object item)
        {
            if (item is not SongRow row)
                return false;

            if (SelectedModScopeButton.IsChecked == true &&
                !PathsEqual(row.Entry.ModRoot, selectedModRoot))
            {
                return false;
            }

            var difficultyFilter = DifficultyFilterBox.SelectedItem as DifficultyFilterOption;
            var levelFilter = LevelFilterBox.SelectedItem as LevelFilterOption;
            if (!MatchesDifficultyFilters(
                row.Entry,
                difficultyFilter == null || difficultyFilter.IsAll
                    ? null
                    : difficultyFilter.NormalizedName,
                levelFilter == null || levelFilter.IsAll || levelFilter.IsUnknown
                    ? null
                    : levelFilter.NumericLevel,
                levelFilter?.IsUnknown == true))
            {
                return false;
            }

            var runStatusFilter = RunStatusFilterBox.SelectedItem as RunStatusFilterOption;
            if (runStatusFilter?.Status is SongRunStatus requiredStatus && row.RunStatus != requiredStatus)
                return false;

            var searchText = SearchBox.Text?.Trim();
            return String.IsNullOrWhiteSpace(searchText) ||
                   row.SearchText.IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!initialized)
                return;

            var hasSearch = !String.IsNullOrWhiteSpace(SearchBox.Text);
            SearchPlaceholder.Visibility = hasSearch ? Visibility.Collapsed : Visibility.Visible;
            ClearSearchButton.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
            RefreshFilteredView();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void ScopeButton_Checked(object sender, RoutedEventArgs e)
        {
            if (initialized)
                RefreshFilteredView();
        }

        private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initialized)
                RefreshFilteredView();
        }

        private void RefreshFilteredView()
        {
            songsView.Refresh();
            UpdateViewSummary();
        }

        private void UpdateViewSummary()
        {
            var visibleCount = songsView.Cast<object>().Count();
            CountText.Text = $"{visibleCount} 首歌曲";

            if (visibleCount == 0)
            {
                var filtered = songs.Count > 0;
                SetListState(
                    ListState.Empty,
                    filtered ? "没有匹配的歌曲" : "未找到 Custom Songs",
                    filtered ? "请调整搜索、难度、星级或显示范围" : "可导入完整的 MEGA39+ 歌曲包");
            }
            else
            {
                SetListState(ListState.Ready, String.Empty, String.Empty);
                if (SongGrid.SelectedItem == null)
                    SongGrid.SelectedIndex = 0;
            }
        }

        private void SongGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectionDetails(SongGrid.SelectedItem as SongRow);
        }

        private void UpdateSelectionDetails(SongRow row)
        {
            var hasSelection = row != null && !isBusy;
            var isSongPatch = row?.Entry.IsSongPatch == true;
            var canEditMetadata = hasSelection &&
                                  !isSongPatch &&
                                  !String.IsNullOrWhiteSpace(row.Entry.DatabasePath) &&
                                  File.Exists(row.Entry.DatabasePath);
            SongNameBox.IsEnabled = canEditMetadata;
            SongNameEnglishBox.IsEnabled = canEditMetadata;
            SongNameReadingBox.IsEnabled = canEditMetadata;
            SaveMetadataButton.IsEnabled = canEditMetadata;
            ManualRunStatusBox.IsEnabled = hasSelection;
            SaveRunStatusOverrideButton.IsEnabled = hasSelection;
            ReplaceArtworkButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = hasSelection;
            DeleteSongButton.IsEnabled = hasSelection && !isSongPatch;

            const string patchProtectionMessage =
                "这是歌曲补丁，会复用或扩展其他歌曲。为避免破坏补丁与原曲的关系，不能修改歌名或删除；仍可预览图片并打开目录。";
            var metadataToolTip = isSongPatch ? patchProtectionMessage : null;
            SongNameBox.ToolTip = metadataToolTip;
            SongNameEnglishBox.ToolTip = metadataToolTip;
            SongNameReadingBox.ToolTip = metadataToolTip;
            SaveMetadataButton.ToolTip = metadataToolTip;
            DeleteSongButton.ToolTip = isSongPatch
                ? patchProtectionMessage
                : hasSelection
                    ? "删除数据库条目和独占资源，并在修改前创建备份"
                    : null;
            PatchWriteProtectionText.Text = patchProtectionMessage;
            PatchWriteProtectionText.Visibility = isSongPatch ? Visibility.Visible : Visibility.Collapsed;

            if (row == null)
            {
                DetailsTitleText.Text = "未选择歌曲";
                DetailsMetaText.Text = "请从左侧列表中选择歌曲";
                DetailsAuthorText.Text = String.Empty;
                DetailsAuthorText.Visibility = Visibility.Collapsed;
                SongNameBox.Clear();
                SongNameEnglishBox.Clear();
                SongNameReadingBox.Clear();
                DetailsDifficultiesLabel.Text = "难度谱面";
                DetailsDifficultiesText.Text = "—";
                DetailsRunStatusText.Text = "—";
                DetailsRunStatusText.Foreground = Brushes.White;
                ManualRunStatusBox.SelectedItem = ManualRunStatusOption.Automatic;
                ManualRunStatusHintText.Text = String.Empty;
                ManualRunStatusHintText.Visibility = Visibility.Collapsed;
                DetailsAssetStatusText.Text = "—";
                DetailsWarningText.Text = String.Empty;
                DetailsWarningText.Visibility = Visibility.Collapsed;
                ConflictSourcesItems.ItemsSource = null;
                ConflictSourcesPanel.Visibility = Visibility.Collapsed;
                PatchSourcesItems.ItemsSource = null;
                PatchSourcesPanel.Visibility = Visibility.Collapsed;
                ClearArtworkPreview("请选择歌曲");
                return;
            }

            DetailsTitleText.Text = row.SongName;
            var officialMarker = row.Entry.IsMega39PlusOfficialPvId ? " · 官曲 PVID" : String.Empty;
            DetailsMetaText.Text = $"PV {row.PvId} · {row.ModName} · {row.FormatDisplayName}{officialMarker}";
            DetailsAuthorText.Text = String.IsNullOrWhiteSpace(row.Entry.AuthorSummary)
                ? String.Empty
                : $"作者：{row.Entry.AuthorSummary}";
            DetailsAuthorText.Visibility = String.IsNullOrWhiteSpace(DetailsAuthorText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SongNameBox.Text = row.Entry.SongName;
            SongNameEnglishBox.Text = row.Entry.SongNameEnglish;
            SongNameReadingBox.Text = row.Entry.SongNameReading;
            DetailsDifficultiesLabel.Text = $"难度谱面（{row.Entry.Difficulties.Count}）";
            DetailsDifficultiesText.Text = FormatDifficulties(row.Entry.Difficulties);
            DetailsRunStatusText.Text = row.RunStatusTooltip;
            DetailsRunStatusText.Foreground = row.RunStatus switch
            {
                SongRunStatus.Broken => new SolidColorBrush(Color.FromRgb(255, 107, 107)),
                SongRunStatus.Warning => new SolidColorBrush(Color.FromRgb(255, 209, 102)),
                _ => new SolidColorBrush(Color.FromRgb(228, 228, 228))
            };
            ManualRunStatusBox.SelectedItem = ManualRunStatusOption.ForStatus(row.Entry.ManualRunStatusOverride);
            ManualRunStatusHintText.Text = row.Entry.HasManualRunStatusOverride
                ? $"已保存人工覆盖；自动判断仍为“{FormatRunStatus(row.Entry.AutomaticRunStatus)}”，自动诊断内容未被修改。"
                : "当前使用自动扫描结果。";
            ManualRunStatusHintText.Foreground = row.Entry.HasManualRunStatusOverride
                ? new SolidColorBrush(Color.FromRgb(118, 199, 192))
                : new SolidColorBrush(Color.FromRgb(168, 168, 168));
            ManualRunStatusHintText.Visibility = Visibility.Visible;
            DetailsAssetStatusText.Text = row.AssetStatusDisplay;
            DetailsWarningText.Text = row.Entry.WarningsDisplay;
            DetailsWarningText.Visibility = String.IsNullOrWhiteSpace(row.Entry.WarningsDisplay)
                ? Visibility.Collapsed
                : Visibility.Visible;
            ConflictSourcesItems.ItemsSource = row.Entry.IdConflictSources;
            ConflictSourcesLabel.Text = row.Entry.HasIdConflict
                ? $"PVID 冲突路径（{row.Entry.IdConflictSources.Count}）"
                : $"同 PVID 兼容来源（{row.Entry.IdConflictSources.Count}）";
            ConflictSourcesLabel.Foreground = row.Entry.HasIdConflict
                ? new SolidColorBrush(Color.FromRgb(255, 138, 128))
                : new SolidColorBrush(Color.FromRgb(168, 168, 168));
            ConflictSourcesPanel.Visibility = row.Entry.IdConflictSources.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            PatchSourcesItems.ItemsSource = row.Entry.PatchSources;
            PatchSourcesLabel.Text = $"已启用的歌曲补丁（{row.Entry.PatchSources.Count}）";
            PatchSourcesPanel.Visibility = row.Entry.PatchSources.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (hasSelection)
                _ = LoadArtworkPreviewAsync(row);
            else
                CancelArtworkPreview();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSongsAsync();
        }

        private void SaveRunStatusOverrideButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SongGrid.SelectedItem as SongRow;
            var option = ManualRunStatusBox.SelectedItem as ManualRunStatusOption;
            if (row == null || option == null)
                return;

            try
            {
                if (option.Status.HasValue)
                    runStatusOverrideStore.Set(row.Entry, option.Status.Value);
                else
                    runStatusOverrideStore.Clear(row.Entry);

                row.NotifyRunStatusChanged();
                RefreshFilteredView();
                UpdateSelectionDetails(SongGrid.SelectedItem as SongRow);
                StatusText.Text = option.Status.HasValue
                    ? $"已将 PV {row.PvId} 人工标记为{FormatRunStatus(option.Status.Value)}"
                    : $"PV {row.PvId} 已恢复自动运行判定";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    $"无法保存人工运行判定。{Environment.NewLine}{UserErrorMessage.From(exception)}",
                    "保存失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void StateActionButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSongsAsync();
        }

        private async void SaveMetadataButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SongGrid.SelectedItem as SongRow;
            if (row == null)
                return;
            if (row.Entry.IsSongPatch)
            {
                MessageBox.Show(
                    this,
                    "歌曲补丁会复用或扩展其他歌曲，不能修改歌名。请打开其模组目录进行管理。",
                    "歌曲补丁受保护",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var update = new SongMetadataUpdate
            {
                SongName = SongNameBox.Text.Trim(),
                SongNameEnglish = SongNameEnglishBox.Text.Trim(),
                SongNameReading = SongNameReadingBox.Text.Trim()
            };

            if (String.IsNullOrWhiteSpace(update.SongName))
            {
                MessageBox.Show(this, "歌名不能为空。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                SongNameBox.Focus();
                return;
            }

            await RunEditAsync(
                cancellationToken => UpdateMetadataAsync(row.Entry, update, cancellationToken),
                "正在保存歌曲名称…",
                "歌曲名称已保存");
        }

        private async void ReplaceArtworkButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SongGrid.SelectedItem as SongRow;
            if (row == null)
                return;

            var dialog = new OpenFileDialog
            {
                Title = $"选择新的{SongArtworkService.GetKindDisplayName(selectedArtworkKind)}",
                Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            await RunEditAsync(
                cancellationToken => artworkService.ReplaceAsync(
                    row.Entry,
                    selectedArtworkKind,
                    dialog.FileName,
                    cancellationToken),
                $"正在替换{SongArtworkService.GetKindDisplayName(selectedArtworkKind)}…",
                $"{SongArtworkService.GetKindDisplayName(selectedArtworkKind)}已替换");
        }

        private void ArtworkKind_Checked(object sender, RoutedEventArgs e)
        {
            if (sender == ThumbnailArtworkButton)
                selectedArtworkKind = SongArtworkKind.Thumbnail;
            else if (sender == BackgroundArtworkButton)
                selectedArtworkKind = SongArtworkKind.Background;
            else
                selectedArtworkKind = SongArtworkKind.Jacket;

            if (initialized)
                _ = LoadArtworkPreviewAsync(SongGrid.SelectedItem as SongRow);
        }

        private async Task LoadArtworkPreviewAsync(SongRow row)
        {
            CancelArtworkPreview();
            currentArtworkPreview = null;
            ReplaceArtworkButton.IsEnabled = false;
            if (row == null || isBusy)
            {
                ClearArtworkPreview(row == null ? "请选择歌曲" : "操作完成后刷新预览");
                return;
            }

            artworkPreviewCancellation = new CancellationTokenSource();
            var cancellationToken = artworkPreviewCancellation.Token;
            ArtworkPreviewImage.Source = null;
            ArtworkPreviewStatePanel.Visibility = Visibility.Visible;
            ArtworkPreviewProgress.Visibility = Visibility.Visible;
            ArtworkPreviewStateText.Text = $"正在读取{SongArtworkService.GetKindDisplayName(selectedArtworkKind)}…";
            ArtworkPreviewSourceText.Text = "—";

            try
            {
                var requestedKind = selectedArtworkKind;
                var preview = await artworkService.LoadPreviewAsync(row.Entry, requestedKind, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(SongGrid.SelectedItem, row) || requestedKind != selectedArtworkKind)
                    return;

                currentArtworkPreview = preview;
                ArtworkPreviewProgress.Visibility = Visibility.Collapsed;
                ArtworkPreviewSourceText.Text = preview.Message;
                if (!preview.IsAvailable)
                {
                    ArtworkPreviewImage.Source = null;
                    ArtworkPreviewStatePanel.Visibility = Visibility.Visible;
                    ArtworkPreviewStateText.Text = preview.Message;
                    return;
                }

                ArtworkPreviewImage.Source = CreateBitmapImage(preview.PngBytes);
                ArtworkPreviewStatePanel.Visibility = Visibility.Collapsed;
                ReplaceArtworkButton.IsEnabled = !isBusy && preview.CanReplace;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ArtworkPreviewProgress.Visibility = Visibility.Collapsed;
                ArtworkPreviewStatePanel.Visibility = Visibility.Visible;
                ArtworkPreviewStateText.Text = "无法预览：" + UserErrorMessage.From(exception);
                ArtworkPreviewSourceText.Text = ArtworkPreviewStateText.Text;
            }
        }

        private void CancelArtworkPreview()
        {
            artworkPreviewCancellation?.Cancel();
            artworkPreviewCancellation?.Dispose();
            artworkPreviewCancellation = null;
        }

        private void ClearArtworkPreview(string message)
        {
            CancelArtworkPreview();
            currentArtworkPreview = null;
            ArtworkPreviewImage.Source = null;
            ArtworkPreviewProgress.Visibility = Visibility.Collapsed;
            ArtworkPreviewStatePanel.Visibility = Visibility.Visible;
            ArtworkPreviewStateText.Text = message;
            ArtworkPreviewSourceText.Text = "—";
            ReplaceArtworkButton.IsEnabled = false;
        }

        private static BitmapImage CreateBitmapImage(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private async void DeleteSongButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SongGrid.SelectedItem as SongRow;
            if (row == null)
                return;
            if (row.Entry.IsSongPatch)
            {
                MessageBox.Show(
                    this,
                    "歌曲补丁不能作为独立歌曲删除。请打开其模组目录管理补丁，以免破坏它与原曲的关系。",
                    "歌曲补丁受保护",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var firstConfirmation = MessageBox.Show(
                this,
                $"确定要从模组“{row.ModName}”中删除歌曲“{row.SongName}”（PV {row.PvId}）吗？\n\n数据库条目和关联资源都会被移除，并在修改前创建备份。",
                "删除歌曲",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (firstConfirmation != MessageBoxResult.Yes)
                return;

            var finalConfirmation = MessageBox.Show(
                this,
                $"最后确认：删除 PV {row.PvId}。此操作不能在管理器中直接撤销，只能从备份恢复。",
                "确认删除歌曲",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error,
                MessageBoxResult.No);
            if (finalConfirmation != MessageBoxResult.Yes)
                return;

            await RunEditAsync(
                cancellationToken => DeleteSongAsync(row.Entry, cancellationToken),
                "正在删除歌曲并备份文件…",
                "歌曲已删除");
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 MEGA39+ 歌曲包",
                Filter = "歌曲包 (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            await RunEditAsync(
                cancellationToken => ImportSongPackageAsync(dialog.FileName, cancellationToken),
                "正在验证并导入歌曲包…",
                "歌曲包已导入");
        }

        private async Task RunEditAsync(
            Func<CancellationToken, Task<SongEditResult>> operation,
            string progressMessage,
            string successMessage)
        {
            if (isBusy)
                return;

            operationCancellation?.Cancel();
            operationCancellation?.Dispose();
            operationCancellation = new CancellationTokenSource();
            var cancellationToken = operationCancellation.Token;
            string completedStatus = null;

            SetBusyState(true);
            SetOperationOverlay(true, progressMessage);

            try
            {
                SongEditResult result;
                writeOperationInProgress = true;
                try
                {
                    result = await operation(cancellationToken);
                }
                finally
                {
                    writeOperationInProgress = false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!result.Success)
                {
                    if (!closeRequested)
                    {
                        MessageBox.Show(
                            this,
                            String.IsNullOrWhiteSpace(result.Message) ? "操作未完成。" : result.Message,
                            "歌曲管理",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    StatusText.Text = "操作未完成";
                    return;
                }

                var status = String.IsNullOrWhiteSpace(result.Message) ? successMessage : result.Message;
                if (!String.IsNullOrWhiteSpace(result.BackupPath))
                    status += $"；备份：{result.BackupPath}";
                StatusText.Text = status;
                completedStatus = status;

                if (!closeRequested)
                {
                    SetOperationOverlay(true, "正在刷新歌曲列表…");
                    if (await ReloadSongsAsync() && !String.IsNullOrWhiteSpace(completedStatus))
                        StatusText.Text = completedStatus;
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "操作已取消";
                return;
            }
            catch (Exception exception)
            {
                if (!closeRequested)
                    MessageBox.Show(this, UserErrorMessage.From(exception), "歌曲管理失败", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "操作失败";
                Global.logger?.WriteLine($"歌曲管理操作失败。({exception})", LoggerType.Error);
                return;
            }
            finally
            {
                writeOperationInProgress = false;
                SetOperationOverlay(false, String.Empty);
                SetBusyState(false);
                operationCancellation?.Dispose();
                operationCancellation = null;

                if (closeRequested)
                {
                    closeRequested = false;
                    _ = Dispatcher.BeginInvoke(new Action(Close));
                }
            }
        }

        private Task<SongEditResult> UpdateMetadataAsync(
            SongEntry song,
            SongMetadataUpdate update,
            CancellationToken cancellationToken)
        {
            return editService.UpdateMetadataAsync(song, update, cancellationToken);
        }

        private Task<SongEditResult> DeleteSongAsync(SongEntry song, CancellationToken cancellationToken)
        {
            return editService.DeleteSongAsync(song, cancellationToken);
        }

        private Task<SongEditResult> ImportSongPackageAsync(string sourcePath, CancellationToken cancellationToken)
        {
            return editService.ImportSongPackageAsync(sourcePath, modsFolder, cancellationToken);
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SongGrid.SelectedItem as SongRow;
            if (row == null)
                return;

            var path = Directory.Exists(row.Entry.ContentRoot) ? row.Entry.ContentRoot : row.Entry.ModRoot;
            OpenPathInExplorer(path, "歌曲目录不存在，可能已被移动或删除。");
        }

        private void RelatedSourcePathButton_Click(object sender, RoutedEventArgs e)
        {
            var path = (sender as FrameworkElement)?.Tag as string;
            OpenPathInExplorer(path, "关联的模组路径不存在，可能已被移动或删除。");
        }

        private void OpenPathInExplorer(string path, string missingMessage)
        {
            try
            {
                var isFile = !String.IsNullOrWhiteSpace(path) && File.Exists(path);
                var isDirectory = !String.IsNullOrWhiteSpace(path) && Directory.Exists(path);
                if (!isFile && !isDirectory)
                {
                    var parent = String.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
                    if (!String.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    {
                        path = parent;
                        isDirectory = true;
                    }
                }

                if (!isFile && !isDirectory)
                {
                    MessageBox.Show(this, missingMessage, "打开路径", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = isFile ? $"/select,\"{path}\"" : $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, UserErrorMessage.From(exception), "无法打开路径", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SetListState(
            ListState state,
            string title,
            string message,
            bool showAction = false)
        {
            ListStatePanel.Visibility = state == ListState.Ready ? Visibility.Collapsed : Visibility.Visible;
            StateProgressBar.Visibility = state == ListState.Loading ? Visibility.Visible : Visibility.Collapsed;
            StateTitleText.Text = title;
            StateMessageText.Text = message;
            StateActionButton.Visibility = showAction ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusyState(bool busy)
        {
            isBusy = busy;
            var enabled = !busy;

            RefreshButton.IsEnabled = enabled;
            ImportButton.IsEnabled = enabled;
            SearchBox.IsEnabled = enabled;
            DifficultyFilterBox.IsEnabled = enabled;
            LevelFilterBox.IsEnabled = enabled;
            RunStatusFilterBox.IsEnabled = enabled;
            ThumbnailArtworkButton.IsEnabled = enabled;
            JacketArtworkButton.IsEnabled = enabled;
            BackgroundArtworkButton.IsEnabled = enabled;
            ClearSearchButton.IsEnabled = enabled;
            AllModsScopeButton.IsEnabled = enabled;
            SelectedModScopeButton.IsEnabled = enabled && !String.IsNullOrWhiteSpace(selectedModRoot);
            SongGrid.IsEnabled = enabled;
            StateActionButton.IsEnabled = enabled;
            UpdateSelectionDetails(SongGrid.SelectedItem as SongRow);
        }

        private static string FormatRunStatus(SongRunStatus status)
        {
            return status switch
            {
                SongRunStatus.Broken => "无法运行",
                SongRunStatus.Warning => "勉强运行",
                _ => "可运行"
            };
        }

        private static string FormatDifficulties(IReadOnlyList<SongDifficulty> difficulties)
        {
            if (difficulties == null || difficulties.Count == 0)
                return "未识别到难度谱面";

            return String.Join(
                Environment.NewLine,
                difficulties.Select((difficulty, index) =>
                {
                    var source = difficulty.Source switch
                    {
                        SongDifficultySource.NewClassicsDatabase => "New Classics",
                        SongDifficultySource.LegacyDatabase => "Legacy",
                        _ => "未注册"
                    };
                    var scriptStatus = difficulty.ScriptExists ? "谱面可用" : "谱面缺失";
                    return $"{index + 1}. {difficulty.DisplayName} · {source} · {scriptStatus}";
                }));
        }

        private void InitializeFilterOptions()
        {
            DifficultyFilterBox.ItemsSource = BuildDifficultyFilterOptions(Array.Empty<SongEntry>());
            DifficultyFilterBox.SelectedIndex = 0;

            var levelOptions = new List<LevelFilterOption>
            {
                LevelFilterOption.All
            };
            for (var level = 1m; level <= 10m; level += 0.5m)
                levelOptions.Add(LevelFilterOption.ForLevel(level));
            levelOptions.Add(LevelFilterOption.Unknown);
            LevelFilterBox.ItemsSource = levelOptions;
            LevelFilterBox.SelectedIndex = 0;

            RunStatusFilterBox.ItemsSource = RunStatusFilterOption.Options;
            RunStatusFilterBox.SelectedIndex = 0;
            ManualRunStatusBox.ItemsSource = ManualRunStatusOption.Options;
            ManualRunStatusBox.SelectedItem = ManualRunStatusOption.Automatic;
        }

        private void RefreshDifficultyFilterOptions(IEnumerable<SongEntry> entries)
        {
            var selectedName = (DifficultyFilterBox.SelectedItem as DifficultyFilterOption)?.NormalizedName;
            var options = BuildDifficultyFilterOptions(entries);
            DifficultyFilterBox.ItemsSource = options;
            DifficultyFilterBox.SelectedItem = options.FirstOrDefault(option =>
                String.Equals(option.NormalizedName, selectedName, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }

        private static IReadOnlyList<DifficultyFilterOption> BuildDifficultyFilterOptions(
            IEnumerable<SongEntry> entries)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "easy", "normal", "hard", "extreme", "encore",
                "ex_easy", "ex_normal", "ex_hard", "ex_extreme", "ex_encore"
            };
            foreach (var name in entries
                .SelectMany(entry => entry.Difficulties)
                .Select(difficulty => difficulty.NormalizedName)
                .Where(name => !String.IsNullOrWhiteSpace(name)))
            {
                names.Add(name);
            }

            var options = new List<DifficultyFilterOption> { DifficultyFilterOption.All };
            options.AddRange(names
                .OrderBy(DifficultyOrder)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(DifficultyFilterOption.ForDifficulty));
            return options;
        }

        private static int DifficultyOrder(string name)
        {
            var knownOrder = new[]
            {
                "easy", "normal", "hard", "extreme", "encore",
                "ex_easy", "ex_normal", "ex_hard", "ex_extreme", "ex_encore"
            };
            var index = Array.FindIndex(knownOrder, candidate =>
                candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? Int32.MaxValue : index;
        }

        internal static bool MatchesDifficultyFilters(
            SongEntry entry,
            string normalizedDifficulty,
            decimal? numericLevel,
            bool unknownLevel)
        {
            if (entry == null)
                return false;
            if (String.IsNullOrWhiteSpace(normalizedDifficulty) &&
                !numericLevel.HasValue &&
                !unknownLevel)
            {
                return true;
            }

            return entry.Difficulties.Any(difficulty =>
                (String.IsNullOrWhiteSpace(normalizedDifficulty) ||
                    String.Equals(
                        difficulty.NormalizedName,
                        normalizedDifficulty,
                        StringComparison.OrdinalIgnoreCase)) &&
                (!numericLevel.HasValue || difficulty.NumericLevel == numericLevel) &&
                (!unknownLevel || !difficulty.NumericLevel.HasValue));
        }

        private static string FormatDifficultyName(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                return "未知";

            return String.Join(" ", name
                .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Equals("ex", StringComparison.OrdinalIgnoreCase)
                    ? "EX"
                    : Char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant()));
        }

        private void SetOperationOverlay(bool visible, string message)
        {
            OperationText.Text = message;
            OperationOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RestoreSelection(SelectionKey key)
        {
            if (key != null)
            {
                var match = songs.FirstOrDefault(row =>
                    row.PvId == key.PvId &&
                    IdentityPathsEqual(row.Entry.ModRoot, key.ModRoot) &&
                    IdentityPathsEqual(row.Entry.ContentRoot, key.ContentRoot) &&
                    IdentityPathsEqual(row.Entry.DatabasePath, key.DatabasePath) &&
                    IdentityPathsEqual(row.Entry.NewClassicsDatabasePath, key.NewClassicsDatabasePath));
                if (match != null && songsView.Contains(match))
                {
                    SongGrid.SelectedItem = match;
                    SongGrid.ScrollIntoView(match);
                    return;
                }
            }

            SongGrid.SelectedIndex = songsView.IsEmpty ? -1 : 0;
        }

        private static SelectionKey GetSelectionKey(SongRow row)
        {
            return row == null
                ? null
                : new SelectionKey(
                    row.PvId,
                    row.Entry.ModRoot,
                    row.Entry.ContentRoot,
                    row.Entry.DatabasePath,
                    row.Entry.NewClassicsDatabasePath);
        }

        private void ValidateModsFolder()
        {
            if (String.IsNullOrWhiteSpace(modsFolder))
                throw new InvalidOperationException("尚未配置 MEGA39+ 模组目录。");
            if (!Directory.Exists(modsFolder))
                throw new DirectoryNotFoundException($"模组目录不存在：{modsFolder}");
        }

        private static string GetConfiguredModsFolder()
        {
            try
            {
                if (Global.config == null ||
                    Global.config.Configs == null ||
                    String.IsNullOrWhiteSpace(Global.config.CurrentGame) ||
                    !Global.config.Configs.ContainsKey(Global.config.CurrentGame))
                {
                    return String.Empty;
                }

                return Global.config.Configs[Global.config.CurrentGame].ModsFolder ?? String.Empty;
            }
            catch
            {
                return String.Empty;
            }
        }

        private static bool PathsEqual(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second))
                return false;

            return String.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IdentityPathsEqual(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) && String.IsNullOrWhiteSpace(second))
                return true;

            return PathsEqual(first, second);
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return String.Empty;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private enum ListState
        {
            Ready,
            Loading,
            Empty,
            Error
        }

        private sealed class SelectionKey
        {
            public SelectionKey(
                int pvId,
                string modRoot,
                string contentRoot,
                string databasePath,
                string newClassicsDatabasePath)
            {
                PvId = pvId;
                ModRoot = modRoot;
                ContentRoot = contentRoot;
                DatabasePath = databasePath;
                NewClassicsDatabasePath = newClassicsDatabasePath;
            }

            public int PvId { get; }
            public string ModRoot { get; }
            public string ContentRoot { get; }
            public string DatabasePath { get; }
            public string NewClassicsDatabasePath { get; }
        }

        private sealed class SongRow : INotifyPropertyChanged
        {
            public SongRow(SongEntry entry)
            {
                Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            }

            public SongEntry Entry { get; }
            public event PropertyChangedEventHandler PropertyChanged;
            public int PvId => Entry.PvId;
            public string SongName => String.IsNullOrWhiteSpace(Entry.SongName) ? "（未命名）" : Entry.SongName;
            public string ModName => String.IsNullOrWhiteSpace(Entry.ModName) ? "（未知模组）" : Entry.ModName;
            public string FormatDisplayName => Entry.FormatDisplayName;
            public string DifficultiesDisplay => Entry.DifficultiesDisplay;
            public SongRunStatus RunStatus => Entry.RunStatus;
            public string RunStatusDisplay => FormatRunStatus(RunStatus) +
                (Entry.HasManualRunStatusOverride ? "（人工）" : String.Empty);
            public string RunStatusTooltip
            {
                get
                {
                    var automaticDiagnosis = String.IsNullOrWhiteSpace(Entry.AutomaticRunStatusReasonsDisplay)
                        ? "未发现阻止运行的问题"
                        : Entry.AutomaticRunStatusReasonsDisplay;
                    if (!Entry.HasManualRunStatusOverride)
                        return RunStatusDisplay + "：" + automaticDiagnosis;

                    return $"{RunStatusDisplay}；自动判断：{FormatRunStatus(Entry.AutomaticRunStatus)}；" +
                           "自动诊断：" + automaticDiagnosis;
                }
            }
            public bool UsesOriginalSongAssets => Entry.IsSongPatch;
            public string AudioStatus => UsesOriginalSongAssets ? "沿用原曲" : Entry.AudioExists ? "可用" : "缺失";
            public string VideoStatus => UsesOriginalSongAssets
                ? "沿用原曲"
                : Entry.Uses3dPv
                    ? "3D PV（无需视频）"
                    : Entry.VideoExists ? "可用" : "缺失";
            public string ArtworkStatus
            {
                get
                {
                    if (UsesOriginalSongAssets)
                        return "沿用原曲";
                    if (Entry.ArtworkComplete)
                        return "可用";

                    var missing = new List<string>();
                    if (!Entry.ThumbnailExists)
                        missing.Add("小图标");
                    if (!Entry.JacketExists)
                        missing.Add("封面");
                    if (!Entry.BackgroundExists)
                        missing.Add("背景");
                    return "缺：" + String.Join("、", missing);
                }
            }
            public string AssetStatusDisplay => UsesOriginalSongAssets
                ? "音频：沿用原曲　视频：沿用原曲　图片：沿用原曲"
                : Entry.Uses3dPv
                    ? $"音频：{AudioStatus}　视频：{VideoStatus}　封面：{ArtworkStatus}"
                : String.IsNullOrWhiteSpace(Entry.AssetStatus)
                    ? $"音频：{AudioStatus}　视频：{VideoStatus}　封面：{ArtworkStatus}"
                    : Entry.AssetStatus;
            public string SearchText => String.Join(
                " ",
                Entry.PvId,
                Entry.RawPvId,
                Entry.SongName,
                Entry.SongNameEnglish,
                Entry.SongNameReading,
                Entry.AuthorSummary,
                Entry.ModName,
                Entry.SearchText);

            public void NotifyRunStatusChanged()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunStatus)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunStatusDisplay)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunStatusTooltip)));
            }
        }

        private sealed class RunStatusFilterOption
        {
            private RunStatusFilterOption(SongRunStatus? status, string displayName)
            {
                Status = status;
                DisplayName = displayName;
            }

            public static IReadOnlyList<RunStatusFilterOption> Options { get; } = new[]
            {
                new RunStatusFilterOption(null, "全部状态"),
                new RunStatusFilterOption(SongRunStatus.Ready, "可运行"),
                new RunStatusFilterOption(SongRunStatus.Warning, "勉强运行"),
                new RunStatusFilterOption(SongRunStatus.Broken, "无法运行")
            };

            public SongRunStatus? Status { get; }
            public string DisplayName { get; }
        }

        private sealed class ManualRunStatusOption
        {
            private ManualRunStatusOption(SongRunStatus? status, string displayName)
            {
                Status = status;
                DisplayName = displayName;
            }

            public static ManualRunStatusOption Automatic { get; } =
                new ManualRunStatusOption(null, "自动判断（清除人工覆盖）");
            public static ManualRunStatusOption Ready { get; } =
                new ManualRunStatusOption(SongRunStatus.Ready, "可运行");
            public static ManualRunStatusOption Warning { get; } =
                new ManualRunStatusOption(SongRunStatus.Warning, "勉强运行");
            public static ManualRunStatusOption Broken { get; } =
                new ManualRunStatusOption(SongRunStatus.Broken, "无法运行");
            public static IReadOnlyList<ManualRunStatusOption> Options { get; } =
                new[] { Automatic, Ready, Warning, Broken };

            public SongRunStatus? Status { get; }
            public string DisplayName { get; }

            public static ManualRunStatusOption ForStatus(SongRunStatus? status)
            {
                if (!status.HasValue)
                    return Automatic;

                return Options.First(option => option.Status == status);
            }
        }

        private sealed class DifficultyFilterOption
        {
            private DifficultyFilterOption(string normalizedName, string displayName)
            {
                NormalizedName = normalizedName;
                DisplayName = displayName;
            }

            public static DifficultyFilterOption All { get; } = new DifficultyFilterOption(null, "全部难度");
            public string NormalizedName { get; }
            public string DisplayName { get; }
            public bool IsAll => String.IsNullOrWhiteSpace(NormalizedName);

            public static DifficultyFilterOption ForDifficulty(string normalizedName)
            {
                return new DifficultyFilterOption(normalizedName, FormatDifficultyName(normalizedName));
            }

            public override string ToString() => DisplayName;
        }

        private sealed class LevelFilterOption
        {
            private LevelFilterOption(decimal? numericLevel, string displayName, bool isUnknown = false)
            {
                NumericLevel = numericLevel;
                DisplayName = displayName;
                IsUnknown = isUnknown;
            }

            public static LevelFilterOption All { get; } = new LevelFilterOption(null, "全部星级");
            public static LevelFilterOption Unknown { get; } = new LevelFilterOption(null, "等级 ?", true);
            public decimal? NumericLevel { get; }
            public string DisplayName { get; }
            public bool IsUnknown { get; }
            public bool IsAll => !NumericLevel.HasValue && !IsUnknown;

            public static LevelFilterOption ForLevel(decimal level)
            {
                return new LevelFilterOption(
                    level,
                    "等级 " + level.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            }

            public override string ToString() => DisplayName;
        }
    }
}
