using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeiumVS.Languages;

namespace CodeiumVS;

#pragma warning disable CS0618  // Type or member is obsolete
internal class CodeiumProposalSource : ProposalSourceBase
{
    private readonly CodeiumVSPackage package;

    private readonly IWpfTextView _view;
    private readonly ITextDocument _document;

    private DateTime _lastRequest = DateTime.MinValue;

    private LangInfo _language;
    private CancellationTokenSource? _requestTokenSource;
    private readonly TimeSpan _intelliSenseDelay = TimeSpan.FromMilliseconds(250.0);

    internal CodeiumProposalSource(IWpfTextView view, ITextDocument document)
    {
        CodeiumVSPackage.EnsurePackageLoaded();
        package = CodeiumVSPackage.Instance;

        _view = view;
        _document = document;

        document.FileActionOccurred += OnFileActionOccurred;
        document.TextBuffer.ContentTypeChanged += OnContentTypeChanged;
        RefreshLanguage();
    }

    public override Task DisposeAsync()
    {
        _document.FileActionOccurred -= OnFileActionOccurred;
        _document.TextBuffer.ContentTypeChanged -= OnContentTypeChanged;
        UpdateRequestTokenSource(null);
        return base.DisposeAsync();
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

    public override async Task<ProposalCollectionBase> RequestProposalsAsync(
        VirtualSnapshotPoint caret, CompletionState completionState, ProposalScenario scenario,
        char triggeringCharacter, CancellationToken cancellationToken)
    {
        if (!package.IsSignedIn())
        {
            return new ProposalCollection("codeium", new List<Proposal>(0));
        }

        await package.LogAsync(
            $"RequestProposalsAsync - Language: {_language.Name}; Caret: {caret.Position.Position}; ASCII: {_document.Encoding.IsSingleByte}");

        CancellationTokenSource cancellationTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = cancellationTokenSource.Token;
        UpdateRequestTokenSource(cancellationTokenSource);

        if (completionState != null)
        {
            DateTime utcNow = DateTime.UtcNow;
            TimeSpan timeSpan = _intelliSenseDelay - (utcNow - _lastRequest);
            _lastRequest = utcNow;
            if (timeSpan > TimeSpan.Zero) { await Task.Delay(timeSpan, cancellationToken); }
        }
        try
        {
            string text = _document.TextBuffer.CurrentSnapshot.GetText();
            int cursorPosition = _document.Encoding.IsSingleByte
                                     ? caret.Position.Position
                                     : Utf16OffsetToUtf8Offset(text, caret.Position.Position);

            VirtualSnapshotPoint newCaret = caret.TranslateTo(caret.Position.Snapshot);

            IList<Packets.CompletionItem>? list = await package.LanguageServer.GetCompletionsAsync(
                _document.FilePath,
                text,
                _language,
                cursorPosition,
                _view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId),
                _view.Options.GetOptionValue(DefaultOptions.TabSizeOptionId),
                _view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId),
                cancellationToken);

            if (list != null && list.Count > 0)
            {
                await package.LogAsync("Generated " + list.Count + $" proposals");
            }

            return ProposalsFromCompletions(list, newCaret, completionState, text);
        }
        catch (Exception ex)
        {
            await package.LogAsync(
                $"Encountered exception when generating completions: {ex.Message} {ex}");
            return new ProposalCollection("codeium", new List<Proposal>(0));
        }
    }

    private void UpdateRequestTokenSource(CancellationTokenSource newSource)
    {
        CancellationTokenSource cancellationTokenSource =
            Interlocked.Exchange(ref _requestTokenSource, newSource);
        if (cancellationTokenSource != null)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }
    }

    internal ProposalCollectionBase ProposalsFromCompletions(
        IList<Packets.CompletionItem> completionItems, VirtualSnapshotPoint caret,
        CompletionState completionState, string text)
    {
        if (completionItems == null || completionItems.Count == 0)
        {
            return new ProposalCollection("codeium", new List<Proposal>(0));
        }

        List<Proposal> list = new(completionItems.Count);
        for (int i = 0; i < completionItems.Count; i++)
        {
            Packets.CompletionItem completionItem = completionItems[i];
            int startOffset = (int)completionItem.range.startOffset;
            int endOffset = (int)completionItem.range.endOffset;
            int insertionStart = (int)completionItem.completionParts[0].offset;

            if (!_document.Encoding.IsSingleByte)
            {
                startOffset = Utf8OffsetToUtf16Offset(text, startOffset);
                endOffset = Utf8OffsetToUtf16Offset(text, endOffset);
                insertionStart = Utf8OffsetToUtf16Offset(text, insertionStart);
            }

            string text2 =
                caret.IsInVirtualSpace
                    ? completionItems[i].completion.text.TrimStart()
                    : completionItems[i].completion.text.Substring(insertionStart - startOffset);

            if (completionState != null)
            {
                string text3 =
                    completionState.SelectedItem.Substring(completionState.ApplicableToSpan.Length);
                if (!text2.StartsWith(text3)) { continue; }
                text2 = text2.Substring(text3.Length);
            }

            SnapshotSpan span =
                new(caret.Position.Snapshot, insertionStart, endOffset - insertionStart);
            ProposedEdit[] array = [new ProposedEdit(span, text2)];

            Proposal proposal =
                new(null,
                    array,
                    caret,
                    completionState,
                    ProposalFlags.SingleTabToAccept | ProposalFlags.ShowCommitHighlight |
                        ProposalFlags.FormatAfterCommit,
                    null,
                    completionItem.completion.completionId);

            list.Add(proposal);
        }

        return new ProposalCollection("codeium", list);
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
}

#pragma warning restore CS0618  // Type or member is obsolete
