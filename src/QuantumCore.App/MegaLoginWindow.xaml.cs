using System.Windows;

namespace QuantumCore.App;

public partial class MegaLoginWindow : Window
{
    public string Email => EmailBox.Text.Trim();
    public string Password => PasswordInput.Password;
    public string MfaCode => MfaBox.Text.Trim();

    public MegaLoginWindow()
    {
        InitializeComponent();
        EmailBox.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        {
            ErrorText.Text = "Enter both your MEGA email and password.";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}