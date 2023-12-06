using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using System.Collections.Generic;

namespace CodeiumVS;

#nullable enable
public class NotificationInfoBar : IVsInfoBarUIEvents
{

    private IVsInfoBarUIElement? view;

    private uint infoBarEventsCookie;

    private IVsInfoBarHost? vsInfoBarHost;

    public bool IsShown { get; private set; }

    private Action? OnCloseCallback { get; set; }

    public IVsInfoBarUIElement? View => view;

    public NotificationInfoBar()
    {
    }

    public void Show(string text, ImageMoniker? icon = null, bool canClose = true, Action? onCloseCallback = null, params KeyValuePair<string, Action>[] actions)
    {
        ThreadHelper.ThrowIfNotOnUIThread("Show");
        if (view != null) return;

        IVsShell vsShell = ServiceProvider.GlobalProvider.GetService<SVsShell, IVsShell>();
        IVsInfoBarUIFactory vsInfoBarFactory = ServiceProvider.GlobalProvider.GetService<SVsInfoBarUIFactory, IVsInfoBarUIFactory>();
        if (vsShell == null || vsInfoBarFactory == null) return;
        
        if (vsInfoBarFactory != null && ErrorHandler.Succeeded(vsShell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var pvar)) && pvar is IVsInfoBarHost vsInfoBarHost)
        {
            InfoBarModel infoBar = new(text, GetActionsItems(actions), icon ?? KnownMonikers.StatusInformation, canClose);
            
            view = vsInfoBarFactory.CreateInfoBar(infoBar);
            view.Advise(this, out infoBarEventsCookie);
            
            this.vsInfoBarHost = vsInfoBarHost;
            this.vsInfoBarHost.AddInfoBar(view);

            IsShown = true;
            OnCloseCallback = onCloseCallback;
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
            if (closeView)
            {
                view.Close();
            }
            view = null;
            IsShown = false;
            OnCloseCallback?.Invoke();
        }
    }

    private IEnumerable<IVsInfoBarActionItem> GetActionsItems(params KeyValuePair<string, Action>[] actions)
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

    void IVsInfoBarUIEvents.OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread("OnActionItemClicked");
        ((Action)actionItem.ActionContext)();
    }
}
#nullable disable