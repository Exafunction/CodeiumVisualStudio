using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Media;

namespace CodeiumVS;
internal class HighlightWordTag : TextMarkerTag
{
    public HighlightWordTag() : base("MarkerFormatDefinition/HighlightWordFormatDefinition") { }
}

[Export(typeof(EditorFormatDefinition))]
[Name("MarkerFormatDefinition/HighlightWordFormatDefinition")]
[UserVisible(true)]
internal class HighlightWordFormatDefinition : MarkerFormatDefinition
{
    public HighlightWordFormatDefinition()
    {
        var c1 = VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxTextInputSelectionBrushKey);
        var c2 = VSColorTheme.GetThemedColor(EnvironmentColors.SystemWindowTextBrushKey);
        BackgroundColor = Color.FromArgb(c1.A, c1.R, c1.G, c1.B);
        ForegroundColor = Color.FromArgb(c2.A, c2.R, c2.G, c2.B);
        DisplayName = "Highlight Word";
        ZOrder = 5;
    }
}

internal class TextHighlighter : ITagger<HighlightWordTag>
{
    internal static TextHighlighter? Instance { get; private set; }

    ITextView View { get; set; }
    ITextBuffer SourceBuffer { get; set; }
    IViewTagAggregatorFactoryService TagAggregatorFactoryService { get; set; }

    private ITagAggregator<IStructureTag> tagAggregator;

    SnapshotSpan? CurrentWord { get; set; }
    SnapshotPoint RequestedPoint { get; set; }
    readonly object updateLock = new();
    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

    private readonly List<ITagSpan<HighlightWordTag>> Spans;

    public TextHighlighter(ITextView view, ITextBuffer sourceBuffer, IViewTagAggregatorFactoryService tagAggregatorFactoryService)
    {
        Instance = this;
        View = view;
        SourceBuffer = sourceBuffer;
        TagAggregatorFactoryService = tagAggregatorFactoryService;
        View.Caret.PositionChanged += CaretPositionChanged;
        Spans = [];
    }

    public IEnumerable<ITagSpan<HighlightWordTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        return Spans;
    }

    public Span GetBlockSpan(out IStructureTag? outtag, int position, ITextSnapshot? snapshot)
    {
        outtag = null;
        snapshot ??= View.TextSnapshot;

        // We want to search for all tags in a span, not just one point. If the `position` is
        // at the beginning of a block, it won't be found, it only returns if the span overlaps
        // with OutliningSpan.Start to OutliningSpan.End. 1000 is an arbitrary number, but it
        // should be enough for most cases, it's not like someone would write 1000 characters
        // in the HeaderSpan
        //     
        // HeaderSpan.Start
        //     ↓          
        //     while (true) ← HeaderSpan.End
        //     { ← OutliningSpan.Start
        //         doSomething();
        //     }
        //     ↑
        // OutliningSpan.End

        int length = snapshot.Length - position;
        if (length > 1000) length = 1000;

        var tagAggregator = GetTagAggregator();
        var tagSpan = new SnapshotSpan(snapshot, position, length);

        IEnumerable<IMappingTagSpan<IStructureTag>> mappingTags = tagAggregator.GetTags(tagSpan);

        int start = 0, end = 0;

        foreach (var mappingTag in mappingTags)
        {
            IStructureTag tag = mappingTag.Tag;
            
            // if the position is right at the begining of a block
            if (tag.HeaderSpan?.Start == position && tag.OutliningSpan.HasValue)
            {
                start = tag.HeaderSpan.Value.Start;
                end = tag.OutliningSpan.Value.End;
                outtag = tag;
                return new Span(start, end - start);
            }
            else
            {
                // or the position is in the middle of a block, find the closest block
                if (tag.HeaderSpan?.Start > position || tag.OutliningSpan?.End <= position) continue;
                if (end == 0 || (start < tag.HeaderSpan?.Start && tag.OutliningSpan.HasValue))
                {
                    start = tag.HeaderSpan.Value.Start;
                    end = tag.OutliningSpan.Value.End;
                    outtag = tag;
                }
            }
        }

        return new Span(start, end - start);
    }

    void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        Spans.Clear();

        //Span blockSpan = GetBlockSpan(out var tag, e.NewPosition.BufferPosition.Position, null);
        //var tagSpan = new SnapshotSpan(View.TextSnapshot, blockSpan);
        //Spans.Add(new TagSpan<HighlightWordTag>(tagSpan, new HighlightWordTag()));
        //SynchronousUpdate();

        SynchronousUpdate();
        return;
    }

    public void ClearAll()
    {
        Spans.Clear();
        SynchronousUpdate();
    }

    public void AddHighlight(Span span, ITextSnapshot? snapshot)
    {
        var tagSpan = new SnapshotSpan(snapshot ?? View.TextSnapshot, span);
        Spans.Add(new TagSpan<HighlightWordTag>(tagSpan, new HighlightWordTag()));
        SynchronousUpdate();
    }

    public void AddHighlight(int position, int length, ITextSnapshot? snapshot)
    {
        AddHighlight(new Span(position, length), snapshot);
    }

    public void HighlightBlock(int position, ITextSnapshot? snapshot)
    {
        Span blockSpan = GetBlockSpan(out var _, position, snapshot);
        if (blockSpan.IsEmpty) return;

        AddHighlight(blockSpan, snapshot);
    }

    private ITagAggregator<IStructureTag> GetTagAggregator()
    {
        return tagAggregator ??= TagAggregatorFactoryService.CreateTagAggregator<IStructureTag>(View);
    }


    private void SynchronousUpdate()
    {
        lock (updateLock)
        {
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
        }
    }
}


[Export(typeof(IViewTaggerProvider))]
[ContentType("code")]
[TagType(typeof(HighlightWordTag))]
internal class HighlightWordTaggerProvider : IViewTaggerProvider
{

    [Import]
    internal IViewTagAggregatorFactoryService TagAggregatorFactoryService { get; set; }

    public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        // Provide highlighting only on the top buffer 
        if (textView.TextBuffer != buffer) return null;
        return new TextHighlighter(textView, buffer, TagAggregatorFactoryService) as ITagger<T>;
    }
}