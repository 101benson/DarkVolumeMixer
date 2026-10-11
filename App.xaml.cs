using System;
using System.Threading;
using System.Windows;

namespace DarkVolumeMixer
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = @"Local\DarkVolumeMixer.SingleInstance";
        private static Mutex? _mutex;
        private static bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            _ownsMutex = createdNew;

            if (!createdNew)
            {
                System.Windows.MessageBox.Show("Der Dark Volume Mixer läuft bereits!", "Bereits gestartet",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Nur freigeben, wenn WIR den Mutex besitzen – sonst wirft ReleaseMutex eine Exception
            // (passierte beim Start einer zweiten Instanz).
            if (_ownsMutex)
            {
                try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
