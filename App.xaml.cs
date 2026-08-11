using System.Windows;
using System.Threading;

namespace DarkVolumeMixer
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private const string MutexName = "DarkVolumeMixer_SingleInstance_Mutex_PC-PAPA";

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // Eindeutiger Aufruf der WPF-MessageBox
                System.Windows.MessageBox.Show("Der Dark Volume Mixer läuft bereits!", "Bereits gestartet", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            base.OnExit(e);
        }
    }
}