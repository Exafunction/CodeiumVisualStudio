using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using System.Collections.Generic;

namespace CodeiumVS;

#nullable enable
public class NotificationInfoBar : IVsInfoBarUIEvents, IVsShellPropertyEvents
{

    private IVsInfoBarUIElement? view;

    private uint infoBarEventsCookie;
    private uint shellPropertyEventsCookie;

    private IVsShell? _vsShell;
    private IVsInfoBarHost? vsInfoBarHost;
    private IVsInfoBarUIFactory? _vsInfoBarFactory;

    public bool IsShown { get; private set; }

    private Action? OnCloseCallback { get; set; }

    public IVsInfoBarUIElement? View => view;

    public static readonly KeyValuePair<string, Action>[] SupportActions = [
        new KeyValuePair<string, Action>(
            "Ask for support on Discord",
            delegate { CodeiumVSPackage.OpenInBrowser("https://discord.gg/3XFf78nAx5"); }),
        new KeyValuePair<string, Action>(
            "Report issue on GitHub",
            delegate {
                CodeiumVSPackage.OpenInBrowser(
                    "https://github.com/Exafunction/CodeiumVisualStudio/issues/new");
            }),
    ];

    public NotificationInfoBar() {}

    public void Show(string text, ImageMoniker? icon = null, bool canClose = true,
                     Action? onCloseCallback = null, params KeyValuePair<string, Action>[] actions)
    {
        ThreadHelper.ThrowIfNotOnUIThread("Show");
        if (view != null) return;

        try
        {
            _vsShell = ServiceProvider.GlobalProvider.GetService<SVsShell, IVsShell>();
            _vsInfoBarFactory = ServiceProvider.GlobalProvider
                                    .GetService<SVsInfoBarUIFactory, IVsInfoBarUIFactory>();
            if (_vsShell == null || _vsInfoBarFactory == null) return;

            InfoBarModel infoBar = new(
                text, GetActionsItems(actions), icon ?? KnownMonikers.StatusInformation, canClose);

            view = _vsInfoBarFactory.CreateInfoBar(infoBar);
            view.Advise(this, out infoBarEventsCookie);

            if (ErrorHandler.Succeeded(_vsShell.GetProperty(
                    (int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var pvar)))
            {
                if (pvar is IVsInfoBarHost vsInfoBarHost)
                {
                    this.vsInfoBarHost = vsInfoBarHost;
                    this.vsInfoBarHost.AddInfoBar(view);
                }
            }
            else
            {
                // the MainWindowInfoBarHost has not been created yet, so we delay showing the
                // notification
                _vsShell.AdviseShellPropertyChanges(this, out shellPropertyEventsCookie);

                IsShown = true;
                OnCloseCallback = onCloseCallback;
            }
        }
        catch (Exception ex)
        {
            CodeiumVSPackage.Instance?.Log(
                $"NotificationInfoBar.Show: Failed to show notificaiton; Exception: {ex}");
            return;
        }
    }

    public async Task CloseAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Close(closeView: true);
    }

    private void Close(bool closeView = true)
    {
        ThreadHelper.ThrowIfNotOnUIThread("Close");
        if (view != null)
        {
            if (infoBarEventsCookie != 0)
            {
                view.Unadvise(infoBarEventsCookie);
                infoBarEventsCookie = 0u;
            }
            if (closeView) { view.Close(); }
            view = null;
            IsShown = false;
            OnCloseCallback?.Invoke();
        }
    }

    private IEnumerable<IVsInfoBarActionItem>
    GetActionsItems(params KeyValuePair<string, Action>[] actions)
    {
        ThreadHelper.ThrowIfNotOnUIThread("GetActionsItems");
        if (actions != null)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                KeyValuePair<string, Action> keyValuePair = actions[i];
                yield return new InfoBarHyperlink(keyValuePair.Key, keyValuePair.Value);
            }
        }
    }

    void IVsInfoBarUIEvents.OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread("OnClosed");
        Close(closeView: false);
    }

    void IVsInfoBarUIEvents.OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement,
                                                IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread("OnActionItemClicked");
        ((Action)actionItem.ActionContext)();
    }

    public int OnShellPropertyChange(int propid, object var)
    {
        ThreadHelper.ThrowIfNotOnUIThread("OnShellPropertyChange");

        // if (propid == (int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost) // for some reaons, this
        // doesn't work
        if (_vsShell?.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost,
                                  out var pvar) == VSConstants.S_OK)
        {
            _vsShell?.UnadviseShellPropertyChanges(shellPropertyEventsCookie);

            if (pvar is IVsInfoBarHost vsInfoBarHost)
            {
                this.vsInfoBarHost = vsInfoBarHost;
                this.vsInfoBarHost.AddInfoBar(view);
            }
        }

        return VSConstants.S_OK;
    }
}
#nullable disable