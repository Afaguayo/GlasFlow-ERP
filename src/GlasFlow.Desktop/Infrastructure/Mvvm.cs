using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace GlasFlow.Desktop.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Synchronous command (navigation, dialogs, paging).</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Async command: never blocks the UI thread and ignores re-entry while running.</summary>
public sealed class AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(parameter); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Base for screens: busy counter + error banner + a RunAsync wrapper that maps API errors to messages.</summary>
public abstract class ViewModelBase : ObservableObject
{
    private int _busy;
    private string? _error;

    public bool IsBusy => _busy > 0;
    public bool NotBusy => _busy == 0;

    public string? ErrorMessage
    {
        get => _error;
        set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>Runs <paramref name="work"/> with the busy flag set; errors go to <see cref="ErrorMessage"/>. Returns success.</summary>
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        _busy++;
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(NotBusy));
        CommandManager.InvalidateRequerySuggested();
        ErrorMessage = null;
        try { await work(); return true; }
        catch (Services.ApiException ex) { ErrorMessage = ex.Message; return false; }
        catch (OperationCanceledException) { ErrorMessage = "La operación excedió el tiempo de espera."; return false; }
        catch (Exception ex) { ErrorMessage = "Error inesperado: " + ex.Message; return false; }
        finally
        {
            _busy--;
            OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(NotBusy));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>A screen the main window can (re)load when it becomes active.</summary>
public interface IScreen
{
    string Title { get; }
    Task LoadAsync();
}

/// <summary>View model shown inside the generic dialog window.</summary>
public abstract class DialogViewModel : ViewModelBase
{
    public abstract string DialogTitle { get; }
    public event Action<bool>? CloseRequested;
    protected void Close(bool result) => CloseRequested?.Invoke(result);
}
