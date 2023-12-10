using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Web.WebView2.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Markup;

namespace CodeiumVS;

[Guid(PackageGuids.ChatToolWindowString)]
public class ChatToolWindow : ToolWindowPane
{
    internal static ChatToolWindow? Instance { get; private set; }

    public ChatToolWindow()
        : base(null)
    {
        Instance = this;
        Caption = "Codeium Chat";
        Content = new ChatToolWindowControl();
    }

    public void Reload()
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(
            (Content as ChatToolWindowControl).ReloadAsync
        ).FireAndForget(true);
    }
}

public partial class ChatToolWindowControl : UserControl, IComponentConnector
{
    private CodeiumVSPackage package;
    private bool _isInitialized = false;

    public ChatToolWindowControl()
    {
        InitializeComponent();
        ThreadHelper.JoinableTaskFactory.RunAsync(InitializeWebViewAsync).FireAndForget(true);
    }

    private async Task InitializeWebViewAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        package = CodeiumVSPackage.Instance;

        // set the default background color to avoid flashing a white window
        webView.DefaultBackgroundColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundBrushKey);

        // create webview2 environment and load the webview
        string webviewDirectory = Path.Combine(package.GetAppDataPath(), "webview2");
        Directory.CreateDirectory(webviewDirectory);
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, webviewDirectory);
        try
        {
            // Try Catch this in case it's causing problems
            await webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            await package.LogAsync($"Failed to initialize webview core enviroment. Exception: {ex}");
            await VS.MessageBox.ShowErrorAsync(
                "Codeium: Failed to initialize webview core enviroment", 
                "Chat might be unavailable. Please see more details in the output window."
            );
        }

        _isInitialized = true;
        // load the loading page
        webView.NavigateToString(Properties.Resources.ChatLoadingPage_html);

        VSColorTheme.ThemeChanged += VSColorTheme_ThemeChanged;

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (!_isInitialized) return;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // wait for the language server
        await package.LanguageServer.WaitReadyAsync();

        Packets.Metadata metadata = package.LanguageServer.GetMetadata();
        Packets.GetProcessesResponse gpr = await package.LanguageServer.GetProcessesAsync();

        string serverUrl = $"ws://127.0.0.1:{gpr.chatWebServerPort}";
        string clientUrl = $"http://127.0.0.1:{gpr.chatClientPort}";

        Dictionary<string, string> data = new()
        {
            { "api_key"                   , metadata.api_key           },
            { "extension_name"            , metadata.extension_name    },
            { "extension_version"         , metadata.extension_version },
            { "ide_name"                  , metadata.ide_name          },
            { "ide_version"               , metadata.ide_version       },
            { "locale"                    , metadata.locale            },
            { "ide_telemetry_enabled"     , "true"                     },
            { "app_name"                  , "Visual Studio"            },
            { "web_server_url"            , serverUrl                  },
            { "has_dev_extension"         , "false"                    },
            { "open_file_pointer_enabled" , "true"                     },
            { "diff_view_enabled"         , "true"                     },
            { "insert_at_cursor_enabled"  , "true"                     },
            {
                "has_enterprise_extension",
                package.HasEnterprise().ToString().ToLower()
            }
        };

        string uriString = clientUrl + "?" + string.Join("&", data.Select((KeyValuePair<string, string> kv) => kv.Key + "=" + kv.Value));
        try
        {
            if (webView.Source?.OriginalString == uriString) webView.Reload();
            else webView.Source = new Uri(uriString);
        }
        catch(Exception ex)
        {
            await package.LogAsync($"Failed to open the chat page. Exception: {ex}");
            await VS.MessageBox.ShowErrorAsync(
                "Codeium: Failed to open the chat page",
                "We're sorry for the inconvenience. Please see more details in the output window."
            );
        }

        await SetChatThemeAsync();
    }

    // Get VS colors and set the theme for chat page
    private async Task SetChatThemeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // System.Drawing.Color is ARGB, we need to convert it to RGBA for css
        static uint GetColor(ThemeResourceKey key)
        {
            var color = VSColorTheme.GetThemedColor(key);
            return (uint)(color.R << 24 | color.G << 16 | color.B << 8 | color.A);
        }

        uint textColor = GetColor(EnvironmentColors.ToolWindowTextBrushKey);

        string script = $@"
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

        await webView.ExecuteScriptAsync(script);
    }

    private void VSColorTheme_ThemeChanged(ThemeChangedEventArgs e)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(SetChatThemeAsync).FireAndForget(true);
    }
}
