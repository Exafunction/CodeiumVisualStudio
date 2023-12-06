using Microsoft.VisualStudio.PlatformUI;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace CodeiumVS;

public class EnterTokenDialogWindow : DialogWindow
{
    public EnterTokenDialogWindow()
    {
        Title = "Authentication Token";
        Content = new EnterTokenDialogWindowControl();
        Width = 400;
        Height = 150;
        MinWidth = 400;
        MinHeight = 150;

        Application curApp = Application.Current;
        Window mainWindow = curApp.MainWindow;

        double left = mainWindow.WindowState == WindowState.Maximized ? 0 : mainWindow.Left;
        double top = mainWindow.WindowState == WindowState.Maximized ? 0 : mainWindow.Top;
        Left = left + (mainWindow.ActualWidth - Width) / 2;
        Top = top + (mainWindow.ActualHeight - Height) / 2;
    }
}
public partial class EnterTokenDialogWindowControl : UserControl
{
    public EnterTokenDialogWindowControl()
    {
        InitializeComponent();
    }

    private void BtnOKClicked(object sender, RoutedEventArgs e)
    {
        _= CodeiumVSPackage.Instance.langServer.SignInWithAuthTokenAsync(authTokenInput.Text);
        Window.GetWindow(this).Close();
    }
    private void BtnCancelClicked(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this).Close();
    }

    private void HelpLinkClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        SettingsPage settingsPage = CodeiumVSPackage.Instance.settingsPage;

        string state = Guid.NewGuid().ToString();
        string portalUrl = settingsPage.EnterpriseMode ? settingsPage.PortalUrl : "https://www.codeium.com";
        string redirectUrl = "show-auth-token";
        string url = $"{portalUrl}/profile?response_type=token&redirect_uri={redirectUrl}&state={state}&scope=openid%20profile%20email&redirect_parameters_type=query";

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
