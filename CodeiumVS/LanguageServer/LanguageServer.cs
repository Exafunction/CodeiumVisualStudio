using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodeiumVS.Packets;

namespace CodeiumVS;

public class LanguageServer
{
    private const string Version = "1.4.23";

    private int port = 0;
    private System.Diagnostics.Process process;

    private readonly HttpClient httpClient;
    private readonly Metadata metadata;
    private readonly CodeiumVSPackage package = null;
    public readonly LanguageServerController controller;

    public int        GetPort()        { return port;                               }
    public string     GetKey()         { return metadata.api_key;                   }
    public string     GetVersion()     { return Version;                            }
    public bool       IsReady()        { return port != 0;                          }
    public async Task WaitReadyAsync() { while (!IsReady()) {await Task.Delay(50);} }

    public LanguageServer()
    {
        package = CodeiumVSPackage.Instance;
        httpClient = new HttpClient();
        controller = new LanguageServerController();
        metadata = new();
    }

    public async Task InitializeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        DTE VSDTE = (DTE)Marshal.GetActiveObject("VisualStudio.DTE");

        metadata.request_id        = 0;
        metadata.ide_name          = "visual_studio";
        metadata.ide_version       = VSDTE.Version;
        metadata.extension_name    = Vsix.Name;
        metadata.extension_version = Version;
        metadata.session_id        = Guid.NewGuid().ToString();
        metadata.locale            = new CultureInfo(VSDTE.LocaleID).Name;
        metadata.disable_telemetry = false;

        await PrepareAsync();
    }

    public void Dispose()
    {
        if (process != null && !process.HasExited)
        {
            process.Kill();
            process.Dispose();
            process = null;
        }

        controller.Disconnect();
    }

    // register the auth token to the language server
    public async Task SignInWithAuthTokenAsync(string authToken)
    {
        string url = package.settingsPage.EnterpriseMode ?
            package.settingsPage.ApiUrl + "/exa.seat_management_pb.SeatManagementService/RegisterUser" :
            "https://api.codeium.com/register_user/";

        RegisterUserRequest data = new() { firebase_id_token = authToken };
        RegisterUserResponse result = await RequestUrlAsync<RegisterUserResponse>(url, data);

        metadata.api_key = result.api_key;

        if (metadata.api_key == null)
        {
            await package.LogAsync("Failed to sign in.");

            // show an error message box
            var msgboxResult = await VS.MessageBox.ShowAsync(
                "Failed to sign in. Please check the output window for more details.",
                "Do you want to retry?",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_RETRYCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
            );

            if (msgboxResult == VSConstants.MessageBoxResult.IDRETRY)
                await SignInWithAuthTokenAsync(authToken);

            return;
        }

        File.WriteAllText(package.GetAPIKeyPath(), metadata.api_key);
        await package.LogAsync("Signed in successfully");
        await package.UpdateSignedInStateAsync();
    }

    // open the browser to sign in
    public async Task SignInAsync()
    {
        // this will blocks until the sign in process has finished
        async Task<string?> WaitForAuthTokenAsync()
        {
            // wait until we got the actual port of the LSP
            await WaitReadyAsync();

            // TODO: should we use timeout = Timeout.InfiniteTimeSpan? default value is 100s (1m40s)
            GetAuthTokenResponse? result = await RequestCommandAsync<GetAuthTokenResponse>("GetAuthToken", new {});

            if (result == null)
            {
                // show an error message box
                var msgboxResult = await VS.MessageBox.ShowAsync(
                    "Failed to get the Authentication Token. Please check the output window for more details.",
                    "Do you want to retry?",
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_RETRYCANCEL,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
                );

                return (msgboxResult == VSConstants.MessageBoxResult.IDRETRY) ? await WaitForAuthTokenAsync() : null;
            }

            return result.auth_token;
        }

        string state = Guid.NewGuid().ToString();
        string portalUrl = package.settingsPage.EnterpriseMode ? package.settingsPage.PortalUrl : "https://www.codeium.com";
        string redirectUrl = Uri.EscapeDataString($"http://127.0.0.1:{port}/auth");
        string url = $"{portalUrl}/profile?response_type=token&redirect_uri={redirectUrl}&state={state}&scope=openid%20profile%20email&redirect_parameters_type=query";

        await package.LogAsync("Opening browser to " + url);

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });

        string authToken = await WaitForAuthTokenAsync();
        if (authToken != null) await SignInWithAuthTokenAsync(authToken);
    }

    // this just deletes the api key
    public async Task SignOutAsync()
    {
        metadata.api_key = "";
        File.Delete(package.GetAPIKeyPath());
        await package.LogAsync("Signed out successfully");
        await package.UpdateSignedInStateAsync();
    }

    // download the language server (if not already) and start it
    public async Task PrepareAsync()
    {
        string langServerFolder = package.GetLanguageServerFolder();
        Directory.CreateDirectory(langServerFolder);

        string binaryPath = package.GetLanguageServerPath();

        if (File.Exists(binaryPath))
        {
            await StartAsync();
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await package.LogAsync("Downloading language server...");

        // show the downloading progress dialog
        var waitDialogFactory = (IVsThreadedWaitDialogFactory)await VS.Services.GetThreadedWaitDialogAsync();
        IVsThreadedWaitDialog4 progDialog = waitDialogFactory.CreateInstance();

        progDialog.StartWaitDialog(
            "Codeium", $"Downloading language server v{Version}", "", null,
            $"Codeium: Downloading language server v{Version}", 0, false, true
        );

        // the language server is downloaded in a thread so that it doesn't block the UI
        // if we remove `while (webClient.IsBusy)`, the DownloadProgressChanged callback won't be called
        // until VS is closing, not sure how we can fix that without spawning a separate thread
        void ThreadDownloadLanguageServer()
        {
            Uri url = new($"https://github.com/Exafunction/codeium/releases/download/language-server-v{Version}/language_server_windows_x64.exe.gz");
            string downloadDest = Path.Combine(package.GetLanguageServerFolder(), "language-server.gz");
            File.Delete(downloadDest);

            WebClient webClient = new();

            int oldPercent = -1;
            webClient.DownloadProgressChanged += (s, e) =>
            {
                // don't update the progress bar too often
                if (e.ProgressPercentage != oldPercent)
                {
                    oldPercent = e.ProgressPercentage;
                    ThreadHelper.JoinableTaskFactory.Run(async delegate
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        double totalBytesMb = e.TotalBytesToReceive / 1024.0 / 1024.0;
                        double recievedBytesMb = e.BytesReceived / 1024.0 / 1024.0;

                        progDialog.UpdateProgress(
                            $"Downloading language server v{Version} ({e.ProgressPercentage}%)",
                            $"{recievedBytesMb:f2}Mb / {totalBytesMb:f2}Mb",
                            $"Codeium: Downloading language server v{Version} ({e.ProgressPercentage}%)",
                            (int)e.BytesReceived, (int)e.TotalBytesToReceive, true, out _);
                    });
                }
            };

            webClient.DownloadFileCompleted += (s, e) =>
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    progDialog.StartWaitDialog(
                        "Codeium", $"Extracting files...", "Almost done", null,
                        $"Codeium: Extracting files...", 0, false, true
                    );

                    await package.LogAsync("Extracting language server...");
                    using FileStream fileStream = new(downloadDest, FileMode.Open);
                    using GZipStream gzipStream = new(fileStream, CompressionMode.Decompress);
                    using FileStream outputStream = new(package.GetLanguageServerPath(), FileMode.Create);
                    await gzipStream.CopyToAsync(outputStream);

                    outputStream.Close();
                    gzipStream.Close();
                    fileStream.Close();

                    progDialog.EndWaitDialog();
                    (progDialog as IDisposable).Dispose();

                    await StartAsync();
                });
            };

            webClient.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            webClient.DownloadFileAsync(url, downloadDest);
            while (webClient.IsBusy)
            {
                System.Threading.Thread.Sleep(100);
            }
        }

        System.Threading.Thread trd = new(new ThreadStart(ThreadDownloadLanguageServer))
        {
            IsBackground = true
        };
        trd.Start();
    }

    // start the language server process
    // TODO: make the LSP exit when VS closes unexpectedly
    private async Task StartAsync()
    {
        port = 0;

        string apiUrl = (package.settingsPage.ApiUrl.Equals("") ? "https://server.codeium.com" : package.settingsPage.ApiUrl);
        string managerDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string databaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".codeium", "database");

        Directory.CreateDirectory(managerDir);
        Directory.CreateDirectory(databaseDir);

        process = new();
        process.StartInfo.FileName = package.GetLanguageServerPath();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.Arguments =
            $"--api_server_url {apiUrl} --manager_dir \"{managerDir}\" --enable_chat_web_server --enable_chat_client --database_dir \"{databaseDir}\" --detect_proxy=false";

        if (package.settingsPage.EnterpriseMode)
            process.StartInfo.Arguments += $" --enterprise_mode --portal_url {package.settingsPage.PortalUrl}";

        process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // get the port from the output of LSP
            if (port == 0)
            {
                Match match = Regex.Match(e.Data, @"Language server listening on random port at (\d+)");
                if (match.Success)
                {
                    if (int.TryParse(match.Groups[1].Value, out port))
                    {
                        package.Log($"Language server started on port {port}");
                        _ = controller.ConnectAsync();
                    }
                    else
                        package.Log($"Error: Failed to parse the port number from \"{match.Groups[1].Value}\"");
                }
            }

            package.Log("Language Server: " + e.Data);
        };

        await package.LogAsync("Starting language server");
        process.Start();
        process.BeginErrorReadLine();

        string apiKeyFilePath = package.GetAPIKeyPath();
        if (File.Exists(apiKeyFilePath))
        {
            metadata.api_key = File.ReadAllText(apiKeyFilePath);
        }

        await package.UpdateSignedInStateAsync();
    }

    private async Task<T?> RequestUrlAsync<T>(string url, object data, CancellationToken cancellationToken = default)
    {
        StringContent post_data = new(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
        try
        {
            HttpResponseMessage rq = await httpClient.PostAsync(url, post_data, cancellationToken);
            if (rq.StatusCode == HttpStatusCode.OK)
            {
                return JsonConvert.DeserializeObject<T>(await rq.Content.ReadAsStringAsync());
            }

            await package.LogAsync($"Error: Failed to send request to {url}, status code: {rq.StatusCode}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await package.LogAsync($"Error: Failed to send request to {url}, exception: {ex.Message}");
        }

        return default;
    }

    private async Task<T?> RequestCommandAsync<T>(string command, object data, CancellationToken cancellationToken = default)
    {
        string url = $"http://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/{command}";
        return await RequestUrlAsync<T>(url, data, cancellationToken);
    }

    public static Packets.Language ContentTypeToLanguage(string language)
    {
        //language = language.ToLower();
        return language switch
        {
            "c#"         => Packets.Language.LANGUAGE_CSHARP,
            "CSharp" => Packets.Language.LANGUAGE_CSHARP,

            "c"          => Packets.Language.LANGUAGE_C,
            "C/C++" => Packets.Language.LANGUAGE_CPP,
            "c++"        => Packets.Language.LANGUAGE_CPP,

            "CMake" => Packets.Language.LANGUAGE_CMAKE,
            "CMakeSettings" => Packets.Language.LANGUAGE_CMAKE,
            "CMakePresets" => Packets.Language.LANGUAGE_CMAKE,

            "css"        => Packets.Language.LANGUAGE_CSS,
            "cssLSPClient" => Packets.Language.LANGUAGE_CSS,
            "cssLSPServer" => Packets.Language.LANGUAGE_CSS,

            "HTML" => Packets.Language.LANGUAGE_HTML,

            "F#" => Packets.Language.LANGUAGE_FSHARP,
            "java"       => Packets.Language.LANGUAGE_JAVA,

            "JavaScript" => Packets.Language.LANGUAGE_JAVASCRIPT,

            "JSON" => Packets.Language.LANGUAGE_JSON,

            "markdown"   => Packets.Language.LANGUAGE_MARKDOWN,
            "vs-markdown" => Packets.Language.LANGUAGE_MARKDOWN,

            "php"        => Packets.Language.LANGUAGE_PHP,
            "powershell" => Packets.Language.LANGUAGE_POWERSHELL,
            "python"     => Packets.Language.LANGUAGE_PYTHON,
            "sql"        => Packets.Language.LANGUAGE_SQL,
            "TypeScript" => Packets.Language.LANGUAGE_TYPESCRIPT,

            "vb"         => Packets.Language.LANGUAGE_VISUALBASIC,
            "vbscript" => Packets.Language.LANGUAGE_VISUALBASIC,
            "VB_LSP" => Packets.Language.LANGUAGE_VISUALBASIC,
            "Basic" => Packets.Language.LANGUAGE_VISUALBASIC,

            "XML" => Packets.Language.LANGUAGE_XML,
            "XAML" => Packets.Language.LANGUAGE_XML,

            "plaintext" => Packets.Language.LANGUAGE_PLAINTEXT,
            "text" => Packets.Language.LANGUAGE_PLAINTEXT,
            "SCSS" => Packets.Language.LANGUAGE_SCSS,
            "yaml" => Packets.Language.LANGUAGE_YAML,

            _ => Packets.Language.LANGUAGE_UNSPECIFIED,
        };

    }

    public async Task<IList<CompletionItem>?> GetCompletionsAsync(string absolutePath, string text, Languages.LangInfo language, int cursorPosition, string lineEnding, int tabSize, bool insertSpaces, CancellationToken token)
    {
        GetCompletionsRequest data = new()
        {
            metadata = GetMetadata(),
            document = new()
            {
                text            = text,
                editor_language = language.Name,
                language        = language.Type,
                cursor_offset   = (ulong)cursorPosition,
                line_ending     = lineEnding,
                absolute_path   = absolutePath,
                relative_path   = Path.GetFileName(absolutePath)
            },
            editor_options = new()
            {
                tab_size = (ulong)tabSize,
                insert_spaces = insertSpaces,
                disable_autocomplete_in_comments = !package.settingsPage.EnableCommentCompletion,
            }
        };

        GetCompletionsResponse? result = await RequestCommandAsync<GetCompletionsResponse>("GetCompletions", data, token);
        return result != null ? result.completion_items : [];
    }

    public async Task AcceptCompletionAsync(string completionId)
    {
        AcceptCompletionRequest data = new()
        {
            metadata = GetMetadata(),
            completion_id = completionId
        };

        await RequestCommandAsync<IList<CompletionItem>>("AcceptCompletion", data);
    }

    public async Task<GetProcessesResponse?> GetProcessesAsync()
    {
        return await RequestCommandAsync<GetProcessesResponse>("GetProcesses", new { });
    }

    public Metadata GetMetadata()
    {
        return new()
        {
            request_id        = metadata.request_id++,
            api_key           = metadata.api_key,
            ide_name          = metadata.ide_name,
            ide_version       = metadata.ide_version,
            extension_name    = metadata.extension_name,
            extension_version = metadata.extension_version,
            session_id        = metadata.session_id,
            locale            = metadata.locale,
            disable_telemetry = metadata.disable_telemetry
        };
    }

}
