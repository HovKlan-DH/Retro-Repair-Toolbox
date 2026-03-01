using System;
using System.Reflection;
using Avalonia.Controls;

namespace RRT
{
    public partial class Splash : Window
    {
        public Splash()
        {
            InitializeComponent();

            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var versionString = AppConfig.GetDisplayVersion(version);

            this.VersionNumberRun.Text = versionString;

            DataManager.StatusChanged += this.OnStatusChanged;
            DataManager.FileDownloadChanged += this.OnFileDownloadChanged;
        }

        // ###########################################################################################
        // Updates the status label whenever DataManager reports a general progress change.
        // ###########################################################################################
        private void OnStatusChanged(string status)
        {
            this.StatusLabel.Text = status;
        }

        // ###########################################################################################
        // Updates the file label with the path of the file currently being downloaded.
        // Hides the label automatically when the download batch finishes (empty string).
        // ###########################################################################################
        private void OnFileDownloadChanged(string filePath)
        {
            this.FileLabel.Text = filePath;
            this.FileLabel.IsVisible = !string.IsNullOrEmpty(filePath);
        }

        protected override void OnClosed(EventArgs e)
        {
            DataManager.StatusChanged -= this.OnStatusChanged;
            DataManager.FileDownloadChanged -= this.OnFileDownloadChanged;
            base.OnClosed(e);
        }
    }
}