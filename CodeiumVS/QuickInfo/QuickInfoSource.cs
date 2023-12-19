using CodeiumVS.Utilities;
using Microsoft;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeiumVS.QuickInfo;

internal sealed class CodeiumAsyncQuickInfoSource
(ITextBuffer textBuffer)
    : PropertyOwnerExtension<ITextBuffer, CodeiumAsyncQuickInfoSource>(textBuffer),
      IAsyncQuickInfoSource,
      IDisposable
{
    private ITextView _tagAggregatorTextView;
    private ITagAggregator<IErrorTag> ? _tagAggregator;

    private static readonly ImageId _icon = KnownMonikers.StatusInformation.ToImageId();

    private static string GetQuickInfoItemText(object content)
    {
        if (content is string str) return str;
        if (content is ClassifiedTextRun textRun) return textRun.Text;

        if (content is ContainerElement containter)
        {
            string text = string.Empty;
            foreach (var element in containter.Elements) text += GetQuickInfoItemText(element);
            return text;
        }

        if (content is ClassifiedTextElement textElement)
        {
            string text = string.Empty;
            foreach (var element in textElement.Runs) text += GetQuickInfoItemText(element);
            return text;
        }

        return string.Empty;
    }

    public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session,
                                                           CancellationToken cancellationToken)
    {
        if (_disposed || session.TextView.TextBuffer != _owner) return null;

        await GetTagAggregatorAsync(session.TextView);

        Assumes.True(_tagAggregator != null,
                     "Codeium Quick Info Source couldn't create a tag aggregator for error tags");

        // Put together the span over which tags are to be discovered.
        // This will be the zero-length span at the trigger point of the session.
        SnapshotPoint? subjectTriggerPoint = session.GetTriggerPoint(_owner.CurrentSnapshot);
        if (!subjectTriggerPoint.HasValue)
        {
            Debug.Fail("The Codeium Quick Info Source is being called when it shouldn't be.");
            return null;
        }

        ITextSnapshot currentSnapshot = subjectTriggerPoint.Value.Snapshot;
        var querySpan = new SnapshotSpan(subjectTriggerPoint.Value, 0);

        // Must be on main thread when dealing with tag aggregator
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Ask for all of the error tags that intersect our query span.  We'll get back a list of
        // mapping tag spans. The first of these is what we'll use for our quick info.
        IEnumerable<IMappingTagSpan<IErrorTag>> tags = _tagAggregator.GetTags(querySpan);
        ITrackingSpan appToSpan = null;

        string problemMessage = string.Empty;

        foreach (var tag in tags.Cast<MappingTagSpan<IErrorTag>>())
        {
            NormalizedSnapshotSpanCollection applicableToSpans = tag.Span.GetSpans(currentSnapshot);
            if ((applicableToSpans.Count == 0) || (tag.Tag.ToolTipContent == null)) continue;

            // We've found a error tag at the right location with a tag span that maps to our
            // subject buffer.
            appToSpan = currentSnapshot.CreateTrackingSpan(applicableToSpans[0].Span,
                                                           SpanTrackingMode.EdgeInclusive);

            appToSpan =
                IntellisenseUtilities.GetEncapsulatingSpan(session.TextView, appToSpan, appToSpan);

            problemMessage += GetQuickInfoItemText(tag.Tag.ToolTipContent) + " and ";
        }

        if (appToSpan != null && problemMessage.Length > 0)
        {
            var hyperLink = ClassifiedTextElement.CreateHyperlink(
                "Codeium: Explain Problem",
                "Ask codeium to explain the problem",
                () =>
                {
                    ThreadHelper.JoinableTaskFactory
                        .RunAsync(async delegate {
                            // TODO: Has the package been loaded at this point?
                            await CodeiumVSPackage.Instance.LanguageServer.Controller
                                .ExplainProblemAsync(
                                    problemMessage.Substring(0, problemMessage.Length - 5),
                                    appToSpan.GetSpan(currentSnapshot));
                        })
                        .FireAndForget(true);
                });

            ContainerElement container =
                new(ContainerElementStyle.Wrapped, new ImageElement(_icon), hyperLink);
            return new QuickInfoItem(appToSpan, container);
        }

        return null;
    }

    private async Task GetTagAggregatorAsync(ITextView textView)
    {
        if (_tagAggregator == null)
        {
            _tagAggregatorTextView = textView;
            _tagAggregator = await IntellisenseUtilities.GetTagAggregatorAsync<IErrorTag>(textView);
        }
        else if (_tagAggregatorTextView != textView)
        {
            throw new ArgumentException(
                "The Codeium Quick Info Source cannot be shared between TextViews.");
        }
    }
}

[Export(typeof(IAsyncQuickInfoSourceProvider))]
[Name("Codeium Quick Info Provider")]
[ContentType("any")]
[Order(After = "Default Quick Info Presenter")]
internal sealed class AsyncQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
{
    public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
    {
        return CodeiumAsyncQuickInfoSource.GetOrCreate(
            textBuffer, () => new CodeiumAsyncQuickInfoSource(textBuffer));
    }
}
