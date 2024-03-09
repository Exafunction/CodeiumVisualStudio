using CodeiumVS.Packets;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodeiumVS;

public class LanguageServer
{
    private string _languageServerURL;
    private string _languageServerVersion = "1.8.6";

    private int _port = 0;
    private System.Diagnostics.Process _process;
    private bool _intializedWorkspace = false;

    private readonly Metadata _metadata;
    private readonly HttpClient _httpClient;
    private readonly CodeiumVSPackage _package;

    public readonly LanguageServerController Controller;

    public LanguageServer()
    {
        _package = CodeiumVSPackage.Instance;
        _metadata = new();
        _httpClient = new HttpClient();
        Controller = new LanguageServerController();
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
        catch (Exception)
        {
        }

        // must be called before setting the metadata to retrieve _languageServerVersion first
        await PrepareAsync();

        _metadata.request_id = 0;
        _metadata.ide_name = "visual_studio";
        _metadata.ide_version = ideVersion;
        _metadata.extension_name = Vsix.Name;
        _metadata.extension_version = _languageServerVersion;
        _metadata.session_id = Guid.NewGuid().ToString();
        _metadata.locale = locale;
        _metadata.disable_telemetry = false;
    }

    public void Dispose()
    {
        // HasExited can throw, i don't know we should properly handle it
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
                _process.Dispose();
                _process = null;
            }
        }
        catch (Exception)
        {
        }

        Controller.Disconnect();
    }

    public int GetPort() { return _port; }
    public string GetKey() { return _metadata.api_key; }
    public string GetVersion() { return _languageServerVersion; }
    public bool IsReady() { return _port != 0; }
    public async Task WaitReadyAsync()
    {
        while (!IsReady())
        {
            await Task.Delay(50);
        }
    }

    // Get API key from the authentication token
    public async Task SignInWithAuthTokenAsync(string authToken)
    {
        string url = _package.SettingsPage.EnterpriseMode
                         ? _package.SettingsPage.ApiUrl +
                               "/exa.seat_management_pb.SeatManagementService/RegisterUser"
                         : "https://api.codeium.com/register_user/";

        RegisterUserRequest data = new() { firebase_id_token = authToken };
        RegisterUserResponse result = await RequestUrlAsync<RegisterUserResponse>(url, data);

        _metadata.api_key = result.api_key;

        if (_metadata.api_key == null)
        {
            await _package.LogAsync("Failed to sign in.");

            // show an error message box
            var msgboxResult = await VS.MessageBox.ShowAsync(
                "Codeium: Failed to sign in. Please check the output window for more details.",
                "Do you want to retry?",
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_RETRYCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            if (msgboxResult == VSConstants.MessageBoxResult.IDRETRY)
                await SignInWithAuthTokenAsync(authToken);

            return;
        }

        File.WriteAllText(_package.GetAPIKeyPath(), _metadata.api_key);
        await _package.LogAsync("Signed in successfully");
        await _package.UpdateSignedInStateAsync();
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
            GetAuthTokenResponse? result =
                await RequestCommandAsync<GetAuthTokenResponse>("GetAuthToken", new {});

            if (result == null)
            {
                // show an error message box
                var msgboxResult = await VS.MessageBox.ShowAsync(
                    "Codeium: Failed to get the Authentication Token. Please check the output window for more details.",
                    "Do you want to retry?",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_RETRYCANCEL,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                return (msgboxResult == VSConstants.MessageBoxResult.IDRETRY)
                           ? await WaitForAuthTokenAsync()
                           : null;
            }

            return result.authToken;
        }

        string state = Guid.NewGuid().ToString();
        string portalUrl = _package.SettingsPage.EnterpriseMode ? _package.SettingsPage.PortalUrl
                                                                : "https://www.codeium.com";
        string redirectUrl = Uri.EscapeDataString($"http://127.0.0.1:{_port}/auth");
        string url =
            $"{portalUrl}/profile?response_type=token&redirect_uri={redirectUrl}&state={state}&scope=openid%20profile%20email&redirect_parameters_type=query";

        await _package.LogAsync("Opening browser to " + url);

        CodeiumVSPackage.OpenInBrowser(url);

        string authToken = await WaitForAuthTokenAsync();
        if (authToken != null) await SignInWithAuthTokenAsync(authToken);
    }

    // Delete the stored API key
    public async Task SignOutAsync()
    {
        _metadata.api_key = "";
        Utilities.FileUtilities.DeleteSafe(_package.GetAPIKeyPath());
        await _package.LogAsync("Signed out successfully");
        await _package.UpdateSignedInStateAsync();
    }

    /// <summary>
    /// Get the language server URL and version, from the portal if we are in enterprise mode
    /// </summary>
    /// <returns></returns>
    private async Task GetLanguageServerInfoAsync()
    {
        string extensionBaseUrl =
            (_package.SettingsPage.ExtensionBaseUrl.Equals("")
                 ? "https://github.com/Exafunction/codeium/releases/download"
                 : _package.SettingsPage.ExtensionBaseUrl.Trim().TrimEnd('/'));

        if (_package.SettingsPage.EnterpriseMode)
        {
            // Get the contents of /api/extension_base_url
            try
            {
                string portalUrl = _package.SettingsPage.PortalUrl.TrimEnd('/');
                string result =
                    await new HttpClient().GetStringAsync(portalUrl + "/api/extension_base_url");
                extensionBaseUrl = result.Trim().TrimEnd('/');
                _languageServerVersion =
                    await new HttpClient().GetStringAsync(portalUrl + "/api/version");
            }
            catch (Exception)
            {
                await _package.LogAsync("Failed to get extension base url");
                extensionBaseUrl = "https://github.com/Exafunction/codeium/releases/download";
            }
        }

        _languageServerURL =
            $"{extensionBaseUrl}/language-server-v{_languageServerVersion}/language_server_windows_x64.exe.gz";
    }

    /// <summary>
    /// Update the progress dialog percentage
    /// </summary>
    private async Task ThreadDownload_UpdateProgressAsync(DownloadProgressChangedEventArgs e,
                                                          IVsThreadedWaitDialog4 progressDialog)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        double totalBytesMb = e.TotalBytesToReceive / 1024.0 / 1024.0;
        double recievedBytesMb = e.BytesReceived / 1024.0 / 1024.0;

        progressDialog.UpdateProgress(
            $"Downloading language server v{_languageServerVersion} ({e.ProgressPercentage}%)",
            $"{recievedBytesMb:f2}Mb / {totalBytesMb:f2}Mb",
            $"Codeium: Downloading language server v{_languageServerVersion} ({e.ProgressPercentage}%)",
            (int)e.BytesReceived,
            (int)e.TotalBytesToReceive,
            true,
            out _);
    }

    /// <summary>
    /// On download completed, extract the language server from the archive and start it. Prompt the
    /// user to retry if failed.
    /// </summary>
    private async Task ThreadDownload_OnCompletedAsync(AsyncCompletedEventArgs e,
                                                       IVsThreadedWaitDialog4 progressDialog,
                                                       string downloadDest)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        progressDialog.StartWaitDialog("Codeium",
                                       $"Extracting files...",
                                       "Almost done",
                                       null,
                                       $"Codeium: Extracting files...",
                                       0,
                                       false,
                                       true);

        // show a notification to ask the user if they wish to retry downloading it
        if (e.Error != null)
        {
            await _package.LogAsync(
                $"ThreadDownload_OnCompletedAsync: Failed to download the language server; Exception: {e.Error}");

            NotificationInfoBar errorBar = new();
            KeyValuePair<string, Action>[] actions = [
                new KeyValuePair<string, Action>("Retry",
                                                 delegate {
                                                     ThreadHelper.JoinableTaskFactory
                                                         .RunAsync(async delegate {
                                                             await errorBar.CloseAsync();
                                                             await PrepareAsync();
                                                         })
                                                         .FireAndForget();
                                                 }),
            ];

            errorBar.Show(
                "[Codeium] Critical Error: Failed to download the language server. Do you want to retry?",
                KnownMonikers.StatusError,
                true,
                null,
                [..actions, ..NotificationInfoBar.SupportActions]);
        }
        else
        {
            // extract the language server archive
            await _package.LogAsync("Extracting language server...");

            using FileStream fileStream = new(downloadDest, FileMode.Open);
            using GZipStream gzipStream = new(fileStream, CompressionMode.Decompress);
            using FileStream outputStream = new(_package.GetLanguageServerPath(), FileMode.Create);

            // if there were an error during extraction, the `StartAsync`
            // function can handle it, so we don't need to do it here
            try
            {
                await gzipStream.CopyToAsync(outputStream);
            }
            catch (Exception ex)
            {
                await _package.LogAsync(
                    $"ThreadDownload_OnCompletedAsync: Error during extraction; Exception: {ex}");
            }

            outputStream.Close();
            gzipStream.Close();
            fileStream.Close();
        }

        Utilities.FileUtilities.DeleteSafe(downloadDest);

        progressDialog.EndWaitDialog();
        (progressDialog as IDisposable)?.Dispose();

        if (e.Error == null) await StartAsync();
    }

    /// <summary>
    /// The language server is downloaded in a thread so that it doesn't block the UI.<br/>
    /// Iff we remove `while (webClient.IsBusy)`, the DownloadProgressChanged callback won't be
    /// called<br/> until VS is closing, not sure how we can fix that without spawning a separate
    /// thread.
    /// </summary>
    /// <param name="progressDialog"></param>
    private void ThreadDownloadLanguageServer(IVsThreadedWaitDialog4 progressDialog)
    {
        string langServerFolder = _package.GetLanguageServerFolder();
        string downloadDest = Path.GetTempFileName();

        Directory.CreateDirectory(langServerFolder);
        Utilities.FileUtilities.DeleteSafe(downloadDest);

        Uri url = new(_languageServerURL);
        WebClient webClient = new();

        int oldPercent = -1;

        webClient.DownloadProgressChanged += (s, e) =>
        {
            // don't update the progress bar too often
            if (e.ProgressPercentage == oldPercent) return;
            oldPercent = e.ProgressPercentage;

            ThreadHelper.JoinableTaskFactory
                .RunAsync(
                    async delegate { await ThreadDownload_UpdateProgressAsync(e, progressDialog); })
                .FireAndForget();
        };

        webClient.DownloadFileCompleted += (s, e) =>
        {
            ThreadHelper.JoinableTaskFactory
                .RunAsync(async delegate {
                    await ThreadDownload_OnCompletedAsync(e, progressDialog, downloadDest);
                })
                .FireAndForget();
        };

        // set no-cache so that we don't have unexpected problems
        webClient.CachePolicy = new System.Net.Cache.RequestCachePolicy(
            System.Net.Cache.RequestCacheLevel.NoCacheNoStore);

        // start downloading and wait for it to finish
        webClient.DownloadFileAsync(url, downloadDest);

        // wait until the download is completed
        while (webClient.IsBusy)
            System.Threading.Thread.Sleep(100);

        webClient.Dispose();
    }

    // Download the language server (if not already) and start it
    public async Task PrepareAsync()
    {
        await GetLanguageServerInfoAsync();
        string binaryPath = _package.GetLanguageServerPath();

        if (File.Exists(binaryPath))
        {
            await StartAsync();
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await _package.LogAsync(
            $"Downloading language server v{_languageServerVersion} from {_languageServerURL}");

        // show the downloading progress dialog before starting the thread to make it feels more
        // responsive
        var waitDialogFactory =
            (IVsThreadedWaitDialogFactory)await VS.Services.GetThreadedWaitDialogAsync();
        IVsThreadedWaitDialog4 progressDialog = waitDialogFactory.CreateInstance();

        progressDialog.StartWaitDialog(
            "Codeium",
            $"Downloading language server v{_languageServerVersion}",
            "",
            null,
            $"Codeium: Downloading language server v{_languageServerVersion}",
            0,
            false,
            true);

        System.Threading.Thread trd =
            new(() => ThreadDownloadLanguageServer(progressDialog)) { IsBackground = true };

        trd.Start();
    }

    /// <summary>
    /// Verify the language server digital signature. If invalid, prompt the user to re-download it,
    /// or ignore and continue.
    /// </summary>
    /// <returns>False if the signature is invalid</returns>
    private async Task<bool> VerifyLanguageServerSignatureAsync()
    {
        try
        {
            X509Certificate2 certificate = new(_package.GetLanguageServerPath());
            RSACryptoServiceProvider publicKey =
                (RSACryptoServiceProvider)certificate.PublicKey.Key;
            if (certificate.Verify()) return true;
        }
        catch (CryptographicException)
        {
        }

        await _package.LogAsync(
            "LanguageServer.VerifyLanguageServerSignatureAsync: Failed to verify the language server digital signature");

        NotificationInfoBar errorBar = new();
        KeyValuePair<string, Action>[] actions = [
            new KeyValuePair<string, Action>("Re-download",
                                             delegate {
                                                 // delete the language server exe and try to
                                                 // re-download the language server
                                                 ThreadHelper.JoinableTaskFactory
                                                     .RunAsync(async delegate {
                                                         Utilities.FileUtilities.DeleteSafe(
                                                             _package.GetLanguageServerPath());
                                                         await errorBar.CloseAsync();
                                                         await PrepareAsync();
                                                     })
                                                     .FireAndForget();
                                             }),
            new KeyValuePair<string, Action>("Ignore and continue",
                                             delegate {
                                                 // ignore the invalid signature and just try to
                                                 // start the language server
                                                 ThreadHelper.JoinableTaskFactory
                                                     .RunAsync(async delegate {
                                                         await errorBar.CloseAsync();
                                                         await StartAsync(true);
                                                     })
                                                     .FireAndForget();
                                             }),
        ];

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        errorBar.Show(
            "[Codeium] Failed to verify the language server digital signature. The executable might be corrupted.",
            KnownMonikers.IntellisenseWarning,
            true,
            null,
            actions);

        return false;
    }

    /// <summary>
    /// Start the language server process and begin reading its pipe output.
    /// </summary>
    /// <param name="ignoreDigitalSignature">If true, ignore the digital signature
    /// verification</param>
    private async Task StartAsync(bool ignoreDigitalSignature = false)
    {
        _port = 0;

        if (!ignoreDigitalSignature && !await VerifyLanguageServerSignatureAsync()) return;

        string apiUrl = (_package.SettingsPage.ApiUrl.Equals("") ? "https://server.codeium.com"
                                                                 : _package.SettingsPage.ApiUrl);
        string managerDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string databaseDir = _package.GetDatabaseDirectory();
        string languageServerPath = _package.GetLanguageServerPath();

        try
        {
            Directory.CreateDirectory(managerDir);
            Directory.CreateDirectory(databaseDir);
        }
        catch (Exception ex)
        {
            await _package.LogAsync(
                $"LanguageServer.StartAsync: Failed to create directories; Exception: {ex}");

            new NotificationInfoBar().Show(
                "[Codeium] Critical error: Failed to create language server directories. Please check the output window for more details.",
                KnownMonikers.StatusError,
                true,
                null,
                NotificationInfoBar.SupportActions);
            return;
        }

        _process = new();
        _process.StartInfo.FileName = languageServerPath;
        _process.StartInfo.UseShellExecute = false;
        _process.StartInfo.CreateNoWindow = true;
        _process.StartInfo.RedirectStandardError = true;
        _process.EnableRaisingEvents = true;

        _process.StartInfo.Arguments =
            $"--api_server_url {apiUrl} --manager_dir \"{managerDir}\" --database_dir \"{databaseDir}\"" +
            $" --enable_chat_web_server --enable_chat_client --detect_proxy={_package.SettingsPage.EnableLanguageServerProxy}";

        if (_package.SettingsPage.EnableIndexing)
            _process.StartInfo.Arguments +=
                $" --enable_local_search --enable_index_service --search_max_workspace_file_count {_package.SettingsPage.IndexingMaxWorkspaceSize}";

        if (_package.SettingsPage.EnterpriseMode)
            _process.StartInfo.Arguments +=
                $" --enterprise_mode --portal_url {_package.SettingsPage.PortalUrl}";

        _process.ErrorDataReceived += LSP_OnPipeDataReceived;
        _process.OutputDataReceived += LSP_OnPipeDataReceived;
        _process.Exited += LSP_OnExited;

        await _package.LogAsync("Starting language server");

        // try to start the process, if it fails, prompt the user if they
        // wish to delete the language server exe and restart VS
        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            // ask the user if they wish to delete the language server exe and try to re-download it

            _process = null;
            await _package.LogAsync(
                $"LanguageServer.StartAsync: Failed to start the language server; Exception: {ex}");

            NotificationInfoBar errorBar = new();
            KeyValuePair<string, Action>[] actions = [
                new KeyValuePair<string, Action>("Retry",
                                                 delegate {
                                                     // delete the language server exe and try to
                                                     // re-download the language server
                                                     _process = null;
                                                     Utilities.FileUtilities.DeleteSafe(
                                                         languageServerPath);

                                                     ThreadHelper.JoinableTaskFactory
                                                         .RunAsync(async delegate {
                                                             await errorBar.CloseAsync();
                                                             await PrepareAsync();
                                                         })
                                                         .FireAndForget();
                                                 }),
            ];

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            errorBar.Show(
                "[Codeium] Critical Error: Failed to start the language server. Do you want to retry?",
                KnownMonikers.StatusError,
                true,
                null,
                [..actions, ..NotificationInfoBar.SupportActions]);

            return;
        }

        // try to read the pipe output, this is a mild error if it fails
        try
        {
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            await _package.LogAsync(
                $"LanguageServer.StartAsync: BeginErrorReadLine failed; Exception: {ex}");

            // warn the user about the issue
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            new NotificationInfoBar().Show(
                "[Codeium] Failed to read output from the language server, Codeium might not work properly.",
                KnownMonikers.IntellisenseWarning,
                true,
                null,
                NotificationInfoBar.SupportActions);

            // fall back to reading the port file
            var timeoutSec = 120;
            var elapsedSec = 0;

            while (elapsedSec++ < timeoutSec)
            {
                // Check for new files in the directory
                var files = Directory.GetFiles(managerDir);

                foreach (var file in files)
                {
                    if (int.TryParse(Path.GetFileName(file), out _port) && _port != 0) break;
                }

                if (_port != 0) break;

                // Wait for a short time before checking again
                await Task.Delay(1000);
            }

            if (_port != 0)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(Controller.ConnectAsync)
                    .FireAndForget(true);
            }
            else
            {
                new NotificationInfoBar().Show(
                    "[Codeium] Critical Error: Failed to get the language server port. Please check the output window for more details.",
                    KnownMonikers.StatusError,
                    true,
                    null,
                    NotificationInfoBar.SupportActions);

                return;
            }
        }

        if (!Utilities.ProcessExtensions.MakeProcessExitOnParentExit(_process))
        {
            await _package.LogAsync(
                "LanguageServer.StartAsync: MakeProcessExitOnParentExit failed");
        }

        string apiKeyFilePath = _package.GetAPIKeyPath();
        if (File.Exists(apiKeyFilePath)) { _metadata.api_key = File.ReadAllText(apiKeyFilePath); }

        await _package.UpdateSignedInStateAsync();
    }

    private void LSP_OnExited(object sender, EventArgs e)
    {
        _package.Log("Language Server Process exited unexpectedly, restarting...");

        _port = 0;
        _process = null;
        Controller.Disconnect();
        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate { await StartAsync(); })
            .FireAndForget(true);
    }

    // This method will be responsible for reading and parsing the output of the LSP
    private void LSP_OnPipeDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;

        // regex to match the port number
        Match match =
            Regex.Match(e.Data, @"Language server listening on (random|fixed) port at (\d{2,5})");

        if (match.Success)
        {
            if (int.TryParse(match.Groups[2].Value, out _port))
            {
                _package.Log($"Language server started on port {_port}");

                ChatToolWindow.Instance?.Reload();
                ThreadHelper.JoinableTaskFactory.RunAsync(Controller.ConnectAsync)
                    .FireAndForget(true);
            }
            else
            {
                _package.Log(
                    $"Error: Failed to parse the port number from \"{match.Groups[1].Value}\"");
            }
        }

        _package.Log("Language Server: " + e.Data);
    }

    private async Task<T?> RequestUrlAsync<T>(string url, object data,
                                              CancellationToken cancellationToken = default)
    {
        StringContent post_data =
            new(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
        try
        {
            HttpResponseMessage rq = await _httpClient.PostAsync(url, post_data, cancellationToken);
            if (rq.StatusCode == HttpStatusCode.OK)
            {
                return JsonConvert.DeserializeObject<T>(await rq.Content.ReadAsStringAsync());
            }

            await _package.LogAsync(
                $"Error: Failed to send request to {url}, status code: {rq.StatusCode}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await _package.LogAsync(
                $"Error: Failed to send request to {url}, exception: {ex.Message}");
        }

        return default;
    }

    private async Task IntializeTrackedWorkspaceAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnvDTE.DTE dte = (DTE)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
        string solutionDir = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
        AddTrackedWorkspaceResponse response = await AddTrackedWorkspaceAsync(solutionDir);
        if (response != null)
        {
            _intializedWorkspace = true;
        }
    }

    private async Task<T?> RequestCommandAsync<T>(string command, object data,
                                                  CancellationToken cancellationToken = default)
    {
        string url =
            $"http://127.0.0.1:{_port}/exa.language_server_pb.LanguageServerService/{command}";
        return await RequestUrlAsync<T>(url, data, cancellationToken);
    }

    public async Task<IList<CompletionItem>?>
    GetCompletionsAsync(string absolutePath, string text, Languages.LangInfo language,
                        int cursorPosition, string lineEnding, int tabSize, bool insertSpaces,
                        CancellationToken token)
    {
        if (!_intializedWorkspace)
        {
            await IntializeTrackedWorkspaceAsync();
        }
        GetCompletionsRequest data =
            new() { metadata = GetMetadata(),
                    document = new() { text = text,
                                       editor_language = language.Name,
                                       language = language.Type,
                                       cursor_offset = (ulong)cursorPosition,
                                       line_ending = lineEnding,
                                       absolute_path = absolutePath,
                                       relative_path = Path.GetFileName(absolutePath) },
                    editor_options = new() {
                        tab_size = (ulong)tabSize,
                        insert_spaces = insertSpaces,
                        disable_autocomplete_in_comments =
                            !_package.SettingsPage.EnableCommentCompletion,
                    } };

        GetCompletionsResponse? result =
            await RequestCommandAsync<GetCompletionsResponse>("GetCompletions", data, token);
        return result != null ? result.completionItems : [];
    }

    public async Task AcceptCompletionAsync(string completionId)
    {
        AcceptCompletionRequest data =
            new() { metadata = GetMetadata(), completion_id = completionId };

        await RequestCommandAsync<AcceptCompletionResponse>("AcceptCompletion", data);
    }

    public async Task<GetProcessesResponse?> GetProcessesAsync()
    {
        if (!_intializedWorkspace)
        {
            await IntializeTrackedWorkspaceAsync();
        }
        return await RequestCommandAsync<GetProcessesResponse>("GetProcesses", new {});
    }

    public async Task<AddTrackedWorkspaceResponse?> AddTrackedWorkspaceAsync(string workspacePath)
    {
        AddTrackedWorkspaceRequest data = new() { workspace = workspacePath };
        return await RequestCommandAsync<AddTrackedWorkspaceResponse>("AddTrackedWorkspace", data);
    }

    public Metadata GetMetadata()
    {
        return new() { request_id = _metadata.request_id++,
                       api_key = _metadata.api_key,
                       ide_name = _metadata.ide_name,
                       ide_version = _metadata.ide_version,
                       extension_name = _metadata.extension_name,
                       extension_version = _metadata.extension_version,
                       session_id = _metadata.session_id,
                       locale = _metadata.locale,
                       disable_telemetry = _metadata.disable_telemetry };
    }
}
