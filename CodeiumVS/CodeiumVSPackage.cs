﻿global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodeiumVS;

//[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
//[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)] //
// VisibilityConstraints example

[Guid(PackageGuids.CodeiumVSString)]
[InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideOptionPage(typeof(SettingsPage), "Windsurf", "Windsurf", 0, 0, true)]
[ProvideToolWindow(
    typeof(ChatToolWindow), MultiInstances = false, Style = VsDockStyle.Tabbed,
    Orientation = ToolWindowOrientation.Right,
    Window =
        "{3AE79031-E1BC-11D0-8F78-00A0C9110057}")] // default docking window, magic string for the
                                                   // guid of
                                                   // VSConstants.StandardToolWindows.SolutionExplorer
public sealed class CodeiumVSPackage : ToolkitPackage
{
    internal static CodeiumVSPackage? Instance { get; private set; }

    private NotificationInfoBar NotificationAuth;

    public OutputWindow OutputWindow;
    public SettingsPage SettingsPage;
    public LanguageServer LanguageServer;

    protected override async Task InitializeAsync(CancellationToken cancellationToken,
                                                  IProgress<ServiceProgressData> progress)
    {
        Instance = this;

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        LanguageServer = new LanguageServer();
        OutputWindow = new OutputWindow();
        NotificationAuth = new NotificationInfoBar();

        try
        {
            SettingsPage = GetDialogPage(typeof(SettingsPage)) as SettingsPage;
        }
        catch (Exception)
        {
        }

        if (SettingsPage == null)
        {
            await LogAsync(
                $"CodeiumVSPackage.InitializeAsync: Failed to get settings page, using the default settings");
            SettingsPage = new SettingsPage();
        }

        try
        {
            await this.RegisterCommandsAsync();
        }
        catch (Exception ex)
        {
            await LogAsync(
                $"CodeiumVSPackage.InitializeAsync: Failed to register commands; Exception {ex}");
            await VS.MessageBox.ShowErrorAsync(
                "Windsurf: Failed to register commands.",
                "Windsurf might not work correctly. Please check the output window for more details.");
        }

        try
        {
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            _ = CodeLensConnectionHandler.AcceptCodeLensConnections();
        }
        catch (Exception ex)
        {
            await LogAsync("Windsurf Error" + ex);
            throw;
        }

        await LanguageServer.InitializeAsync();
        await LogAsync("Windsurf Extension for Visual Studio");
    }

    protected override void Dispose(bool disposing)
    {
        LanguageServer.Dispose();
        base.Dispose(disposing);
    }

    public static void EnsurePackageLoaded()
    {
        if (Instance != null) return;

        ThreadHelper.JoinableTaskFactory.Run(EnsurePackageLoadedAsync);
    }

    public static async Task EnsurePackageLoadedAsync()
    {
        if (Instance != null) return;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        IVsShell vsShell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(IVsShell)) ??
                           throw new NullReferenceException();

        Guid guidPackage = new(PackageGuids.CodeiumVSString);
        if (vsShell.IsPackageLoaded(ref guidPackage, out var _) == VSConstants.S_OK) return;

        if (vsShell.LoadPackage(ref guidPackage, out var _) != VSConstants.S_OK)
            throw new NullReferenceException();
    }

    public async Task UpdateSignedInStateAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

        if ((await GetServiceAsync(typeof(IMenuCommandService)))is OleMenuCommandService cmdService)
        {
            MenuCommand? commandSignIn =
                cmdService.FindCommand(new CommandID(PackageGuids.CodeiumVS, PackageIds.SignIn));
            MenuCommand? commandSignOut =
                cmdService.FindCommand(new CommandID(PackageGuids.CodeiumVS, PackageIds.SignOut));
            MenuCommand? commandEnterToken = cmdService.FindCommand(
                new CommandID(PackageGuids.CodeiumVS, PackageIds.EnterAuthToken));

            if (commandSignIn != null) commandSignIn.Visible = !IsSignedIn();
            if (commandSignOut != null) commandSignOut.Visible = IsSignedIn();
            if (commandEnterToken != null) commandEnterToken.Visible = !IsSignedIn();
        }

        // notify the user they need to sign in
        if (!IsSignedIn())
        {
            KeyValuePair<string, Action>[] actions = [
                new KeyValuePair<string, Action>("Sign in",
                                                 delegate {
                                                     ThreadHelper.JoinableTaskFactory
                                                         .RunAsync(LanguageServer.SignInAsync)
                                                         .FireAndForget(true);
                                                 }),
                new KeyValuePair<string, Action>(
                    "Use authentication token",
                    delegate { new EnterTokenDialogWindow().ShowDialog(); }),
            ];

            NotificationAuth.Show("[Windsurf] To enable Windsurf, please sign in to your account",
                                  KnownMonikers.AddUser,
                                  true,
                                  null,
                                  actions);
        }
        else { await NotificationAuth.CloseAsync(); }

        ChatToolWindow.Instance?.Reload();
    }

    public static string GetDefaultBrowserPath()
    {
        // https://web.archive.org/web/20160304114550/http://www.seirer.net/blog/2014/6/10/solved-how-to-open-a-url-in-the-default-browser-in-csharp

        static string CleanifyBrowserPath(string p)
        {
            string[] url = p.Split('"');
            string clean = url[1];
            return clean;
        }

        string urlAssociation =
            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http";
        string browserPathKey = @"$BROWSER$\shell\open\command";
        try
        {
            // Read default browser path from userChoiceLKey
            RegistryKey userChoiceKey =
                Registry.CurrentUser.OpenSubKey(urlAssociation + @"\UserChoice", false);

            // If user choice was not found, try machine default
            if (userChoiceKey == null)
            {
                // Read default browser path from Win XP registry key
                var browserKey = Registry.ClassesRoot.OpenSubKey(@"HTTP\shell\open\command", false);

                // If browser path wasn’t found, try Win Vista (and newer) registry key
                browserKey ??= Registry.CurrentUser.OpenSubKey(urlAssociation, false);
                var path = CleanifyBrowserPath(browserKey.GetValue(null) as string);
                browserKey.Close();
                return path;
            }
            else
            {
                // user defined browser choice was found
                string progId = (userChoiceKey.GetValue("ProgId").ToString());
                userChoiceKey.Close();

                // now look up the path of the executable
                string concreteBrowserKey = browserPathKey.Replace("$BROWSER$", progId);
                var kp = Registry.ClassesRoot.OpenSubKey(concreteBrowserKey, false);
                string browserPath = CleanifyBrowserPath(kp.GetValue(null) as string);
                kp.Close();
                return browserPath;
            }
        }
        catch (Exception)
        {
            return "";
        }
    }

    // Try three different ways to open url in the default browser
    public static void OpenInBrowser(string url)
    {
        Action<string>[] methods = [
            (_url) =>
            {
                Process.Start(new ProcessStartInfo { FileName = _url, UseShellExecute = true });
            },
            (_url) =>
            { Process.Start("explorer.exe", _url); },
            (_url) =>
            { Process.Start(GetDefaultBrowserPath(), _url); }
        ];

        foreach (var method in methods)
        {
            try
            {
                method(url);
                return;
            }
            catch (Exception ex)
            {
                Instance?.Log(
                    $"Could not open in browser, encountered an exception: {ex}\n Retrying using another method");
            }
        }

        Instance?.Log($"Windsurf failed to open the browser, please use this URL instead: {url}");
        VS.MessageBox.Show(
            "Windsurf: Failed to open browser",
            $"Please use this URL instead (you can copy from the output window):\n{url}");
    }

    public string GetAppDataPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".codeium");
    }

    public string GetLanguageServerFolder()
    {
        return Path.Combine(GetAppDataPath(), $"language_server_v{LanguageServer.GetVersion()}");
    }

    public string GetLanguageServerPath()
    {
        string binaryName = "language_server_windows_x64.exe";
        if (SettingsPage.EnterpriseMode)
        {
            binaryName = "language_server_windows_x64_enterprise.exe";
        }
        return Path.Combine(GetLanguageServerFolder(), binaryName);
    }

    public string GetDatabaseDirectory() { return Path.Combine(GetAppDataPath(), "database"); }

    public string GetAPIKeyPath() { return Path.Combine(GetAppDataPath(), "codeium_api_key"); }

    public bool IsSignedIn() { return LanguageServer.GetKey().Length > 0; }
    public bool HasEnterprise() { return SettingsPage.EnterpriseMode; }

    internal void Log(string v)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate { await LogAsync(v); })
            .FireAndForget(true);
    }

    internal async Task LogAsync(string v)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        OutputWindow.WriteLine(v);
    }
}

// https://gist.github.com/madskristensen/4d205244dd92c37c82e7
// this increase load time idk why, not needed now
// public static class MefExtensions
//{
//    private static IComponentModel _compositionService;

//    public static async Task SatisfyImportsOnceAsync(this object o)
//    {
//        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
//        _compositionService ??= ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel))
//        as IComponentModel; _compositionService?.DefaultCompositionService.SatisfyImportsOnce(o);
//    }
//}
