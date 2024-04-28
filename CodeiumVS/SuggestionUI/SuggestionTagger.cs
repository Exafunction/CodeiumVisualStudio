using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Diagnostics;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using System.Text.RegularExpressions;
using System.Windows.Media.TextFormatting;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Formatting;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodeiumVS
{

internal sealed class SuggestionTagger : ITagger<SuggestionTag>
{
    /// panel with multiline grey text
    private StackPanel stackPanel;

    /// used to set the colour of the grey text
    private Brush greyBrush;

    /// used to set the colour of text that overlaps with the users text
    private Brush transparentBrush;

    /// contains the editor text and OnChange triggers on any text changes
    ITextBuffer buffer;

    /// current editor display, immutable data
    ITextSnapshot snapshot;

    /// the editor display object
    IWpfTextView view;

    /// contains the grey text
    private IAdornmentLayer adornmentLayer;

    /// true if a suggestion should be shown
    private bool showSuggestion = false;
    private bool isTextInsertion = false;

    ///  line number the suggestion should be displayed at
    private int currentTextLineN;
    private int suggestionIndex;
    private int insertionPoint;
    private int userIndex;
    private String userEndingText;
    private String virtualText = "";

    /// suggestion to display
    /// first string is to match against second item: array is for formatting
    private static Tuple<String, String[]> suggestion = null;

    private InlineGreyTextTagger GetTagger()
    {
        var key = typeof(InlineGreyTextTagger);
        var props = view.TextBuffer.Properties;
        if (props.ContainsProperty(key)) { return props.GetProperty<InlineGreyTextTagger>(key); }
        else { return null; }
    }

    public bool SetSuggestion(String newSuggestion, int caretPoint)
    {
        try
        {
            // If this isn't the most up-to-date version of the buffer, then ignore it for now (we'll
            newSuggestion = newSuggestion.TrimEnd();
            newSuggestion = newSuggestion.Replace("\r", "");
            ClearSuggestion();

            int lineN = GetCurrentTextLine();

            if (lineN < 0) return false;

            String untrim = buffer.CurrentSnapshot.GetLineFromLineNumber(lineN).GetText();

            virtualText = "";
            if (String.IsNullOrWhiteSpace(untrim) && untrim.Length < caretPoint)
            {
                virtualText = new string(' ', caretPoint - untrim.Length);
            }
            String line = untrim.TrimStart();
            int offset = untrim.Length - line.Length;

            caretPoint = Math.Max(0, caretPoint - offset);

            String combineSuggestion = line + newSuggestion;
            if (line.Length - caretPoint > 0)
            {
                String currentText = line.Substring(0, caretPoint);
                combineSuggestion = currentText + newSuggestion;
                userEndingText = line.Substring(caretPoint).Trim();
                var userIndex = newSuggestion.IndexOf(userEndingText);

                if (userIndex < 0) { return false; }
                userIndex += currentText.Length;

                this.userIndex = userIndex;
                isTextInsertion = true;
                insertionPoint = line.Length - caretPoint;
            }
            else { isTextInsertion = false; }
            var suggestionLines = combineSuggestion.Split('\n');
            suggestion = new Tuple<String, String[]>(combineSuggestion, suggestionLines);
            return Update();
        }catch (Exception ex)
        {
            CodeiumVSPackage.Instance?.LogAsync("Exception: " + ex.ToString());
            return false;
        }
    }

    public bool OnSameLine() { return GetCurrentTextLine() == currentTextLineN; }

    private void LostFocus(object sender, EventArgs e)
    {
        try
        {
            ClearSuggestion();
        }
        catch (Exception ex)
        {
            CodeiumVSPackage.Instance?.LogAsync("Exception: " + ex.ToString());
        }
    }

    public SuggestionTagger(IWpfTextView view, ITextBuffer buffer)
    {
        this.stackPanel = new StackPanel();

        this.buffer = buffer;
        this.snapshot = buffer.CurrentSnapshot;
        view.TextBuffer.Changed += BufferChanged;
        this.view = view;
        this.adornmentLayer = view.GetAdornmentLayer("CodeiumAdornmentLayer");

        this.view.LayoutChanged += this.OnSizeChanged;

        this.transparentBrush = new SolidColorBrush();
        this.transparentBrush.Opacity = 0;
        this.greyBrush = new SolidColorBrush(Colors.Gray);
        view.LostAggregateFocus += LostFocus;
    }

    public bool IsSuggestionActive() { return showSuggestion; }

    public String GetSuggestion()
    {
        if (suggestion != null && showSuggestion) { return suggestion.Item1; }
        else { return ""; }
    }

    // This an iterator that is used to iterate through all of the test tags
    // tags are like html tags they mark places in the view to modify how those sections look
    // Testtag is a tag that tells the editor to add empty space
    public IEnumerable<ITagSpan<SuggestionTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        var currentSuggestion = suggestion;
        if (!showSuggestion || currentSuggestion == null || currentSuggestion.Item2.Length <= 1)
        {
            yield break;
        }

        SnapshotSpan entire = new SnapshotSpan(spans[0].Start, spans[spans.Count - 1].End)
                                  .TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
        ITextSnapshot currentSnapshot = spans[0].Snapshot;

        var line = currentSnapshot.GetLineFromLineNumber(currentTextLineN).Extent;
        var span = new SnapshotSpan(line.End, line.End);

        var snapshotLine = currentSnapshot.GetLineFromLineNumber(currentTextLineN);

        double height = view.LineHeight * (currentSuggestion.Item2.Length - 1);
        double lineHeight = 0;

        if (String.IsNullOrEmpty(line.GetText())) { lineHeight = view.LineHeight; }
        yield return new TagSpan<SuggestionTag>(
            span,
            new SuggestionTag(
                0, 0, lineHeight, 0, height, PositionAffinity.Predecessor, stackPanel, this));
    }

    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

    // triggers when the editor text buffer changes
    void BufferChanged(object sender, TextContentChangedEventArgs e)
    {
        try
        {
            // If this isn't the most up-to-date version of the buffer, then ignore it for now (we'll
            // eventually get another change event).
            if (e.After != buffer.CurrentSnapshot) return;
            this.Update();
        }
        catch (Exception ex)
        {
            CodeiumVSPackage.Instance?.LogAsync("Exception: " + ex.ToString());
        }
    }

    TextRunProperties GetTextFormat()
    {
        var line = view.TextViewLines.FirstVisibleLine;
        return line.GetCharacterFormatting(line.Start);
    }

    // used to set formatting of the displayed multi lines
    public void FormatText(TextBlock block)
    {
        // var pos = snapshot.GetLineFromLineNumber(currentLineN).Start;

        var line = view.TextViewLines.FirstVisibleLine;
        var format = line.GetCharacterFormatting(line.Start);
        if (format != null)
        {
            block.FontFamily = format.Typeface.FontFamily;
            block.FontSize = format.FontRenderingEmSize;
        }
    }

    String ConvertTabsToSpaces(string text)
    {
        int tabSize = view.Options.GetTabSize();
        return Regex.Replace(text, "\t", new string(' ', tabSize));
    }
    void FormatTextBlock(TextBlock textBlock)
    {
        textBlock.FontStyle = FontStyles.Normal;
        textBlock.FontWeight = FontWeights.Normal;
    }

    TextBlock CreateTextBox(string text, Brush textColour)
    {
        TextBlock textBlock = new TextBlock();
        textBlock.Inlines.Add(item: new Run(text) { Foreground = textColour });
        FormatTextBlock(textBlock);
        return textBlock;
    }

    void AddSuffixTextBlocks(int start, string line, string userText)
    {

        if (isTextInsertion && start > line.Length) { return; }
        int emptySpaceLength = userText.Length - userText.TrimStart().Length;
        string emptySpace = ConvertTabsToSpaces(userText.Substring(0, emptySpaceLength));
        string editedUserText = emptySpace + userText.TrimStart();
        if (isTextInsertion) { editedUserText = emptySpace + line.Substring(0, start); }
        string remainder = line.Substring(start);
        TextBlock textBlock = new TextBlock();
        textBlock.Inlines.Add(item: new Run(editedUserText) { Foreground = transparentBrush });
        textBlock.Inlines.Add(item: new Run(remainder) { Foreground = greyBrush });

        stackPanel.Children.Add(textBlock);
    }

    void AddInsertionTextBlock(int start, int end, string line)
    {
        if (line.Length <= suggestionIndex || end < start) return;
        try
        {
            string remainder = line.Substring(start, end - start);
            var textBlock = CreateTextBox(remainder, greyBrush);
            GetTagger().UpdateAdornment(textBlock);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }
    }

    // Updates the grey text
    public void UpdateAdornment(IWpfTextView view, string userText, int suggestionStart)
    {
        try
        { 
            stackPanel.Children.Clear();
            GetTagger().ClearAdornment();
            for (int i = suggestionStart; i < suggestion.Item2.Length; i++)
            {
                string line = suggestion.Item2[i];

                if (i == 0)
                {
                    int offset = line.Length - line.TrimStart().Length;

                    if (isTextInsertion && suggestionIndex < userIndex)
                    {
                        if (suggestionIndex > 0 && suggestionIndex < line.Length && char.IsWhiteSpace(line[suggestionIndex - 1]) &&
                            userText.Length > insertionPoint + 1 &&
                            !char.IsWhiteSpace(userText[userText.Length - insertionPoint - 1]))
                        {
                            suggestionIndex--;
                        }
                        AddInsertionTextBlock(suggestionIndex + offset, userIndex, line);
                        if (line.Length > userIndex + 1)
                        {
                            AddSuffixTextBlocks(
                                userIndex + userEndingText.Trim().Length, line, userText);
                        }
                        else { stackPanel.Children.Add(CreateTextBox("", greyBrush)); }
                    }
                    else
                    {
                        if (String.IsNullOrEmpty(line))
                        {
                            stackPanel.Children.Add(CreateTextBox("", greyBrush));
                        }
                        else
                        {
                            String suggestedLine =
                                virtualText.Length > 0 ? virtualText + line.TrimStart() : line;
                            AddSuffixTextBlocks(userText.Length > 0 ? suggestionIndex + offset : 0,
                                                suggestedLine,
                                                userText);
                        }
                    }
                }
                else { stackPanel.Children.Add(CreateTextBox(line, greyBrush)); }
            }

            this.adornmentLayer.RemoveAllAdornments();

            // usually only happens the moment a bunch of text has rentered such as an undo operation
                ITextSnapshotLine snapshotLine =
                    view.TextSnapshot.GetLineFromLineNumber(currentTextLineN);
                var start = view.TextViewLines.GetCharacterBounds(snapshotLine.Start);

                // Place the image in the top left hand corner of the line
                Canvas.SetLeft(stackPanel, start.Left);
                Canvas.SetTop(stackPanel, start.TextTop);
                var span = snapshotLine.Extent;
                // Add the image to the adornment layer and make it relative to the viewport
                this.adornmentLayer.AddAdornment(
                    AdornmentPositioningBehavior.TextRelative, span, null, stackPanel, null);
        }
        catch (Exception e)
        { Debug.Write(e);
        }
    }

    // Adds grey text to display
    private void OnSizeChanged(object sender, EventArgs e)
    {

        try
        {
            if (!showSuggestion) { return; }

            foreach (TextBlock block in stackPanel.Children)
            {
                FormatText(block);
            }

            ITextSnapshotLine snapshotLine = view.TextSnapshot.GetLineFromLineNumber(currentTextLineN);

            var start = view.TextViewLines.GetCharacterBounds(snapshotLine.Start);

            InlineGreyTextTagger inlineTagger = GetTagger();
            inlineTagger.FormatText(GetTextFormat());

            if (stackPanel.Children.Count > 0)
            {
                this.adornmentLayer.RemoveAllAdornments();

                var span = snapshotLine.Extent;

                // Place the image in the top left hand corner of the line
                Canvas.SetLeft(stackPanel, start.Left);
                Canvas.SetTop(element: stackPanel, start.TextTop);
                var diff = start.Top - start.TextTop;
                Debug.Print("Top = " + (start.Top.ToString()) +
                            " TextTop = " + (start.TextTop.ToString()) + " bottom " +
                            (start.TextBottom.ToString()));
                // Add the image to the adornment layer and make it relative to the viewport
                this.adornmentLayer.AddAdornment(
                    AdornmentPositioningBehavior.TextRelative, span, null, stackPanel, null);
            }
        }
        catch (Exception ex)
        {
            CodeiumVSPackage.Instance?.LogAsync("Exception: " + ex.ToString());
        }
    }

    // Gets the line number of the caret
    int GetCurrentTextLine()
    {
        CaretPosition caretPosition = view.Caret.Position;

        var textPoint = caretPosition.Point.GetPoint(buffer, caretPosition.Affinity);

        if (!textPoint.HasValue) { return -1; }

        return buffer.CurrentSnapshot.GetLineNumberFromPosition(textPoint.Value);
    }

    // update multiline data
    public bool Update()
    {

        if (suggestion == null) { return false; }

        int textLineN = GetCurrentTextLine();

        if (textLineN < 0) { return false; }

        ITextSnapshot newSnapshot = buffer.CurrentSnapshot;
        this.snapshot = newSnapshot;

        String untrimLine = newSnapshot.GetLineFromLineNumber(textLineN).GetText();
        String line = untrimLine.TrimStart();

        // get line carat is on
        // if suggestion matches line (possibly including preceding lines)
        //   show suggestion
        // else
        //   clear suggestions

        int newIndex =
            StringCompare.CheckSuggestion(suggestion.Item1, line, isTextInsertion, insertionPoint);
        if (newIndex >= 0)
        {
            this.currentTextLineN = textLineN;
            this.suggestionIndex = newIndex;
            ShowSuggestion(untrimLine, 0);
            return true;
        }
        else { ClearSuggestion(); }

        return false;
    }

    // Adds the grey text to the file replacing current line in the process
    public bool CompleteText()
    {
        try
        {
            if (!showSuggestion || suggestion == null)
            {
                return false;
            }

            String untrimLine = this.snapshot.GetLineFromLineNumber(currentTextLineN).GetText();
            String line = untrimLine.Trim();

            int suggestionLineN =
                StringCompare.CheckSuggestion(suggestion.Item1, line, isTextInsertion, insertionPoint);
            if (suggestionLineN >= 0)
            {
                int diff = untrimLine.Length - untrimLine.TrimStart().Length;
                string whitespace =
                    String.IsNullOrWhiteSpace(untrimLine) ? "" : untrimLine.Substring(0, diff);
                ReplaceText(whitespace + suggestion.Item1, currentTextLineN);
                return true;
            }

        }
        catch (Exception e)
        {
            CodeiumVSPackage.Instance?.LogAsync("Exception: " + e.ToString());
        }

        return false;
    }

    // replaces text in the editor
    void ReplaceText(string text, int lineN)
    {
        if (view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId) == "\r\n")
        {
            text = text.Replace("\n", "\r\n");
        }
        var oldLineN = lineN + suggestion.Item2.Length - 1;
        bool insertion = isTextInsertion && suggestion.Item2.Length == 1;
        var oldUserIndex = userIndex;
        int offset = text.Length - suggestion.Item1.Length;
        ClearSuggestion();
        SnapshotSpan span = this.snapshot.GetLineFromLineNumber(lineN).Extent;
        ITextEdit edit = view.TextBuffer.CreateEdit();
        var spanLength = span.Length;
        edit.Replace(span, text);
        var newSnapshot = edit.Apply();

        if (spanLength == 0 && text.Length > 0)
        {
            view.Caret.MoveTo(newSnapshot.GetLineFromLineNumber(oldLineN).End);
        }

        if (insertion)
        {
            view.Caret.MoveTo(
                newSnapshot.GetLineFromLineNumber(oldLineN).Start.Add(oldUserIndex + offset));
        }
    }

    // sets up the suggestion for display
    void ShowSuggestion(String text, int suggestionLineStart)
    {
        UpdateAdornment(view, text, suggestionLineStart);

        showSuggestion = true;
        MarkDirty();
    }

    // removes the suggestion
    public void ClearSuggestion()
    {
        try
        {
            if (!showSuggestion) return;
            InlineGreyTextTagger inlineTagger = GetTagger();
            inlineTagger.ClearAdornment();
            inlineTagger.MarkDirty();
            suggestion = null;
            adornmentLayer.RemoveAllAdornments();
            showSuggestion = false;

            MarkDirty();

        }
        catch (Exception ex)
        {

        }
    }

    // triggers refresh of the screen
    void MarkDirty()
    {
        try
        {
            GetTagger().MarkDirty();
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
        } catch (Exception e) { Debug.Write(e); }
    }
}

[Export(typeof(IViewTaggerProvider))]
[TagType(typeof(SuggestionTag))]
[ContentType("text")]
internal sealed class SuggestionProvider : IViewTaggerProvider
{

    [Export(typeof(AdornmentLayerDefinition))]
    [Name("CodeiumAdornmentLayer")]
    [Order(After = PredefinedAdornmentLayers.Caret)]
    private AdornmentLayerDefinition editorAdornmentLayer;

#pragma warning restore 649, 169

    // create a single tagger for each buffer.
    // the MultilineGreyTextTagger displays the grey text in the editor.
    public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer)
        where T : ITag
    {
        Func<ITagger<T>> sc = delegate()
        {
            return new SuggestionTagger((IWpfTextView)textView, buffer) as ITagger<T>;
        };
        return buffer.Properties.GetOrCreateSingletonProperty<ITagger<T>>(typeof(SuggestionTagger),
                                                                          sc);
    }
}
}
