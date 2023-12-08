using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CodeiumVS.Utilities;


internal static class CodeAnalyzer
{
    private static readonly Dictionary<Guid, object> _languageServices = [];

    // This method will return a Span(0,0) if not found
    public static Span GetBlockSpan(ITextView view, int position, out IStructureTag? outtag)
    {
        outtag = null;

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

        int length = view.TextSnapshot.Length - position;
        if (length > 1000) length = 1000;
        else if (length < 0) return new Span(0, 0);

        // get the tag aggregator
        ITagAggregator<IStructureTag> tagAggregator =
            MefProvider.Instance.TagAggregatorFactoryService.CreateTagAggregator<IStructureTag>(view);

        // not sure if this could happen
        if (tagAggregator == null) return new Span(0, 0);

        // get all the structure tag that intersect with the span
        SnapshotSpan tagSpan = new(view.TextSnapshot, position, length);
        IEnumerable<IMappingTagSpan<IStructureTag>> mappingTags = tagAggregator.GetTags(tagSpan);

        int start = 0, end = 0;

        // iterate through the tags and find the closest block to the position
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

    public static async Task<T?> GetLanguageServiceAsync<T>(Guid languageServiceId) where T : class
    {
        if (_languageServices.TryGetValue(languageServiceId, out var languageService))
            return languageService as T;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ServiceProvider.GlobalProvider.QueryService(languageServiceId, out languageService);
        
        if (languageService != null)
            _languageServices.Add(languageServiceId, languageService);

        return languageService as T;
    }

    public static T? GetLanguageService<T>(Guid languageServiceId) where T : class
    {
        if (_languageServices.TryGetValue(languageServiceId, out var languageService))
            return languageService as T;

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ServiceProvider.GlobalProvider.QueryService(languageServiceId, out languageService);

            if (languageService != null)
                _languageServices.Add(languageServiceId, languageService);
        });

        return languageService as T;
    }

    public static async Task<FunctionBlock?> GetFunctionBlockAsync(IWpfTextView view, int line, int column)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        IVsTextView? vsTextView = view.ToIVsTextView();

        // get the vs text buffer and language service id
        if (vsTextView == null) return null;
        if (ErrorHandler.Failed(vsTextView.GetBuffer(out var vsTextLines))) return null;
        if (ErrorHandler.Failed(vsTextLines.GetLanguageServiceID(out var languageServiceID))) return null;

        IVsLanguageBlock? vsLanguageBlock = await GetLanguageServiceAsync<IVsLanguageBlock>(languageServiceID);
        if (vsLanguageBlock == null) return null;

        TextSpan[] spans = [new TextSpan()];

        if (ErrorHandler.Failed(
            vsLanguageBlock.GetCurrentBlock(vsTextLines, line, column, spans, out string desc, out int avail))
        ) return null;

        var splits = desc.Split('(');
        string fullname = splits[0].Split(' ').Last();
        string name = fullname.Split(':').Last().Split('.').Last();
        string parameters = "";

        if (splits.Length >= 2)
        {
            for (int i = 1; i < splits.Length; i++)
            {
                parameters += splits[i] + "(";
            }

            parameters = parameters.Substring(0, parameters.LastIndexOf(')'));
        }

        return new(fullname, name, parameters, spans[0]);
    }

    public static FunctionBlock? GetFunctionBlock(IWpfTextView view, int line, int column)
    {
        // i don't know why this got flagged
        // https://github.com/Microsoft/vs-threading/blob/main/doc/analyzers/VSTHRD102.md
        return ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            return await GetFunctionBlockAsync(view, line, column);
        });
    }
}
