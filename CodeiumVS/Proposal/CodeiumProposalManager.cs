using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace CodeiumVS;

#pragma warning disable CS0618 // Type or member is obsolete
internal class CodeiumProposalManager : ProposalManagerBase
{
    private readonly IWpfTextView view;

    private readonly CodeiumProposalManagerProvider factory;

    internal static Task<ProposalManagerBase> TryCreateAsync(ITextView view,
                                                             CodeiumProposalManagerProvider factory)
    {
        if (view is IWpfTextView wpfTextView)
        {
            return Task.FromResult(
                (ProposalManagerBase) new CodeiumProposalManager(wpfTextView, factory));
        }
        return Task.FromResult<ProposalManagerBase>(null);
    }

    private CodeiumProposalManager(IWpfTextView view, CodeiumProposalManagerProvider factory)
    {
        this.view = view;
        this.factory = factory;
    }

    private static readonly char[] LineBreakCharacters = ['\r', '\n', '\u0085', '\u2028', '\u2029'];
    public override bool TryGetIsProposalPosition(VirtualSnapshotPoint caret,
                                                  ProposalScenario scenario, char triggerCharacter,
                                                  ref bool value)
    {
        switch (scenario)
        {
        case ProposalScenario.Return:
            if (caret.Position.GetContainingLine().End == caret.Position) value = true;
            break;

        case ProposalScenario.TypeChar:
            if (char.IsWhiteSpace(triggerCharacter) && caret.Position.Position >= 2)
            {
                char c = caret.Position.Snapshot[caret.Position.Position - 2];
                if (LineBreakCharacters.Contains(c) || !char.IsWhiteSpace(c)) value = true;
            }
            else { value = true; }
            break;

        case ProposalScenario.CaretMove:
            value = false;
            break;

        default:
            value = true;
            break;
        }

        return value;
    }
}

#pragma warning restore CS0618 // Type or member is obsolete
