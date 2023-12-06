global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodeiumVS;

[Guid(PackageGuids.CodeiumVSString)]
//[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideOptionPage(typeof(SettingsPage), "Codeium", "Codeium", 0, 0, true)]
[ProvideToolWindow(typeof(ChatToolWindow))]
public sealed class CodeiumVSPackage : ToolkitPackage
{
    public static CodeiumVSPackage? Instance = null;
    private NotificationInfoBar notificationAuth;
    private NotificationInfoBar notificationDownloading;

    public OutputWindow outputWindow;
    public SettingsPage settingsPage;
    public LanguageServer langServer;


    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await this.SatisfyImportsOnceAsync();

        Instance = this;
        langServer = new LanguageServer();
        outputWindow = new OutputWindow();
        settingsPage = (SettingsPage)GetDialogPage(typeof(SettingsPage));

        notificationAuth = new NotificationInfoBar(ServiceProvider.GlobalProvider);
        notificationDownloading = new NotificationInfoBar(ServiceProvider.GlobalProvider);

        await this.RegisterCommandsAsync();
        await langServer.InitializeAsync();

        await LogAsync("Codeium Extension for Visual Studio");

    }

    protected override void Dispose(bool disposing)
    {
        langServer.Dispose();
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
                new KeyValuePair<string, Action>("Sign in", delegate { _ = langServer.SignInAsync(); }),
                new KeyValuePair<string, Action>("Use authentication token", delegate { new EnterTokenDialogWindow().ShowDialog(); }),
            ];

            notificationAuth.Show("[Codeium] To enable Codeium, please sign in to your account", KnownMonikers.AddUser, true, null, actions);
        }
        else
        {
            await notificationAuth.CloseAsync();
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
        return Path.Combine(GetAppDataPath(), $"language_server_v{langServer.GetVersion()}");
    }

    public string GetLanguageServerPath()
    {
        return Path.Combine(GetLanguageServerFolder(), "language_server_windows_x64.exe");
    }

    public string GetAPIKeyPath()
    {
        return Path.Combine(GetLanguageServerFolder(), "codeium_api_key");
    }

    public bool IsSignedIn()                         { return langServer.GetKey().Length > 0; }
    public bool HasEnterprise()                      { return settingsPage.EnterpriseMode;    }

    internal void Log(string v)
    {
        // fix this ....
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await LogAsync(v);
        });
    }

    internal async Task LogAsync(string v)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        outputWindow.WriteLine(v);
    }
}


// https://gist.github.com/madskristensen/4d205244dd92c37c82e7
public static class MefExtensions
{
    private static IComponentModel _compositionService;

    public static async Task SatisfyImportsOnceAsync(this object o)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _compositionService ??= ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        _compositionService?.DefaultCompositionService.SatisfyImportsOnce(o);
    }
}
