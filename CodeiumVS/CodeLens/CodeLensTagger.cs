using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Diagnostics;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using System.Windows.Media.TextFormatting;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Projection;
using CodeiumVS.Languages;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CodeiumVS.Packets;
using CodeiumVS.Utilities;
using System.Windows.Shapes;
using System;
using WebSocketSharp;
using System.Windows.Forms;

namespace CodeiumVS
{

    internal sealed class CodeLensTagger : ITagger<CodeLensTag>, IDisposable
    {

        /// used to set the colour of the grey text
        private Brush greyBrush;

        /// contains the editor text and OnChange triggers on any text changes
        ITextBuffer buffer;

        /// current editor display, immutable data
        ITextSnapshot snapshot;

        /// the editor display object
        IWpfTextView view;

        /// contains the grey text
        private IAdornmentLayer adornmentLayer;

        private ITextDocument _document;
        private LangInfo _language;

        private CancellationTokenSource? _requestTokenSource;
        private CancellationTokenSource currentCancellTokenSource = null;
        private CancellationToken currentCancellToken;

        List<FunctionInfo> _functions;
        List<ClassInfo> _classes;
        List<SnapshotSpan> originalSpans = new List<SnapshotSpan>();
        List<StackPanel> panels = new List<StackPanel>();
        private double lastViewPortTop = 0;

        public CodeLensTagger(IWpfTextView view, ITextBuffer buffer, ITextDocument document)
        {
            this.buffer = buffer;
            this.snapshot = buffer.CurrentSnapshot;
            this.view = view;
            this.adornmentLayer = view.GetAdornmentLayer("CodeiumCodeLensAdornmentLayer");
            _document = document;
            this.greyBrush = new SolidColorBrush(Colors.Gray);

            RefreshLanguage();

            view.TextBuffer.Changed += BufferChanged;
            this.view.LayoutChanged += this.OnSizeChanged;

            if (_document != null)
            {
                _document.FileActionOccurred += OnFileActionOccurred;
                _document.TextBuffer.ContentTypeChanged += OnContentTypeChanged;
            }
            Task.Run(() => Update());
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
            if (_document != null)
            {
                _language = Mapper.GetLanguage(_document.TextBuffer.ContentType,
                    System.IO.Path.GetExtension(_document.FilePath)?.Trim('.'));
            }
        }

        // This an iterator that is used to iterate through all of the test tags
        // tags are like html tags they mark places in the view to modify how those sections look
        // Testtag is a tag that tells the editor to add empty space
        public IEnumerable<ITagSpan<CodeLensTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            ITextSnapshot currentSnapshot;
            double height, lineHeight;
            try
            {
                SnapshotSpan entire = new SnapshotSpan(spans[0].Start, spans[spans.Count - 1].End)
                    .TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
                currentSnapshot = spans[0].Snapshot;

                height = view.LineHeight;

                lineHeight = view.LineHeight;

            }
            catch (Exception e)
            {
                yield break;
            }

            int index = 0;
            if (_functions != null)
            {
                foreach (FunctionInfo function in _functions)
                {
                    int lineN = function.DefinitionLine;
                    SnapshotSpan line;
                    SnapshotSpan span;

                    try
                    {
                        line = currentSnapshot.GetLineFromLineNumber(lineN).Extent;
                        span = new SnapshotSpan(line.Start, line.Start);
                    }
                    catch (ArgumentOutOfRangeException e)
                    {
                        yield break;
                    }

                    yield return new TagSpan<CodeLensTag>(
                        span,
                        new CodeLensTag(
                            0, height, 0, 0, 0, PositionAffinity.Predecessor, panels[index], this));
                    index++;
                }
            }

            if (_classes != null)
            {
                foreach (ClassInfo c in _classes)
                {
                    int lineN = c.StartLine;
                    SnapshotSpan line;
                    SnapshotSpan span;

                    try
                    {
                        line = currentSnapshot.GetLineFromLineNumber(lineN).Extent;
                        span = new SnapshotSpan(line.Start, line.Start);
                    }
                    catch (ArgumentOutOfRangeException e)
                    {
                        yield break;
                    }

                    yield return new TagSpan<CodeLensTag>(
                        span,
                        new CodeLensTag(
                            0, height, 0, lineHeight, 0, PositionAffinity.Predecessor, panels[index], this));
                }
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        // triggers when the editor text buffer changes
        void BufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // If this isn't the most up-to-date version of the buffer, then ignore it for now (we'll
            // eventually get another change event).
            if (e.After != buffer.CurrentSnapshot) return;

            Task.Run(() => Update());
        }

        TextBlock CreateTextBox(int i, bool needsGoDoc)
        {
            TextBlock textBlock = new TextBlock();

            var RefactorRun = new Run("Refactor") { Foreground = greyBrush };
            RefactorRun.MouseUp += (object sender, MouseButtonEventArgs e) => ClickRefactor(i);
            var ExplainRun = new Run("Explain") { Foreground = greyBrush };
            ExplainRun.MouseUp += (object sender, MouseButtonEventArgs e) => ClickExplain(i);
            var DocRun = new Run("DocString") { Foreground = greyBrush };
            DocRun.MouseUp += (object sender, MouseButtonEventArgs e) => ClickDoc(i);

            textBlock.Inlines.Add(new Run("Codeium: ") { Foreground = greyBrush });
            textBlock.Inlines.Add(RefactorRun);
            textBlock.Inlines.Add(new Run(" | ") { Foreground = greyBrush });
            textBlock.Inlines.Add(ExplainRun);

            if (needsGoDoc)
            {
                textBlock.Inlines.Add(new Run(" | ") { Foreground = greyBrush });
                textBlock.Inlines.Add(DocRun);
            }

            return textBlock;
        }

        FunctionInfo GetFunN(int n)
        {
            var funcLength = _functions == null ? 0 : _functions.Count;

            if (n >= funcLength)
            {
                return null;
            }
            else
            {
                return _functions[n];
            }
        }

        ClassInfo GetClassN(int n)
        {
            var funcLength = _functions == null ? 0 : _functions.Count;

            if (n >= funcLength)
            {
                n -= funcLength;
                if (_classes.Count <= n) return null;
                return _classes[n];
            }
            else
            {
                return null;
            }
        }

        async void ClickRefactor(int i)
        {
            try
            {
                LanguageServerController controller =
                    CodeiumVSPackage.Instance.LanguageServer.Controller;

                FunctionInfo functionInfo = GetFunN(i);
                ClassInfo classInfo;
                int lineN;
                if (functionInfo == null)
                {
                    classInfo = GetClassN(i);
                    lineN = classInfo.StartLine;
                }
                else
                {
                    lineN = functionInfo.DefinitionLine;
                }
                var span = originalSpans[i].TranslateTo(view.TextSnapshot, SpanTrackingMode.EdgePositive);
                ITextSnapshotLine snapshotLine = span.Start.GetContainingLine();
                var start = view.TextViewLines.GetCharacterBounds(snapshotLine.Start);

                // highlight the selected codeblock
                TextHighlighter? highlighter = TextHighlighter.GetInstance(view);
                highlighter?.AddHighlight(snapshotLine.Extent);
                var dialog = RefactorCodeDialogWindow.GetOrCreate();
                var prompt =
                    await dialog.ShowAndGetPromptAsync(_language, start.Left - view.ViewportLeft, start.Top - view.ViewportTop);

                highlighter?.ClearAll();

                // user did not select any of the prompt
                if (prompt == null) return;
                if (functionInfo != null)
                {
                    controller.RefactorFunctionAsync(
                        prompt, _document.FilePath, functionInfo);
                }
                else
                {
                    classInfo = GetClassN(i);
                    if (classInfo == null) return;
                    CodeBlockInfo codeBlockInfo = ClassToCodeBlock(classInfo);

                    controller.ExplainCodeBlockAsync(_document.FilePath, _language.Type, codeBlockInfo);
                }

            }
            catch (Exception e)
            {

            }
        }

        void ClickExplain(int i)
        {
            LanguageServerController controller =
                CodeiumVSPackage.Instance.LanguageServer.Controller;

            FunctionInfo functionInfo = GetFunN(i);
            if (functionInfo != null)
            {
                controller.ExplainFunctionAsync(_document.FilePath, functionInfo);
            }
            else
            {
                ClassInfo classInfo = GetClassN(i);
                if (classInfo == null) return;
                CodeBlockInfo codeBlockInfo = ClassToCodeBlock(classInfo);
                controller.ExplainCodeBlockAsync(
                    _document.FilePath, _language.Type, codeBlockInfo);
            }
        }

        CodeBlockInfo ClassToCodeBlock(ClassInfo classInfo)
        {
            CodeBlockInfo codeBlockInfo = new CodeBlockInfo();
            codeBlockInfo.start_line = classInfo.StartLine;
            codeBlockInfo.end_line = classInfo.EndLine;
            codeBlockInfo.start_col = classInfo.StartCol;
            codeBlockInfo.end_col = classInfo.EndCol;
            return codeBlockInfo;
        }

        void ClickDoc(int i)
        {
            LanguageServerController controller =
                CodeiumVSPackage.Instance.LanguageServer.Controller;

            FunctionInfo functionInfo = GetFunN(i);

            if (functionInfo != null)
            {
                controller.GenerateFunctionDocstringAsync(_document.FilePath, functionInfo);
            }
        }

        private void UpdatePanel(StackPanel panel, int index, bool needsGoDoc)
        {
            panel.Children.Clear();
            panel.Children.Add(CreateTextBox(index, needsGoDoc));
        }
        private void AddPanel(bool needsGoDoc)
        {
            CreateStackPanel(needsGoDoc);
        }

        private void RemovePanel(List<StackPanel> panels)
        {
            panels.RemoveAt(panels.Count - 1);
        }

        void CreateStackPanel(bool needsGoDoc)
        {
            var stackPanel = new StackPanel();
            stackPanel.Children.Add(CreateTextBox(panels.Count, needsGoDoc));
            panels.Add(stackPanel);
        }

        private void SetPosition(SnapshotSpan orginalLine, StackPanel panel)
        {
            try
            {

                var snapshotSpan = orginalLine.TranslateTo(view.TextSnapshot, SpanTrackingMode.EdgeExclusive);
                ITextSnapshotLine snapshotLine = snapshotSpan.Start.GetContainingLine();
                if (view.TextViewLines.FirstVisibleLine.Start < snapshotLine.Start &&
                    view.TextViewLines.LastVisibleLine.End >= snapshotLine.Start)
                {
                    var text = snapshotLine.GetText();
                    int emptySpaceLength = text.Length - text.TrimStart().Length;

                    var start = view.TextViewLines.GetCharacterBounds(snapshotLine.Start.Add(emptySpaceLength));

                    if (panel.Children.Count > 0)
                    {
                        var span = snapshotLine.Extent;
                        // Place the image in the top left hand corner of the line
                        Canvas.SetLeft(panel, start.Left);
                        Canvas.SetTop(element: panel, start.TextTop - view.LineHeight);

                        // Add the image to the adornment layer and make it relative to the viewport
                        this.adornmentLayer.AddAdornment(
                            AdornmentPositioningBehavior.TextRelative, span, null, panel, null);
                    }
                }
            }
            catch (Exception e) { Debug.Write(e); }
        }

        private void UpdateAdornments()
        {
            this.adornmentLayer.RemoveAllAdornments();
            int i = 0;

            if(originalSpans == null) return;
            if (_functions != null)
            {
                foreach (var function in _functions)
                {
                    if (originalSpans.Count > i)
                    {
                        SetPosition(originalSpans[i], panels[i]);
                    }
                    i++;
                }
            }

            if (_classes != null)
            {
                foreach (var c in _classes)
                {
                    if (originalSpans.Count > i)
                    {
                        SetPosition(originalSpans[i], panels[i]);
                    }
                    i++;
                }
            }

        }

        // Adds grey text to display
        private void OnSizeChanged(object sender, EventArgs e)
        {
            UpdateAdornments();
            if ( Math.Abs(lastViewPortTop - view.ViewportTop) > Double.Epsilon)
            {
                lastViewPortTop = view.ViewportTop;
                MarkDirty();
            }
        }

        // update multiline data
        public async Task<bool> Update()
        {

            while (CodeiumVSPackage.Instance == null || CodeiumVSPackage.Instance.LanguageServer == null)
            {
                await Task.Delay(100);
            }

            try
            {
                lastViewPortTop = view.ViewportTop;

                string text = _document.TextBuffer.CurrentSnapshot.GetText();
                SnapshotPoint? caretPoint = view.Caret.Position.Point.GetPoint(
                    textBuffer => (!textBuffer.ContentType.IsOfType("projection")),
                    PositionAffinity.Successor);
                if (!caretPoint.HasValue)
                {
                    return false;
                }

                var caretPosition = caretPoint.Value.Position;

                int cursorPosition = _document.Encoding.IsSingleByte
                    ? caretPosition
                    : Utf16OffsetToUtf8Offset(text, caretPosition);

                if (cursorPosition > text.Length)
                {
                    Debug.Print("Error Caret past text position");
                    return false;
                }

                UpdateRequestTokenSource(new CancellationTokenSource());
                IList<Packets.FunctionInfo>? functions =
                    await CodeiumVSPackage.Instance.LanguageServer.GetFunctionsAsync(
                        _document.FilePath,
                        text,
                        _language,
                        cursorPosition,
                        view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId),
                        currentCancellTokenSource.Token);

                IList<Packets.ClassInfo>? classes = await CodeiumVSPackage.Instance.LanguageServer.GetClassInfosAsync(
                    _document.FilePath,
                    text,
                    _language,
                    cursorPosition,
                    view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId),
                    currentCancellTokenSource.Token);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _functions = (List<FunctionInfo>)functions;
                _classes = (List<ClassInfo>)classes;

                originalSpans.Clear();
                int index = 0;
                foreach (FunctionInfo function in _functions)
                {
                    if (panels.Count > index)
                    {
                        var panel = panels[index];
                        var needsGoDoc = function.Docstring.IsNullOrEmpty();
                        var childrenCount = (panel.Children[0] as TextBlock).Inlines.Count;
                        if ((needsGoDoc && childrenCount < 5) || (!needsGoDoc && childrenCount > 5))
                        {
                            UpdatePanel(panel, index, needsGoDoc);
                        }
                    }
                    originalSpans.Add(view.TextSnapshot.GetLineFromLineNumber(function.DefinitionLine).Extent);
                    index++;
                }

                foreach (ClassInfo c in classes)
                {
                    if (panels.Count > index)
                    {
                        var panel = panels[index];
                        if((panel.Children[0] as TextBlock).Inlines.Count >= 5)
                        {
                            UpdatePanel(panel, index, false);
                        }
                    }

                    originalSpans.Add(view.TextSnapshot.GetLineFromLineNumber(c.DefinitionLine).Extent);
                    index++;
                }

                int panelDiff = (_functions.Count + _classes.Count) - panels.Count;
                if (panelDiff > 0)
                {
                    for (int i = 0; i < panelDiff; i++)
                    {
                        FunctionInfo function = GetFunN(i);
                        bool needsGoDoc = function != null && function.Docstring.IsNullOrEmpty();
                        AddPanel(needsGoDoc);
                    }
                }
                else if (panelDiff < 0)
                {
                    for (int i = 0; i < panelDiff; i++)
                    {
                        RemovePanel(panels);
                    }
                }

                UpdateAdornments();
                MarkDirty();

                return true;
            }
            catch (Exception e)
            {
                return false;
            }
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

        // triggers refresh of the screen
        void MarkDirty()
        {
            try
            {
                ITextSnapshot newSnapshot = buffer.CurrentSnapshot;
                this.snapshot = newSnapshot;

                if (view.TextViewLines == null) return;
                if (!view.TextViewLines.IsValid) return;

                var changeStart = view.TextViewLines.FirstVisibleLine.Start;
                var changeEnd = view.TextViewLines.LastVisibleLine.Start;

                var startLine = view.TextSnapshot.GetLineFromPosition(changeStart);
                var endLine = view.TextSnapshot.GetLineFromPosition(changeEnd);

                var span = new SnapshotSpan(startLine.Start, endLine.EndIncludingLineBreak)
                    .TranslateTo(targetSnapshot: newSnapshot, SpanTrackingMode.EdgePositive);

                // lines we are marking dirty
                // currently all of them for simplicity
                if (this.TagsChanged != null) { TagsChanged(this, new SnapshotSpanEventArgs(span)); }
            }
            catch (Exception e) { Debug.Write(e); }
        }

        public void Dispose()
        {
            _document.FileActionOccurred -= OnFileActionOccurred;
            _document.TextBuffer.ContentTypeChanged -= OnContentTypeChanged;
            UpdateRequestTokenSource(null);
        }

    }

    [Export(typeof(IViewTaggerProvider))]
    [TagType(typeof(CodeLensTag))]
    [ContentType("text")]
    internal sealed class CodeLensProvider : IViewTaggerProvider
    {

        [Export(typeof(AdornmentLayerDefinition))]
        [Name("CodeiumCodeLensAdornmentLayer")]
        [Order(After = PredefinedAdornmentLayers.Caret)]
        private AdornmentLayerDefinition codeLensAdornmentLayer;

#pragma warning restore 649, 169

        // document factory is used to get information about the current text document such as filepath,
        // language, etc.
        [Import]
        internal ITextDocumentFactoryService documentFactory = null;

        // create a single tagger for each buffer.
        // the MultilineGreyTextTagger displays the grey text in the editor.
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer)
            where T : ITag
        {
            var topBuffer = textView.BufferGraph.TopBuffer;

            var projectionBuffer = topBuffer as IProjectionBufferBase;

            ITextBuffer textBuffer =
                projectionBuffer != null ? projectionBuffer.SourceBuffers[0] : topBuffer;
            ITextDocument _document;
            documentFactory.TryGetTextDocument(textBuffer, out _document);
            if(_document == null) return null;
            Func<ITagger<T>> sc = delegate ()
            {
                return new CodeLensTagger((IWpfTextView)textView, buffer, _document) as ITagger<T>;
            };
            return buffer.Properties.GetOrCreateSingletonProperty<ITagger<T>>(typeof(CodeLensTagger),
                                                                              sc);
        }
    }
}
