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

[Guid(GuidString)]
public class ChatToolWindow : ToolWindowPane
{
    public const string GuidString = "1a46fd64-28d5-434c-8eb3-17a02d419b53";
    public ChatToolWindow()
        : base(null)
    {
        Caption = "Codeium Chat";
        Content = new ChatToolWindowControl();
    }

    public void Reload()
    {
        _ = (Content as ChatToolWindowControl).ReloadAsync();
    }
}

public partial class ChatToolWindowControl : UserControl, IComponentConnector
{
    private CodeiumVSPackage package;

    public ChatToolWindowControl()
    {
        InitializeComponent();
        _ = InitializeWebViewAsync();
    }
    async Task InitializeWebViewAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        package = CodeiumVSPackage.Instance;

        IVsUIShell5 vsShell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell5;

        // set the default background color to avoid flashing a white window
        if (vsShell != null)
            webView.DefaultBackgroundColor = vsShell.GetThemedGDIColor(EnvironmentColors.ToolWindowBackgroundBrushKey);

        // create webview2 environment and load the webview
        string webviewDirectory = Path.Combine(package.GetAppDataPath(), "webview2");
        Directory.CreateDirectory(webviewDirectory);
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, webviewDirectory);
        await webView.EnsureCoreWebView2Async(env);

        // load the loading page
        webView.NavigateToString(Properties.Resources.ChatLoadingPage_html);

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        // wait for the language server
        await package.langServer.WaitReadyAsync();

        Packets.Metadata metadata = package.langServer.GetMetadata();
        Packets.GetProcessesResponse gpr = await package.langServer.GetProcessesAsync();

        string serverUrl = $"ws://127.0.0.1:{gpr.chat_web_server_port}";
        string clientUrl = $"http://127.0.0.1:{gpr.chat_client_port}";

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
            //{ "has_enterprise_extension"  , "false"                    },
            { "open_file_pointer_enabled" , "true"                     },
            { "diff_view_enabled"         , "true"                     },
            { "insert_at_cursor_enabled"  , "true"                     },
            {
                "has_enterprise_extension",
                package.HasEnterprise().ToString().ToLower()
            }
        };

        string uriString = clientUrl + "?" + string.Join("&", data.Select((KeyValuePair<string, string> kv) => kv.Key + "=" + kv.Value));
        webView.Source = new Uri(uriString);

        await SetChatThemeAsync();
    }

    private async Task SetChatThemeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        IVsUIShell5 vsShell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell5;

        // get vs colors and set the theme for chat page
        uint GetColor(ThemeResourceKey key)
        {
            var color = VsColors.GetThemedWPFColor(vsShell, key);
            return (uint)(color.R << 24 | color.G << 16 | color.R << 8 | color.A);
        }

        uint colorWindowBg = GetColor(EnvironmentColors.ToolWindowBackgroundBrushKey);
        uint colorTextBoxBg = GetColor(EnvironmentColors.ComboBoxBackgroundBrushKey);
        uint colorTextBoxText = GetColor(EnvironmentColors.ComboBoxTextBrushKey);
        uint colorTextBoxPlaceholder = GetColor(EnvironmentColors.CommandBarTextInactiveBrushKey);

        string script = $@"
            var style = document.createElement('style');

            style.textContent = `
            body {{
	            --vscode-sideBar-background: #{colorWindowBg:x8};
	            --vscode-input-background: #{colorTextBoxBg:x8};
	            --vscode-input-foreground: #{colorTextBoxText:x8};
	            --vscode-input-placeholderForeground: #{colorTextBoxPlaceholder:x8};
	            --vscode-list-inactiveSelectionBackground: #{colorTextBoxBg:x8};
            }}`;

            document.head.appendChild(style);";

        await webView.ExecuteScriptAsync(script);
    }

}
