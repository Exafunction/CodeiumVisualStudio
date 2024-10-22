using CodeiumVS.Languages;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;

namespace CodeiumVS
{

internal class CodeiumCompletionHandler : IOleCommandTarget, IDisposable
{
    private readonly CodeiumVSPackage package;

    private readonly ITextView _view;
    private readonly IVsTextView _textViewAdapter;
    private readonly ITextDocument _document;

    private DateTime _lastRequest = DateTime.MinValue;

    private LangInfo _language;
    private CancellationTokenSource? _requestTokenSource;
    private readonly TimeSpan _intelliSenseDelay = TimeSpan.FromMilliseconds(250.0);

    private IOleCommandTarget m_nextCommandHandler;
    private TextViewListener m_provider;
    private CancellationTokenSource currentCancellTokenSource = null;
    private CancellationToken currentCancellToken;

    private string currentCompletionID;
    private bool hasCompletionUpdated;
    private List<Tuple<String, String>> suggestions;
    private int suggestionIndex;
    private Command CompleteSuggestionCommand;
    [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)] 
    public static extern short GetAsyncKeyState(Int32 keyCode);

    public async void GetCompletion()
    {
        try
        {
            if (_document == null || !package.IsSignedIn())
            {
                return;
            }

            UpdateRequestTokenSource(new CancellationTokenSource());

            SnapshotPoint? caretPoint = _view.Caret.Position.Point.GetPoint(
                textBuffer => (!textBuffer.ContentType.IsOfType("projection")),
                PositionAffinity.Successor);
            if (!caretPoint.HasValue)
            {
                return;
            }

            var caretPosition = caretPoint.Value.Position;

            string text = _document.TextBuffer.CurrentSnapshot.GetText();
            int cursorPosition = _document.Encoding.IsSingleByte
                ? caretPosition
                : Utf16OffsetToUtf8Offset(text, caretPosition);

            if (cursorPosition > text.Length)
            {
                Debug.Print("Error Caret past text position");
                return;
            }

            IList<Packets.CompletionItem>? list = await package.LanguageServer.GetCompletionsAsync(
                _document.FilePath,
                text,
                _language,
                cursorPosition,
                _view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId),
                _view.Options.GetOptionValue(DefaultOptions.TabSizeOptionId),
                _view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId),
                currentCancellTokenSource.Token);

            int lineN;
            int characterN;

            int res = _textViewAdapter.GetCaretPos(out lineN, out characterN);
            String line = _view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(lineN).GetText();
            Debug.Print("completions " + list.Count.ToString());

            if (res != VSConstants.S_OK)
            {
                return;
            }

            if (list != null && list.Count > 0)
            {
                Debug.Print("completions " + list.Count.ToString());

                string prefix = line.Substring(0, Math.Min(characterN, line.Length));
                suggestions = ParseCompletion(list, text, line, prefix, characterN);

                SuggestionTagger tagger = GetTagger();
                if (suggestions != null && suggestions.Count > 0 && tagger != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    suggestionIndex = 0;
                    currentCompletionID = suggestions[0].Item2;
                    var valid = tagger.SetSuggestion(suggestions[0].Item1, characterN);
                }

                await package.LogAsync("Generated " + list.Count + $" proposals");
            }

        }
        catch (Exception ex)
        {
            await package.LogAsync("Exception: " + ex.ToString());
        }
    }

    List<Tuple<String, String>> ParseCompletion(IList<Packets.CompletionItem> completionItems,
                                                string text, string line, string prefix,
                                                int cursorPoint)
    {
        if (completionItems == null || completionItems.Count == 0) { return null; }

        List<Tuple<String, String>> list = new(completionItems.Count);
        for (int i = 0; i < completionItems.Count; i++)
        {
            Packets.CompletionItem completionItem = completionItems[i];
            int startOffset = (int)completionItem.range.startOffset;
            int endOffset = (int)completionItem.range.endOffset;
            if (completionItem.completionParts.Count == 0) { continue; }
            int insertionStart = (int)completionItem.completionParts[0].offset;

            if (!_document.Encoding.IsSingleByte)
            {
                startOffset = Utf8OffsetToUtf16Offset(text, startOffset);
                endOffset = Utf8OffsetToUtf16Offset(text, endOffset);
                insertionStart = Utf8OffsetToUtf16Offset(text, insertionStart);
            }
            if (endOffset > text.Length) { endOffset = text.Length; }
            string end = text.Substring(endOffset);
            String completionText = completionItems[i].completion.text;
            if (!String.IsNullOrEmpty(end))
            {
                int endNewline = StringCompare.IndexOfNewLine(end);

                if (endNewline <= -1)
                    endNewline = end.Length;

                completionText = completionText + end.Substring(0, endNewline);
            }
            int offset = StringCompare.CheckSuggestion(completionText, prefix);
            if (offset < 0 || offset > completionText.Length) { continue; }

            completionText = completionText.Substring(offset);
            string completionID = completionItem.completion.completionId;
            var set = new Tuple<String, String>(completionText, completionID);

            // Filter out completions that don't match the current intellisense prefix
            ICompletionSession session = m_provider.CompletionBroker.GetSessions(_view).FirstOrDefault();
            if (session != null && session.SelectedCompletionSet != null)
            {
                var completion = session.SelectedCompletionSet.SelectionStatus.Completion;
                if (completion == null) { continue; }
                string intellisenseSuggestion = completion.InsertionText;
                ITrackingSpan intellisenseSpan = session.SelectedCompletionSet.ApplicableTo;
                SnapshotSpan span = intellisenseSpan.GetSpan(intellisenseSpan.TextBuffer.CurrentSnapshot);
                if (span.Length > intellisenseSuggestion.Length) { continue; }
                string intellisenseInsertion = intellisenseSuggestion.Substring(span.Length);
                if (!completionText.StartsWith(intellisenseInsertion))
                {
                    continue;
                }
            }
            list.Add(set);
        }

        return list;
    }

    public record KeyItem(string Name, string KeyBinding, string Category, string Scope);

    public static async Task<Command> GetCommandsAsync(String name)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            List<Command> items = new();
            DTE2 dte = await VS.GetServiceAsync<DTE, DTE2>();

            foreach (Command command in dte.Commands)
            {
                if (string.IsNullOrEmpty(command.Name))
                {
                    continue;
                }

                if (command.Name.Contains(name) && command.Bindings is object[] bindings)
                {
                    items.Add(command);
                }
            }

            if (items.Count > 0)
            {
                return items[0];
            }
        }
        catch (Exception ex)
        {
            CodeiumVSPackage.Instance.LogAsync(ex.ToString());
        }

        return null;
    }

    private void OnSuggestionAccepted(String proposalId)
    {
        // unfortunately in the SDK version 17.5.33428.388, there are no
        // SuggestionAcceptedEventArgs so we have to use reflection here
        ThreadHelper.JoinableTaskFactory
            .RunAsync(async delegate {
                await CodeiumVSPackage.Instance.LogAsync($"Accepted completion {proposalId}");
                await CodeiumVSPackage.Instance.LanguageServer.AcceptCompletionAsync(proposalId);
            })
            .FireAndForget(true);
    }

    public LangInfo GetLanguage()
    { 
        return _language;
    }

    private void UpdateRequestTokenSource(CancellationTokenSource newSource)
    {
        if (currentCancellTokenSource != null)
        {
            currentCancellTokenSource.Cancel();
            currentCancellTokenSource.Dispose();
        }
        currentCancellTokenSource = newSource;
    }

    public static int Utf16OffsetToUtf8Offset(string str, int utf16Offset)
    {
        return Encoding.UTF8.GetByteCount(str.ToCharArray(), 0, utf16Offset);
    }

    public static int Utf8OffsetToUtf16Offset(string str, int utf8Offset)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        return Encoding.UTF8.GetString(bytes.Take(utf8Offset).ToArray()).Length;
    }

    internal CodeiumCompletionHandler(IVsTextView textViewAdapter, ITextView view,
        TextViewListener provider)
    {
        try
        {
            CodeiumVSPackage.EnsurePackageLoaded();
            package = CodeiumVSPackage.Instance;
            _view = view;
            m_provider = provider;
            var topBuffer = view.BufferGraph.TopBuffer;

            var projectionBuffer = topBuffer as IProjectionBufferBase;

            ITextBuffer textBuffer =
                projectionBuffer != null ? projectionBuffer.SourceBuffers[0] : topBuffer;
            provider.documentFactory.TryGetTextDocument(textBuffer, out _document);

            if (_document != null)
            {
                CodeiumVSPackage.Instance.LogAsync("CodeiumCompletionHandler filepath = " + _document.FilePath);

                if (!provider.documentDictionary.ContainsKey(_document.FilePath.ToLower()))
                {
                    provider.documentDictionary.Add(_document.FilePath.ToLower(), _document);
                }

                _document.FileActionOccurred += OnFileActionOccurred;
                _document.TextBuffer.ContentTypeChanged += OnContentTypeChanged;
                RefreshLanguage();
            }

            _textViewAdapter = textViewAdapter;
            // add the command to the command chain
            textViewAdapter.AddCommandFilter(this, out m_nextCommandHandler);
            // ShowIntellicodeMsg();

            view.Caret.PositionChanged += CaretUpdate;

            _ = Task.Run(() =>
            {
                try
                {
                    CompleteSuggestionCommand = GetCommandsAsync("CodeiumAcceptCompletion").Result;
                }
                catch (Exception e)
                {
                    Debug.Write(e);
                }
            });

        }
        catch (Exception ex)
        {
            CodeiumVSPackage.Instance.LogAsync(ex.ToString());
        }
    }

    private void CaretUpdate(object sender, CaretPositionChangedEventArgs e)
    {
        try
        {
            var tagger = GetTagger();
            if (tagger == null)
            {
                return;
            }

            if (CompleteSuggestionCommand != null && CompleteSuggestionCommand.Bindings is object[] bindings &&
                bindings.Length > 0)
            {
                tagger.ClearSuggestion();
                return;
            }

            var key = GetAsyncKeyState(0x09);
            if ((0x8000 & key) > 0)
            {
                CompleteSuggestion(false);
            }
            else if (!tagger.OnSameLine())
            {
                tagger.ClearSuggestion();
            }
        }
        catch (Exception ex)
        {
            ThreadHelper.JoinableTaskFactory
                .RunAsync(async delegate { await CodeiumVSPackage.Instance.LogAsync(ex.ToString()); })
                .FireAndForget(true);
        }
    }

    private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs e)
    {
        RefreshLanguage();
    }

    private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
    {
        RefreshLanguage();
    }

    private void RefreshLanguage()
    {
        try
        {
            if (_document != null)
            {
                _language = Mapper.GetLanguage(_document.TextBuffer.ContentType,
                    Path.GetExtension(_document.FilePath)?.Trim('.'));
            }
        }
        catch (Exception ex)
        {

        }
    }

    public async void ShowNextSuggestion()
    {
        try
        {
            if (suggestions != null && suggestions.Count > 1)
            {

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var oldSuggestion = suggestionIndex;
                suggestionIndex = (suggestionIndex + 1) % suggestions.Count;
                currentCompletionID = suggestions[suggestionIndex].Item2;

                SuggestionTagger tagger = GetTagger();

                int lineN, characterN;
                int res = _textViewAdapter.GetCaretPos(out lineN, out characterN);

                if (res != VSConstants.S_OK)
                {
                    suggestionIndex = oldSuggestion;
                    currentCompletionID = suggestions[suggestionIndex].Item2;
                    return;
                }

                bool validSuggestion = tagger.SetSuggestion(suggestions[suggestionIndex].Item1, characterN);
                if (!validSuggestion)
                {
                    suggestionIndex = oldSuggestion;
                    currentCompletionID = suggestions[suggestionIndex].Item2;

                    tagger.SetSuggestion(suggestions[suggestionIndex].Item1, characterN);
                }
            }
        }
        catch (Exception ex)
        {
            ThreadHelper.JoinableTaskFactory
                .RunAsync(async delegate { await CodeiumVSPackage.Instance.LogAsync(ex.ToString()); })
                .FireAndForget(true);
        }

    }

    public bool CompleteSuggestion(bool checkLine = true)
    {
        var tagger = GetTagger();
        bool onSameLine = tagger.OnSameLine();
        if (tagger != null)
        {
            if (tagger.IsSuggestionActive() && (onSameLine || !checkLine) && tagger.CompleteText())
            {
                ClearCompletionSessions();
                OnSuggestionAccepted(currentCompletionID);
                return true;
            }
            else { tagger.ClearSuggestion(); }
        }

        return false;
    }

    void ClearSuggestion()
    {
        var tagger = GetTagger();
        if (tagger != null) { tagger.ClearSuggestion(); }
    }

    // Used to detect when the user interacts with the intellisense popup
    void CheckSuggestionUpdate(uint nCmdID)
    {
        switch (nCmdID)
        {
        case ((uint)VSConstants.VSStd2KCmdID.UP):
        case ((uint)VSConstants.VSStd2KCmdID.DOWN):
        case ((uint)VSConstants.VSStd2KCmdID.PAGEUP):
        case ((uint)VSConstants.VSStd2KCmdID.PAGEDN):
            if (m_provider.CompletionBroker.IsCompletionActive(_view))
            {
                hasCompletionUpdated = true;
            }

            break;
        case ((uint)VSConstants.VSStd2KCmdID.TAB):
        case ((uint)VSConstants.VSStd2KCmdID.RETURN):
            hasCompletionUpdated = false;
            break;
        }
    }
    private SuggestionTagger GetTagger()
    {
        var key = typeof(SuggestionTagger);
        var props = _view.TextBuffer.Properties;
        if (props.ContainsProperty(key)) { return props.GetProperty<SuggestionTagger>(key); }
        else { return null; }
    }

    public bool IsIntellicodeEnabled()
    {
        var vsSettingsManager =
            m_provider.ServiceProvider.GetService(typeof(SVsSettingsManager)) as IVsSettingsManager;

        vsSettingsManager.GetCollectionScopes(collectionPath: "ApplicationPrivateSettings",
                                              out var applicationPrivateSettings);
        vsSettingsManager.GetReadOnlySettingsStore(applicationPrivateSettings,
                                                   out IVsSettingsStore readStore);
        var res2 =
            readStore.GetString("ApplicationPrivateSettings\\Microsoft\\VisualStudio\\IntelliCode",
                                "WholeLineCompletions",
                                out var str);
        return str != "1*System.Int64*2";
    }

    void ShowIntellicodeMsg()
    {
        if (IsIntellicodeEnabled())
        {
            VsShellUtilities.ShowMessageBox(
                this.package,
                "Please disable IntelliCode to use Codeium. You can access Intellicode settings via Tools --> Options --> Intellicode.",
                "Disable IntelliCode",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn,
        IntPtr pvaOut)
        {
        _textViewAdapter.RemoveCommandFilter(this);
        _textViewAdapter.AddCommandFilter(this, out m_nextCommandHandler);
        
        // let the other handlers handle automation functions
        if (VsShellUtilities.IsInAutomationFunction(m_provider.ServiceProvider))
        {
            return m_nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        
        // check for a commit character
        bool regenerateSuggestion = false;
        if (!hasCompletionUpdated && nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB)
        {
            if (CompleteSuggestionCommand != null)
            {
                var bindings = CompleteSuggestionCommand.Bindings as object[];
                if (bindings == null || bindings.Length <= 0)
                {
                    var tagger = GetTagger();

                    ICompletionSession session = m_provider.CompletionBroker.GetSessions(_view).FirstOrDefault();
                    if (session != null && session.SelectedCompletionSet != null)
                    {
                        tagger.ClearSuggestion();
                        regenerateSuggestion = true;
                    }
                    else if (CompleteSuggestion())
                    {
                        return VSConstants.S_OK;
                    }
                }
            }
        }
        else if (nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN ||
                 nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL)
        {
            ClearSuggestion();
        }

        CheckSuggestionUpdate(nCmdID);

        // make a copy of this so we can look at it after forwarding some commands
        uint commandID = nCmdID;
        char typedChar = char.MinValue;

        // make sure the input is a char before getting it
        if (pguidCmdGroup == VSConstants.VSStd2K &&
            nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR)
        {
            typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
        }

        // pass along the command so the char is added to the buffer
        int retVal =
            m_nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        bool handled = false;

        if (hasCompletionUpdated) { ClearSuggestion(); }
        // gets lsp completions on added character or deletions
        if (!typedChar.Equals(char.MinValue) || commandID == (uint)VSConstants.VSStd2KCmdID.RETURN || regenerateSuggestion)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    GetCompletion();
                }
                catch (Exception e)
                {
                    Debug.Write(e);
                }
            });
            handled = true;
        }
        else if (commandID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE ||
                 commandID == (uint)VSConstants.VSStd2KCmdID.DELETE)
        {
            ClearSuggestion();

            _ = Task.Run(() =>
            {
                try
                {
                    GetCompletion();
                }
                catch (Exception e)
                {
                    Debug.Write(e);
                }
            });
            handled = true;
        }

        if (handled) return VSConstants.S_OK;
        return retVal;
    }

    // clears the intellisense popup window
    void ClearCompletionSessions() { m_provider.CompletionBroker.DismissAllSessions(_view); }

    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        //package.LogAsync("QueeryStatus " + cCmds + " prgCmds = " + prgCmds + "pcmdText " + pCmdText);
        return m_nextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }

    public void Dispose()
    {
        if (_document != null)
        {
            _document.FileActionOccurred -= OnFileActionOccurred;
            _document.TextBuffer.ContentTypeChanged -= OnContentTypeChanged;
        }
        UpdateRequestTokenSource(null);
    }
}

[Export(typeof(IVsTextViewCreationListener))]
[Name("TextViewListener")]
[ContentType("code")]
[TextViewRole(PredefinedTextViewRoles.Document)]

internal class TextViewListener : IVsTextViewCreationListener
{
    // adapters are used to get the IVsTextViewAdapter from the IVsTextView
    [Import]
    internal IVsEditorAdaptersFactoryService AdapterService = null;

    // service provider is used to get the IVsServiceProvider which is needed to access lsp
    [Import]
    internal SVsServiceProvider ServiceProvider { get; set; }

    // CompletionBroker is used by intellisense (popups) to provide completion items.
    [Import]
    internal ICompletionBroker CompletionBroker {
        get; set;
    }

    // document factory is used to get information about the current text document such as filepath,
    // language, etc.
    [Import]
    internal ITextDocumentFactoryService documentFactory = null;

    internal static TextViewListener? Instance { get; private set; }

    public Dictionary<string, ITextDocument> documentDictionary = new Dictionary<string, ITextDocument>();
    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        Instance = this;
        ITextView textView = AdapterService.GetWpfTextView(textViewAdapter);
        if (textView == null) return;

        Func<CodeiumCompletionHandler> createCommandHandler = delegate()
        {
            return new CodeiumCompletionHandler(textViewAdapter, textView, this);
        };
        textView.TextBuffer.Properties.GetOrCreateSingletonProperty<CodeiumCompletionHandler>(typeof(CodeiumCompletionHandler), createCommandHandler);
    }
}
}
