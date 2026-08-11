using System.Windows;
using System.Windows.Input;

namespace DarkVolumeMixer
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadTemporarySettings();
        }

        private void LoadTemporarySettings()
        {
            AlwaysOnTopCheckBox.IsChecked = AppSettings.Current.IsAlwaysOnTop;
            AutoAdjustWidthCheckBox.IsChecked = AppSettings.Current.AutoAdjustWidth;
            DiscordLimiterCheckBox.IsChecked = AppSettings.Current.AutoVolumeDiscord;
            DiscordVolumeSlider.Value = AppSettings.Current.DiscordVolumeValue;
            
            // NEU: Laden
            ProportionalMasterCheckBox.IsChecked = AppSettings.Current.ProportionalMaster;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.IsAlwaysOnTop = AlwaysOnTopCheckBox.IsChecked ?? false;
            AppSettings.Current.AutoAdjustWidth = AutoAdjustWidthCheckBox.IsChecked ?? true;
            AppSettings.Current.AutoVolumeDiscord = DiscordLimiterCheckBox.IsChecked ?? true;
            AppSettings.Current.DiscordVolumeValue = (float)DiscordVolumeSlider.Value;
            
            // NEU: Speichern
            AppSettings.Current.ProportionalMaster = ProportionalMasterCheckBox.IsChecked ?? true;

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
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}