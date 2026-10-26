using System.Windows;

namespace GlasFlow.Desktop.Infrastructure;

public static class Dialogs
{
    /// <summary>Shows the view model in a modal window (its DataTemplate picks the content) and returns the dialog result.</summary>
    public static bool Show(DialogViewModel vm)
    {
        var window = new Views.DialogWindow { DataContext = vm, Title = vm.DialogTitle };
        if (Application.Current.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, window)) window.Owner = owner;
        vm.CloseRequested += result =>
        {
            window.DialogResult = result;
        };
        return window.ShowDialog() == true;
    }
}
