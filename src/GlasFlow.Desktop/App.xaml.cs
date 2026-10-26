using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using GlasFlow.Desktop.Services;
using GlasFlow.Desktop.ViewModels;
using GlasFlow.Desktop.Views;

namespace GlasFlow.Desktop;

public partial class App : Application
{
    private ApiClient _api = null!;
    private Window? _current;
    private bool _handlingExpiry;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var culture = new CultureInfo("es-MX");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("es-MX")));

        _api = new ApiClient(Settings.ResolveApiUrl());

        if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunSmokeAsync();
            return;
        }

        _api.SessionExpired += () => Dispatcher.BeginInvoke(() =>
        {
            if (_handlingExpiry) return;
            _handlingExpiry = true;
            ShowLogin("Su sesión expiró. Inicie sesión de nuevo.");
        });
        ShowLogin(null);
    }

    private async Task RunSmokeAsync()
    {
        var code = await SmokeTest.RunAsync(_api);
        Shutdown(code);
    }

    private void ShowLogin(string? notice)
    {
        var vm = new LoginViewModel(_api, notice);
        var login = new LoginWindow(vm);
        vm.LoggedIn += session =>
        {
            _handlingExpiry = false;
            var main = new MainWindow
            {
                DataContext = new MainViewModel(_api, session, () =>
                {
                    _api.Logout();
                    ShowLogin(null);
                })
            };
            var old = _current;
            MainWindow = main;
            _current = main;
            main.Show();
            old?.Close();
        };
        var previous = _current;
        MainWindow = login;
        _current = login;
        login.Show();
        previous?.Close();
    }
}
