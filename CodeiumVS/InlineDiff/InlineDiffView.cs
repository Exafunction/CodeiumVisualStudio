using CodeiumVS.Utilities;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace CodeiumVs.InlineDiff;

internal class InlineDiffView
{
    private class InlineDiffTextDataModel : ITextDataModel, IDisposable
    {
        public ITextBuffer DocumentBuffer { get; }
        public ITextBuffer DataBuffer { get; }
        public IContentType ContentType => DocumentBuffer.ContentType;

        public event EventHandler<TextDataModelContentTypeChangedEventArgs>? ContentTypeChanged;

        // `dataBuffer` and `documentBuffer` can be the same
        internal InlineDiffTextDataModel(ITextBuffer dataBuffer, ITextBuffer documentBuffer)
        {
            DataBuffer = Requires.NotNull(dataBuffer, "dataBuffer");
            DocumentBuffer = Requires.NotNull(documentBuffer, "documentBuffer");
            DocumentBuffer.ContentTypeChanged += OnContentTypeChanged;
        }

        private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs e)
        {
            ContentTypeChanged?.Invoke(this, new TextDataModelContentTypeChangedEventArgs(e.BeforeContentType, e.AfterContentType));
        }

        public void Dispose()
        {
            DocumentBuffer.ContentTypeChanged -= OnContentTypeChanged;
        }
    }

    private readonly IWpfTextView _hostView;
    private readonly IWpfDifferenceViewer _viewer;
    private readonly IDifferenceBuffer _diffBuffer;
    private readonly IVsTextLines _leftTextLines;
    private readonly IVsTextLines _rightTextLines;

    private readonly InlineDiffTextDataModel _leftDataModel;
    private readonly InlineDiffTextDataModel _rightDataModel;

    // Default roles for the diff views
    private static readonly IEnumerable<string> _defaultRoles = new string[] {
        PredefinedTextViewRoles.PrimaryDocument,
        PredefinedTextViewRoles.Analyzable,
        Role
    };

    // options for the difference buffer
    private static readonly StringDifferenceOptions _diffBufferOptions = new()
    {
        DifferenceType = StringDifferenceTypes.Line | StringDifferenceTypes.Word,
        IgnoreTrimWhiteSpace = true
    };

    public const string Role = "CODEIUM_INLINE_DIFFERENCE_VIEW";

    public IWpfDifferenceViewer? Viewer => _viewer;
    public IWpfTextView? LeftView => _viewer?.LeftView;
    public IWpfTextView? RightView => _viewer?.RightView;
    public IVsTextView? LeftVsView { get; private set; }
    public IVsTextView? RightVsView { get; private set; }
    public IWpfTextView? ActiveView { get => _viewer?.ActiveViewType == DifferenceViewType.LeftView ? LeftView : RightView; }
    public IVsTextView? ActiveVsView { get => _viewer?.ActiveViewType == DifferenceViewType.LeftView ? LeftVsView : RightVsView; }

    public InlineDiffControl VisualElement;

    public InlineDiffView(IWpfTextView hostView, IProjectionBuffer leftProjection, ITextBuffer leftBuffer, IProjectionBuffer rightProjection, ITextBuffer rightBuffer)
    {
        ThreadHelper.ThrowIfNotOnUIThread("CreateTextLines");
        _hostView = hostView;

        // create data model and text lines
        _leftDataModel = new(leftProjection, leftBuffer);
        _rightDataModel = new(rightProjection, rightBuffer);
        _leftTextLines = CreateTextLines(_leftDataModel);
        _rightTextLines = CreateTextLines(_rightDataModel);

        //// disable undo on the left view
        //if (ErrorHandler.Succeeded(_leftTextLines.GetUndoManager(out var ppUndoManager)))
        //{
        //    ppUndoManager?.Enable(0);
        //}

        // disable editting for the left view, enable for the right view
        _diffBuffer = MefProvider.Instance.DifferenceBufferFactory.CreateDifferenceBuffer(
            _leftDataModel, _rightDataModel, _diffBufferOptions, false, false, false, true
        );

        // create the difference viewer, and intialize it manually
        _viewer = MefProvider.Instance.DifferenceViewerFactory.CreateUninitializedDifferenceView();
        _viewer.Initialize(_diffBuffer, CreateTextViewHostCallback);

        // this make it doesn't auto scroll to the first difference
        _viewer.Options.SetOptionValue(DifferenceViewerOptions.ScrollToFirstDiffId, value: false);

        VisualElement = new InlineDiffControl(this);

        // when in focus, show the selected line highlight, and vice versa
        VisualElement.GotFocus += VisualElement_OnGotFocus;
        VisualElement.LostFocus += VisualElement_OnLostFocus;
        _diffBuffer.SnapshotDifferenceChanged += DiffBuffer_SnapshotDifferenceChanged;

        _viewer.Closed += DifferenceViewer_OnClosed;
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread("Dispose");
        _viewer.Close();

        if (_leftTextLines is IVsPersistDocData vsPersistDocData)
            vsPersistDocData.Close();

        if (_rightTextLines is IVsPersistDocData vsPersistDocData2)
            vsPersistDocData2.Close();

        _leftDataModel.Dispose();
        _rightDataModel.Dispose();
    }

    // Callback to manually intialize the views for IWpfDifferenceViewer
    private void CreateTextViewHostCallback(IDifferenceTextViewModel textViewModel, ITextViewRoleSet roles, IEditorOptions options, out FrameworkElement visualElement, out IWpfTextViewHost textViewHost)
    {
        // create the VS text view
        IVsTextView vsTextView = MefProvider.Instance.EditorAdaptersFactoryService.CreateVsTextViewAdapter(MefProvider.Instance.OleServiceProvider);

        // should not happen
        if (vsTextView is not IVsUserData codeWindowData)
            throw new InvalidOperationException("Creating DifferenceViewerWithAdapters failed: Unable to cast IVsTextView to IVsUserData.");

        // set the roles and text view model for it
        SetRolesAndModel(codeWindowData, textViewModel, roles);

        // manually set the default properties for the text view
        if (vsTextView is IVsTextEditorPropertyCategoryContainer vsTextEditorProps)
        {
            Guid rguidCategory = DefGuidList.guidEditPropCategoryViewMasterSettings;
            if (ErrorHandler.Succeeded(vsTextEditorProps.GetPropertyCategory(ref rguidCategory, out var ppProp)))
            {
                ppProp.SetProperty(VSEDITPROPID.VSEDITPROPID_ViewComposite_AllCodeWindowDefaults, true);
            }
        }

        IVsTextLines vsTextLines = (IVsTextLines)MefProvider.Instance.EditorAdaptersFactoryService.GetBufferAdapter(textViewModel.DataModel.DataBuffer);
        Assumes.NotNull(vsTextLines);

        // initialize the text view with these options
        vsTextView.Initialize(vsTextLines, IntPtr.Zero, (uint)TextViewInitFlags3.VIF_NO_HWND_SUPPORT,
        [
            new INITVIEW
            {
                fSelectionMargin = 0u,
                fWidgetMargin = 0u,
                fVirtualSpace = 0u,
                fDragDropMove = 1u
            }
        ]);

        textViewHost = MefProvider.Instance.EditorAdaptersFactoryService.GetWpfTextViewHost(vsTextView);
        visualElement = textViewHost.HostControl;

        IWpfTextView textView = textViewHost.TextView;
        InitializeView(textView, textViewHost);

        // disable line number, only for the left view
        if (textViewModel.ViewType == DifferenceViewType.LeftView)
        {
            textView.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, false);
            LeftVsView = vsTextView;
            textView.Caret.PositionChanged += LeftView_OnCaretPositionChanged;
        }
        else if (textViewModel.ViewType == DifferenceViewType.RightView)
        {
            RightVsView = vsTextView;
            textView.Caret.PositionChanged += RightView_OnCaretPositionChanged;
        }
        else
        {
            throw new InvalidOperationException("Unknow difference viewer mode");
        }

        textView.Closed += DiffView_OnClosed;
        textView.LayoutChanged += DiffView_OnLayoutChanged;
        textView.ViewportHeightChanged += DiffView_OnViewportHeightChanged;
        textView.VisualElement.PreviewMouseWheel += DiffView_OnPreviewMouseWheel;
    }

    private void SetRolesAndModel(IVsUserData codeWindowData, IDifferenceTextViewModel textViewModel, ITextViewRoleSet diffRoles)
    {
        // set the text view model for this code window
        // unfortunately, we had to use magic string yet again
        Guid riidKey = new("756E1D18-1976-40BE-AA45-916B02F7B809"); ;
        codeWindowData.SetData(ref riidKey, textViewModel);

        // create the default roles and add roles from the diffview
        IEnumerable<string> enumerable = _defaultRoles.Concat<string>(MefProvider.Instance.TextEditorFactoryService.DefaultRoles);
        if (diffRoles != null)
            enumerable = enumerable.Concat(MefProvider.Instance.TextEditorFactoryService.CreateTextViewRoleSet(diffRoles));

        // set the roles for this code window
        string vtData = MefProvider.Instance.TextEditorFactoryService.CreateTextViewRoleSet(enumerable).ToString();
        riidKey = VSConstants.VsTextBufferUserDataGuid.VsTextViewRoles_guid;
        codeWindowData.SetData(ref riidKey, vtData);
    }

    private void InitializeView(IWpfTextView view, IWpfTextViewHost host)
    {
        // some references:
        // - https://github.com/dotnet/roslyn/blob/376b78a73ab5c612ea23abca3cd6efd044935d0e/src/EditorFeatures/Core.Wpf/Preview/PreviewFactoryService.cs#L68
        // - https://github.com/microsoft/PTVS/blob/b72355d62889900e963f5a70a99c5ffc9fe8e50d/Python/Product/PythonTools/PythonTools/Intellisense/PreviewChangesService.cs#L92

        // the zoom level is already managed by the host view, so this is ok
        view.ZoomLevel = 100;

        // disable scrollbars
        view.Options.SetOptionValue(DefaultTextViewHostOptions.HorizontalScrollBarId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.VerticalScrollBarId, false);

        // enable caret rendering
        view.Options.SetOptionValue(DefaultTextViewOptions.ShouldCaretsBeRenderedId, true);

        // disable all the unwanted margins
        view.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.SelectionMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.SuggestionMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.LineEndingMarginOptionId, false);

        // enable this will alow ctrl+c and ctrl+x on blank lines
        view.Options.SetOptionValue(DefaultTextViewOptions.CutOrCopyBlankLineIfNoSelectionId, true);

        // show url in the code
        view.Options.SetOptionValue(DefaultTextViewOptions.DisplayUrlsAsHyperlinksId, true);

        // enable drag and drop selected code
        view.Options.SetOptionValue(DefaultTextViewOptions.DragDropEditingId, true);

        // when ctrl+a, the caret will move to the end
        view.Options.SetOptionValue(DefaultTextViewOptions.ShouldMoveCaretToEndOnSelectAllId, true);

        // common stuffs
        view.Options.SetOptionValue(DefaultTextViewOptions.ShouldSelectionsBeRenderedId, true);
        view.Options.SetOptionValue(DefaultTextViewOptions.ShowBlockStructureId, true);
        view.Options.SetOptionValue(DefaultTextViewOptions.ShowErrorSquigglesId, true);

        // disable read-only
        view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, false);

        // not sure what this is
        view.Options.SetOptionValue(DefaultTextViewOptions.IsViewportLeftClippedId, false);

        // disable zooming by ctrl+mouse_wheel
        view.Options.SetOptionValue(DefaultWpfViewOptions.EnableMouseWheelZoomId, false);

        // these don't work, i have no idea why
        view.Options.SetOptionValue(DefaultWpfViewOptions.ClickGoToDefEnabledId, true);
        view.Options.SetOptionValue(DefaultWpfViewOptions.ClickGoToDefOpensPeekId, false);

        //view.Options.SetOptionValue("EnableGitChangeMarginEditorOptionName", false);
        //view.Options.SetOptionValue("Diff/View/ShowDiffOverviewMargin", false);

        // magic string, once again
        host.GetTextViewMargin("deltadifferenceViewerOverview").VisualElement.Visibility = Visibility.Collapsed;
        host.GetTextViewMargin(PredefinedMarginNames.Bottom).VisualElement.Visibility = Visibility.Collapsed;
        host.GetTextViewMargin(PredefinedMarginNames.LineEndingMargin).VisualElement.Visibility = Visibility.Collapsed;

        // enable focus for the diff viewer
        view.VisualElement.Focusable = true;

        // leave here for future debugging
        //foreach (EditorOptionDefinition i in view.Options.SupportedOptions)
        //{
        //    Debug.WriteLine(i.Name);
        //}
    }

    // Get the max height of both windows, not including the adornments like "peek definition" window
    public double ContentHeight()
    {
        ITextView textView = _viewer.RightView;
        ITextView textView2 = _viewer.LeftView;
        Difference difference = _viewer.DifferenceBuffer.CurrentSnapshotDifference?.LineDifferences.Differences.FirstOrDefault();
        if (difference != null && difference.Before == null && difference.Right.Length == 0)
        {
            textView = _viewer.LeftView;
            textView2 = _viewer.RightView;
        }
        textView.DisplayTextLineContainingBufferPosition(new SnapshotPoint(textView.TextSnapshot, 0), 0.0, ViewRelativePosition.Top, 10000.0, 10000.0);

        return Math.Max(textView.TextViewLines[textView.TextViewLines.Count - 1].Bottom - textView.ViewportTop, textView2.TextViewLines[textView2.TextViewLines.Count - 1].Bottom - textView2.ViewportTop);
    }

    // We disabled the scroll bar, this makes sure the top is always at line 1
    private void EnsureContentsVisible()
    {
        _viewer.RightView.QueuePostLayoutAction(delegate
        {
            if (_viewer.RightView.TextViewLines[0].Start.Position != 0 || _viewer.RightView.TextViewLines[0].Top < _viewer.RightView.ViewportTop)
                _viewer.RightView.DisplayTextLineContainingBufferPosition(new SnapshotPoint(_viewer.RightView.TextSnapshot, 0), 0.0, ViewRelativePosition.Top);
        });

        _viewer.LeftView.QueuePostLayoutAction(delegate
        {
            if (_viewer.LeftView.TextViewLines[0].Start.Position != 0 || _viewer.LeftView.TextViewLines[0].Top < _viewer.LeftView.ViewportTop)
                _viewer.LeftView.DisplayTextLineContainingBufferPosition(new SnapshotPoint(_viewer.LeftView.TextSnapshot, 0), 0.0, ViewRelativePosition.Top);
        });
    }

    // Fit the containing window size to the code size (no scroll bar)
    private void RecalculateSize()
    {
        var size = ContentHeight();
        _viewer.LeftView.ZoomLevel = 100;
        _viewer.RightView.ZoomLevel = 100;
        _viewer.LeftHost.HostControl.Height = size * _viewer.LeftView.ZoomLevel * 0.01;
        _viewer.RightHost.HostControl.Height = size * _viewer.RightView.ZoomLevel * 0.01;
    }

    // On closed event for the two diff view
    private void DiffView_OnClosed(object sender, EventArgs e)
    {
        IWpfTextView view = sender as IWpfTextView;
        view.Closed -= DiffView_OnClosed;
        view.LayoutChanged -= DiffView_OnLayoutChanged;
        view.ViewportHeightChanged -= DiffView_OnViewportHeightChanged;
        view.VisualElement.PreviewMouseWheel -= DiffView_OnPreviewMouseWheel;
    }

    // On closed event for the difference viewer
    private void DifferenceViewer_OnClosed(object sender, EventArgs e)
    {
        //LeftView.Caret.PositionChanged -= LeftView_OnCaretPositionChanged;
        //RightView.Caret.PositionChanged -= RightView_OnCaretPositionChanged;

        VisualElement.GotFocus -= VisualElement_OnGotFocus;
        VisualElement.LostFocus -= VisualElement_OnLostFocus;

        _diffBuffer.SnapshotDifferenceChanged -= DiffBuffer_SnapshotDifferenceChanged;
        _viewer.Closed -= DifferenceViewer_OnClosed;
    }

    // Block the mouse wheel input to the diff view and send it to the hostview instead
    private void DiffView_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;

        // TODO: get the Vertical scrolling sensitivity setting instead of hardcoded
        // or just find a way to send mouse scroll to host view, RaiseEvent doesn't work
        _hostView.ViewScroller.ScrollViewportVerticallyByLines(e.Delta > 0 ? ScrollDirection.Up : ScrollDirection.Down, 3);
        e.Handled = true;
    }

    private void DiffView_OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        EnsureContentsVisible();
    }

    private void DiffView_OnViewportHeightChanged(object sender, EventArgs e)
    {
        RecalculateSize();
    }

    private void DiffBuffer_SnapshotDifferenceChanged(object sender, SnapshotDifferenceChangeEventArgs e)
    {
        RecalculateSize();
    }

    // When the difference viewer got focus, show its selected line highlight and hide the hostview's one
    private void VisualElement_OnGotFocus(object sender, RoutedEventArgs e)
    {
        LeftView.Options.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, true);
        RightView.Options.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, true);
        _hostView.Options.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, false);
        ActiveVsView.SendExplicitFocus();
    }

    // When the difference viewer lost focus, hide its selected line highlight and show the hostview's one
    private void VisualElement_OnLostFocus(object sender, RoutedEventArgs e)
    {
        LeftView.Options.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, false);
        RightView.Options.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, false);
        _hostView.Options.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, true);
    }

    // Attemp to sync the caret selected line between both views
    private void RightView_OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        //ITextSnapshotLine snapshotLine = e.NewPosition.BufferPosition.GetContainingLine();
        //if (snapshotLine.LineNumber >= LeftView.TextSnapshot.LineCount) return;

        //snapshotLine = LeftView.TextSnapshot.GetLineFromLineNumber(snapshotLine.LineNumber);
        //SnapshotPoint point = new(LeftView.TextSnapshot, snapshotLine.Start);

        //ITextViewLine line = LeftView.TextViewLines.GetTextViewLineContainingBufferPosition(point);
        //if (line != null) LeftView.Caret.MoveTo(line);
    }

    // Attemp to sync the caret selected line between both views
    private void LeftView_OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        //ITextSnapshotLine snapshotLine = e.NewPosition.BufferPosition.GetContainingLine();
        //if (snapshotLine.LineNumber >= RightView.TextSnapshot.LineCount) return;

        //snapshotLine = RightView.TextSnapshot.GetLineFromLineNumber(snapshotLine.LineNumber);
        //SnapshotPoint point = new(RightView.TextSnapshot, snapshotLine.Start);

        //ITextViewLine line = RightView.TextViewLines.GetTextViewLineContainingBufferPosition(point);
        //if (line != null) RightView.Caret.MoveTo(line);
    }

    private static ConstructorInfo? _vsTextBufferAdapterCtor = null;
    private static readonly object?[] _twoNulls = new object[2];

    private IVsTextLines CreateTextLines(ITextDataModel dataModel)
    {
        ThreadHelper.ThrowIfNotOnUIThread("CreateTextLines");

        if (_vsTextBufferAdapterCtor == null)
        {
            // here we ago again
            Assembly assembly = MefProvider.Instance.EditorAdaptersFactoryService.GetType().Assembly;
            Type typeVsTextBufferAdapter = assembly.GetType("Microsoft.VisualStudio.Editor.Implementation.VsTextBufferAdapter");
            _vsTextBufferAdapterCtor = typeVsTextBufferAdapter.GetConstructor([]);
        }

        IVsTextLines vsTextLines = (IVsTextLines)_vsTextBufferAdapterCtor.Invoke([]);
        Type type = vsTextLines.GetType();
        Type baseType = type.BaseType;

        BindingFlags privateFlag = BindingFlags.Instance | BindingFlags.NonPublic;
        BindingFlags publicFlag = BindingFlags.Instance | BindingFlags.Public;

        type.GetField("_documentTextBuffer", privateFlag).SetValue(vsTextLines, dataModel.DataBuffer);
        type.GetField("_dataTextBuffer"    , privateFlag).SetValue(vsTextLines, dataModel.DataBuffer);
        type.GetField("_serviceProvider"   , privateFlag).SetValue(vsTextLines, MefProvider.Instance.OleServiceProvider);
        type.GetMethod("SetSite"           ,  publicFlag).Invoke(vsTextLines,  [MefProvider.Instance.OleServiceProvider]);

        baseType.GetMethod("InitializeUndoManager"       , privateFlag).Invoke(vsTextLines, []);
        baseType.GetMethod("InitializeDocumentTextBuffer", privateFlag).Invoke(vsTextLines, []);
        baseType.GetMethod("OnTextBufferInitialized"     , privateFlag).Invoke(vsTextLines, _twoNulls);

        return vsTextLines;
    }
}
