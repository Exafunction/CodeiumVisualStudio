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

#pragma warning disable CS0618  // Type or member is obsolete

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
    internal CodeiumProposalSourceProvider(ITextDocumentFactoryService textDocumentFactoryService,
                                           SuggestionServiceBase suggestionServiceBase)
    {
        _textDocumentFactoryService = textDocumentFactoryService;
        EventInfo? acceptedEvent = suggestionServiceBase.GetType().GetEvent(
            "SuggestionAcceptedInternal", BindingFlags.Instance | BindingFlags.Public);
        acceptedEvent?.AddEventHandler(suggestionServiceBase,
                                       new EventHandler<EventArgs>(OnSuggestionAccepted));
    }

    internal CodeiumProposalSource TryCreate(ITextView view)
    {
        IWpfTextView wpfView = null;
        wpfView = view as IWpfTextView;
        if (wpfView != null)
        {
            _textDocumentFactoryService.TryGetTextDocument(view.TextDataModel.DocumentBuffer,
                                                           out ITextDocument document);
            if (document != null && IsAbsolutePath(document.FilePath))
            {
                return view.Properties.GetOrCreateSingletonProperty(
                    typeof(CodeiumProposalSource),
                    () => new CodeiumProposalSource(wpfView, document));
            }
        }
        return null;
    }

    private static bool IsAbsolutePath(string path)
    {
        return Uri.TryCreate(path.Replace('/', '\\'), UriKind.Absolute, out _);
    }
    public override Task<ProposalSourceBase?> GetProposalSourceAsync(ITextView view,
                                                                     CancellationToken cancel)
    {
        return Task.FromResult<ProposalSourceBase?>(TryCreate(view));
    }

    private void OnSuggestionAccepted(object sender, EventArgs e)
    {
        // string proposalId = ((SuggestionAcceptedEventArgs)e).FinalProposal.ProposalId;

        // unfortunately in the SDK version 17.5.33428.388, there are no
        // SuggestionAcceptedEventArgs so we have to use reflection here

        FieldInfo? fieldFinalProposal =
            e.GetType().GetField("FinalProposal", BindingFlags.Instance | BindingFlags.Public);
        if (fieldFinalProposal == null) return;

        object finalProposal = fieldFinalProposal.GetValue(e);
        if (finalProposal == null) return;

        PropertyInfo? propertydProposalId = fieldFinalProposal.FieldType.GetProperty(
            "ProposalId", BindingFlags.Instance | BindingFlags.Public);
        if (propertydProposalId == null) return;

        if (propertydProposalId.GetValue(finalProposal) is not string proposalId) return;

        ThreadHelper.JoinableTaskFactory
            .RunAsync(async delegate {
                await CodeiumVSPackage.Instance.LogAsync($"Accepted completion {proposalId}");
                await CodeiumVSPackage.Instance.LanguageServer.AcceptCompletionAsync(proposalId);
            })
            .FireAndForget(true);
    }
}

#pragma warning restore CS0618  // Type or member is obsolete
