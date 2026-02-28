using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RRT
{
    // ###########################################################################################
    // Central configuration — all tunable application values are defined here.
    // Referenced by: OnlineServices, DataManager, UpdateService, Main, Logger.
    // ###########################################################################################
    public static class AppConfig
    {
        // ===== Debug ===============================================================================
        // Only referenced inside #if DEBUG blocks — ignored entirely in Release builds.

        // Enables online sync in DEBUG builds (normally skipped for faster development iteration).
        // Used by: DataManager.InitializeAsync
        public const bool DebugSimulateSync = true;

        // Simulates an available app update in DEBUG builds for UI testing.
        // Used by: UpdateService.CheckForUpdateAsync, UpdateService.PendingVersion
        public const bool DebugSimulateUpdate = true;

        // Fake version string shown in the update banner during debug update simulations.
        // Used by: UpdateService.PendingVersion
        public const string DebugSimulatedVersion = "99.0.0";

        // ===== App Identity ========================================================================

        // Short application code name used for User-Agent headers and API control payloads.
        // Used by: OnlineServices
        public const string AppShortName = "RRT";

        // Name of the local AppData subfolder used for data and log storage.
        // Used by: DataManager.ResolveDataRoot, Logger.Initialize
        public const string AppFolderName = "Retro-Repair-Toolbox";

        // Name of the log file written inside the AppFolderName directory.
        // Used by: Logger.Initialize
        public const string LogFileName = "Retro-Repair-Toolbox.log";
        // Name of the JSON file storing user preferences. Stored alongside the log file.
        // Used by: UserSettings.Load
        public const string SettingsFileName = "Retro-Repair-Toolbox.settings.json";

        // Name of the main Excel file containing all hardware and board definitions.
        // Used by: DataManager.InitializeAsync, DataManager.LoadMainExcel
        public const string MainExcelFileName = "Retro-Repair-Toolbox.xlsx";

        // ===== Online Services =====================================================================

        // URL to the JSON manifest listing all data files and their SHA-256 checksums.
        // Used by: OnlineServices.FetchManifestAsync
        public const string ChecksumsUrl = "https://retro-repair-toolbox.dk/app-data/dataChecksums.json";

        // URL for the phone-home version check endpoint.
        // Used by: OnlineServices.CheckVersionAsync
        public const string CheckVersionUrl = "https://retro-repair-toolbox.dk/app-checkin/";

        // Timeout for lightweight API calls (manifest fetch, version check).
        // Used by: OnlineServices.FetchManifestAsync, OnlineServices.CheckVersionAsync
        public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(5);

        // Timeout per individual file download — files can be large on slow connections.
        // Used by: OnlineServices.SyncFilesAsync
        public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

        // ===== GitHub Updates ======================================================================

        // GitHub repository owner used to check for application updates via Velopack.
        // Used by: UpdateService.CheckForUpdateAsync
        public const string GitHubOwner = "HovKlan-DH";

        // GitHub repository name used to check for application updates via Velopack.
        // Used by: UpdateService.CheckForUpdateAsync
        public const string GitHubRepo = "Retro-Repair-Toolbox";

        // Whether to include pre-release versions in the update check.
        // Used by: UpdateService.CheckForUpdateAsync
        public const bool IncludePreReleases = true;

        // ===== Schematics Viewer ==================================================================

        // Zoom multiplier applied per mouse wheel step.
        // Used by: Main.OnSchematicsZoom
        public const double SchematicsZoomFactor = 1.5;

        // Minimum allowed zoom level (1.0 = 100%).
        // Used by: Main.OnSchematicsZoom
        public const double SchematicsMinZoom = 0.9;

        // Maximum allowed zoom level.
        // Used by: Main.OnSchematicsZoom
        public const double SchematicsMaxZoom = 20.0;

        // Maximum pixel width used when pre-scaling schematic thumbnail images.
        // Used by: Main.OnBoardSelectionChanged, Main.CreateScaledThumbnail, Main.CreateScaledThumbnailWithHighlights
        public const int ThumbnailMaxWidth = 800;

        // Logical pixel size of the splash screen window, matching Splash.axaml Width/Height.
        // Used by: App.OnFrameworkInitializationCompleted to center the splash on the saved screen.
        public const int SplashWidth = 600;
        public const int SplashHeight = 350;
    }

    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        // ###########################################################################################
        // Shows the splash screen, initializes data (syncing with online source), then opens the main window.
        // ###########################################################################################
        public override async void OnFrameworkInitializationCompleted()
        {
            Logger.Initialize();
            UserSettings.Load();

            // Apply selected theme early
            if (UserSettings.ThemeVariant == "Dark")
                this.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            else if (UserSettings.ThemeVariant == "Light")
                this.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            else
                this.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Logger.Info(version != null
                ? $"Retro Repair Toolbox version [{version.Major}.{version.Minor}.{version.Build}] launched"
                : "Retro Repair Toolbox launched");

            var os = RuntimeInformation.OSDescription;
            Logger.Info($"Operating system is [{os}]");

            var archDescription = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "64-bit",
                Architecture.X86 => "32-bit",
                Architecture.Arm64 => "ARM 64-bit",
                Architecture.Arm => "ARM 32-bit",
                var a => a.ToString()
            };
            Logger.Info($"CPU architecture is [{archDescription}]");

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var splash = new Splash();

                // Place the splash on the same screen the main window will open on
                if (UserSettings.HasWindowPlacement)
                {
                    double scaling = UserSettings.WindowScreenScaling;
                    var centerX = UserSettings.WindowScreenX + UserSettings.WindowScreenWidth / 2;
                    var centerY = UserSettings.WindowScreenY + UserSettings.WindowScreenHeight / 2;
                    splash.WindowStartupLocation = WindowStartupLocation.Manual;
                    splash.Position = new PixelPoint(
                        centerX - (int)(AppConfig.SplashWidth * scaling / 2),
                        centerY - (int)(AppConfig.SplashHeight * scaling / 2));
                }

                desktop.MainWindow = splash;
                splash.Show();

                // Either use local data or sync it from online source
                await DataManager.InitializeAsync(desktop.Args ?? []);

                var main = new Main();
                desktop.MainWindow = main;
                main.Show();
                splash.Close();

                Logger.Info("Main window opened");

                if (UserSettings.CheckVersionOnLaunch)
                {
                    _ = OnlineServices.CheckVersionAsync();
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}