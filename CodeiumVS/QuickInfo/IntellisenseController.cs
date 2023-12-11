using CodeiumVS.Utilities;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace CodeiumVS.QuickInfo;

internal class IntellisenseController : TextViewExtension<ITextView, IntellisenseController>, IIntellisenseController
{
    private readonly IList<ITextBuffer> _subjectBuffer;

    internal IntellisenseController(ITextView hostView, IList<ITextBuffer> subjectBuffers) : base(hostView)
    {
        _subjectBuffer = subjectBuffers;
        _hostView.MouseHover += OnTextViewMouseHover;
    }

    private void OnTextViewMouseHover(object sender, MouseHoverEventArgs e)
    {
        // already active
        if (MefProvider.Instance.AsyncQuickInfoBroker.IsQuickInfoActive(_hostView)) return;

        // find the mouse position by mapping down to the subject buffer
        SnapshotPoint? point = _hostView.BufferGraph.MapDownToFirstMatch(
            new SnapshotPoint(_hostView.TextSnapshot, e.Position),
            PointTrackingMode.Positive,
            snapshot => _subjectBuffer.Contains(snapshot.TextBuffer),
            PositionAffinity.Predecessor
        );

        if (!point.HasValue) return;

        // make a tracking point of the source
        ITrackingPoint triggerPoint = point.Value.Snapshot.CreateTrackingPoint(
            point.Value.Position, PointTrackingMode.Positive
        );

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await MefProvider.Instance.AsyncQuickInfoBroker.TriggerQuickInfoAsync(
                _hostView, triggerPoint, QuickInfoSessionOptions.TrackMouse
            );
        });
    }

    public void Detach(ITextView textView)
    {
        if (_hostView == textView)
        {
            _hostView.MouseHover -= OnTextViewMouseHover;
            base.Dispose();
        }
    }

    public void ConnectSubjectBuffer(ITextBuffer subjectBuffer)
    {
    }

    public void DisconnectSubjectBuffer(ITextBuffer subjectBuffer)
    {
    }
}

// This causes it to call `TriggerQuickInfoAsync` too early and dead lock
// the UI, I'm not sure what happened and how should we check if quickinfo
// is ready yet, but we don't need this right now, leave here for the future
//
//[Export(typeof(IIntellisenseControllerProvider))]
//[Name("Codeium Intellisense Controller Provider")]
//[ContentType("any")]
//internal sealed class IntellisenseControllerProvider : IIntellisenseControllerProvider
//{
//    public IIntellisenseController TryCreateIntellisenseController(ITextView textView, IList<ITextBuffer> subjectBuffers)
//    {
//        return new IntellisenseController(textView, subjectBuffers);
//    }
//}