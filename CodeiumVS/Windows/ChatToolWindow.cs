using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Markup;

namespace CodeiumVS;

[Guid(PackageGuids.ChatToolWindowString)]
public class ChatToolWindow : ToolWindowPane
{
    internal static ChatToolWindow? Instance { get; private set; }

    public ChatToolWindow() : base(null)
    {
        Instance = this;
        Caption = "Codeium Chat";
        Content = new ChatToolWindowControl();
    }

    public void Reload()
    {
        ThreadHelper.JoinableTaskFactory.RunAsync((Content as ChatToolWindowControl).ReloadAsync)
            .FireAndForget();
    }
}

public partial class ChatToolWindowControl : UserControl, IComponentConnector
{
    private CodeiumVSPackage package;
    private bool _isInitialized = false;
    private bool _isChatPageLoaded = false;
    private string? _themeScriptId = null;
    private InfoBar? _infoBar = null;

    public ChatToolWindowControl()
    {
        InitializeComponent();
        ThreadHelper.JoinableTaskFactory.RunAsync(InitializeWebViewAsync).FireAndForget();
    }

    private async Task InitializeWebViewAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        package = CodeiumVSPackage.Instance;

        // set the default background color to avoid flashing a white window
        webView.DefaultBackgroundColor =
            VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundBrushKey);

        // create webview2 environment and load the webview
        string webviewDirectory = Path.Combine(package.GetAppDataPath(), "webview2");
        Directory.CreateDirectory(webviewDirectory);
        CoreWebView2Environment env =
            await CoreWebView2Environment.CreateAsync(null, webviewDirectory);
        try
        {
            // Try Catch this in case it's causing problems
            await webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            await package.LogAsync(
                $"Failed to initialize webview core enviroment. Exception: {ex}");
            await VS.MessageBox.ShowErrorAsync(
                "Codeium: Failed to initialize webview core enviroment",
                "Chat might be unavailable. Please see more details in the output window.");
        }

        _isInitialized = true;

        webView.CoreWebView2.DOMContentLoaded += WebView_OnDOMContentLoaded;
        webView.GotFocus += WebView_OnGotFocus;

        // load the loading page
        webView.NavigateToString(Properties.Resources.ChatLoadingPage_html);

        // add theme script to the list of scripts that get executed on document load
        await AddThemeScriptOnLoadAsync();

        // add the info bar to notify the user when the webview failed to load
        var model = new InfoBarModel(
            new[] {
                new InfoBarTextSpan(
                    "It looks like Codeium Chat is taking too long to load, do you want to reload? "),
                new InfoBarHyperlink("Reload")
            },
            KnownMonikers.IntellisenseWarning,
            true);

        _infoBar =
            await VS.InfoBar.CreateAsync(ChatToolWindow.Instance.Frame as IVsWindowFrame, model);
        if (_infoBar != null) _infoBar.ActionItemClicked += InfoBar_OnActionItemClicked;

        // listen for theme changes
        VSColorTheme.ThemeChanged += VSColorTheme_ThemeChanged;

        // load the chat
        await ReloadAsync();
    }

    /// <summary>
    /// Reload, or in another word, navigate to the chat web page
    /// </summary>
    /// <returns></returns>
    public async Task ReloadAsync()
    {
        if (!_isInitialized) return;

        _isChatPageLoaded = false;

        // check again in 10 seconds to see if the dom content was loaded
        // if it's not, show the info bar and ask the user if they want to
        // reload the page
        Task.Delay(10_000)
            .ContinueWith(
                (task) =>
                {
                    if (_isChatPageLoaded) return;
                    _infoBar?.TryShowInfoBarUIAsync().FireAndForget();
                },
                TaskScheduler.Default)
            .FireAndForget();

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // wait for the language server
        await package.LanguageServer.WaitReadyAsync();

        Packets.Metadata metadata = package.LanguageServer.GetMetadata();
        Packets.GetProcessesResponse gpr = await package.LanguageServer.GetProcessesAsync();

        string serverUrl = $"ws://127.0.0.1:{gpr.chatWebServerPort}";
        string clientUrl = $"http://127.0.0.1:{gpr.chatClientPort}";

        Dictionary<string, string> data =
            new() { { "api_key", metadata.api_key },
                    { "extension_name", metadata.extension_name },
                    { "extension_version", metadata.extension_version },
                    { "ide_name", metadata.ide_name },
                    { "ide_version", metadata.ide_version },
                    { "locale", metadata.locale },
                    { "ide_telemetry_enabled", "true" },
                    { "app_name", "Visual Studio" },
                    { "web_server_url", serverUrl },
                    { "has_dev_extension", "false" },
                    { "open_file_pointer_enabled", "true" },
                    { "diff_view_enabled", "true" },
                    { "insert_at_cursor_enabled", "true" },
                    { "has_enterprise_extension", package.HasEnterprise().ToString().ToLower() } };

        string uriString =
            clientUrl + "?" + string.Join("&", data.Select((pair) => $"{pair.Key}={pair.Value}"));

        try
        {
            if (webView.Source?.OriginalString == uriString)
                webView.Reload();
            else
                webView.Source = new Uri(uriString);
        }
        catch (Exception ex)
        {
            await package.LogAsync($"Failed to open the chat page. Exception: {ex}");
            await VS.MessageBox.ShowErrorAsync(
                "Codeium: Failed to open the chat page",
                "We're sorry for the inconvenience. Please see more details in the output window.");
        }
    }

    /// <summary>
    /// Activate the chat tool window and focus the text input when the webview is focused
    /// </summary>
    private void WebView_OnGotFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        ThreadHelper.JoinableTaskFactory
            .RunAsync(async delegate {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // the GotFocus event is fired event when the user is switching to another tab
                // i don't know why this happens, but this is a workaround

                // code taken from VS.Windows.GetCurrentWindowAsync
                IVsMonitorSelection? monitorSelection =
                    await VS.Services.GetMonitorSelectionAsync();
                monitorSelection.GetCurrentElementValue(
                    (uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out object selection);

                IVsWindowFrame chatFrame = ChatToolWindow.Instance.Frame as IVsWindowFrame;

                if (selection is IVsWindowFrame6 frame && frame != chatFrame &&
                    !frame.IsInSameTabGroup(chatFrame))
                {
                    chatFrame.Show();
                }

                // focus the text input
                await webView.ExecuteScriptAsync(
                    "document.getElementsByClassName('ql-editor')[0].focus()");
            })
            .FireAndForget();
    }

    /// <summary>
    /// Get the script to set the theme color to match that of Visual Studio.
    /// </summary>
    /// <returns></returns>
    private string GetSetThemeScript()
    {
        // System.Drawing.Color is ARGB, we need to convert it to RGBA for css
        static uint GetColor(ThemeResourceKey key)
        {
            var color = VSColorTheme.GetThemedColor(key);
            return (uint)(color.R << 24 | color.G << 16 | color.B << 8 | color.A);
        }

        uint textColor = GetColor(EnvironmentColors.ToolWindowTextBrushKey);

        return $@"
            var style = document.getElementById(""vs-code-theme"");
            if (style == null)
            {{
                style = document.createElement('style');
                style.id = ""vs-code-theme"";
                document.head.appendChild(style);
            }}

            style.textContent = `
            body {{

                /* window background */
	            --vscode-editor-background:                #{GetColor(CommonControlsColors.ComboBoxBackgroundBrushKey):x8};
	            --vscode-editor-foreground:                #{textColor:x8};
	            --vscode-sideBar-background:               #{GetColor(EnvironmentColors.ToolWindowBackgroundBrushKey):x8};
	            --vscode-foreground:                       #{textColor:x8};

                 /* user message block */
	            --vscode-list-activeSelectionBackground:   #{GetColor(EnvironmentColors.VizSurfaceSteelBlueMediumBrushKey):x8};
	            --vscode-list-hoverBackground:             #{GetColor(CommonControlsColors.ComboBoxBackgroundHoverBrushKey):x8};
                --vscode-list-activeSelectionForeground:   #{textColor:x8};

                 /* bot message block */
	            --vscode-list-inactiveSelectionBackground: #{GetColor(CommonControlsColors.ComboBoxBackgroundDisabledBrushKey):x8};

                 /* textbox input */
	            --vscode-input-background:                 #{GetColor(CommonControlsColors.TextBoxBackgroundBrushKey):x8};
	            --vscode-input-foreground:                 #{GetColor(CommonControlsColors.TextBoxTextBrushKey):x8};
	            --vscode-input-placeholderForeground:      #{GetColor(CommonControlsColors.TextBoxTextDisabledBrushKey):x8};

                /* border */
	            --vscode-contrastBorder:                   #{GetColor(CommonDocumentColors.ListItemBorderFocusedBrushKey):x8};
	            --vscode-focusBorder:                      #{GetColor(CommonControlsColors.ButtonBorderBrushKey):x8};

                /* scroll bar */
	            --vscode-scrollbarSlider-background:       #{GetColor(EnvironmentColors.ScrollBarThumbBackgroundBrushKey):x8};
	            --vscode-scrollbarSlider-hoverBackground:  #{GetColor(EnvironmentColors.ScrollBarThumbMouseOverBackgroundBrushKey):x8};
	            --vscode-scrollbarSlider-activeBackground: #{GetColor(EnvironmentColors.ScrollBarThumbPressedBackgroundBrushKey):x8};

                /* primary button */
	            --vscode-button-border:                    #{GetColor(CommonControlsColors.ButtonBorderBrushKey):x8};
	            --vscode-button-background:                #{GetColor(CommonControlsColors.ButtonDefaultBrushKey):x8};
	            --vscode-button-foreground:                #{GetColor(CommonControlsColors.ButtonDefaultTextBrushKey):x8};
	            --vscode-button-hoverBackground:           #{GetColor(CommonControlsColors.ButtonHoverBrushKey):x8};

                /* second button */
	            --vscode-button-secondaryBackground:       #{GetColor(CommonControlsColors.ButtonBrushKey):x8};
	            --vscode-button-secondaryForeground:       #{GetColor(CommonControlsColors.ButtonTextBrushKey):x8};
	            --vscode-button-secondaryHoverBackground:  #{GetColor(CommonControlsColors.ButtonHoverBrushKey):x8};

                /* checkbox */
	            --vscode-checkbox-background:              #{GetColor(CommonControlsColors.CheckBoxBackgroundBrushKey):x8};
	            --vscode-checkbox-border:                  #{GetColor(CommonControlsColors.CheckBoxBorderBrushKey):x8};
	            --vscode-checkbox-foreground:              #{GetColor(CommonControlsColors.CheckBoxTextBrushKey):x8};

                /* drop down */
	            --vscode-settings-dropdownListBorder:      #{GetColor(EnvironmentColors.DropDownBorderBrushKey):x8};
	            --vscode-dropdown-background:              #{GetColor(EnvironmentColors.DropDownBackgroundBrushKey):x8};
	            --vscode-dropdown-border:                  #{GetColor(EnvironmentColors.DropDownBorderBrushKey):x8};
	            --vscode-dropdown-foreground:              #{GetColor(EnvironmentColors.DropDownTextBrushKey):x8};

                /* hyperlink */
	            --vscode-textLink-foreground:              #{GetColor(EnvironmentColors.DiagReportLinkTextBrushKey):x8};
	            --vscode-textLink-activeForeground:        #{GetColor(EnvironmentColors.DiagReportLinkTextHoverColorKey):x8};

                /* progressbar */
	            --vscode-progressBar-background:           #{GetColor(ProgressBarColors.IndicatorFillBrushKey):x8};

                /* panel */
	            --vscode-panelTitle-activeBorder:          #{GetColor(CommonDocumentColors.InnerTabActiveIndicatorBrushKey):x8};
	            --vscode-panelTitle-activeForeground:      #{GetColor(CommonDocumentColors.InnerTabActiveTextBrushKey):x8};
	            --vscode-panelTitle-inactiveForeground:    #{GetColor(CommonDocumentColors.InnerTabInactiveTextBrushKey):x8};
	            --vscode-panel-background:                 #{GetColor(CommonDocumentColors.InnerTabBackgroundBrushKey):x8};
	            --vscode-panel-border:                     #{GetColor(EnvironmentColors.PanelBorderBrushKey):x8};
            }}`;";
    }

    /// <summary>
    /// Add the set-theme script to the list of script that get executed on webview document load.
    /// </summary>
    /// <remarks>This will also remove any previous scripts that was added</remarks>
    /// <param name="execute">Whether or not to execute the script immediately</param>
    /// <returns>The script created by <see cref="GetSetThemeScript"/></returns>
    private async Task<string> AddThemeScriptOnLoadAsync(bool execute = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (_themeScriptId != null)
            webView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_themeScriptId);

        string script = GetSetThemeScript();
        _themeScriptId = await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            // clang-format off
            @$"addEventListener(
                ""DOMContentLoaded"",
                (event) => {{
                    {script}
                }}
            );"
            // clang-format on
        );

        if (execute) await webView.ExecuteScriptAsync(script);

        return script;
    }

    /// <summary>
    /// Fire when the user clicked the "Reload" button on the info bar
    /// </summary>
    private void InfoBar_OnActionItemClicked(object sender, InfoBarActionItemEventArgs e)
    {
        _infoBar.Close();
        ThreadHelper.JoinableTaskFactory.RunAsync(ReloadAsync).FireAndForget();
    }

    private void VSColorTheme_ThemeChanged(ThemeChangedEventArgs e)
    {
        ThreadHelper.JoinableTaskFactory
            .RunAsync(async delegate { await AddThemeScriptOnLoadAsync(true); })
            .FireAndForget();
    }

    private void WebView_OnDOMContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        if (webView.Source.OriginalString != "about:blank")
        {
            _isChatPageLoaded = true;
            _infoBar.Close();
        }
    }
}
