using CodeiumVS.Packets;
using EnvDTE80;
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

namespace CodeiumVS;

public class LanguageServer
{
    private const string Version = "1.6.10";

    private int Port = 0;
    private Process process;

    private readonly Metadata Metadata;
    private readonly HttpClient HttpClient;
    private readonly CodeiumVSPackage Package;
    private readonly NotificationInfoBar NotificationDownloading;

    public  readonly LanguageServerController Controller;

    public LanguageServer()
    {
        NotificationDownloading = new NotificationInfoBar();

        Package = CodeiumVSPackage.Instance;
        HttpClient = new HttpClient();
        Controller = new LanguageServerController();
        Metadata = new();
    }

    public async Task InitializeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        
        string ideVersion = "17.0", locale = "en-US";

        try
        {
            locale = CultureInfo.CurrentUICulture.Name;
            Version? version = await VS.Shell.GetVsVersionAsync();
            if (version != null) ideVersion = version.ToString();
        }
        catch (Exception) { }

        Metadata.request_id        = 0;
        Metadata.ide_name          = "visual_studio";
        Metadata.ide_version       = ideVersion;
        Metadata.extension_name    = Vsix.Name;
        Metadata.extension_version = Version;
        Metadata.session_id        = Guid.NewGuid().ToString();
        Metadata.locale            = locale;
        Metadata.disable_telemetry = false;

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

        Controller.Disconnect();
    }
    
    public int        GetPort()        { return Port;                               }
    public string     GetKey()         { return Metadata.api_key;                   }
    public string     GetVersion()     { return Version;                            }
    public bool       IsReady()        { return Port != 0;                          }
    public async Task WaitReadyAsync() { while (!IsReady()) {await Task.Delay(50);} }

    // Get API key from the authentication token
    public async Task SignInWithAuthTokenAsync(string authToken)
    {
        string url = Package.SettingsPage.EnterpriseMode ?
            Package.SettingsPage.ApiUrl + "/exa.seat_management_pb.SeatManagementService/RegisterUser" :
            "https://api.codeium.com/register_user/";

        RegisterUserRequest data = new() { firebase_id_token = authToken };
        RegisterUserResponse result = await RequestUrlAsync<RegisterUserResponse>(url, data);

        Metadata.api_key = result.api_key;

        if (Metadata.api_key == null)
        {
            await Package.LogAsync("Failed to sign in.");

            // show an error message box
            var msgboxResult = await VS.MessageBox.ShowAsync(
                "Codeium: Failed to sign in. Please check the output window for more details.",
                "Do you want to retry?",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_RETRYCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
            );

            if (msgboxResult == VSConstants.MessageBoxResult.IDRETRY)
                await SignInWithAuthTokenAsync(authToken);

            return;
        }

        File.WriteAllText(Package.GetAPIKeyPath(), Metadata.api_key);
        await Package.LogAsync("Signed in successfully");
        await Package.UpdateSignedInStateAsync();
    }

    // Open the browser to sign in
    public async Task SignInAsync()
    {
        // this will block until the sign in process has finished
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
                    "Codeium: Failed to get the Authentication Token. Please check the output window for more details.", 
                    "Do you want to retry?",
                    OLEMSGICON.OLEMSGICON_INFO, 
                    OLEMSGBUTTON.OLEMSGBUTTON_RETRYCANCEL,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
                );

                return (msgboxResult == VSConstants.MessageBoxResult.IDRETRY) ? await WaitForAuthTokenAsync() : null;
            }

            return result.authToken;
        }

        string state = Guid.NewGuid().ToString();
        string portalUrl = Package.SettingsPage.EnterpriseMode ? Package.SettingsPage.PortalUrl : "https://www.codeium.com";
        string redirectUrl = Uri.EscapeDataString($"http://127.0.0.1:{Port}/auth");
        string url = $"{portalUrl}/profile?response_type=token&redirect_uri={redirectUrl}&state={state}&scope=openid%20profile%20email&redirect_parameters_type=query";

        await Package.LogAsync("Opening browser to " + url);

        Package.OpenInBrowser(url);

        string authToken = await WaitForAuthTokenAsync();
        if (authToken != null) await SignInWithAuthTokenAsync(authToken);
    }

    // Delete the stored API key
    public async Task SignOutAsync()
    {
        Metadata.api_key = "";
        File.Delete(Package.GetAPIKeyPath());
        await Package.LogAsync("Signed out successfully");
        await Package.UpdateSignedInStateAsync();
    }

    // Download the language server (if not already) and start it
    public async Task PrepareAsync()
    {
        string binaryPath = Package.GetLanguageServerPath();

        if (File.Exists(binaryPath))
        {
            await StartAsync();
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await Package.LogAsync("Downloading language server...");

        // show the downloading progress dialog
        var waitDialogFactory = (IVsThreadedWaitDialogFactory)await VS.Services.GetThreadedWaitDialogAsync();
        IVsThreadedWaitDialog4 progDialog = waitDialogFactory.CreateInstance();



        string extensionBaseUrl = "https://github.com/Exafunction/codeium/releases/download";
        string languageServerVersion = Version;
        if (Package.SettingsPage.EnterpriseMode)
        {
            // Get the contents of /api/extension_base_url
            try
            {
                string portalUrl = Package.SettingsPage.PortalUrl.TrimEnd('/');
                string result = await new HttpClient().GetStringAsync(portalUrl + "/api/extension_base_url");
                extensionBaseUrl = result.Trim().TrimEnd('/');
                languageServerVersion = await new HttpClient().GetStringAsync(portalUrl + "/api/version");
            }
            catch (Exception)
            {
                await Package.LogAsync("Failed to get extension base url");
            }
        }

        progDialog.StartWaitDialog(
            "Codeium", $"Downloading language server v{languageServerVersion}", "", null,
            $"Codeium: Downloading language server v{languageServerVersion}", 0, false, true
        );

        // the language server is downloaded in a thread so that it doesn't block the UI
        // if we remove `while (webClient.IsBusy)`, the DownloadProgressChanged callback won't be called
        // until VS is closing, not sure how we can fix that without spawning a separate thread
        void ThreadDownloadLanguageServer()
        {
            string langServerFolder = Package.GetLanguageServerFolder();
            string downloadDest = Path.Combine(langServerFolder, "language-server.gz");

            Directory.CreateDirectory(langServerFolder);
            if (File.Exists(downloadDest)) File.Delete(downloadDest);

            Uri url = new($"{extensionBaseUrl}/language-server-v{languageServerVersion}/language_server_windows_x64.exe.gz");

            WebClient webClient = new();

            int oldPercent = -1;
            webClient.DownloadProgressChanged += (s, e) =>
            {
                // don't update the progress bar too often
                if (e.ProgressPercentage != oldPercent)
                {
                    oldPercent = e.ProgressPercentage;
                    ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                        double totalBytesMb = e.TotalBytesToReceive / 1024.0 / 1024.0;
                        double recievedBytesMb = e.BytesReceived / 1024.0 / 1024.0;

                        progDialog.UpdateProgress(
                            $"Downloading language server v{languageServerVersion} ({e.ProgressPercentage}%)",
                            $"{recievedBytesMb:f2}Mb / {totalBytesMb:f2}Mb",
                            $"Codeium: Downloading language server v{languageServerVersion} ({e.ProgressPercentage}%)",
                            (int)e.BytesReceived, (int)e.TotalBytesToReceive, true, out _
                        );

                    }).FireAndForget(true);
                }
            };

            webClient.DownloadFileCompleted += (s, e) =>
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    progDialog.StartWaitDialog(
                        "Codeium", $"Extracting files...", "Almost done", null,
                        $"Codeium: Extracting files...", 0, false, true
                    );

                    await Package.LogAsync("Extracting language server...");
                    using FileStream fileStream = new(downloadDest, FileMode.Open);
                    using GZipStream gzipStream = new(fileStream, CompressionMode.Decompress);
                    using FileStream outputStream = new(Package.GetLanguageServerPath(), FileMode.Create);
                    await gzipStream.CopyToAsync(outputStream);

                    outputStream.Close();
                    gzipStream.Close();
                    fileStream.Close();

                    progDialog.EndWaitDialog();
                    (progDialog as IDisposable).Dispose();

                    await StartAsync();
                }).FireAndForget(true);
            };

            // set no-cache so that we don't have unexpected problems
            webClient.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);

            // start downloading and wait for it to finish
            webClient.DownloadFileAsync(url, downloadDest);

            while (webClient.IsBusy) 
                Thread.Sleep(100);
        }

        Thread trd = new(new ThreadStart(ThreadDownloadLanguageServer))
        {
            IsBackground = true
        };
        trd.Start();
    }

    // Start the language server process
    private async Task StartAsync()
    {
        Port = 0;

        string apiUrl = (Package.SettingsPage.ApiUrl.Equals("") ? "https://server.codeium.com" : Package.SettingsPage.ApiUrl);
        string managerDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string databaseDir = Package.GetDatabaseDirectory();

        try
        {
            Directory.CreateDirectory(managerDir);
            Directory.CreateDirectory(databaseDir);
        }
        catch (Exception ex)
        {
            await Package.LogAsync($"LanguageServer.StartAsync: Failed to create directories; Exception: {ex}");
            await VS.MessageBox.ShowErrorAsync(
                "Codeium: Failed to create language server directories.",
                "Please check the output window for more details."
            );
            return;
        }

        process = new();
        process.StartInfo.FileName              = Package.GetLanguageServerPath();
        process.StartInfo.UseShellExecute       = false;
        process.StartInfo.CreateNoWindow        = true;
        process.StartInfo.RedirectStandardError = true;
        process.EnableRaisingEvents             = true;

        process.StartInfo.Arguments =
            $"--api_server_url {apiUrl} --manager_dir \"{managerDir}\" --database_dir \"{databaseDir}\"" +
            " --enable_chat_web_server --enable_chat_client --detect_proxy=false";

        if (Package.SettingsPage.EnterpriseMode)
            process.StartInfo.Arguments += $" --enterprise_mode --portal_url {Package.SettingsPage.PortalUrl}";

        process.ErrorDataReceived += LSP_OnPipeDataReceived;
        process.Exited += LSP_OnExited;

        await Package.LogAsync("Starting language server");

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            Utilities.ProcessExtensions.MakeProcessExitOnParentExit(process);
        }
        catch (Exception ex)
        {
            await Package.LogAsync($"LanguageServer.StartAsync: Failed to start the language server; Exception: {ex}");
            await VS.MessageBox.ShowErrorAsync(
                "Codeium: Failed to start the language server.",
                "Please check the output window for more details."
            );
        }

        string apiKeyFilePath = Package.GetAPIKeyPath();
        if (File.Exists(apiKeyFilePath))
        {
            Metadata.api_key = File.ReadAllText(apiKeyFilePath);
        }

        await Package.UpdateSignedInStateAsync();
    }

    private void LSP_OnExited(object sender, EventArgs e)
    {
        Package.Log("Language Server Process exited unexpectedly, restarting...");

        Port = 0;
        process = null;
        Controller.Disconnect();
        ThreadHelper.JoinableTaskFactory.RunAsync(StartAsync).FireAndForget(true);
    }

    // This method will be responsible for reading and parsing the output of the LSP
    private void LSP_OnPipeDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;

        // regex to match the port number
        Match match = Regex.Match(e.Data, @"Language server listening on (random|fixed) port at (\d{2,5})");

        if (match.Success)
        {
            if (int.TryParse(match.Groups[2].Value, out Port))
            {
                Package.Log($"Language server started on port {Port}");

                ChatToolWindow.Instance?.Reload();
                ThreadHelper.JoinableTaskFactory.RunAsync(Controller.ConnectAsync).FireAndForget(true);
            }
            else
            {
                Package.Log($"Error: Failed to parse the port number from \"{match.Groups[1].Value}\"");
            }
        }

        Package.Log("Language Server: " + e.Data);
    }

    private async Task<T?> RequestUrlAsync<T>(string url, object data, CancellationToken cancellationToken = default)
    {
        StringContent post_data = new(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
        try
        {
            HttpResponseMessage rq = await HttpClient.PostAsync(url, post_data, cancellationToken);
            if (rq.StatusCode == HttpStatusCode.OK)
            {
                return JsonConvert.DeserializeObject<T>(await rq.Content.ReadAsStringAsync());
            }

            await Package.LogAsync($"Error: Failed to send request to {url}, status code: {rq.StatusCode}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Package.LogAsync($"Error: Failed to send request to {url}, exception: {ex.Message}");
        }

        return default;
    }

    private async Task<T?> RequestCommandAsync<T>(string command, object data, CancellationToken cancellationToken = default)
    {
        string url = $"http://127.0.0.1:{Port}/exa.language_server_pb.LanguageServerService/{command}";
        return await RequestUrlAsync<T>(url, data, cancellationToken);
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
                disable_autocomplete_in_comments = !Package.SettingsPage.EnableCommentCompletion,
            }
        };

        GetCompletionsResponse? result = await RequestCommandAsync<GetCompletionsResponse>("GetCompletions", data, token);
        return result != null ? result.completionItems : [];
    }

    public async Task AcceptCompletionAsync(string completionId)
    {
        AcceptCompletionRequest data = new()
        {
            metadata = GetMetadata(),
            completion_id = completionId
        };

        await RequestCommandAsync<AcceptCompletionResponse>("AcceptCompletion", data);
    }

    public async Task<GetProcessesResponse?> GetProcessesAsync()
    {
        return await RequestCommandAsync<GetProcessesResponse>("GetProcesses", new { });
    }

    public Metadata GetMetadata()
    {
        return new()
        {
            request_id        = Metadata.request_id++,
            api_key           = Metadata.api_key,
            ide_name          = Metadata.ide_name,
            ide_version       = Metadata.ide_version,
            extension_name    = Metadata.extension_name,
            extension_version = Metadata.extension_version,
            session_id        = Metadata.session_id,
            locale            = Metadata.locale,
            disable_telemetry = Metadata.disable_telemetry
        };
    }
}