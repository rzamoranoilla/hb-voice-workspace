using System.Windows;
using System.Windows.Navigation;

namespace HB;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        if (!UserStore.HasUsers)
            IntroText.Text = "There are no users yet. Type a user name and a password, then press Create new user.";
        Loaded += (_, _) => UserBox.Focus();
    }

    /// <summary>The user that logged in, once the dialog closed with a positive result.</summary>
    public UserSession? Session { get; private set; }

    private void Login_Click(object sender, RoutedEventArgs e) =>
        Finish(UserStore.Login(UserBox.Text.Trim(), PasswordBox.Password, out var error), error);

    private void Create_Click(object sender, RoutedEventArgs e) =>
        Finish(UserStore.Create(UserBox.Text.Trim(), PasswordBox.Password, out var error), error);

    private void Finish(UserSession? session, string error)
    {
        if (session == null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Session = session;
        DialogResult = true;
    }

    private void Zamoilla_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Zamoilla.OpenSite();
        e.Handled = true;
    }
}
