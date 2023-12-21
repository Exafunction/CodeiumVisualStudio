using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Media;

namespace CodeiumVS.Utilities;

internal class TextHighlighter : TextViewExtension<ITextView, TextHighlighter>,
                                 ITagger<HighlightWordTag>
{
    private readonly ITextBuffer _sourceBuffer;
    private readonly List<ITagSpan<HighlightWordTag>> _spans;
    private readonly object updateLock = new();

    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

    public TextHighlighter(ITextView view, ITextBuffer sourceBuffer) : base(view)
    {
        _sourceBuffer = sourceBuffer;
        _hostView.Caret.PositionChanged += CaretPositionChanged;
        _spans = [];
    }

    public IEnumerable<ITagSpan<HighlightWordTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        return _spans;
    }

    private void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        _spans.Clear();
        SynchronousUpdate();
        return;
    }

    public void ClearAll()
    {
        _spans.Clear();
        SynchronousUpdate();
    }

    public void AddHighlight(Span span)
    {
        var tagSpan = new SnapshotSpan(_hostView.TextSnapshot, span);
        _spans.Add(new TagSpan<HighlightWordTag>(tagSpan, new HighlightWordTag()));
        SynchronousUpdate();
    }

    public void AddHighlight(int position, int length) { AddHighlight(new Span(position, length)); }

    public void HighlightBlock(int position)
    {
        Span blockSpan = CodeAnalyzer.GetBlockSpan(_hostView, position, out var _);
        if (blockSpan.IsEmpty) return;

        AddHighlight(blockSpan);
    }

    private void SynchronousUpdate()
    {
        lock (updateLock)
        {
            TagsChanged?.Invoke(
                this,
                new SnapshotSpanEventArgs(new SnapshotSpan(
                    _sourceBuffer.CurrentSnapshot, 0, _sourceBuffer.CurrentSnapshot.Length)));
        }
    }
}

internal class HighlightWordTag : TextMarkerTag
{
    public HighlightWordTag() : base("MarkerFormatDefinition/HighlightWordFormatDefinition") {}
}

[Export(typeof(EditorFormatDefinition))]
[Name("MarkerFormatDefinition/HighlightWordFormatDefinition")]
[UserVisible(true)]
internal class HighlightWordFormatDefinition : MarkerFormatDefinition
{
    public HighlightWordFormatDefinition()
    {
        var c1 =
            VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxTextInputSelectionBrushKey);
        var c2 = VSColorTheme.GetThemedColor(EnvironmentColors.SystemWindowTextBrushKey);
        BackgroundColor = Color.FromArgb(c1.A, c1.R, c1.G, c1.B);
        ForegroundColor = Color.FromArgb(c2.A, c2.R, c2.G, c2.B);
        DisplayName = "Highlight Word";
        ZOrder = 5;
    }
}

[Export(typeof(IViewTaggerProvider))]
[ContentType("code")]
[TagType(typeof(HighlightWordTag))]
internal class HighlightWordTaggerProvider : IViewTaggerProvider
{
    public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer)
        where T : ITag
    {
        // Provide highlighting only on the top buffer
        if (textView.TextBuffer != buffer) return null;
        return TextHighlighter.GetOrCreate(textView, () => new TextHighlighter(textView, buffer))
            as ITagger<T>;
    }
}
