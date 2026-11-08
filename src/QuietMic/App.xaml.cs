using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace QuietMic;

public partial class App : Application
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietMic");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(DataDirectory);
        try
        {
            ApplicationThemeManager.ApplySystemTheme();
        }
        catch
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "QuietMic 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
