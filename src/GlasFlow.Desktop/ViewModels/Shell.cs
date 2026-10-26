using System.Collections.ObjectModel;
using System.Windows.Input;
using GlasFlow.Desktop.Infrastructure;
using GlasFlow.Desktop.Services;

namespace GlasFlow.Desktop.ViewModels;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private string _username = "admin";
    private string _password = "";

    public LoginViewModel(ApiClient api, string? notice = null)
    {
        _api = api;
        if (notice is not null) ErrorMessage = notice;
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => NotBusy);
    }

    public string ApiUrl => _api.BaseUrl;
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public ICommand LoginCommand { get; }

    public event Action<LoginResponse>? LoggedIn;

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Capture usuario y contraseña.";
            return;
        }
        LoginResponse? session = null;
        if (await RunAsync(async () => session = await _api.LoginAsync(Username.Trim(), Password)) && session is not null)
            LoggedIn?.Invoke(session);
    }
}

public sealed class MainViewModel : ObservableObject
{
    private IScreen? _selected;

    public MainViewModel(ApiClient api, LoginResponse session, Action logout)
    {
        UserName = session.DisplayName;
        Role = session.Role;
        ApiUrl = api.BaseUrl;
        Screens =
        [
            new DashboardViewModel(api),
            new InventoryViewModel(api),
            new ProductsViewModel(api),
            new SalesOrdersViewModel(api)
        ];
        LogoutCommand = new RelayCommand(logout);
        Selected = Screens[0];
    }

    public string UserName { get; }
    public string Role { get; }
    public string ApiUrl { get; }
    public ObservableCollection<IScreen> Screens { get; }
    public ICommand LogoutCommand { get; }

    public IScreen? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(Current));
            if (value is not null) _ = value.LoadAsync();
        }
    }

    /// <summary>Same object as Selected, typed for the error banner / progress bar bindings.</summary>
    public ViewModelBase? Current => _selected as ViewModelBase;
}

/// <summary>Screen with a paged list: shared paging state and Prev/Next/Refresh commands.</summary>
public abstract class PagedScreen : ViewModelBase, IScreen
{
    private int _page = 1, _totalPages = 1, _totalCount;

    protected PagedScreen()
    {
        PrevCommand = new AsyncRelayCommand(async () => { Page--; await LoadAsync(); }, () => NotBusy && Page > 1);
        NextCommand = new AsyncRelayCommand(async () => { Page++; await LoadAsync(); }, () => NotBusy && Page < TotalPages);
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => NotBusy);
        SearchCommand = new AsyncRelayCommand(async () => { Page = 1; await LoadAsync(); }, () => NotBusy);
    }

    public abstract string Title { get; }
    public int Page { get => _page; protected set { if (SetProperty(ref _page, value)) OnPropertyChanged(nameof(PageText)); } }
    public int TotalPages => _totalPages;
    public string PageText => $"Página {Page} de {Math.Max(_totalPages, 1)}  ·  {_totalCount} registros";

    public ICommand PrevCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }

    protected abstract Task FetchAsync();
    public Task LoadAsync() => RunAsync(FetchAsync);

    protected void ApplyPaging<T>(PagedResult<T> result)
    {
        _totalCount = result.TotalCount;
        _totalPages = result.TotalPages > 0 ? result.TotalPages : Math.Max(1, (int)Math.Ceiling(result.TotalCount / (double)Math.Max(result.PageSize, 1)));
        Page = result.Page > 0 ? result.Page : Page;
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageText));
    }
}
