using System.Windows;

namespace DraftLedger.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\DraftLedger.Desktop", out bool first);
        if (!first) { MessageBox.Show("DraftLedger is already open. Switch to the existing window.", "DraftLedger"); Shutdown(); return; }
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { MessageBox.Show($"DraftLedger could not open. Your writing files have not been removed.\n\n{ex.Message}", "DraftLedger", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
