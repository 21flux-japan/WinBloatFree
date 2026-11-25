using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinBloatFree.Core;
using WinBloatFree.Platform;

namespace WinBloatFree;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var catalog = TweakCatalog.LoadEmbedded();
        var errors = CatalogValidator.Validate(catalog);
        if (errors.Count > 0)
        {
            MessageBox.Show("The built-in tweak list has problems:\n\n" + string.Join("\n", errors.Take(10)), "WinBloatFree", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var vm = new MainViewModel(new WindowsSystem(), catalog, new HistoryStore(HistoryStore.DefaultDirectory()));
        vm.IsDark = !SystemUsesLightTheme();
        ApplyTheme(vm.IsDark);
        var win = new MainWindow { DataContext = vm };
        MainWindow = win;
        win.Show();
        _ = vm.DetectAllAsync();
    }

    static bool SystemUsesLightTheme()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }

    /// <summary>Swaps the color dictionary. Every brush is used through DynamicResource, so the UI updates live.</summary>
    public static void ApplyTheme(bool dark)
    {
        if (Current is null) return;
        var dicts = Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        dicts[0] = theme;
        if (Current.MainWindow is MainWindow w) w.UpdateDarkTitleBar(dark);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Shown locally only. Nothing is reported anywhere.
        MessageBox.Show("Unexpected error: " + e.Exception.Message, "WinBloatFree", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
