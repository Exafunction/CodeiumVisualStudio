using CodeiumVS;
using CodeiumVS.Languages;
using CodeiumVS.Packets;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ApplicationInsights.Channel;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TextBox;

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

    public async void GetCompletion()
    {

        if (!package.IsSignedIn()) { return; }

        UpdateRequestTokenSource(new CancellationTokenSource());

        SnapshotPoint? caretPoint = _view.Caret.Position.Point.GetPoint(
            textBuffer => (!textBuffer.ContentType.IsOfType("projection")),
            PositionAffinity.Successor);
        if (!caretPoint.HasValue) { return; }

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

        if (res != VSConstants.S_OK) { return; }

        if (list != null && list.Count > 0)
        {
            Debug.Print("completions " + list.Count.ToString());

            string prefix = line.Substring(0, Math.Min(characterN, line.Length));

            List<Tuple<String, String>> suggestions;
            try
            {
                suggestions = ParseCompletion(list, text, line, prefix, characterN);
            }
            catch (Exception ex)
            {
                await package.LogAsync("Exception: " + ex.ToString());
                return;
            }

            SuggestionTagger tagger = GetTagger();
            if (suggestions != null && suggestions.Count > 0 && tagger != null)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                currentCompletionID = suggestions[0].Item2;

                tagger.SetSuggestion(suggestions[0].Item1, characterN);
            }

            await package.LogAsync("Generated " + list.Count + $" proposals");
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
            string end = text.Substring(endOffset);
            String completionText = completionItems[i].completion.text;
            if (!String.IsNullOrEmpty(end))
            {
                int endNewline = end.IndexOf('\r');
                endNewline = endNewline <= -1 ? end.IndexOf('\n') : endNewline;
                endNewline = endNewline <= -1 ? end.Length : endNewline;

                completionText = completionText + end.Substring(0, endNewline);
            }
            int offset = StringCompare.CheckSuggestion(completionText, prefix);
            if (offset < 0) { continue; }

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
        CodeiumVSPackage.EnsurePackageLoaded();
        package = CodeiumVSPackage.Instance;
        _view = view;
        m_provider = provider;
        var topBuffer = view.BufferGraph.TopBuffer;

        var projectionBuffer = topBuffer as IProjectionBufferBase;

        ITextBuffer textBuffer =
            projectionBuffer != null ? projectionBuffer.SourceBuffers[0] : topBuffer;
        provider.documentFactory.TryGetTextDocument(textBuffer, out _document);

        _document.FileActionOccurred += OnFileActionOccurred;
        _document.TextBuffer.ContentTypeChanged += OnContentTypeChanged;
        RefreshLanguage();

        _textViewAdapter = textViewAdapter;
        // add the command to the command chain
        textViewAdapter.AddCommandFilter(this, out m_nextCommandHandler);
        // ShowIntellicodeMsg();
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
        _language = Mapper.GetLanguage(_document.TextBuffer.ContentType,
                                       Path.GetExtension(_document.FilePath)?.Trim('.'));
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

        // let the other handlers handle automation functions
        if (VsShellUtilities.IsInAutomationFunction(m_provider.ServiceProvider))
        {
            return m_nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        // check for a commit character
        bool regenerateSuggestion = false;
        if (!hasCompletionUpdated && nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB)
        {

            var tagger = GetTagger();

            if (tagger != null)
            {
                if (tagger.IsSuggestionActive())
                {
                    // If there is an active Intellisense session, let that one get accepted first.
                    ICompletionSession session = m_provider.CompletionBroker.GetSessions(_view).FirstOrDefault();
                    if (session != null && session.SelectedCompletionSet != null)
                    {
                            tagger.ClearSuggestion();
                            regenerateSuggestion = true;
                    }
                    if (tagger.CompleteText())
                    {
                            ClearCompletionSessions();
                            OnSuggestionAccepted(currentCompletionID);
                            return VSConstants.S_OK;
                    }
                }
                else { tagger.ClearSuggestion(); }
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
            _ = Task.Run(() => GetCompletion());
            handled = true;
        }
        else if (commandID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE ||
                 commandID == (uint)VSConstants.VSStd2KCmdID.DELETE)
        {
            ClearSuggestion();

            _ = Task.Run(() => GetCompletion());
            handled = true;
        }

        if (handled) return VSConstants.S_OK;
        return retVal;
    }

    // clears the intellisense popup window
    void ClearCompletionSessions() { m_provider.CompletionBroker.DismissAllSessions(_view); }

    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        return m_nextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }

    public void Dispose()
    {
        _document.FileActionOccurred -= OnFileActionOccurred;
        _document.TextBuffer.ContentTypeChanged -= OnContentTypeChanged;
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

    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        ITextView textView = AdapterService.GetWpfTextView(textViewAdapter);
        if (textView == null) return;

        Func<CodeiumCompletionHandler> createCommandHandler = delegate()
        {
            return new CodeiumCompletionHandler(textViewAdapter, textView, this);
        };
        textView.Properties.GetOrCreateSingletonProperty(createCommandHandler);
    }
}
}
