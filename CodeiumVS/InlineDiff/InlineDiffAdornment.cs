using CodeiumVS.Utilities;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace CodeiumVs.InlineDiff;

internal class InlineDiffAdornment : TextViewExtension<IWpfTextView, InlineDiffAdornment>, ILineTransformSource, IOleCommandTarget
{
#pragma warning disable CS0169, IDE0051 // The field 'InlineDiffAdornment._codeiumInlineDiffAdornment' is never used
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("CodeiumInlineDiffAdornment")]
    [Order(After = "Text")]
    private static readonly AdornmentLayerDefinition _codeiumInlineDiffAdornment;
#pragma warning restore CS0169, IDE0051 // The field 'InlineDiffAdornment._codeiumInlineDiffAdornment' is never used
    private static readonly LineTransform _defaultTransform = new(1.0);

    private readonly IAdornmentLayer _layer;
    private readonly IVsTextView _vsHostView;
    private readonly IOleCommandTarget _nextCommand;

    private readonly ITagAggregator<InterLineAdornmentTag> _tagAggregator;

    private InlineDiffView? _diffView = null;

    private ITrackingSpan? _leftTrackingSpan;
    private ITrackingSpan? _rightTrackingSpan;
    private ITrackingSpan? _trackingSpanExtended;
    private IProjectionBuffer? _leftProjectionBuffer;
    private IProjectionBuffer? _rightProjectionBuffer;
    private ITextBuffer? _rightSourceBuffer;

    private IVsWindowFrame? _rightWindowFrame;
    private ITextDocument? _rightDocument;

    private double _codeBlockHeight = 0;
    private LineTransform _lineTransform = _defaultTransform;

    public bool HasAdornment => _diffView != null;
    public bool IsAdornmentFocused => HasAdornment && (_diffView.ActiveView != null) && _diffView.ActiveView.VisualElement.IsKeyboardFocused;

    public InlineDiffAdornment(IWpfTextView view) : base(view)
    {
        _vsHostView = _hostView.ToIVsTextView();
        _layer = _hostView.GetAdornmentLayer("CodeiumInlineDiffAdornment");

        _hostView.Closed += HostView_OnClosed;
        _hostView.LayoutChanged += HostView_OnLayoutChanged;
        _hostView.ZoomLevelChanged += HostView_OnZoomLevelChanged;
        _hostView.Caret.PositionChanged += HostView_OnCaretPositionChanged;

        _vsHostView.AddCommandFilter(this, out _nextCommand);

        _tagAggregator = MefProvider.Instance.TagAggregatorFactoryService.CreateTagAggregator<InterLineAdornmentTag>(_hostView);
    }

    private void HostView_OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
    {
        if (HasAdornment)
        {
            _diffView.VisualElement.SetValue(CrispImage.ScaleFactorProperty, e.NewZoomLevel * 0.01);
        }
    }

    private void HostView_OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        if (!HasAdornment || IsAdornmentFocused) return;

        SnapshotSpan span = _leftTrackingSpan.GetSpan(_hostView.TextSnapshot);
        int position = e.NewPosition.BufferPosition.Position;
        if (position >= span.Start.Position && position <= span.End.Position)
        {
            SnapshotPoint point = new(_diffView.LeftView.TextSnapshot, position - span.Start.Position);
            _diffView.LeftView.Caret.MoveTo(point);
            _diffView.LeftVsView.SendExplicitFocus();
        }
    }

    private IProjectionBuffer CreateRightBuffer(
        string tempFileName,
        int position,
        int length,
        string replacement,
        out ITextBuffer sourceBuffer,
        out ITextDocument textDocument,
        out IVsWindowFrame windowFrame,
        out ITrackingSpan trackingSpan
        )
    {
        ThreadHelper.ThrowIfNotOnUIThread("CreateRightBuffer");

        // copy the snapshot into the temporary file
        using (StreamWriter writer = new(tempFileName, append: false, Encoding.UTF8))
        {
            _hostView.TextSnapshot.Write(writer);
        }
        // open the temporary file
        MefProvider.Instance.DocumentOpeningService.OpenDocumentViaProject(
            tempFileName, Guid.Empty, out var _, out var _, out var _, out windowFrame
        );

        VsShellUtilities.GetTextView(windowFrame).GetBuffer(out var sourceTextLines);
        Assumes.NotNull(sourceTextLines);

        sourceBuffer = MefProvider.Instance.EditorAdaptersFactoryService.GetDocumentBuffer(sourceTextLines);
        Assumes.NotNull(sourceBuffer);

        Assumes.True(
            MefProvider.Instance.TextDocumentFactoryService.TryGetTextDocument(sourceBuffer, out textDocument),
            "InlineDiffAdornment.CreateRightBuffer: Could not get text document for the temp file"
        );

        // apply the diff
        using ITextEdit textEdit = sourceBuffer.CreateEdit();
        textEdit.Replace(position, length, replacement);
        ITextSnapshot snapshot = textEdit.Apply();

        // tell visual studio this document has not changed, although it is
        textDocument.UpdateDirtyState(false, DateTime.UtcNow);

        // create the right projection buffer that projects onto the temporary file
        return CreateProjectionBuffer(snapshot, position, replacement.Length, out trackingSpan);
    }

    // get the extended tracking span of the diff, meaning up and down one line from the actual diff
    private void CalculateExtendedTrackingSpan(int position, int length)
    {
        ITextSnapshotLine startLine = _hostView.TextSnapshot.GetLineFromPosition(position);
        ITextSnapshotLine endLine = _hostView.TextSnapshot.GetLineFromPosition(position + length);

        // move up one line, only if it's possible to do so
        if (startLine.LineNumber > 0)
            startLine = _hostView.TextSnapshot.GetLineFromLineNumber(startLine.LineNumber - 1);

        // move down one line, you know the deal
        if (endLine.LineNumber <= _hostView.TextSnapshot.LineCount - 1)
            endLine = _hostView.TextSnapshot.GetLineFromLineNumber(endLine.LineNumber + 1);

        _trackingSpanExtended = CreateTrackingSpan(
            _hostView.TextSnapshot, startLine.Start.Position, endLine.End.Position - startLine.Start.Position
        );
    }

    // calculate the height, in pixel, of the left code block
    private void CalculateCodeBlockHeight()
    {
        SnapshotPoint pointStart = _leftTrackingSpan.GetStartPoint(_hostView.TextSnapshot);
        SnapshotPoint pointEnd = _leftTrackingSpan.GetEndPoint(_hostView.TextSnapshot);
        ITextViewLine lineStart = _hostView.TextViewLines.GetTextViewLineContainingBufferPosition(pointStart);
        ITextViewLine lineEnd = _hostView.TextViewLines.GetTextViewLineContainingBufferPosition(pointEnd);

        // the lines are out of view, so they're null
        if (lineStart != null && lineEnd != null)
        {
            _codeBlockHeight = lineEnd.TextBottom - lineStart.TextTop;
            return;
        }

        // get the text bounds instead, i'm not sure which is better
        int lineNoStart = _hostView.TextSnapshot.GetLineNumberFromPosition(pointStart.Position);
        int lineNoEnd = _hostView.TextSnapshot.GetLineNumberFromPosition(pointStart.Position);
        _codeBlockHeight = (lineNoEnd - lineNoStart) * _hostView.LineHeight;
    }

    // Create a tracking span, tracking span will reposition itself
    // when the user did something that offset it position, like adding new line before it
    private static ITrackingSpan CreateTrackingSpan(ITextSnapshot snapshot, int position, int length)
    {
        // don't use _hostView.TextViewLines.GetTextViewLineContainingBufferPosition here
        // because if the line is not currently on the screen, it won't be in the TextViewLines
        SnapshotPoint start = new(snapshot, position);
        SnapshotPoint end = new(snapshot, position + length);
        SnapshotSpan span = new(start.GetContainingLine().Start, end.GetContainingLine().End);
        return snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive);
    }

    // Create a projection buffer for the snapshot
    private static IProjectionBuffer CreateProjectionBuffer(ITextSnapshot snapshot, int position, int length, out ITrackingSpan trackingSpan)
    {
        trackingSpan = CreateTrackingSpan(snapshot, position, length);
        return MefProvider.Instance.ProjectionBufferFactoryService.CreateProjectionBuffer(
            null, new List<object>(1) { trackingSpan }, ProjectionBufferOptions.PermissiveEdgeInclusiveSourceSpans
        );
    }

    public void CreateDiff(int position, int length, string replacement)
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await CreateDiffAsync(position, length, replacement);
        });
    }

    public async Task CreateDiffAsync(int position, int length, string replacement)
    {
        await DisposeDiffAsync();

        // for the OpenDocumentViaProject function
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        Assumes.True(position > 0 && length > 0 && (position + length) <= _hostView.TextSnapshot.Length,
            "InlineDiffAdornment.CreateDiff: Invalid position and length parameter"
        );
        Assumes.True(
            MefProvider.Instance.TextDocumentFactoryService.TryGetTextDocument(_hostView.TextDataModel.DocumentBuffer, out var textDocument),
            "InlineDiffAdornment.CreateDiff: Could not get text document for the current host view"
        );

        // create a temporary file to store the diff
        string rightFileName = Path.GetTempFileName() + Path.GetExtension(textDocument.FilePath);

        // create the projection buffers, left projects onto host view, right projects onto a temp file
        _leftProjectionBuffer = CreateProjectionBuffer(_hostView.TextSnapshot, position, length, out _leftTrackingSpan);
        _rightProjectionBuffer = CreateRightBuffer(
            rightFileName, position, length, replacement, out _rightSourceBuffer, out _rightDocument, out _rightWindowFrame, out _rightTrackingSpan
        );

        _diffView = new InlineDiffView(_hostView, _leftProjectionBuffer, _hostView.TextDataModel.DocumentBuffer, _rightProjectionBuffer, _rightSourceBuffer);
        _diffView.VisualElement.SizeChanged += DiffAdornment_OnSizeChanged;
        _diffView.VisualElement.OnAccepted = DiffView_OnAccepted;
        _diffView.VisualElement.OnRejected = DiffView_OnRejected;

        _diffView.VisualElement.SetValue(CrispImage.ScaleFactorProperty, _hostView.ZoomLevel * 0.01);

        if (!_hostView.Roles.Contains(PredefinedTextViewRoles.EmbeddedPeekTextView) && IsPeekOnDiffView())
            MefProvider.Instance.PeekBroker.DismissPeekSession(_hostView);

        if (MefProvider.Instance.CompletionBroker.IsCompletionActive(_hostView))
            MefProvider.Instance.CompletionBroker.DismissAllSessions(_hostView);

        if (MefProvider.Instance.AsyncCompletionBroker.IsCompletionActive(_hostView))
            MefProvider.Instance.AsyncCompletionBroker.GetSession(_hostView)?.Dismiss();

        CalculateExtendedTrackingSpan(position, length);
        CalculateCodeBlockHeight();
        RefreshLineTransform();
        UpdateAdornment();
    }

    private void DiffView_OnAccepted()
    {
        using (ITextEdit textEdit = _leftProjectionBuffer.CreateEdit())
        {
            textEdit.Replace(0, _leftProjectionBuffer.CurrentSnapshot.Length, _rightProjectionBuffer.CurrentSnapshot.GetText());
            textEdit.Apply();
        }

        DisposeDiff();
    }

    private void DiffView_OnRejected()
    {
        DisposeDiff();
    }

    public async Task DisposeDiffAsync()
    {
        _diffView?.Dispose();
        if (_rightWindowFrame != null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _rightWindowFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);

            File.Delete(_rightDocument.FilePath);
            _rightWindowFrame = null;

        }

        _diffView = null;

        _leftTrackingSpan = null;
        _rightTrackingSpan = null;
        _trackingSpanExtended = null;

        _leftProjectionBuffer = null;
        _rightProjectionBuffer = null;

        _rightSourceBuffer = null;
        _codeBlockHeight = 0;

        UpdateAdornment();
        RefreshLineTransform();
    }

    public void DisposeDiff()
    {
        ThreadHelper.JoinableTaskFactory.Run(DisposeDiffAsync);
    }

    private void HostView_OnClosed(object sender, EventArgs e)
    {
        DisposeDiff();
        _hostView.Closed -= HostView_OnClosed;
        _hostView.LayoutChanged -= HostView_OnLayoutChanged;
        _hostView.ZoomLevelChanged -= HostView_OnZoomLevelChanged;
        _hostView.Caret.PositionChanged -= HostView_OnCaretPositionChanged;
    }

    private void RefreshLineTransform()
    {
        // this triggers the GetLineTransform so it refreshes, we had to use some magic once again
        // `(ViewRelativePosition)4` is some undocumented enum i found on an internal source
        // that doesn't actually "display" the position, but only refreshes stuffs
        _hostView.DisplayTextLineContainingBufferPosition(default, 0.0, (ViewRelativePosition)4);
    }

    private void DiffAdornment_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _lineTransform = new LineTransform(0.0, e.NewSize.Height - _codeBlockHeight, 1.0);
        RefreshLineTransform();
    }

    private void HostView_OnLayoutChanged(object sender, EventArgs e)
    {
        if (!HasAdornment) return;

        CalculateCodeBlockHeight();
        UpdateAdornment();
    }

    // Update the position and width of the adornment
    private void UpdateAdornment()
    {
        if (!HasAdornment)
        {
            _layer.RemoveAllAdornments();
            return;
        }

        SnapshotPoint point = _leftTrackingSpan.GetStartPoint(_hostView.TextSnapshot);
        IWpfTextViewLine containningLine = _hostView.TextViewLines.GetTextViewLineContainingBufferPosition(point);
        if (containningLine == null) return;

        double glyphWidth = _hostView.FormattedLineSource.ColumnWidth;
        Canvas.SetLeft(_diffView.VisualElement, -glyphWidth);
        Canvas.SetTop(_diffView.VisualElement, containningLine.TextTop);
        _diffView.VisualElement.Width = _hostView.ViewportWidth + glyphWidth;

        _diffView.VisualElement.SetContentBorderLeftMargin(glyphWidth + 1);
        // Note that if we remove the adornments before calling `GetTextViewLineContainingBufferPosition`
        // it will return null, because the text view line got removed from TextViewLines
        _layer.RemoveAllAdornments();
        _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _diffView.VisualElement, null);
    }


    LineTransform ILineTransformSource.GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
    {
        if (_leftTrackingSpan != null)
        {
            SnapshotPoint pointEnd = _leftTrackingSpan.GetEndPoint(_hostView.TextSnapshot);
            if (pointEnd.Position >= line.Extent.Start && pointEnd.Position <= line.Extent.End)
            {
                return _lineTransform;
            }
        }
        return _defaultTransform;
    }

    // Is there a "peek definition" window intersect with the diff view
    private bool IsPeekOnDiffView()
    {
        ThreadHelper.ThrowIfNotOnUIThread("IsPeekOnAnchor");

        SnapshotSpan extent = _leftTrackingSpan.GetSpan(_hostView.TextSnapshot);

        foreach (IMappingTagSpan<InterLineAdornmentTag> tag in _tagAggregator.GetTags(extent))
        {
            if (!tag.Tag.IsAboveLine && tag.Span.GetSpans(_hostView.TextSnapshot).IntersectsWith(extent))
            {
                return true;
            }
        }
        return false;
    }

#pragma warning disable VSTHRD010 // Invoke single-threaded types on Main thread

    // By default, the adornments doesn't received the keyboard inputs it deserved, sadly.
    // We have to "hook" the host view commands filter list and check if our adornments
    // are focused, and if so, we pass the command to them.
    //
    // I had spent an embarrassing amount of time to come up with this solution.
    //
    // A good reference: https://joshvarty.com/2014/08/01/ripping-the-visual-studio-editor-apart-with-projection-buffers/

    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        IOleCommandTarget _commandTarget = IsAdornmentFocused ?
            _diffView.ActiveVsView as IOleCommandTarget : _nextCommand;

        return _commandTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }

    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        IOleCommandTarget _commandTarget = IsAdornmentFocused ?
            _diffView.ActiveVsView as IOleCommandTarget : _nextCommand;

        // handle caret transistion from the diff view to host view
        if (pguidCmdGroup == VSConstants.CMDSETID.StandardCommandSet2K_guid && IsAdornmentFocused)
        {
            bool isUp = (nCmdID == (uint)VSConstants.VSStd2KCmdID.UP);
            bool isDown = (nCmdID == (uint)VSConstants.VSStd2KCmdID.DOWN);
            bool isRight = (nCmdID == (uint)VSConstants.VSStd2KCmdID.RIGHT);
            bool isLeft = (nCmdID == (uint)VSConstants.VSStd2KCmdID.LEFT);

            var view = _diffView.ActiveView;
            int position = view.Caret.Position.BufferPosition.Position;
            int lineNo = view.TextSnapshot.GetLineNumberFromPosition(position);

            SnapshotPoint? point = null;

            if (isUp && lineNo == 0)
            {
                point = _trackingSpanExtended.GetStartPoint(_hostView.TextSnapshot);
            }
            else if (isDown && lineNo == view.TextSnapshot.LineCount - 1)
            {
                point = _trackingSpanExtended.GetEndPoint(_hostView.TextSnapshot);
            }
            else if (isLeft && position == 0)
            {
                point = _trackingSpanExtended.GetStartPoint(_hostView.TextSnapshot);
                var viewLine = _hostView.TextViewLines.GetTextViewLineContainingBufferPosition(point.Value);
                point = viewLine.End;
            }
            else if (isRight && position == view.TextSnapshot.Length - 1)
            {
                point = _trackingSpanExtended.GetEndPoint(_hostView.TextSnapshot);
                var viewLine = _hostView.TextViewLines.GetTextViewLineContainingBufferPosition(point.Value);
                point = viewLine.Start;
            }

            if (point.HasValue)
            {
                if ((isUp || isDown))
                {
                    ITextViewLine viewLine = _hostView.TextViewLines.GetTextViewLineContainingBufferPosition(point.Value);
                    _hostView.Caret.MoveTo(viewLine);
                }
                else
                {
                    _hostView.Caret.MoveTo(point.Value);
                }

                _hostView.Caret.EnsureVisible();
                _hostView.VisualElement.Focus();
            }
        }

        return _commandTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
    }
#pragma warning restore VSTHRD010 // Invoke single-threaded types on Main thread
}

[Export(typeof(ILineTransformSourceProvider))]
[Name("CodeiumInlineDiffViewProvider")]
[ContentType("code")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal class CodeiumInlineDiffViewProvider : ILineTransformSourceProvider
{
    public ILineTransformSource Create(IWpfTextView textView)
    {
        return (textView.Roles.Contains(InlineDiffView.Role)) ? null : new InlineDiffAdornment(textView);
    }
}
