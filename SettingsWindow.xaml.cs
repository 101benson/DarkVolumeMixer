using System;
using System.Windows;
using System.Windows.Input;

namespace DarkVolumeMixer
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            ShowVersion();
            LoadSettings();
        }

        private void ShowVersion()
        {
            var v = typeof(SettingsWindow).Assembly.GetName().Version;
            if (v != null) VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        private void LoadSettings()
        {
            var s = AppSettings.Current;
            AlwaysOnTopCheckBox.IsChecked = s.IsAlwaysOnTop;
            AutoAdjustWidthCheckBox.IsChecked = s.AutoAdjustWidth;
            DiscordLimiterCheckBox.IsChecked = s.AutoVolumeDiscord;
            DiscordVolumeSlider.Value = s.DiscordVolumeValue;
            ProportionalMasterCheckBox.IsChecked = s.ProportionalMaster;
            ListLayoutCheckBox.IsChecked = s.ListLayout;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var s = AppSettings.Current;
            s.IsAlwaysOnTop = AlwaysOnTopCheckBox.IsChecked ?? false;
            s.AutoAdjustWidth = AutoAdjustWidthCheckBox.IsChecked ?? true;
            s.AutoVolumeDiscord = DiscordLimiterCheckBox.IsChecked ?? true;
            s.DiscordVolumeValue = (float)DiscordVolumeSlider.Value;
            s.ProportionalMaster = ProportionalMasterCheckBox.IsChecked ?? true;
            s.ListLayout = ListLayoutCheckBox.IsChecked ?? false;

            AppSettings.Save();

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }
}
