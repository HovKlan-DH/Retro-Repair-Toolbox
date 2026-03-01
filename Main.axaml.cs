using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CRT
{
    public partial class Main : Window
    {
        // Zoom
        private Matrix _schematicsMatrix = Matrix.Identity;

        // Thumbnails
        private List<SchematicThumbnail> _currentThumbnails = [];

        // Full-res viewer
        private Bitmap? _currentFullResBitmap;
        private CancellationTokenSource? _fullResLoadCts;

        // Panning
        private bool _isPanning;
        private Point _panStartPoint;
        private Matrix _panStartMatrix;

        // Highlights
        private Dictionary<string, HighlightSpatialIndex> _highlightIndexBySchematic = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, BoardSchematicEntry> _schematicByName = new(StringComparer.OrdinalIgnoreCase);

        // Highlight rects per schematic per board label — built at board load, used for on-demand highlighting
        private Dictionary<string, Dictionary<string, List<Rect>>> _highlightRectsBySchematicAndLabel = new(StringComparer.OrdinalIgnoreCase);

        // Window placement: tracks the last known normal-state size and position
        private double _restoreWidth;
        private double _restoreHeight;
        private PixelPoint _restorePosition;
        private DispatcherTimer? _windowPlacementSaveTimer;

        // Category filter: suppresses saves during programmatic selection changes
        private bool _suppressCategoryFilterSave;

        private BoardData? _currentBoardData;
        private bool _suppressComponentHighlightUpdate;
        private ComponentInfoWindow? _singleComponentInfoWindow;
        private readonly Dictionary<string, ComponentInfoWindow> _componentInfoWindowsByKey = new(StringComparer.OrdinalIgnoreCase);

        // Blink selected highlights
        private DispatcherTimer? _blinkSelectedTimer;
        private bool _blinkSelectedPhaseVisible = true;
        private bool _blinkSelectedEnabled;

        public Main()
        {
            InitializeComponent();

            // Restore left panel width from settings
            this.RootGrid.ColumnDefinitions[0].Width = new GridLength(UserSettings.LeftPanelWidth);
            this.RootGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);

            // Subscribe to splitter pointer-release to persist positions when a drag ends.
            // handledEventsToo: true is required because GridSplitter marks the event as handled.
            this.MainSplitter.AddHandler(
                InputElement.PointerReleasedEvent,
                this.OnMainSplitterPointerReleased,
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            this.SchematicsSplitter.AddHandler(
                InputElement.PointerReleasedEvent,
                this.OnSchematicsSplitterPointerReleased,
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            // Initialize restore values from settings, then apply window placement before Show()
            // so Normal windows appear at the right place/size with zero flicker.
            // Maximized windows are positioned on the saved screen before being maximized so the
            // OS maximizes them on the correct monitor.
            _restoreWidth = Math.Max(this.MinWidth, UserSettings.WindowWidth);
            _restoreHeight = Math.Max(this.MinHeight, UserSettings.WindowHeight);
            _restorePosition = new PixelPoint(UserSettings.WindowX, UserSettings.WindowY);

            // Wireup "blink" button
            this.BlinkSelectedCheckBox.IsChecked = false;
            this.BlinkSelectedCheckBox.IsCheckedChanged += this.OnBlinkSelectedChanged;

            if (UserSettings.HasWindowPlacement)
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Width = _restoreWidth;
                this.Height = _restoreHeight;

                if (UserSettings.WindowState == nameof(Avalonia.Controls.WindowState.Maximized))
                {
                    // Place anywhere on the saved screen so the OS maximizes it there
                    this.Position = new PixelPoint(UserSettings.WindowScreenX + 100, UserSettings.WindowScreenY + 100);
                    this.WindowState = Avalonia.Controls.WindowState.Maximized;
                }
                else
                {
                    this.Position = _restorePosition;
                }
            }

            this.Opened += this.OnWindowFirstOpened;
            this.Closing += this.OnWindowClosing;

            // Align the visual transform origin with the top-left coordinate system used in ClampSchematicsMatrix
            this.SchematicsImage.RenderTransformOrigin = RelativePoint.TopLeft;
            this.SchematicsHighlightsOverlay.RenderTransformOrigin = RelativePoint.TopLeft;

            // Keep highlights correct after layout changes (e.g. splitter drags)
            this.SchematicsContainer.PropertyChanged += (s, e) =>
            {
                if (e.Property == Visual.BoundsProperty)
                    this.ClampSchematicsMatrix();
            };

            // Also clamp when the individual image object updates its logical dimensions
            this.SchematicsImage.PropertyChanged += (s, e) =>
            {
                if (e.Property == Visual.BoundsProperty)
                    this.ClampSchematicsMatrix();
            };

            this.UpdateRegionButtonsState();
            this.HardwareComboBox.SelectionChanged += this.OnHardwareSelectionChanged;
            this.BoardComboBox.SelectionChanged += this.OnBoardSelectionChanged;
            this.SchematicsThumbnailList.SelectionChanged += this.OnSchematicsThumbnailSelectionChanged;
            this.CategoryFilterListBox.SelectionChanged += this.OnCategoryFilterSelectionChanged;
            this.ComponentFilterListBox.SelectionChanged += this.OnComponentFilterSelectionChanged;
            this.PopulateHardwareDropDown();

            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var hasVersion = version != null;
            var versionString = AppConfig.GetDisplayVersion(version);

            this.AppVersionText.Text = $"Version {versionString}";

            this.PopulateAboutTab(assembly, hasVersion ? versionString : null);

            this.Title = hasVersion
                ? $"Classic Repair Toolbox {versionString}"
                : "Classic Repair Toolbox";

            // Determine if Dark Theme is actively evaluated during startup.
            var isDark = Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark ||
                         (Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Default &&
                          Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

            this.ThemeToggleSwitch.IsChecked = isDark;
            this.ThemeToggleSwitch.IsCheckedChanged += this.OnThemeToggleSwitchChanged;

            this.MultipleInstancesForComponentPopupToggleSwitch.IsChecked = UserSettings.MultipleInstancesForComponentPopup;
            this.MultipleInstancesForComponentPopupToggleSwitch.IsCheckedChanged += this.OnMultipleInstancesForComponentPopupChanged;

            this.MaximizeComponentPopupToggleSwitch.IsChecked = UserSettings.MaximizeComponentPopup;
            this.MaximizeComponentPopupToggleSwitch.IsCheckedChanged += this.OnMaximizeComponentPopupChanged;

            // Initialize configuration checkboxes — subscribe after setting initial values
            // to avoid triggering redundant saves during startup
            this.CheckVersionOnLaunchCheckBox.IsChecked = UserSettings.CheckVersionOnLaunch;
            this.CheckDataOnLaunchCheckBox.IsChecked = UserSettings.CheckDataOnLaunch;
            this.CheckVersionOnLaunchCheckBox.IsCheckedChanged += this.OnCheckVersionOnLaunchChanged;
            this.CheckDataOnLaunchCheckBox.IsCheckedChanged += this.OnCheckDataOnLaunchChanged;

            this.SchematicsContainer.PointerExited += this.OnSchematicsPointerExited;

            if (UserSettings.CheckVersionOnLaunch)
            {
                this.CheckForAppUpdate();
            }

            this.StartBackgroundSyncAsync();
        }

        // ###########################################################################################
        // Checks for an available update on startup and shows the banner if one is found.
        // ###########################################################################################
        private async void CheckForAppUpdate()
        {
            bool? available = await UpdateService.CheckForUpdateAsync();

            if (available == true)
            {
                this.UpdateBannerText.Text = $"Version {UpdateService.PendingVersion} is available";
                this.UpdateBanner.IsVisible = true;
            }
        }

        // ###########################################################################################
        // Shows the sync banner during background sync, then hides it automatically if nothing
        // changed, or keeps it visible with a summary if files were updated.
        // ###########################################################################################
        private async void StartBackgroundSyncAsync()
        {
            if (!DataManager.HasPendingSync)
                return;

            this.SyncBannerText.Text = "Checking data from online source...";
            this.SyncBanner.IsVisible = true;

            int changed = await DataManager.SyncRemainingAsync(status =>
                Dispatcher.UIThread.Post(() => this.SyncBannerText.Text = status));

            if (changed > 0)
            {
                this.SyncBannerText.Text = changed == 1
                    ? "1 file updated in the background"
                    : $"{changed} files updated in the background";
            }
            else
            {
                this.SyncBanner.IsVisible = false;
            }
        }

        // ###########################################################################################
        // Dismisses the sync banner.
        // ###########################################################################################
        private void OnSyncBannerDismiss(object? sender, RoutedEventArgs e)
        {
            this.SyncBanner.IsVisible = false;
        }

        // ###########################################################################################
        // Dismisses the sync banner when clicking anywhere on it.
        // ###########################################################################################
        private void OnSyncBannerPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            this.SyncBanner.IsVisible = false;
        }

        // ###########################################################################################
        // Dismisses the update banner without cancelling the update.
        // ###########################################################################################
        private void OnUpdateBannerDismiss(object? sender, RoutedEventArgs e)
        {
            this.UpdateBanner.IsVisible = false;
        }

        // ###########################################################################################
        // Downloads and installs the pending update, showing progress in the banner text.
        // ###########################################################################################
        private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
        {
            this.UpdateBannerInstallButton.IsEnabled = false;
            this.UpdateBannerDismissButton.IsEnabled = false;
            this.UpdateBannerText.Text = "Downloading update...";

            await UpdateService.DownloadAndInstallAsync(progress =>
            {
                Dispatcher.UIThread.Post(() => this.UpdateBannerText.Text = $"Downloading update... {progress}%");
            });
            // DownloadAndInstallAsync calls ApplyUpdatesAndRestart internally - app relaunches automatically
        }

        // ###########################################################################################
        // Populates the hardware drop-down with distinct hardware names from loaded data.
        // Restores the last selected hardware if available.
        // ###########################################################################################
        private void PopulateHardwareDropDown()
        {
            var hardwareNames = DataManager.HardwareBoards
                .Select(e => e.HardwareName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            this.HardwareComboBox.ItemsSource = hardwareNames;

            if (hardwareNames.Count == 0)
            {
                this.HardwareComboBox.SelectedIndex = -1;
                return;
            }

            var lastHardware = UserSettings.GetLastHardware();
            var savedIndex = hardwareNames.FindIndex(h =>
                string.Equals(h, lastHardware, StringComparison.OrdinalIgnoreCase));

            this.HardwareComboBox.SelectedIndex = savedIndex >= 0 ? savedIndex : 0;
        }

        // ###########################################################################################
        // Filters the board drop-down to only show boards belonging to the selected hardware.
        // Restores the last selected board for that hardware if available.
        // ###########################################################################################
        private void OnHardwareSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var selectedHardware = this.HardwareComboBox.SelectedItem as string;

            var boards = DataManager.HardwareBoards
                .Where(entry => string.Equals(entry.HardwareName, selectedHardware, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.BoardName)
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .ToList();

            this.BoardComboBox.ItemsSource = boards;

            if (string.IsNullOrWhiteSpace(selectedHardware) || boards.Count == 0)
            {
                this.BoardComboBox.SelectedIndex = -1;
                return;
            }

            UserSettings.SetLastHardware(selectedHardware);

            var lastBoard = UserSettings.GetLastBoardForHardware(selectedHardware);
            var savedIndex = boards.FindIndex(b =>
                string.Equals(b, lastBoard, StringComparison.OrdinalIgnoreCase));

            this.BoardComboBox.SelectedIndex = savedIndex >= 0 ? savedIndex : 0;
        }

        // ###########################################################################################
        // Handles board selection changes - loads board data and builds the thumbnail gallery.
        // Also builds per-schematic, per-label highlight rect lookup for selection-driven rendering.
        // ###########################################################################################
        private async void OnBoardSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            // Suppress category saves immediately — setting ItemsSource = null below fires
            // SelectionChanged with an empty selection, which would overwrite the new board's
            // saved categories before the restore logic has a chance to run.
            this._suppressCategoryFilterSave = true;

            foreach (var thumb in this._currentThumbnails)
            {
                if (!ReferenceEquals(thumb.ImageSource, thumb.BaseThumbnail))
                    (thumb.ImageSource as IDisposable)?.Dispose();
                (thumb.BaseThumbnail as IDisposable)?.Dispose();
            }
            this._currentThumbnails = [];
            this.SchematicsThumbnailList.ItemsSource = null;
            this.CategoryFilterListBox.ItemsSource = null;
            this.ComponentFilterListBox.ItemsSource = null;

            this._highlightIndexBySchematic = new(StringComparer.OrdinalIgnoreCase);
            this._schematicByName = new(StringComparer.OrdinalIgnoreCase);
            this._highlightRectsBySchematicAndLabel = new(StringComparer.OrdinalIgnoreCase);
            this._currentBoardData = null;

            this.ResetSchematicsViewer();

            var selectedHardware = this.HardwareComboBox.SelectedItem as string;
            var selectedBoard = this.BoardComboBox.SelectedItem as string;

            if (string.IsNullOrEmpty(selectedHardware) || string.IsNullOrEmpty(selectedBoard))
                return;

            UserSettings.SetLastHardware(selectedHardware);
            UserSettings.SetLastBoardForHardware(selectedHardware, selectedBoard);

            var entry = DataManager.HardwareBoards.FirstOrDefault(ent =>
                string.Equals(ent.HardwareName, selectedHardware, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ent.BoardName, selectedBoard, StringComparison.OrdinalIgnoreCase));

            if (entry == null || string.IsNullOrWhiteSpace(entry.ExcelDataFile))
                return;

            var boardData = await DataManager.LoadBoardDataAsync(entry);
            if (boardData == null)
                return;

            this._currentBoardData = boardData;

            // Populate category filter in insertion order
            var categories = BuildDistinctCategories(boardData);
            var boardKey = this.GetCurrentBoardKey();

            this.CategoryFilterListBox.ItemsSource = categories;

            var savedCategories = UserSettings.GetSelectedCategories(boardKey);
            if (savedCategories == null)
            {
                // No saved selection yet — default: select all
                this.CategoryFilterListBox.SelectAll();
            }
            else
            {
                // Restore the previously saved per-board selection
                for (int i = 0; i < categories.Count; i++)
                {
                    if (savedCategories.Contains(categories[i], StringComparer.OrdinalIgnoreCase))
                        this.CategoryFilterListBox.Selection.Select(i);
                }
            }
            this._suppressCategoryFilterSave = false;

            // Populate component filter for this board, filtered by the active region and categories
            var activeCategories = new HashSet<string>(
                this.CategoryFilterListBox.SelectedItems?.Cast<string>() ?? [],
                StringComparer.OrdinalIgnoreCase);
            var componentItems = BuildComponentItems(boardData, UserSettings.Region, activeCategories);
            this.ComponentFilterListBox.ItemsSource = componentItems;

            // Build per-schematic, per-label highlight rects for selection-driven highlighting
            this._highlightRectsBySchematicAndLabel = await Task.Run(() => BuildHighlightRects(boardData, UserSettings.Region));
            this._schematicByName = boardData.Schematics
                .Where(s => !string.IsNullOrWhiteSpace(s.SchematicName))
                .ToDictionary(s => s.SchematicName, s => s, StringComparer.OrdinalIgnoreCase);
            this._highlightIndexBySchematic = new(StringComparer.OrdinalIgnoreCase);

            // Load full-resolution bitmaps on a background thread
            var loaded = await Task.Run(() =>
            {
                var result = new List<(string Name, string FullPath, Bitmap? FullBitmap)>();

                foreach (var schematic in boardData.Schematics)
                {
                    if (string.IsNullOrWhiteSpace(schematic.SchematicImageFile))
                        continue;

                    var fullPath = Path.Combine(DataManager.DataRoot,
                        schematic.SchematicImageFile.Replace('/', Path.DirectorySeparatorChar));

                    Bitmap? bitmap = null;
                    if (File.Exists(fullPath))
                    {
                        try { bitmap = new Bitmap(fullPath); }
                        catch (Exception ex) { Logger.Warning($"Could not load schematic image [{fullPath}] - [{ex.Message}]"); }
                    }

                    result.Add((schematic.SchematicName, fullPath, bitmap));
                }

                return result;
            });

            // Pre-scale to base thumbnails (no highlights) on the UI thread, then release full-resolution originals
            var thumbnails = new List<SchematicThumbnail>();

            foreach (var (name, fullPath, fullBitmap) in loaded)
            {
                RenderTargetBitmap? baseThumbnail = null;
                PixelSize originalPixelSize = default;

                if (fullBitmap != null)
                {
                    baseThumbnail = CreateScaledThumbnail(fullBitmap, AppConfig.ThumbnailMaxWidth);
                    originalPixelSize = fullBitmap.PixelSize;
                    fullBitmap.Dispose();
                }

                thumbnails.Add(new SchematicThumbnail
                {
                    Name = name,
                    ImageFilePath = fullPath,
                    BaseThumbnail = baseThumbnail,
                    OriginalPixelSize = originalPixelSize,
                    ImageSource = baseThumbnail,
                    VisualOpacity = 1.0,
                    IsMatchForSelection = false
                });
            }

            this._currentThumbnails = thumbnails;
            this.SchematicsThumbnailList.ItemsSource = thumbnails;

            if (thumbnails.Count > 0)
            {
                // Restore previously selected schematic for this board, fallback to 0
                var savedSchematic = UserSettings.GetLastSchematicForBoard(boardKey);
                var savedIndex = string.IsNullOrEmpty(savedSchematic) ? -1 : thumbnails.FindIndex(t =>
                    string.Equals(t.Name, savedSchematic, StringComparison.OrdinalIgnoreCase));

                this.SchematicsThumbnailList.SelectedIndex = savedIndex >= 0 ? savedIndex : 0;
            }

            // Restore schematics splitter ratio saved for this specific board
            var ratio = UserSettings.GetSchematicsSplitterRatio(boardKey);
            this.SchematicsInnerGrid.ColumnDefinitions[0].Width = new GridLength(ratio * 100.0, GridUnitType.Star);
            this.SchematicsInnerGrid.ColumnDefinitions[2].Width = new GridLength((1.0 - ratio) * 100.0, GridUnitType.Star);
        }

        // ###########################################################################################
        // Loads the full-resolution image for the selected thumbnail and sets up the highlight overlay.
        // ###########################################################################################
        private async void OnSchematicsThumbnailSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            this._fullResLoadCts?.Cancel();
            this._fullResLoadCts = new CancellationTokenSource();
            var cts = this._fullResLoadCts;

            var selected = this.SchematicsThumbnailList.SelectedItem as SchematicThumbnail;

            this.SchematicsImage.Source = null;
            this._schematicsMatrix = Matrix.Identity;
            ((MatrixTransform)this.SchematicsImage.RenderTransform!).Matrix = this._schematicsMatrix;
            ((MatrixTransform)this.SchematicsHighlightsOverlay.RenderTransform!).Matrix = this._schematicsMatrix;

            this.SchematicsHighlightsOverlay.HighlightIndex = null;
            this.SchematicsHighlightsOverlay.BitmapPixelSize = new PixelSize(0, 0);
            this.SchematicsHighlightsOverlay.ViewMatrix = this._schematicsMatrix;

            if (selected == null || string.IsNullOrEmpty(selected.ImageFilePath))
                return;

            // Save the newly selected schematic for this board
            var boardKey = this.GetCurrentBoardKey();
            if (!string.IsNullOrEmpty(boardKey))
            {
                UserSettings.SetLastSchematicForBoard(boardKey, selected.Name);
            }

            var bitmap = await Task.Run(() =>
            {
                if (cts.Token.IsCancellationRequested)
                    return null;

                try { return new Bitmap(selected.ImageFilePath); }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not load full-res schematic [{selected.ImageFilePath}] - [{ex.Message}]");
                    return null;
                }
            }, cts.Token);

            if (cts.Token.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }

            this._currentFullResBitmap?.Dispose();
            this._currentFullResBitmap = bitmap;
            this.SchematicsImage.Source = bitmap;

            if (bitmap != null)
            {
                // Always set BitmapPixelSize so the overlay can render as soon as a component is selected,
                // even if no highlight index exists yet at the time this schematic loads.
                this.SchematicsHighlightsOverlay.BitmapPixelSize = bitmap.PixelSize;

                if (this._highlightIndexBySchematic.TryGetValue(selected.Name, out var index) &&
                    this._schematicByName.TryGetValue(selected.Name, out var schematic))
                {
                    this.SchematicsHighlightsOverlay.HighlightIndex = index;
                    this.SchematicsHighlightsOverlay.HighlightColor = ParseColorOrDefault(schematic.MainImageHighlightColor, Colors.IndianRed);
                    this.SchematicsHighlightsOverlay.HighlightOpacity = ParseOpacityOrDefault(schematic.MainHighlightOpacity, 0.20);
                }
            }

            this.SchematicsHighlightsOverlay.ViewMatrix = this._schematicsMatrix;
            this.SchematicsHighlightsOverlay.InvalidateVisual();

            // Defer a clamp call so the engine can measure and center the new image layout 
            // immediately instead of waiting for a window resize or banner collapse.
            Dispatcher.UIThread.Post(() => this.ClampSchematicsMatrix());
        }

        // ###########################################################################################
        // Clears the main schematics image and resets the zoom and highlight overlay state.
        // ###########################################################################################
        private void ResetSchematicsViewer()
        {
            this._fullResLoadCts?.Cancel();
            this._fullResLoadCts = null;

            this._currentFullResBitmap?.Dispose();
            this._currentFullResBitmap = null;

            this.SchematicsImage.Source = null;

            this._schematicsMatrix = Matrix.Identity;
            ((MatrixTransform)this.SchematicsImage.RenderTransform!).Matrix = this._schematicsMatrix;
            ((MatrixTransform)this.SchematicsHighlightsOverlay.RenderTransform!).Matrix = this._schematicsMatrix;

            this.SchematicsHighlightsOverlay.HighlightIndex = null;
            this.SchematicsHighlightsOverlay.BitmapPixelSize = new PixelSize(0, 0);
            this.SchematicsHighlightsOverlay.ViewMatrix = this._schematicsMatrix;

            this._isPanning = false;
            this.HideSchematicsHoverUi();
        }

        // ###########################################################################################
        // Returns the rectangle (in the image control's local coordinate space) that the actual
        // bitmap content occupies, accounting for Stretch="Uniform" letterboxing on either axis.
        // ###########################################################################################
        private Rect GetImageContentRect()
        {
            var imageSize = this.SchematicsImage.Bounds.Size;
            var bitmap = this._currentFullResBitmap;

            if (bitmap == null || imageSize.Width <= 0 || imageSize.Height <= 0)
                return new Rect(imageSize);

            double containerAspect = imageSize.Width / imageSize.Height;

            // Use .Size (logical dimensions) instead of .PixelSize to account for image DPI metadata
            double bitmapAspect = bitmap.Size.Width / bitmap.Size.Height;

            double contentX, contentY, contentWidth, contentHeight;

            if (bitmapAspect > containerAspect)
            {
                // Letterbox top and bottom
                contentWidth = imageSize.Width;
                contentHeight = imageSize.Width / bitmapAspect;
                contentX = 0;
                contentY = (imageSize.Height - contentHeight) / 2.0;
            }
            else
            {
                // Letterbox left and right
                contentHeight = imageSize.Height;
                contentWidth = imageSize.Height * bitmapAspect;
                contentX = (imageSize.Width - contentWidth) / 2.0;
                contentY = 0;
            }

            return new Rect(contentX, contentY, contentWidth, contentHeight);
        }

        // ###########################################################################################
        // Clamps the current schematics matrix so no empty space is visible inside the container.
        // If the scaled content is smaller than the container horizontally it is centered on that axis.
        // Vertically, content is always top-aligned. Always writes the corrected matrix back to the
        // RenderTransform.
        // ###########################################################################################
        private void ClampSchematicsMatrix()
        {
            var containerSize = this.SchematicsContainer.Bounds.Size;
            if (containerSize.Width <= 0 || containerSize.Height <= 0)
                return;

            var contentRect = this.GetImageContentRect();
            double scale = this._schematicsMatrix.M11;
            double tx = this._schematicsMatrix.M31;
            double ty = this._schematicsMatrix.M32;

            var transformedRect = contentRect.TransformToAABB(this._schematicsMatrix);

            double scaledWidth = transformedRect.Width;
            double scaledHeight = transformedRect.Height;
            double scaledLeft = transformedRect.Left;
            double scaledTop = transformedRect.Top;
            double scaledRight = transformedRect.Right;
            double scaledBottom = transformedRect.Bottom;

            // Horizontal - prevent empty space; center if content is narrower than container
            if (scaledWidth >= containerSize.Width)
            {
                if (scaledLeft > 0) tx -= scaledLeft;
                else if (scaledRight < containerSize.Width) tx += containerSize.Width - scaledRight;
            }
            else
            {
                tx = (containerSize.Width - scaledWidth) / 2.0 - scale * contentRect.Left;
            }

            // Vertical - prevent empty space; top-align if content is shorter than container
            if (scaledHeight >= containerSize.Height)
            {
                if (scaledTop > 0) ty -= scaledTop;
                else if (scaledBottom < containerSize.Height) ty += containerSize.Height - scaledBottom;
            }
            else
            {
                ty = -(scale * contentRect.Top);
            }

            this._schematicsMatrix = new Matrix(scale, 0, 0, scale, tx, ty);
            ((MatrixTransform)this.SchematicsImage.RenderTransform!).Matrix = this._schematicsMatrix;
            ((MatrixTransform)this.SchematicsHighlightsOverlay.RenderTransform!).Matrix = this._schematicsMatrix;

            this.SchematicsHighlightsOverlay.ViewMatrix = this._schematicsMatrix;
            this.SchematicsHighlightsOverlay.InvalidateVisual();
        }

        // ###########################################################################################
        // Creates a pre-scaled bitmap from a full-resolution source image.
        // ###########################################################################################
        private static RenderTargetBitmap CreateScaledThumbnail(Bitmap source, int maxWidth)
        {
            double scale = Math.Min(1.0, (double)maxWidth / source.PixelSize.Width);
            int tw = Math.Max(1, (int)(source.PixelSize.Width * scale));
            int th = Math.Max(1, (int)(source.PixelSize.Height * scale));

            var imageControl = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform
            };
            imageControl.Measure(new Size(tw, th));
            imageControl.Arrange(new Rect(0, 0, tw, th));

            var rtb = new RenderTargetBitmap(new PixelSize(tw, th), new Vector(96, 96));
            rtb.Render(imageControl);
            return rtb;
        }

        // ###########################################################################################
        // Composites highlight rectangles onto a base thumbnail and returns the new rendered bitmap.
        // ###########################################################################################
        private static RenderTargetBitmap CreateHighlightedThumbnail(
    IImage baseThumbnail, PixelSize originalPixelSize,
    HighlightSpatialIndex index, BoardSchematicEntry schematic, double opacityMultiplier = 1.0)
        {
            int tw = 1, th = 1;
            if (baseThumbnail is RenderTargetBitmap rtb)
            {
                tw = rtb.PixelSize.Width;
                th = rtb.PixelSize.Height;
            }
            else if (baseThumbnail is Bitmap bmp)
            {
                tw = bmp.PixelSize.Width;
                th = bmp.PixelSize.Height;
            }

            var root = new Grid();

            var image = new Image
            {
                Source = baseThumbnail,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };

            var overlay = new SchematicHighlightsOverlay
            {
                HighlightIndex = index,
                BitmapPixelSize = originalPixelSize,
                ViewMatrix = Matrix.Identity,
                HighlightColor = ParseColorOrDefault(schematic.ThumbnailImageHighlightColor, Colors.IndianRed),
                HighlightOpacity = ParseOpacityOrDefault(schematic.ThumbnailHighlightOpacity, 0.20) * Math.Clamp(opacityMultiplier, 0.0, 1.0),
                IsHitTestVisible = false
            };

            root.Children.Add(image);
            root.Children.Add(overlay);

            root.Measure(new Size(tw, th));
            root.Arrange(new Rect(0, 0, tw, th));

            var result = new RenderTargetBitmap(new PixelSize(tw, th), new Vector(96, 96));
            result.Render(root);
            return result;
        }

        // ###########################################################################################
        // Handles mouse wheel zoom on the Schematics image, centered on the cursor position.
        // ###########################################################################################
        private void OnSchematicsZoom(object? sender, PointerWheelEventArgs e)
        {
            var pos = e.GetPosition(this.SchematicsImage);
            double delta = e.Delta.Y > 0 ? AppConfig.SchematicsZoomFactor : 1.0 / AppConfig.SchematicsZoomFactor;

            double newScale = this._schematicsMatrix.M11 * delta;

            if (newScale > AppConfig.SchematicsMaxZoom)
                return;

            if (newScale < AppConfig.SchematicsMinZoom)
            {
                this._schematicsMatrix = Matrix.Identity;
                ((MatrixTransform)this.SchematicsImage.RenderTransform!).Matrix = this._schematicsMatrix;
                ((MatrixTransform)this.SchematicsHighlightsOverlay.RenderTransform!).Matrix = this._schematicsMatrix;

                this.SchematicsHighlightsOverlay.ViewMatrix = this._schematicsMatrix;
                this.SchematicsHighlightsOverlay.InvalidateVisual();

                e.Handled = true;
                return;
            }

            // Build a zoom matrix centered at the cursor position in image-local space
            var zoomMatrix = Matrix.CreateTranslation(-pos.X, -pos.Y)
                           * Matrix.CreateScale(delta, delta)
                           * Matrix.CreateTranslation(pos.X, pos.Y);

            this._schematicsMatrix = zoomMatrix * this._schematicsMatrix;
            this.ClampSchematicsMatrix();

            e.Handled = true;
        }

        // ###########################################################################################
        // Handles right-click toggle on hovered component; otherwise right-click starts panning.
        // Left-click selects hovered component, and single-click opens the component info popup.
        // Double-click currently has no extra functionality.
        // ###########################################################################################
        private void OnSchematicsPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetPosition(this.SchematicsContainer);
            var pointer = e.GetCurrentPoint(this.SchematicsContainer);

            if (pointer.Properties.IsRightButtonPressed)
            {
                if (this.TryGetHoveredBoardLabel(point, out var hoveredBoardLabel, out _))
                {
                    this.ToggleComponentSelectionByBoardLabel(hoveredBoardLabel);
                    this.UpdateSchematicsHoverUi(point);
                    e.Handled = true;
                    return;
                }

                this._isPanning = true;
                this._panStartPoint = point;
                this._panStartMatrix = this._schematicsMatrix;
                this.SchematicsContainer.Cursor = new Cursor(StandardCursorType.SizeAll);
                this.HideSchematicsHoverUi();
                e.Pointer.Capture(this.SchematicsContainer);
                e.Handled = true;
                return;
            }

            if (pointer.Properties.IsLeftButtonPressed &&
                this.TryGetHoveredBoardLabel(point, out var boardLabel, out var displayText))
            {
                this.SelectComponentByBoardLabel(boardLabel);

                if (e.ClickCount == 1)
                    this.OpenComponentInfoPopup(boardLabel, displayText);

                e.Handled = true;
            }
        }

        // ###########################################################################################
        // Translates the schematics image while the right mouse button is held down.
        // ###########################################################################################
        private void OnSchematicsPointerMoved(object? sender, PointerEventArgs e)
        {
            var point = e.GetPosition(this.SchematicsContainer);

            if (this._isPanning)
            {
                var delta = point - this._panStartPoint;
                this._schematicsMatrix = this._panStartMatrix * Matrix.CreateTranslation(delta.X, delta.Y);
                this.ClampSchematicsMatrix();
                e.Handled = true;
                return;
            }

            this.UpdateSchematicsHoverUi(point);
        }

        // ###########################################################################################
        // Exits pan mode when the right mouse button is released.
        // ###########################################################################################
        private void OnSchematicsPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!this._isPanning)
                return;

            this._isPanning = false;
            e.Pointer.Capture(null);
            this.UpdateSchematicsHoverUi(e.GetPosition(this.SchematicsContainer));
            e.Handled = true;
        }

        // ###########################################################################################
        // Builds per-schematic, per-board-label highlight rect lookup from the loaded board data,
        // filtered by the active region. Used for on-demand highlighting when a component is selected.
        // ###########################################################################################
        private static Dictionary<string, Dictionary<string, List<Rect>>> BuildHighlightRects(BoardData boardData, string region)
        {
            var componentRegionsByLabel = boardData.Components
                .Where(c => !string.IsNullOrWhiteSpace(c.BoardLabel))
                .GroupBy(c => c.BoardLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(c => c.Region?.Trim() ?? string.Empty)
                          .Where(r => !string.IsNullOrWhiteSpace(r))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            bool IsVisibleByRegion(string boardLabel)
            {
                if (!componentRegionsByLabel.TryGetValue(boardLabel, out var regionsForLabel))
                    return true;

                // Empty region means "visible in all regions".
                if (regionsForLabel.Count == 0)
                    return true;

                return regionsForLabel.Any(r => string.Equals(r, region, StringComparison.OrdinalIgnoreCase));
            }

            var result = new Dictionary<string, Dictionary<string, List<Rect>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var h in boardData.ComponentHighlights)
            {
                if (string.IsNullOrWhiteSpace(h.SchematicName) || string.IsNullOrWhiteSpace(h.BoardLabel))
                    continue;

                if (!IsVisibleByRegion(h.BoardLabel))
                    continue;

                if (!TryParseDouble(h.X, out var x) ||
                    !TryParseDouble(h.Y, out var y) ||
                    !TryParseDouble(h.Width, out var w) ||
                    !TryParseDouble(h.Height, out var hh))
                    continue;

                if (w <= 0 || hh <= 0)
                    continue;

                if (!result.TryGetValue(h.SchematicName, out var byLabel))
                {
                    byLabel = new Dictionary<string, List<Rect>>(StringComparer.OrdinalIgnoreCase);
                    result[h.SchematicName] = byLabel;
                }

                if (!byLabel.TryGetValue(h.BoardLabel, out var rects))
                {
                    rects = [];
                    byLabel[h.BoardLabel] = rects;
                }

                rects.Add(new Rect(x, y, w, hh));
            }

            return result;
        }

        // ###########################################################################################
        // Handles component selection changes and drives highlight updates in both the main viewer
        // and all thumbnails.
        // ###########################################################################################
        private void OnComponentFilterSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (this._suppressComponentHighlightUpdate)
                return;

            var boardLabels = this.ComponentFilterListBox.SelectedItems?
                .Cast<ComponentListItem>()
                .Select(item => item.BoardLabel)
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList() ?? [];

            this.UpdateHighlightsForComponents(boardLabels);
        }

        // ###########################################################################################
        // Rebuilds highlight indices from the selected board labels, then applies highlight visuals
        // to the main schematic and all thumbnails. Also updates blink timer state.
        // ###########################################################################################
        private void UpdateHighlightsForComponents(List<string> boardLabels)
        {
            // Rebuild per-schematic highlight indices containing only the selected board labels
            this._highlightIndexBySchematic = new(StringComparer.OrdinalIgnoreCase);

            if (boardLabels.Count > 0)
            {
                foreach (var (schematicName, byLabel) in this._highlightRectsBySchematicAndLabel)
                {
                    var rects = new List<Rect>();
                    foreach (var label in boardLabels)
                    {
                        if (byLabel.TryGetValue(label, out var labelRects))
                            rects.AddRange(labelRects);
                    }

                    if (rects.Count > 0)
                        this._highlightIndexBySchematic[schematicName] = new HighlightSpatialIndex(rects);
                }
            }

            bool hasSelection = boardLabels.Count > 0;
            this.ApplyHighlightVisuals(hasSelection);
            this.UpdateBlinkTimer(hasSelection);
        }

        // ###########################################################################################
        // Parses a double using invariant culture for Excel-origin numeric text.
        // ###########################################################################################
        private static bool TryParseDouble(string text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // ###########################################################################################
        // Parses an Avalonia color string or returns a fallback.
        // ###########################################################################################
        private static Color ParseColorOrDefault(string text, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            try { return Color.Parse(text.Trim()); }
            catch { return fallback; }
        }

        // ###########################################################################################
        // Parses opacity; supports 0-1 and 0-100 (treated as percent). Returns fallback on failure.
        // ###########################################################################################
        private static double ParseOpacityOrDefault(string text, double fallback)
        {
            if (!TryParseDouble(text, out var v))
                return fallback;

            if (v > 1.0)
                v /= 100.0;

            return Math.Clamp(v, 0.0, 1.0);
        }

        // ###########################################################################################
        // Applies and persists the selected application theme dynamically.
        // ###########################################################################################
        private void OnThemeToggleSwitchChanged(object? sender, RoutedEventArgs e)
        {
            var isDark = this.ThemeToggleSwitch.IsChecked == true;
            var newVariant = isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;

            if (Application.Current != null)
            {
                Application.Current.RequestedThemeVariant = newVariant;
            }

            UserSettings.ThemeVariant = isDark ? "Dark" : "Light";
        }

        // ###########################################################################################
        // Persists the "Multiple instances for component popup" preference when the toggle is changed.
        // ###########################################################################################
        private void OnMultipleInstancesForComponentPopupChanged(object? sender, RoutedEventArgs e)
        {
            UserSettings.MultipleInstancesForComponentPopup = this.MultipleInstancesForComponentPopupToggleSwitch.IsChecked == true;
        }

        // ###########################################################################################
        // Persists the "Maximize component popup" preference when the toggle is changed.
        // ###########################################################################################
        private void OnMaximizeComponentPopupChanged(object? sender, RoutedEventArgs e)
        {
            UserSettings.MaximizeComponentPopup = this.MaximizeComponentPopupToggleSwitch.IsChecked == true;
        }

        // ###########################################################################################
        // Persists the "Check for new version at launch" preference when the checkbox is toggled.
        // ###########################################################################################
        private void OnCheckVersionOnLaunchChanged(object? sender, RoutedEventArgs e)
        {
            UserSettings.CheckVersionOnLaunch = this.CheckVersionOnLaunchCheckBox.IsChecked == true;
        }

        // ###########################################################################################
        // Persists the "Check for new or updated data at launch" preference when the checkbox is toggled.
        // ###########################################################################################
        private void OnCheckDataOnLaunchChanged(object? sender, RoutedEventArgs e)
        {
            UserSettings.CheckDataOnLaunch = this.CheckDataOnLaunchCheckBox.IsChecked == true;
        }

        // ###########################################################################################
        // Saves the selected category list for the current board whenever the user changes it.
        // Skipped during programmatic population to avoid overwriting a valid saved state.
        // ###########################################################################################
        private void OnCategoryFilterSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (this._suppressCategoryFilterSave)
                return;

            var boardKey = this.GetCurrentBoardKey();
            if (string.IsNullOrEmpty(boardKey))
                return;

            var selected = this.CategoryFilterListBox.SelectedItems?
                .Cast<string>()
                .ToList() ?? [];

            UserSettings.SetSelectedCategories(boardKey, selected);

            if (this._currentBoardData != null)
            {
                // Capture selected component keys before the list is rebuilt
                var previouslySelectedKeys = new HashSet<string>(
                    this.ComponentFilterListBox.SelectedItems?.Cast<ComponentListItem>()
                        .Select(i => i.SelectionKey) ?? [],
                    StringComparer.OrdinalIgnoreCase);

                var categoryFilter = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                var componentItems = BuildComponentItems(this._currentBoardData, UserSettings.Region, categoryFilter);

                // Suppress highlight updates during ItemsSource replacement and re-selection
                this._suppressComponentHighlightUpdate = true;
                this.ComponentFilterListBox.ItemsSource = componentItems;

                // Re-select only exact surviving rows
                for (int i = 0; i < componentItems.Count; i++)
                {
                    if (previouslySelectedKeys.Contains(componentItems[i].SelectionKey))
                        this.ComponentFilterListBox.Selection.Select(i);
                }
                this._suppressComponentHighlightUpdate = false;

                // Drive a single highlight update with only the surviving selected rows
                var survivingLabels = componentItems
                    .Where(item => previouslySelectedKeys.Contains(item.SelectionKey))
                    .Select(item => item.BoardLabel)
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();

                this.UpdateHighlightsForComponents(survivingLabels);
            }
        }

        // ###########################################################################################
        // Returns a composite key uniquely identifying the current hardware and board selection.
        // Used to store and retrieve per-board settings such as the schematics splitter position.
        // ###########################################################################################
        private string GetCurrentBoardKey()
        {
            var hw = this.HardwareComboBox.SelectedItem as string;
            var board = this.BoardComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(hw) || string.IsNullOrEmpty(board))
            {
                return string.Empty;
            }
            return $"{hw}|{board}";
        }

        // ###########################################################################################
        // Saves the left panel width after the main splitter drag ends.
        // Deferred via Post to ensure Bounds reflects the completed layout pass.
        // ###########################################################################################
        private void OnMainSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            Dispatcher.UIThread.Post(() => UserSettings.LeftPanelWidth = this.LeftPanel.Bounds.Width);
        }

        // ###########################################################################################
        // Saves the schematics/thumbnail split ratio for the current board after the drag ends.
        // Deferred via Post to ensure Bounds reflects the completed layout pass.
        // ###########################################################################################
        private void OnSchematicsSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            var boardKey = this.GetCurrentBoardKey();
            if (string.IsNullOrEmpty(boardKey))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                var leftWidth = this.SchematicsContainer.Bounds.Width;
                var rightWidth = this.SchematicsThumbnailList.Bounds.Width;
                var total = leftWidth + rightWidth;
                if (total <= 0)
                {
                    return;
                }
                UserSettings.SetSchematicsSplitterRatio(boardKey, leftWidth / total);
            });
        }

        // ###########################################################################################
        // On first open: validates the saved position is on a live screen (corrects to primary
        // if the monitor was disconnected), then subscribes to size, position, and state tracking.
        // ###########################################################################################
        private void OnWindowFirstOpened(object? sender, EventArgs e)
        {
            this.Opened -= this.OnWindowFirstOpened;

            if (UserSettings.HasWindowPlacement && this.WindowState == Avalonia.Controls.WindowState.Normal)
            {
                bool isOnScreen = this.Screens.All.Any(s =>
                    _restorePosition.X >= s.Bounds.X &&
                    _restorePosition.Y >= s.Bounds.Y &&
                    _restorePosition.X < s.Bounds.X + s.Bounds.Width &&
                    _restorePosition.Y < s.Bounds.Y + s.Bounds.Height);

                if (!isOnScreen)
                {
                    var primary = this.Screens.Primary;
                    if (primary != null)
                    {
                        this.Position = new PixelPoint(
                            primary.Bounds.X + Math.Max(0, (primary.Bounds.Width - (int)this.Width) / 2),
                            primary.Bounds.Y + Math.Max(0, (primary.Bounds.Height - (int)this.Height) / 2));
                    }
                }
            }

            // Save when the window is maximized, restored, or moved to another screen
            this.PropertyChanged += (s, args) =>
            {
                if (args.Property == Window.WindowStateProperty)
                {
                    this.ScheduleWindowPlacementSave();
                }
            };

            this.PositionChanged += this.OnWindowPositionChanged;
            this.SizeChanged += this.OnWindowSizeChanged;
        }

        // ###########################################################################################
        // Tracks the window's position in Normal state and schedules a debounced save.
        // ###########################################################################################
        private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
        {
            if (this.WindowState == Avalonia.Controls.WindowState.Normal)
            {
                _restorePosition = e.Point;
                this.ScheduleWindowPlacementSave();
            }
        }

        // ###########################################################################################
        // Tracks the window's size in Normal state and schedules a debounced save.
        // ###########################################################################################
        private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (this.WindowState == Avalonia.Controls.WindowState.Normal)
            {
                _restoreWidth = e.NewSize.Width;
                _restoreHeight = e.NewSize.Height;
                this.ScheduleWindowPlacementSave();
            }
        }

        // ###########################################################################################
        // Resets and starts a 500 ms debounce timer; saves only after the window has been
        // idle for that period, avoiding a write on every pixel during resize or move.
        // ###########################################################################################
        private void ScheduleWindowPlacementSave()
        {
            if (_windowPlacementSaveTimer == null)
            {
                _windowPlacementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _windowPlacementSaveTimer.Tick += (s, e) =>
                {
                    _windowPlacementSaveTimer.Stop();
                    this.CommitWindowPlacement();
                };
            }

            _windowPlacementSaveTimer.Stop();
            _windowPlacementSaveTimer.Start();
        }

        // ###########################################################################################
        // Captures the current window state and screen, then persists to settings.
        // Called by the debounce timer and directly on close.
        // ###########################################################################################
        private void CommitWindowPlacement()
        {
            var state = this.WindowState == Avalonia.Controls.WindowState.Minimized
                ? Avalonia.Controls.WindowState.Normal
                : this.WindowState;

            var screen = this.Screens.All.FirstOrDefault(s =>
                this.Position.X >= s.Bounds.X &&
                this.Position.Y >= s.Bounds.Y &&
                this.Position.X < s.Bounds.X + s.Bounds.Width &&
                this.Position.Y < s.Bounds.Y + s.Bounds.Height)
                ?? this.Screens.Primary;

            UserSettings.SaveWindowPlacement(
                state.ToString(),
                _restoreWidth,
                _restoreHeight,
                _restorePosition.X,
                _restorePosition.Y,
                screen?.Bounds.X ?? 0,
                screen?.Bounds.Y ?? 0,
                screen?.Bounds.Width ?? 1920,
                screen?.Bounds.Height ?? 1080,
                screen?.Scaling ?? 1.0);
        }

        // ###########################################################################################
        // Stops any pending debounce timer and does a final synchronous save on close.
        // ###########################################################################################
        private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            this._blinkSelectedTimer?.Stop();
            _windowPlacementSaveTimer?.Stop();
            this.CommitWindowPlacement();
        }

        // ###########################################################################################
        // Builds a distinct list of component categories in the order they first appear.
        // ###########################################################################################
        private static List<string> BuildDistinctCategories(BoardData boardData)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var categories = new List<string>();

            foreach (var component in boardData.Components)
            {
                if (!string.IsNullOrWhiteSpace(component.Category) && seen.Add(component.Category))
                    categories.Add(component.Category);
            }

            return categories;
        }

        // ###########################################################################################
        // Builds component list items filtered by the given region.
        // Each item carries the board label for highlight lookups and a display text assembled
        // from the non-empty parts: BoardLabel | FriendlyName | TechnicalNameOrValue.
        // Components with an empty Region column are always included regardless of the active region.
        // ###########################################################################################
        private static List<ComponentListItem> BuildComponentItems(BoardData boardData, string region, HashSet<string>? categoryFilter = null)
        {
            var items = new List<ComponentListItem>();

            foreach (var component in boardData.Components)
            {
                var componentRegion = component.Region?.Trim() ?? string.Empty;

                if (!string.IsNullOrEmpty(componentRegion) &&
                    !string.Equals(componentRegion, region, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (categoryFilter != null && !categoryFilter.Contains(component.Category ?? string.Empty))
                    continue;

                var parts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(component.BoardLabel))
                    parts.Add(component.BoardLabel.Trim());
                if (!string.IsNullOrWhiteSpace(component.FriendlyName))
                    parts.Add(component.FriendlyName.Trim());
                if (!string.IsNullOrWhiteSpace(component.TechnicalNameOrValue))
                    parts.Add(component.TechnicalNameOrValue.Trim());

                if (parts.Count == 0)
                    continue;

                items.Add(new ComponentListItem
                {
                    BoardLabel = component.BoardLabel?.Trim() ?? string.Empty,
                    DisplayText = string.Join(" | ", parts),
                    SelectionKey = string.Join("\u001F",
                        component.BoardLabel?.Trim() ?? string.Empty,
                        component.FriendlyName?.Trim() ?? string.Empty,
                        component.TechnicalNameOrValue?.Trim() ?? string.Empty,
                        component.Region?.Trim() ?? string.Empty)
                });
            }

            return items;
        }

        // ###########################################################################################
        // Lightweight view model for a component list item — carries the board label for
        // highlight lookups alongside the display text shown in the UI.
        // ###########################################################################################
        private sealed class ComponentListItem
        {
            public string DisplayText { get; init; } = string.Empty;
            public string BoardLabel { get; init; } = string.Empty;
            public string SelectionKey { get; init; } = string.Empty;
            public override string ToString() => this.DisplayText;
        }

        // ###########################################################################################
        // Builds component list items filtered by the given region.
        // Each item carries the board label for highlight lookups and a display text assembled
        // from the non-empty parts: BoardLabel | FriendlyName | TechnicalNameOrValue.
        // Components with an empty Region column are always included regardless of the active region.
        // ###########################################################################################
        private static List<ComponentListItem> BuildComponentItems(BoardData boardData, string region)
        {
            var items = new List<ComponentListItem>();

            foreach (var component in boardData.Components)
            {
                var componentRegion = component.Region?.Trim() ?? string.Empty;

                if (!string.IsNullOrEmpty(componentRegion) &&
                    !string.Equals(componentRegion, region, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(component.BoardLabel))
                    parts.Add(component.BoardLabel.Trim());
                if (!string.IsNullOrWhiteSpace(component.FriendlyName))
                    parts.Add(component.FriendlyName.Trim());
                if (!string.IsNullOrWhiteSpace(component.TechnicalNameOrValue))
                    parts.Add(component.TechnicalNameOrValue.Trim());

                if (parts.Count == 0)
                    continue;

                items.Add(new ComponentListItem
                {
                    BoardLabel = component.BoardLabel?.Trim() ?? string.Empty,
                    DisplayText = string.Join(" | ", parts),
                    SelectionKey = string.Join("\u001F",
                        component.BoardLabel?.Trim() ?? string.Empty,
                        component.FriendlyName?.Trim() ?? string.Empty,
                        component.TechnicalNameOrValue?.Trim() ?? string.Empty,
                        component.Region?.Trim() ?? string.Empty)
                });
            }

            return items;
        }

        // ###########################################################################################
        // Opens the persistent AppData folder that contains the log and settings files.
        // ###########################################################################################
        private void OnOpenAppDataFolderClick(object? sender, RoutedEventArgs e)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directory = Path.Combine(appData, AppConfig.AppFolderName);

            try
            {
                Directory.CreateDirectory(directory);

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", directory);
                }
                else
                {
                    Process.Start("xdg-open", directory);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to open app data folder - [{directory}] - [{ex.Message}]");
            }
        }

        // ###########################################################################################
        // Populates About tab fields and loads changelog content from embedded assets.
        // ###########################################################################################
        private void PopulateAboutTab(Assembly assembly, string? versionString)
        {
            this.AboutAssemblyTitleText.Text = this.GetAssemblyTitle(assembly);
            this.AppVersionText.Text = versionString ?? "(unknown)";
            this.ChangelogTextBox.Text = this.LoadTextAsset("Assets/Changelog.txt");
        }

        // ###########################################################################################
        // Resolves assembly title from metadata, with a fallback to assembly name.
        // ###########################################################################################
        private string GetAssemblyTitle(Assembly assembly)
        {
            var titleAttribute = assembly.GetCustomAttribute<AssemblyTitleAttribute>();
            if (!string.IsNullOrWhiteSpace(titleAttribute?.Title))
                return titleAttribute.Title;

            return assembly.GetName().Name ?? "Classic Repair Toolbox";
        }

        // ###########################################################################################
        // Loads a text asset from Avalonia resources and returns the raw file content.
        // ###########################################################################################
        private string LoadTextAsset(string assetPath)
        {
            try
            {
                var assetUri = new Uri($"avares://Classic-Repair-Toolbox/{assetPath}");
                using var stream = AssetLoader.Open(assetUri);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load text asset - [{assetPath}] - [{ex.Message}]");
                return "Unable to load changelog.";
            }
        }

        // ###########################################################################################
        // Opens the configured URL in the system default browser.
        // ###########################################################################################
        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to open URL - [{url}] - [{ex.Message}]");
            }
        }

        // ###########################################################################################
        // Opens the GitHub project page from the About tab.
        // ###########################################################################################
        private void OnGitHubProjectPageClick(object? sender, RoutedEventArgs e)
        {
            this.OpenUrl("https://github.com/HovKlan-DH/Classic-Repair-Toolbox");
        }

        // ###########################################################################################
        // Opens the helper page from the About tab.
        // ###########################################################################################
        private void OnHelperPageClick(object? sender, RoutedEventArgs e)
        {
            this.OpenUrl("https://classic-repair-toolbox.dk");
        }

        // ###########################################################################################
        // Clears all currently selected items in the Component filter list box.
        // ###########################################################################################
        private void OnClearComponentsClick(object? sender, RoutedEventArgs e)
        {
            this.ComponentFilterListBox.SelectedItems?.Clear();
        }

        // ###########################################################################################
        // Selects all available items currently populated within the Component filter list box.
        // ###########################################################################################
        private void OnMarkAllComponentsClick(object? sender, RoutedEventArgs e)
        {
            this.ComponentFilterListBox.SelectAll();
        }

        // ###########################################################################################
        // Updates the PAL/NTSC buttons' active visual class to reflect selected region.
        // ###########################################################################################
        private void UpdateRegionButtonsState()
        {
            var isPal = string.Equals(UserSettings.Region, "PAL", StringComparison.OrdinalIgnoreCase);

            this.PalRegionButton.Classes.Set("active", isPal);
            this.NtscRegionButton.Classes.Set("active", !isPal);
        }

        // ###########################################################################################
        // Switches to PAL, updates visually, and synchronously adjusts the filter items and view.
        // ###########################################################################################
        private void OnPalRegionClick(object? sender, RoutedEventArgs e)
        {
            if (string.Equals(UserSettings.Region, "PAL", StringComparison.OrdinalIgnoreCase)) return;
            UserSettings.Region = "PAL";
            this.UpdateRegionButtonsState();
            _ = this.ApplyRegionFilterAsync();
        }

        // ###########################################################################################
        // Switches to NTSC, updates visually, and synchronously adjusts the filter items and view.
        // ###########################################################################################
        private void OnNtscRegionClick(object? sender, RoutedEventArgs e)
        {
            if (string.Equals(UserSettings.Region, "NTSC", StringComparison.OrdinalIgnoreCase)) return;
            UserSettings.Region = "NTSC";
            this.UpdateRegionButtonsState();
            _ = this.ApplyRegionFilterAsync();
        }

        // ###########################################################################################
        // Refresh the component list according to the active region while recovering any matching
        // existing selection, similar to category filter switching.
        // ###########################################################################################
        private async Task ApplyRegionFilterAsync()
        {
            if (this._currentBoardData == null)
                return;

            // Rebuild highlight rectangles for just the targeted region out of the current board data
            this._highlightRectsBySchematicAndLabel = await Task.Run(() =>
                BuildHighlightRects(this._currentBoardData, UserSettings.Region));

            // Snapshot previously selected component keys
            var previouslySelectedKeys = new HashSet<string>(
                this.ComponentFilterListBox.SelectedItems?.Cast<ComponentListItem>()
                    .Select(i => i.SelectionKey) ?? [],
                StringComparer.OrdinalIgnoreCase);

            var activeCategories = new HashSet<string>(
                this.CategoryFilterListBox.SelectedItems?.Cast<string>() ?? [],
                StringComparer.OrdinalIgnoreCase);
            var componentItems = BuildComponentItems(this._currentBoardData, UserSettings.Region, activeCategories);

            // Re-populate ComponentFilterListBox contents
            this._suppressComponentHighlightUpdate = true;
            this.ComponentFilterListBox.ItemsSource = componentItems;

            for (int i = 0; i < componentItems.Count; i++)
            {
                if (previouslySelectedKeys.Contains(componentItems[i].SelectionKey))
                    this.ComponentFilterListBox.Selection.Select(i);
            }
            this._suppressComponentHighlightUpdate = false;

            var survivingLabels = componentItems
                .Where(item => previouslySelectedKeys.Contains(item.SelectionKey))
                .Select(item => item.BoardLabel)
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            this.UpdateHighlightsForComponents(survivingLabels);
        }

        // ###########################################################################################
        // Clears hover label and resets schematic cursor.
        // ###########################################################################################
        private void HideSchematicsHoverUi()
        {
            this.SchematicsHoverLabelBorder.IsVisible = false;
            this.SchematicsHoverLabelText.Text = string.Empty;
            this.SchematicsContainer.Cursor = Cursor.Default;
        }

        // ###########################################################################################
        // Clears hover UI when pointer exits schematic area.
        // ###########################################################################################
        private void OnSchematicsPointerExited(object? sender, PointerEventArgs e)
        {
            if (this._isPanning)
                return;

            this.HideSchematicsHoverUi();
        }

        // ###########################################################################################
        // Updates hover label/cursor from current pointer position.
        // ###########################################################################################
        private void UpdateSchematicsHoverUi(Point pointerInContainer)
        {
            if (this.TryGetHoveredBoardLabel(pointerInContainer, out _, out var displayText))
            {
                this.SchematicsContainer.Cursor = new Cursor(StandardCursorType.Hand);
                this.SchematicsHoverLabelText.Text = displayText;
                this.SchematicsHoverLabelBorder.IsVisible = true;
                return;
            }

            this.HideSchematicsHoverUi();
        }

        // ###########################################################################################
        // Resolves hovered board label and the exact text shown in component selector.
        // Includes components that are visible in the selector even when not selected/highlighted.
        // ###########################################################################################
        private bool TryGetHoveredBoardLabel(Point pointerInContainer, out string boardLabel, out string displayText)
        {
            boardLabel = string.Empty;
            displayText = string.Empty;

            if (this._currentFullResBitmap == null)
                return false;

            var selectedThumb = this.SchematicsThumbnailList.SelectedItem as SchematicThumbnail;
            if (selectedThumb == null)
                return false;

            if (!this._highlightRectsBySchematicAndLabel.TryGetValue(selectedThumb.Name, out var byLabel))
                return false;

            if (!TryInvert(this._schematicsMatrix, out var inv))
                return false;

            var localPoint = new Point(
                (pointerInContainer.X * inv.M11) + (pointerInContainer.Y * inv.M21) + inv.M31,
                (pointerInContainer.X * inv.M12) + (pointerInContainer.Y * inv.M22) + inv.M32);

            var contentRect = this.GetImageContentRect();
            if (contentRect.Width <= 0 || contentRect.Height <= 0 || !contentRect.Contains(localPoint))
                return false;

            double px = ((localPoint.X - contentRect.X) / contentRect.Width) * this._currentFullResBitmap.PixelSize.Width;
            double py = ((localPoint.Y - contentRect.Y) / contentRect.Height) * this._currentFullResBitmap.PixelSize.Height;
            var pixelPoint = new Point(px, py);

            // Use all currently visible component rows (not only selected rows).
            var visibleItems = this.ComponentFilterListBox.ItemsSource?.Cast<ComponentListItem>().ToList() ?? [];
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in visibleItems)
            {
                if (string.IsNullOrWhiteSpace(item.BoardLabel))
                    continue;

                if (!seenLabels.Add(item.BoardLabel))
                    continue;

                if (!byLabel.TryGetValue(item.BoardLabel, out var rects))
                    continue;

                if (!rects.Any(r => r.Contains(pixelPoint)))
                    continue;

                boardLabel = item.BoardLabel;
                displayText = item.DisplayText;
                return true;
            }

            return false;
        }

        // ###########################################################################################
        // Selects first component row matching board label and scrolls it into view.
        // ###########################################################################################
        private void SelectComponentByBoardLabel(string boardLabel)
        {
            var items = this.ComponentFilterListBox.ItemsSource?.Cast<ComponentListItem>().ToList() ?? [];
            int index = items.FindIndex(i => string.Equals(i.BoardLabel, boardLabel, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            this.ComponentFilterListBox.Selection.Select(index);
            this.ComponentFilterListBox.ScrollIntoView(items[index]);
        }

        // ###########################################################################################
        // Tries to invert a 2D affine matrix.
        // ###########################################################################################
        private static bool TryInvert(Matrix m, out Matrix inv)
        {
            double a = m.M11, b = m.M12, c = m.M21, d = m.M22, e = m.M31, f = m.M32;
            double det = (a * d) - (b * c);

            if (Math.Abs(det) < 1e-12)
            {
                inv = Matrix.Identity;
                return false;
            }

            double idet = 1.0 / det;

            double na = d * idet;
            double nb = -b * idet;
            double nc = -c * idet;
            double nd = a * idet;

            double ne = -((e * na) + (f * nc));
            double nf = -((e * nb) + (f * nd));

            inv = new Matrix(na, nb, nc, nd, ne, nf);
            return true;
        }

        // ###########################################################################################
        // Deselects all component rows that match the given board label.
        // ###########################################################################################
        private void DeselectComponentByBoardLabel(string boardLabel)
        {
            var items = this.ComponentFilterListBox.ItemsSource?.Cast<ComponentListItem>().ToList() ?? [];
            if (items.Count == 0)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].BoardLabel, boardLabel, StringComparison.OrdinalIgnoreCase))
                    this.ComponentFilterListBox.Selection.Deselect(i);
            }
        }

        // ###########################################################################################
        // Opens a component info popup according to user settings:
        // - MultipleInstancesForComponentPopup: reuse per-component window (no duplicates) or single window
        // - MaximizeComponentPopup: maximized or normal window state
        // ###########################################################################################
        private void OpenComponentInfoPopup(string boardLabel, string displayText)
        {
            string componentKey = $"{boardLabel}\u001F{displayText}";

            if (UserSettings.MultipleInstancesForComponentPopup)
            {
                if (!this._componentInfoWindowsByKey.TryGetValue(componentKey, out var popup) || !popup.IsVisible)
                {
                    popup = new ComponentInfoWindow();
                    this._componentInfoWindowsByKey[componentKey] = popup;

                    popup.Closed += (_, _) =>
                    {
                        if (this._componentInfoWindowsByKey.TryGetValue(componentKey, out var existing) && ReferenceEquals(existing, popup))
                            this._componentInfoWindowsByKey.Remove(componentKey);
                    };
                }

                popup.SetComponent(boardLabel, displayText);
                popup.WindowState = UserSettings.MaximizeComponentPopup
                    ? Avalonia.Controls.WindowState.Maximized
                    : Avalonia.Controls.WindowState.Normal;

                if (!popup.IsVisible)
                    popup.Show();
                else
                    popup.Activate();

                return;
            }

            if (this._singleComponentInfoWindow == null)
            {
                this._singleComponentInfoWindow = new ComponentInfoWindow();
                this._singleComponentInfoWindow.Closed += (_, _) => this._singleComponentInfoWindow = null;
            }

            this._singleComponentInfoWindow.SetComponent(boardLabel, displayText);
            this._singleComponentInfoWindow.WindowState = UserSettings.MaximizeComponentPopup
                ? Avalonia.Controls.WindowState.Maximized
                : Avalonia.Controls.WindowState.Normal;

            if (!this._singleComponentInfoWindow.IsVisible)
                this._singleComponentInfoWindow.Show();
            else
                this._singleComponentInfoWindow.Activate();
        }

        // ###########################################################################################
        // Lightweight popup window that shows component information text.
        // ###########################################################################################
        // ###########################################################################################
        // Lightweight popup window that shows component information text.
        // ###########################################################################################
        private sealed class ComponentInfoWindow : Window
        {
            private readonly TextBlock _titleText;
            private readonly TextBox _infoText;

            public ComponentInfoWindow()
            {
                this.Title = "Component Information";
                this.Width = 680;
                this.Height = 420;
                this.MinWidth = 420;
                this.MinHeight = 260;

                this._titleText = new TextBlock
                {
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                this._infoText = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };

                ScrollViewer.SetVerticalScrollBarVisibility(this._infoText, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(this._infoText, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

                var infoBorder = new Border
                {
                    Padding = new Thickness(8),
                    Child = this._infoText
                };
                Grid.SetRow(infoBorder, 1);

                this.Content = new Grid
                {
                    Margin = new Thickness(12),
                    RowDefinitions = new RowDefinitions("Auto,*"),
                    Children =
            {
                this._titleText,
                infoBorder
            }
                };

                this.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        this.Close();
                    }
                };
            }

            // ###########################################################################################
            // Updates popup content with the currently targeted component.
            // ###########################################################################################
            public void SetComponent(string boardLabel, string displayText)
            {
                this._titleText.Text = displayText;
                this._infoText.Text = $"Board label: {boardLabel}{Environment.NewLine}{Environment.NewLine}{displayText}";
            }
        }

        // ###########################################################################################
        // Toggles selection for a component board label:
        // if any matching row is selected, all matching rows are deselected; otherwise first is selected.
        // ###########################################################################################
        private void ToggleComponentSelectionByBoardLabel(string boardLabel)
        {
            bool hasSelectedMatch = this.ComponentFilterListBox.SelectedItems?
                .Cast<ComponentListItem>()
                .Any(i => string.Equals(i.BoardLabel, boardLabel, StringComparison.OrdinalIgnoreCase)) ?? false;

            if (hasSelectedMatch)
            {
                this.DeselectComponentByBoardLabel(boardLabel);
                return;
            }

            this.SelectComponentByBoardLabel(boardLabel);
        }

        // ###########################################################################################
        // Handles "Blink selected" checkbox changes and refreshes highlight visuals immediately.
        // ###########################################################################################
        private void OnBlinkSelectedChanged(object? sender, RoutedEventArgs e)
        {
            this._blinkSelectedEnabled = this.BlinkSelectedCheckBox.IsChecked == true;

            bool hasSelection = this._highlightIndexBySchematic.Count > 0;

            if (this._blinkSelectedEnabled && hasSelection)
            {
                // Start with hidden highlights immediately (no initial delay).
                this._blinkSelectedPhaseVisible = false;
                this.ApplyHighlightVisuals(true);
                this.UpdateBlinkTimer(true);
                return;
            }

            // When disabled (or no selection), force visible state and stop blinking.
            this._blinkSelectedPhaseVisible = true;
            this.UpdateBlinkTimer(hasSelection);
            this.ApplyHighlightVisuals(hasSelection);
        }

        // ###########################################################################################
        // Applies current highlight visuals (including blink phase) to main schematic and thumbnails.
        // ###########################################################################################
        private void ApplyHighlightVisuals(bool hasSelection)
        {
            double blinkFactor = this.GetCurrentBlinkFactor(hasSelection);

            var selectedThumb = this.SchematicsThumbnailList.SelectedItem as SchematicThumbnail;
            if (selectedThumb != null &&
                this._highlightIndexBySchematic.TryGetValue(selectedThumb.Name, out var mainIndex) &&
                this._schematicByName.TryGetValue(selectedThumb.Name, out var mainSchematic))
            {
                this.SchematicsHighlightsOverlay.HighlightIndex = mainIndex;
                this.SchematicsHighlightsOverlay.BitmapPixelSize = this._currentFullResBitmap?.PixelSize ?? new PixelSize(0, 0);
                this.SchematicsHighlightsOverlay.HighlightColor = ParseColorOrDefault(mainSchematic.MainImageHighlightColor, Colors.IndianRed);
                this.SchematicsHighlightsOverlay.HighlightOpacity = ParseOpacityOrDefault(mainSchematic.MainHighlightOpacity, 0.20) * blinkFactor;
            }
            else
            {
                this.SchematicsHighlightsOverlay.HighlightIndex = null;
            }

            this.SchematicsHighlightsOverlay.InvalidateVisual();

            foreach (var thumb in this._currentThumbnails)
            {
                if (thumb.BaseThumbnail == null)
                    continue;

                bool hasMatch = false;

                if (this._highlightIndexBySchematic.TryGetValue(thumb.Name, out var thumbIndex) &&
                    this._schematicByName.TryGetValue(thumb.Name, out var thumbSchematic))
                {
                    hasMatch = true;
                    var highlighted = CreateHighlightedThumbnail(thumb.BaseThumbnail, thumb.OriginalPixelSize, thumbIndex, thumbSchematic, blinkFactor);
                    var old = thumb.ImageSource;
                    thumb.ImageSource = highlighted;
                    if (!ReferenceEquals(old, thumb.BaseThumbnail))
                        (old as IDisposable)?.Dispose();
                }
                else
                {
                    if (!ReferenceEquals(thumb.ImageSource, thumb.BaseThumbnail))
                    {
                        var old = thumb.ImageSource;
                        thumb.ImageSource = thumb.BaseThumbnail;
                        (old as IDisposable)?.Dispose();
                    }
                }

                bool isRelevantForDimming = !hasSelection || hasMatch;
                thumb.VisualOpacity = isRelevantForDimming ? 1.0 : 0.35;
                thumb.IsMatchForSelection = hasSelection && hasMatch;
            }
        }

        // ###########################################################################################
        // Starts or stops the blink timer depending on current checkbox state and selection state.
        // ###########################################################################################
        private void UpdateBlinkTimer(bool hasSelection)
        {
            bool shouldBlink = this._blinkSelectedEnabled && hasSelection;

            if (!shouldBlink)
            {
                this._blinkSelectedTimer?.Stop();
                this._blinkSelectedPhaseVisible = true;
                return;
            }

            if (this._blinkSelectedTimer == null)
            {
                this._blinkSelectedTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(450)
                };
                this._blinkSelectedTimer.Tick += this.OnBlinkSelectedTimerTick;
            }

            if (!this._blinkSelectedTimer.IsEnabled)
                this._blinkSelectedTimer.Start();
        }

        // ###########################################################################################
        // Advances blink phase and re-applies highlight visuals while selection exists.
        // ###########################################################################################
        private void OnBlinkSelectedTimerTick(object? sender, EventArgs e)
        {
            bool hasSelection = this._highlightIndexBySchematic.Count > 0;
            if (!hasSelection)
            {
                this.UpdateBlinkTimer(false);
                this.ApplyHighlightVisuals(false);
                return;
            }

            this._blinkSelectedPhaseVisible = !this._blinkSelectedPhaseVisible;
            this.ApplyHighlightVisuals(true);
        }

        // ###########################################################################################
        // Computes effective blink multiplier for current frame.
        // ###########################################################################################
        private double GetCurrentBlinkFactor(bool hasSelection)
        {
            if (!hasSelection || !this._blinkSelectedEnabled)
                return 1.0;

            return this._blinkSelectedPhaseVisible ? 1.0 : 0.0;
        }

    }
}