global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodeiumVS;

//[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
//[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)] // VisibilityConstraints example

[Guid(PackageGuids.CodeiumVSString)]
[InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideOptionPage(typeof(SettingsPage), "Codeium", "Codeium", 0, 0, true)]
[ProvideToolWindow(typeof(ChatToolWindow),
    MultiInstances = false,
    Style = VsDockStyle.Tabbed, 
    Orientation = ToolWindowOrientation.Right,
    Window = "{3AE79031-E1BC-11D0-8F78-00A0C9110057}")] // default docking window, magic string for the guid of VSConstants.StandardToolWindows.SolutionExplorer
public sealed class CodeiumVSPackage : ToolkitPackage
{
    internal static CodeiumVSPackage? Instance { get; private set; }

    private NotificationInfoBar NotificationAuth;

    public OutputWindow OutputWindow;
    public SettingsPage SettingsPage;
    public LanguageServer LanguageServer;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Instance = this;

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        //await this.SatisfyImportsOnceAsync();

        LanguageServer = new LanguageServer();
        OutputWindow = new OutputWindow();
        SettingsPage = (SettingsPage)GetDialogPage(typeof(SettingsPage));
        NotificationAuth = new NotificationInfoBar();

        await this.RegisterCommandsAsync();
        await LanguageServer.InitializeAsync();
        await LogAsync("Codeium Extension for Visual Studio");
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
        IVsShell vsShell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(IVsShell)) ?? throw new NullReferenceException();

        Guid guidPackage = new(PackageGuids.CodeiumVSString);
        if (vsShell.IsPackageLoaded(ref guidPackage, out var _) == VSConstants.S_OK) return;

        if (vsShell.LoadPackage(ref guidPackage, out var _) != VSConstants.S_OK)
            throw new NullReferenceException();
    }

    public async Task UpdateSignedInStateAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

        OleMenuCommandService obj = (await GetServiceAsync(typeof(IMenuCommandService))) as OleMenuCommandService;
        obj.FindCommand(new CommandID(PackageGuids.CodeiumVS, PackageIds.SignIn)).Visible = !IsSignedIn();
        obj.FindCommand(new CommandID(PackageGuids.CodeiumVS, PackageIds.SignOut)).Visible = IsSignedIn();
        obj.FindCommand(new CommandID(PackageGuids.CodeiumVS, PackageIds.EnterAuthToken)).Visible = !IsSignedIn();

        // notify the user they need to sign in
        if (!IsSignedIn())
        {
            KeyValuePair<string, Action>[] actions = [
                new KeyValuePair<string, Action>("Sign in", delegate { _ = LanguageServer.SignInAsync(); }),
                new KeyValuePair<string, Action>("Use authentication token", delegate { new EnterTokenDialogWindow().ShowDialog(); }),
            ];

            NotificationAuth.Show("[Codeium] To enable Codeium, please sign in to your account", KnownMonikers.AddUser, true, null, actions);
        }
        else
        {
            await NotificationAuth.CloseAsync();
        }

        // find the ChatToolWindow and update it
        ChatToolWindow chatWindowPane = (await FindWindowPaneAsync(typeof(ChatToolWindow), 0, false, DisposalToken)) as ChatToolWindow;
        chatWindowPane?.Reload();
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
        return Path.Combine(GetLanguageServerFolder(), "language_server_windows_x64.exe");
    }

    public string GetDatabaseDirectory()
    {
        return Path.Combine(GetAppDataPath(), "database");
    }

    public string GetAPIKeyPath()
    {
        return Path.Combine(GetAppDataPath(), "codeium_api_key");
    }

    public bool IsSignedIn()                         { return LanguageServer.GetKey().Length > 0; }
    public bool HasEnterprise()                      { return SettingsPage.EnterpriseMode;    }

    internal void Log(string v)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            await LogAsync(v);
        }).FireAndForget(true);
    }

    internal async Task LogAsync(string v)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        OutputWindow.WriteLine(v);
    }
}


// https://gist.github.com/madskristensen/4d205244dd92c37c82e7
// this increase load time idk why, not needed now
//public static class MefExtensions
//{
//    private static IComponentModel _compositionService;

//    public static async Task SatisfyImportsOnceAsync(this object o)
//    {
//        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
//        _compositionService ??= ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
//        _compositionService?.DefaultCompositionService.SatisfyImportsOnce(o);
//    }
//}
