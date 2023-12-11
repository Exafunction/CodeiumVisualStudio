using CodeiumVS;
using CodeiumVS.Utilities;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.GraphModel.CodeSchema;
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
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace CodeiumVs.InlineDiff;

internal class InlineDiffAdornment : TextViewExtension<IWpfTextView, InlineDiffAdornment>, ILineTransformSource, IOleCommandTarget
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("CodeiumInlineDiffAdornment")]
    [Order(After = "Text")]
    private static readonly AdornmentLayerDefinition _codeiumInlineDiffAdornment;

    private static readonly LineTransform _defaultTransform   = new(1.0);
    private static MethodInfo? _fnSetInterceptsAggregateFocus = null;

    private readonly IAdornmentLayer   _layer;
    private readonly IVsTextView       _vsHostView;
    private readonly IOleCommandTarget _nextCommand;

    private InlineDiffView?            _diffView = null;

    private ITrackingSpan?             _leftTrackingSpan;
    private ITrackingSpan?             _rightTrackingSpan;
    private ITrackingSpan?             _trackingSpanExtended;

    private IProjectionBuffer?         _leftProjectionBuffer;
    private IProjectionBuffer?         _rightProjectionBuffer;

    private IReadOnlyRegion?           _leftReadOnlyRegion;

    private ITextBuffer?               _rightSourceBuffer;
    private IVsWindowFrame?            _rightWindowFrame;
    private ITextDocument?             _rightTextDocument;

    private double _adornmentTop    = 0;
    private double _codeBlockHeight = 0;

    private LineTransform _lineTransform = _defaultTransform;
    private ITagAggregator<InterLineAdornmentTag>? _tagAggregator = null;


    public bool HasAdornment => _diffView != null;
    public bool IsAdornmentFocused => HasAdornment && (_diffView.ActiveView != null) && _diffView.ActiveView.VisualElement.IsKeyboardFocused;

    public InlineDiffAdornment(IWpfTextView view) : base(view)
    {
        _vsHostView = _hostView.ToIVsTextView();
        _layer = _hostView.GetAdornmentLayer("CodeiumInlineDiffAdornment");

        _hostView.Closed                += HostView_OnClosed;
        _hostView.LayoutChanged         += HostView_OnLayoutChanged;
        _hostView.ViewportLeftChanged   += HostView_OnLayoutChanged;
        _hostView.ViewportWidthChanged  += HostView_OnLayoutChanged;
        _hostView.ZoomLevelChanged      += HostView_OnZoomLevelChanged;
        _hostView.Caret.PositionChanged += HostView_OnCaretPositionChanged;

        _vsHostView.AddCommandFilter(this, out _nextCommand);

        // attempt to get the `AggregateFocusInterceptor.GetInterceptsAggregateFocus`
        // method in the Microsoft.VisualStudio.Text.Internal.dll
        if (_fnSetInterceptsAggregateFocus == null)
        {
            try
            {
                string name = "Microsoft.VisualStudio.Text.Internal";
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly => assembly.GetName().Name == name);

                Type type = assembly?.GetType("Microsoft.VisualStudio.Text.Editor.AggregateFocusInterceptor");
                _fnSetInterceptsAggregateFocus = type?.GetMethod("SetInterceptsAggregateFocus", BindingFlags.Static | BindingFlags.Public);
            }
            catch (Exception ex)
            {
                CodeiumVSPackage.Instance?.Log($"InlineDiffAdornment: Failed to get the SetInterceptsAggregateFocus method; Exception: {ex}");
            }
        }
    }

    // Create a difference view at a given position, any current diff will be disposed
    public async Task CreateDiffAsync(int position, int length, string replacement)
    {
        await DisposeDiffAsync();

        // for the OpenDocumentViaProject and IsPeekOnDiffView
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
        CreateLeftProjectionBuffer(position, length);
        CreateRightProjectionBuffer(rightFileName, position, length, replacement);

        _diffView = new InlineDiffView(_hostView, _leftProjectionBuffer, _hostView.TextDataModel.DocumentBuffer, _rightProjectionBuffer, _rightSourceBuffer);

        _diffView.VisualElement.GotFocus    += DiffView_OnGotFocus;
        _diffView.VisualElement.LostFocus   += DiffView_OnLostFocus;
        _diffView.VisualElement.SizeChanged += DiffView_OnSizeChanged;
        _diffView.VisualElement.OnAccepted   = DiffView_OnAccepted;
        _diffView.VisualElement.OnRejected   = DiffView_OnRejected;

        // set the scale factor for CrispImage, without this, it'll be blurry
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

    // Create a difference view at a given position, any current diff will be disposed
    public void CreateDiff(int position, int length, string replacement)
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await CreateDiffAsync(position, length, replacement);
        });
    }

    // Dispose the current diff
    public async Task DisposeDiffAsync()
    {
        _diffView?.Dispose();
        if (_rightWindowFrame != null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _rightWindowFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);

            File.Delete(_rightTextDocument.FilePath);

        }

        if (_leftReadOnlyRegion != null)
        {
            using IReadOnlyRegionEdit readOnlyRegionEdit = _hostView.TextBuffer.CreateReadOnlyRegionEdit();
            readOnlyRegionEdit.RemoveReadOnlyRegion(_leftReadOnlyRegion);
            readOnlyRegionEdit.Apply();
        }

        _diffView              = null;

        _leftTrackingSpan      = null;
        _rightTrackingSpan     = null;
        _trackingSpanExtended  = null;

        _leftProjectionBuffer  = null;
        _rightProjectionBuffer = null;

        _leftReadOnlyRegion    = null;
        _rightSourceBuffer     = null;
        _rightWindowFrame      = null;
        _codeBlockHeight       = 0;

        UpdateAdornment();
        RefreshLineTransform();
    }

    // Dispose the current diff
    public void DisposeDiff()
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(DisposeDiffAsync).FireAndForget(true);
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

        List<object> sourceSpans = [trackingSpan];
        var options = ProjectionBufferOptions.PermissiveEdgeInclusiveSourceSpans;

        return MefProvider.Instance.ProjectionBufferFactoryService.CreateProjectionBuffer(null, sourceSpans, options);
    }

    private void CreateLeftProjectionBuffer(int position, int length)
    {
        _leftProjectionBuffer = CreateProjectionBuffer(_hostView.TextSnapshot, position, length, out _leftTrackingSpan);

        // make sure that the user cannot edit the left buffer while we're showing the diff
        using IReadOnlyRegionEdit readOnlyRegionEdit = _hostView.TextBuffer.CreateReadOnlyRegionEdit();

        _leftReadOnlyRegion = readOnlyRegionEdit.CreateReadOnlyRegion(
            _leftTrackingSpan.GetSpan(_hostView.TextSnapshot),
            SpanTrackingMode.EdgeInclusive, EdgeInsertionMode.Deny
        );

        readOnlyRegionEdit.Apply();
    }

    // Create a temporary projection buffer for the right side,
    // it will be disposed when the diff is disposed
    private void CreateRightProjectionBuffer(string tempFileName, int position, int length, string replacement)
    {
        ThreadHelper.ThrowIfNotOnUIThread("CreateRightBuffer");

        // copy the snapshot into the temporary file
        using (StreamWriter writer = new(tempFileName, append: false, Encoding.UTF8))
        {
            _hostView.TextSnapshot.Write(writer);
        }

        // open the temporary file
        int openingResult = MefProvider.Instance.DocumentOpeningService.OpenDocumentViaProject(
            tempFileName, Guid.Empty, out var _, out var _, out var _, out _rightWindowFrame
        );

        Assumes.True(ErrorHandler.Succeeded(openingResult),
            "InlineDiffAdornment.CreateRightProjectionBuffer: Could not open the document for temporary file"
        );

        VsShellUtilities.GetTextView(_rightWindowFrame).GetBuffer(out var sourceTextLines);
        Assumes.True(sourceTextLines != null,
            "InlineDiffAdornment.CreateRightProjectionBuffer: Could not get source text lines"
        );

        _rightSourceBuffer = MefProvider.Instance.EditorAdaptersFactoryService.GetDocumentBuffer(sourceTextLines);

        Assumes.True(_rightSourceBuffer != null,
            "InlineDiffAdornment.CreateRightProjectionBuffer: Could not create source buffer"
        );

        Assumes.True(
            MefProvider.Instance.TextDocumentFactoryService.TryGetTextDocument(_rightSourceBuffer, out _rightTextDocument),
            "InlineDiffAdornment.CreateRightProjectionBuffer: Could not get text document for the temp file"
        );

        // apply the diff
        using ITextEdit textEdit = _rightSourceBuffer.CreateEdit();
        textEdit.Replace(position, length, replacement);
        ITextSnapshot snapshot = textEdit.Apply();

        // tell visual studio this document has not changed, although it is
        _rightTextDocument.UpdateDirtyState(false, DateTime.UtcNow);

        // create the right projection buffer that projects onto the temporary file
        _rightProjectionBuffer = CreateProjectionBuffer(snapshot, position, replacement.Length, out _rightTrackingSpan);
    }

    // Get the extended tracking span of the diff, meaning up and down one line from the actual diff
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

    // Calculate the height, in pixel, of the left code block
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

        // caculate the height from the line count
        int lineNoStart = _hostView.TextSnapshot.GetLineNumberFromPosition(pointStart.Position);
        int lineNoEnd = _hostView.TextSnapshot.GetLineNumberFromPosition(pointEnd.Position);
        _codeBlockHeight = (lineNoEnd - lineNoStart) * _hostView.LineHeight;
    }

    // We need to refresh the adornment when the host view changes its layout
    private void HostView_OnLayoutChanged(object sender, EventArgs e)
    {
        if (!HasAdornment) return;

        CalculateCodeBlockHeight();
        UpdateAdornment();
    }

    // Set the scale factor for CrispImage, without this, it'll be blurry
    private void HostView_OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
    {
        if (!HasAdornment) return;

        _diffView.VisualElement.SetValue(CrispImage.ScaleFactorProperty, e.NewZoomLevel * 0.01);
        CalculateCodeBlockHeight();
        UpdateAdornment();
    }

    // Attemp to make a smooth caret transistion into the diff view
    private void HostView_OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        if (!HasAdornment || IsAdornmentFocused || (_diffView.ActiveView == null)) return;

        SnapshotSpan span = _leftTrackingSpan.GetSpan(_hostView.TextSnapshot);
        int position = e.NewPosition.BufferPosition.Position;

        if (position >= span.Start.Position && position <= span.End.Position)
        {
            SnapshotPoint point = new(_diffView.ActiveView.TextSnapshot, position - span.Start.Position);
            _diffView.ActiveView.Caret.MoveTo(point);
            _diffView.ActiveVsView?.SendExplicitFocus();
        }
    }

    private void HostView_OnClosed(object sender, EventArgs e)
    {
        DisposeDiff();
        _hostView.Closed                -= HostView_OnClosed;
        _hostView.LayoutChanged         -= HostView_OnLayoutChanged;
        _hostView.ViewportLeftChanged   -= HostView_OnLayoutChanged;
        _hostView.ViewportWidthChanged  -= HostView_OnLayoutChanged;
        _hostView.ZoomLevelChanged      -= HostView_OnZoomLevelChanged;
        _hostView.Caret.PositionChanged -= HostView_OnCaretPositionChanged;
    }

    // The proposed diff has been accepted by the user
    private void DiffView_OnAccepted()
    {
        using (ITextEdit textEdit = _leftProjectionBuffer.CreateEdit())
        {
            textEdit.Replace(0, _leftProjectionBuffer.CurrentSnapshot.Length, _rightProjectionBuffer.CurrentSnapshot.GetText());
            textEdit.Apply();
        }

        DisposeDiff();
    }

    // The proposed diff has been rejected by the user
    private void DiffView_OnRejected()
    {
        DisposeDiff();
    }

    private void DiffView_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _lineTransform = new LineTransform(0.0, e.NewSize.Height - _codeBlockHeight, 1.0);
        RefreshLineTransform();
    }

    private void DiffView_OnLostFocus(object sender, RoutedEventArgs e)
    {
        SetHostViewInterceptsAggregateFocus(false);
    }

    private void DiffView_OnGotFocus(object sender, RoutedEventArgs e)
    {
        SetHostViewInterceptsAggregateFocus(true);
        _diffView.ActiveVsView?.SendExplicitFocus();
    }

    // Intercept the focus of the host view
    private void SetHostViewInterceptsAggregateFocus(bool intercept)
    {
        if (_fnSetInterceptsAggregateFocus == null) return;
        try
        {
            _fnSetInterceptsAggregateFocus.Invoke(null, [_hostView as DependencyObject, intercept]);
        }
        catch (Exception ex)
        {
            CodeiumVSPackage.Instance?.Log($"InlineDiffAdornment: SetHostViewInterceptsAggregateFocus({intercept}) failed; Exception: {ex}");
        }
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
        if (containningLine != null)
            _adornmentTop = containningLine.TextTop;

        // set the buttons position to be on top of the diff view if it's visible
        bool btnsShouldOnTop = _adornmentTop > _hostView.ViewportTop;
        if (_diffView.VisualElement.AreButtonsOnTop != btnsShouldOnTop)
        {
            _diffView.VisualElement.AreButtonsOnTop = btnsShouldOnTop;

            // make sure it updates
            _hostView.QueuePostLayoutAction(RefreshLineTransform);
        }
        

        //double glyphWidth = _hostView.FormattedLineSource.ColumnWidth;
        Canvas.SetLeft(_diffView.VisualElement, 0);
        Canvas.SetTop(_diffView.VisualElement, _adornmentTop - _diffView.VisualElement.TopOffset);

        // `_hostView.ViewportLeft` is the horizontal scroll position
        _diffView.VisualElement.Width = _hostView.ViewportLeft + _hostView.ViewportWidth;

        // Note that if we remove the adornments before calling `GetTextViewLineContainingBufferPosition`
        // it will return null, because the text view line got removed from TextViewLines

        // I don't even know why we have to remove the adornment before adding it again
        // no documentation mentioned ANYTHING about this. All code snippets and even
        // the official overview do this. But here's the thing: it works just fine even
        // if we removed these two lines. Why, microsoft, why?
        _layer.RemoveAllAdornments();
        _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _diffView.VisualElement, null);
    }

    private void RefreshLineTransform()
    {
        // this triggers the GetLineTransform so it refreshes, we had to use some magic once again
        // `(ViewRelativePosition)4` is some undocumented enum i found on an internal source
        // that doesn't actually "display" the position, but only refreshes stuffs
        _hostView.DisplayTextLineContainingBufferPosition(default, 0.0, (ViewRelativePosition)4);
    }

    LineTransform ILineTransformSource.GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
    {
        if (_leftTrackingSpan != null)
        {
            SnapshotPoint point = _leftTrackingSpan.GetEndPoint(_hostView.TextSnapshot);

            if (point.Position >= line.Extent.Start && point.Position <= line.Extent.End)
            {
                return new LineTransform(0.0, _diffView.VisualElement.ActualHeight - _diffView.VisualElement.TopOffset - _codeBlockHeight, 1.0);
            }
            else if (_diffView.VisualElement.TopOffset > 0)
            {
                point = _leftTrackingSpan.GetStartPoint(_hostView.TextSnapshot);

                if (point.Position >= line.Extent.Start && point.Position <= line.Extent.End)
                {
                    return new LineTransform(_diffView.VisualElement.TopOffset, 0.0, 1.0);
                }
            }
        }
        return _defaultTransform;
    }

    // Is there a "peek definition" window intersect with the diff view
    private bool IsPeekOnDiffView()
    {
        ThreadHelper.ThrowIfNotOnUIThread("IsPeekOnAnchor");
        _tagAggregator ??= IntellisenseUtilities.GetTagAggregator<InterLineAdornmentTag>(_hostView);

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
            var view = _diffView.ActiveView;
            int position = view.Caret.Position.BufferPosition.Position;
            int lineNo = view.TextSnapshot.GetLineNumberFromPosition(position);

            bool isUp    = (nCmdID == (uint)VSConstants.VSStd2KCmdID.UP);
            bool isDown  = (nCmdID == (uint)VSConstants.VSStd2KCmdID.DOWN);
            bool isRight = (nCmdID == (uint)VSConstants.VSStd2KCmdID.RIGHT);
            bool isLeft  = (nCmdID == (uint)VSConstants.VSStd2KCmdID.LEFT);

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
                    if (viewLine != null) _hostView.Caret.MoveTo(viewLine);
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
        if (textView.Roles.Contains(InlineDiffView.Role)) return null;

        return InlineDiffAdornment.GetOrCreate(textView, () => new InlineDiffAdornment(textView));
    }
}
