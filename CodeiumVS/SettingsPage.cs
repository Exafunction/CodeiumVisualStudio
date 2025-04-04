using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodeiumVS;

[ComVisible(true)]
public class SettingsPage : DialogPage
{
    private bool enterpriseMode;
    private string portalUrl = "";
    private string apiUrl = "";
    private string extensionBaseUrl = "https://github.com/Exafunction/codeium/releases/download";
    private bool enableCommentCompletion = true;
    private bool enableLanguageServerProxy = false;
    private bool enableIndexing = true;
    private bool enableCodeLens = true;
    private int indexingMaxFileCount = 5000;
    private string indexingFilesListPath = "";
    private bool indexOpenFiles = true;

    [Category("Windsurf")]
    [DisplayName("Self-Hosted Enterprise Mode")]
    [Description(
        "Set this to True if using Visual Studio with Windsurf Enterprise. Requires restart.")]
    public bool EnterpriseMode
    {
        get {
            return enterpriseMode;
        }
        set {
            enterpriseMode = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Portal Url")]
    [Description("URL of the Windsurf Enterprise Portal. Requires restart.")]
    public string PortalUrl
    {
        get {
            return portalUrl;
        }
        set {
            portalUrl = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Language Server Download URL")]
    [Description(
        "If you're experiencing network issues with GitHub and can't download the language server, please change this to a GitHub Mirror URL instead. For example: https://gh.api.99988866.xyz/https://github.com/Exafunction/codeium/releases/download")]
    public string ExtensionBaseUrl
    {
        get {
            return extensionBaseUrl;
        }
        set {
            extensionBaseUrl = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("API Url")]
    [Description("API Url for Windsurf Enterprise. Requires restart.")]
    public string ApiUrl
    {
        get {
            return apiUrl;
        }
        set {
            apiUrl = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Enable comment completion")]
    [Description("Whether or not Windsurf will provide completions for comments.")]
    public bool EnableCommentCompletion
    {
        get {
            return enableCommentCompletion;
        }
        set {
            enableCommentCompletion = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Enable Code Lens")]
    [Description("AI-powered inline action buttons in your editor. (Reload Required)")]
    public bool EnableCodeLens
    {
        get
        {
            return enableCodeLens;
        }
        set
        {
            enableCodeLens = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Enable language server proxy")]
    [Description(
        "If you're experiencing network issues with the language server, we recommend enabling this option and using a VPN to resolve the issue. Requires restart.")]
    public bool EnableLanguageServerProxy
    {
        get {
            return enableLanguageServerProxy;
        }
        set {
            enableLanguageServerProxy = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Enable Windsurf Indexing")]
    [Description(
        "Allows Windsurf to index your current repository and provide better chat and autocomplete responses based on relevant parts of your codebase. Requires restart.")]
    public bool EnableIndexing
    {
        get {
            return enableIndexing;
        }
        set {
            enableIndexing = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Indexing Max Workspace Size (File Count)")]
    [Description(
        "If indexing is enabled, we will only attempt to index workspaces that have up to this many files. This file count ignores .gitignore and binary files.")]
    public int IndexingMaxWorkspaceSize
    {
        get {
            return indexingMaxFileCount;
        }
        set {
            indexingMaxFileCount = value;
        }
    }

    [Category("Windsurf")]
    [DisplayName("Directories to Index List Path")]
    [Description(
        "Absolute path to a .txt file that contains a line separated list of absolute paths of directories to index. Requires restart.")]
    public string IndexingFilesListPath
    {
        get {
            return indexingFilesListPath;
        }
        set {
            indexingFilesListPath = value;
        }
    }
    [Category("Windsurf")]
    [DisplayName("Index Open Files")]
    [Description(
       "Windsurf will attempt to parse the project files that the files open upon IDE startup belong to. Requires restart.")]
    public bool IndexOpenFiles
    {
        get
        {
            return indexOpenFiles;
        }
        set
        {
            indexOpenFiles = value;
        }
    }
}
