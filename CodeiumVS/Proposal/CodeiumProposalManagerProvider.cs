using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace CodeiumVS;

[Export(typeof(ProposalManagerProviderBase))]
[Name("CodeiumProposalManagerProvider")]
[Order(Before = "InlineCSharpProposalManagerProvider")]
[Order(Before = "Highest Priority")]
[ContentType("any")]
internal class CodeiumProposalManagerProvider : ProposalManagerProviderBase
{
    public override Task<ProposalManagerBase?> GetProposalManagerAsync(ITextView view, CancellationToken cancel)
    {
        return CodeiumProposalManager.TryCreateAsync(view, this);
    }
}
