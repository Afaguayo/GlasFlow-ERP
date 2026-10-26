using System.Windows;
using GlasFlow.Desktop.ViewModels;

namespace GlasFlow.Desktop.Views;

public partial class LoginWindow : Window
{
    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    // PasswordBox cannot be data-bound; this is the only code-behind a view needs.
    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) vm.Password = PasswordInput.Password;
    }
}
