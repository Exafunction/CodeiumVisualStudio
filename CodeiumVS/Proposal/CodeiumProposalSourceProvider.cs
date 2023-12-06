using System;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using CodeiumVS;

namespace CodeiumVS;

// this get called first
[Export(typeof(CodeiumProposalSourceProvider))]
[Export(typeof(ProposalSourceProviderBase))]
[Name("CodeiumProposalSourceProvider")]
[Order(Before = "InlineCSharpProposalSourceProvider")]
[Order(Before = "Highest Priority")]
[ContentType("any")]
internal class CodeiumProposalSourceProvider : ProposalSourceProviderBase
{
    private readonly ITextDocumentFactoryService _textDocumentFactoryService;

    [ImportingConstructor]
    internal CodeiumProposalSourceProvider(ITextDocumentFactoryService textDocumentFactoryService, SuggestionServiceBase suggestionServiceBase)
    {
        _textDocumentFactoryService = textDocumentFactoryService;
        suggestionServiceBase.GetType().GetEvent("SuggestionAcceptedInternal", BindingFlags.Instance | BindingFlags.Public)?.AddEventHandler(suggestionServiceBase, new EventHandler<EventArgs>(OnSuggestionAccepted));
    }

    internal CodeiumProposalSource TryCreate(ITextView view)
    {
        IWpfTextView wpfView = null;
        wpfView = view as IWpfTextView;
        if (wpfView != null)
        {
            ITextDocument document = null;
            _textDocumentFactoryService.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out document);
            if (document != null && IsAbsolutePath(document.FilePath))
            {
                return view.Properties.GetOrCreateSingletonProperty(typeof(CodeiumProposalSource), () => new CodeiumProposalSource(wpfView, document));
            }
        }
        return null;
    }

    private static bool IsAbsolutePath(string path)
    {
        return Uri.TryCreate(path.Replace('/', '\\'), UriKind.Absolute, out _);
    }
    public override Task<ProposalSourceBase?> GetProposalSourceAsync(ITextView view, CancellationToken cancel)
    {
        return Task.FromResult<ProposalSourceBase?>(TryCreate(view));
    }

    private void OnSuggestionAccepted(object sender, EventArgs e)
    {
        string proposalId = ((SuggestionAcceptedEventArgs)e).FinalProposal.ProposalId;

        CodeiumVSPackage.Instance.Log("Accepted completion " + proposalId);
        _ = CodeiumVSPackage.Instance.LanguageServer.AcceptCompletionAsync(proposalId);
    }
}
