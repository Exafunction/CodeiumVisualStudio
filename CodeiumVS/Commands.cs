﻿using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CodeiumVS.Packets;
using CodeiumVS.Utilities;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Windows.Shapes;
using static System.Windows.Forms.LinkLabel;
using System.Threading;
using Microsoft.VisualStudio.Language.CodeLens;

namespace CodeiumVS.Commands;

[Command(PackageIds.OpenChatWindow)]
internal sealed class CommandOpenChatWindow : BaseCommand<CommandOpenChatWindow>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        ToolWindowPane toolWindowPane = await CodeiumVSPackage.Instance.ShowToolWindowAsync(
            typeof(ChatToolWindow), 0, create: true, CodeiumVSPackage.Instance.DisposalToken);
        if (toolWindowPane == null || toolWindowPane.Frame == null)
        {
            throw new NotSupportedException("Cannot create Codeium chat tool window");
        }
    }
}

[Command(PackageIds.SignIn)]
internal sealed class CommandSignIn : BaseCommand<CommandSignIn>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await CodeiumVSPackage.Instance.LanguageServer.SignInAsync();
    }
}

[Command(PackageIds.SignOut)]
internal sealed class CommandSignOut : BaseCommand<CommandSignOut>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await CodeiumVSPackage.Instance.LanguageServer.SignOutAsync();
    }
}

[Command(PackageIds.CompleteSuggestion)]
internal sealed class CommandCompleteSuggestion : BaseCommandCompletionHandler<CommandCompleteSuggestion>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        try
        {
            if(completionHandler == null) return;
            completionHandler.CompleteSuggestion();
        }
        catch (Exception ex)
        {
            await CodeiumVSPackage.Instance.LogAsync(
                $"CommandShowNextSuggestion: Failed to complete suggestion; Exception: {ex}");
        }
    }
}

[Command(PackageIds.ShowNextSuggestion)]
internal sealed class CommandShowNextSuggestion : BaseCommandCompletionHandler<CommandShowNextSuggestion>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        try
        {
            if (completionHandler == null) return;

            completionHandler.ShowNextSuggestion();
        }
        catch (Exception ex)
        {
            await CodeiumVSPackage.Instance.LogAsync(
                $"CommandShowNextSuggestion: Failed to show next suggestion; Exception: {ex}");
        }
    }
}
internal class BaseCommandCompletionHandler<T> : BaseCommand<T>
    where T : class, new()
{
    internal static long lastQuery = 0;
    protected static DocumentView? docView;
    protected static CodeiumCompletionHandler? completionHandler;
    protected override void BeforeQueryStatus(EventArgs e)
    {
        // Derived menu commands will call this repeatedly upon openning
        // so we only want to do it once, i can't find a better way to do it
        long timeStamp = Stopwatch.GetTimestamp();
        if (lastQuery != 0 && timeStamp - lastQuery < 500) return;
        lastQuery = timeStamp;

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            // any interactions with the `IVsTextView` should be done on the main thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextView == null) return false;

                var key = typeof(CodeiumCompletionHandler);
                var props = docView.TextBuffer.Properties;
                if (props.ContainsProperty(key))
                {
                    completionHandler = props.GetProperty<CodeiumCompletionHandler>(key);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                await CodeiumVSPackage.Instance.LogAsync(
                    $"BaseCommandDocView: Failed to get the active document view; Exception: {ex}");
                return false;
            }
        });
    }
}

//[Command(PackageIds.DebugButton)]
// internal sealed class CommandDebugButton : BaseCommand<CommandDebugButton>
//{

//    //protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
//    //{
//    //}
//}

// this class provide the code context for the 4 right-click menu commands
// otherwise, those commands will need to do the same thing repeatedly
// this is rather ugly, but it works
internal class BaseCommandContextMenu<T> : BaseCommand<T>
    where T : class, new()
{
    internal static long lastQuery = 0;
    internal static bool is_visible = false;

    protected static DocumentView? docView;
    protected static string text; // the selected text
    protected static bool is_function = false;
    protected static int start_line, end_line;
    protected static int start_col, end_col;
    protected static int start_position, end_position;
    protected static Languages.LangInfo languageInfo;

    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Visible = is_visible;

        // Derived menu commands will call this repeatedly upon openning
        // so we only want to do it once, i can't find a better way to do it
        long timeStamp = Stopwatch.GetTimestamp();
        if (lastQuery != 0 && timeStamp - lastQuery < 500) return;
        lastQuery = timeStamp;

        // If there are no selection, and we couldn't find any block that the caret is in
        // then we don't want to show the command
        is_visible = Command.Visible = ThreadHelper.JoinableTaskFactory.Run(async delegate {
            is_function = false;

            // any interactions with the `IVsTextView` should be done on the main thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextView == null) return false;
            }
            catch (Exception ex)
            {
                await CodeiumVSPackage.Instance.LogAsync(
                    $"BaseCommandContextMenu: Failed to get the active document view; Exception: {ex}");
                return false;
            }

            languageInfo = Languages.Mapper.GetLanguage(docView);
            ITextSelection selection = docView.TextView.Selection;

            start_position = selection.Start.Position;
            end_position = selection.End.Position;

            // if there is no selection, attempt to get the code block at the caret
            if (selection.SelectedSpans.Count == 0 || start_position == end_position)
            {
                Span blockSpan = CodeAnalyzer.GetBlockSpan(
                    docView.TextView, selection.Start.Position.Position, out var tag);
                if (tag == null) return false;

                start_position = blockSpan.Start;
                end_position = blockSpan.End;

                // "Type"          | class, struct, enum
                // "Member"        | function
                // "Namespace"     | namespace
                // "Expression"    | lambda
                // "Nonstructural" | nothing
                is_function = tag?.Type == "Member";
            }

            ITextSnapshotLine selectionStart =
                docView.TextBuffer.CurrentSnapshot.GetLineFromPosition(start_position);
            ITextSnapshotLine selectionEnd =
                docView.TextBuffer.CurrentSnapshot.GetLineFromPosition(end_position);

            start_line = selectionStart.LineNumber + 1;
            end_line = selectionEnd.LineNumber + 1;
            start_col = start_position - selectionStart.Start.Position + 1;
            end_col = end_position - selectionEnd.Start.Position + 1;

            text = docView.TextBuffer.CurrentSnapshot.GetText(start_position,
                                                              end_position - start_position);

            return true;
        });
    }

    protected async Task<FunctionInfo?> GetFunctionInfoAsync()
    {
        FunctionBlock? func =
            await CodeAnalyzer.GetFunctionBlockAsync(docView.TextView, start_line, start_col);

        if (func == null)
        {
            await CodeiumVSPackage.Instance.LogAsync("Error: Could not get function info");
            return null;
        }

        return new() {
            RawSource = text,
            CleanFunction = text,
            NodeName = func.Name,
            Params = func.Params,
            DefinitionLine = start_line,
            StartLine = start_line,
            EndLine = end_line,
            StartCol = start_col,
            EndCol = end_col,
            Language = languageInfo.Type,
        };
    }

    protected CodeBlockInfo GetCodeBlockInfo()
    {
        return new() {
            raw_source = text,
            start_line = start_line,
            end_line = end_line,
            start_col = start_col,
            end_col = end_col,
        };
    }
}

[Command(PackageIds.ExplainCodeBlock)]
internal class CommandExplainCodeBlock : BaseCommandContextMenu<CommandExplainCodeBlock>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        base.BeforeQueryStatus(e);
        if (Command.Visible)
            Command.Text =
                is_function ? "Codeium: Explain Function" : "Codeium: Explain Code block";
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        LanguageServerController controller =
            (Package as CodeiumVSPackage).LanguageServer.Controller;

        if (is_function)
        {
            FunctionInfo? functionInfo = await GetFunctionInfoAsync();

            if (functionInfo != null)
                await controller.ExplainFunctionAsync(docView.Document.FilePath, functionInfo);
        }
        else
        {
            CodeBlockInfo codeBlockInfo = GetCodeBlockInfo();
            await controller.ExplainCodeBlockAsync(
                docView.Document.FilePath, languageInfo.Type, codeBlockInfo);
        }
    }
}

[Command(PackageIds.RefactorCodeBlock)]
internal class CommandRefactorCodeBlock : BaseCommandContextMenu<CommandRefactorCodeBlock>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        base.BeforeQueryStatus(e);
        if (Command.Visible)
            Command.Text =
                is_function ? "Codeium: Refactor Function" : "Codeium: Refactor Code block";
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        // get the caret screen position and create the dialog at that position
        TextBounds caretLine = docView.TextView.TextViewLines.GetCharacterBounds(
            docView.TextView.Caret.Position.BufferPosition);
        Point caretScreenPos = docView.TextView.VisualElement.PointToScreen(
            new Point(caretLine.Left - docView.TextView.ViewportLeft,
                      caretLine.Top - docView.TextView.ViewportTop));

        // highlight the selected codeblock
        TextHighlighter? highlighter = TextHighlighter.GetInstance(docView.TextView);
        highlighter?.AddHighlight(start_position, end_position - start_position);
        var dialog = RefactorCodeDialogWindow.GetOrCreate();
        string? prompt =
            await dialog.ShowAndGetPromptAsync(languageInfo, caretScreenPos.X, caretScreenPos.Y);

        highlighter?.ClearAll();

        // user did not select any of the prompt
        if (prompt == null) return;

        LanguageServerController controller =
            (Package as CodeiumVSPackage).LanguageServer.Controller;
        if (is_function)
        {
            FunctionInfo? functionInfo = await GetFunctionInfoAsync();

            if (functionInfo != null)
                await controller.RefactorFunctionAsync(
                    prompt, docView.Document.FilePath, functionInfo);
            
        }
        else
        {
            CodeBlockInfo codeBlockInfo = GetCodeBlockInfo();
            await controller.RefactorCodeBlockAsync(
                prompt, docView.Document.FilePath, languageInfo.Type, codeBlockInfo);
        }
    }
}

internal class BaseCommandCodeLens<T> : BaseCommand<T>
    where T : class, new()
{
    internal static long lastQuery = 0;

    protected static DocumentView? docView;
    protected static string text; // the selected text
    protected static Languages.LangInfo languageInfo;
    protected FunctionInfo functionInfo;
    protected ClassInfo classInfo;
    protected override void BeforeQueryStatus(EventArgs e)
    {
        // Derived menu commands will call this repeatedly upon openning
        // so we only want to do it once, i can't find a better way to do it
        long timeStamp = Stopwatch.GetTimestamp();
        if (lastQuery != 0 && timeStamp - lastQuery < 500) return;
        lastQuery = timeStamp;

        ThreadHelper.JoinableTaskFactory.Run(async delegate {

            // any interactions with the `IVsTextView` should be done on the main thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                docView = await VS.Documents.GetActiveDocumentViewAsync();
                text = docView.TextBuffer.CurrentSnapshot.GetText();
                if (docView?.TextView == null) return false;
            }
            catch (Exception ex)
            {
                await CodeiumVSPackage.Instance.LogAsync(
                    $"BaseCommandContextMenu: Failed to get the active document view; Exception: {ex}");
                return false;
            }

            languageInfo = Languages.Mapper.GetLanguage(docView);
            ITextSelection selection = docView.TextView.Selection;

            return true;
        });
    }

    protected async Task<bool> ResolveCodeBlock(int startLine)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        IList<Packets.FunctionInfo>? functions =
            await CodeiumVSPackage.Instance.LanguageServer.GetFunctionsAsync(
                docView.FilePath,
                text,
                languageInfo,
                0,
                cts.Token);

        IList<Packets.ClassInfo>? classes = await CodeiumVSPackage.Instance.LanguageServer.GetClassInfosAsync(
            docView.FilePath,
            text,
            languageInfo,
            0,
            docView.TextView.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId),
            cts.Token);

        FunctionInfo minFunction = null;
        int minDistance = int.MaxValue;
        foreach (var f in functions)
        {
            var distance = Math.Abs(f.DefinitionLine - startLine);
            if (distance < minDistance)
            {
                minDistance = distance;
                functionInfo = f;
            }
        }

        foreach (var c in classes)
        {
            var distance = Math.Abs(c.StartLine - startLine);
            if (distance < minDistance)
            {
                minDistance = distance;
                functionInfo = null;
                classInfo = c;
            }
        }
        return true;
    }

    protected CodeBlockInfo ClassToCodeBlock(ClassInfo c)
    {
        CodeBlockInfo codeBlockInfo = null;
        try
        {
            var snapshotLineStart = docView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(c.StartLine);
            var snapShotLineEnd = docView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(c.EndLine);

            var start_position = snapshotLineStart.Start;
            var end_position = snapShotLineEnd.End;

            var start_line = snapshotLineStart.LineNumber + 1;
            var end_line = snapShotLineEnd.LineNumber + 1;
            var start_col = start_position - snapshotLineStart.Start.Position + 1;
            var end_col = end_position - snapShotLineEnd.Start.Position + 1;

            text = docView.TextBuffer.CurrentSnapshot.GetText(start_position,
                end_position - start_position);

            codeBlockInfo = new()
            {
                raw_source = text,
                start_line = start_line,
                end_line = end_line,
                start_col = start_col,
                end_col = end_col,
            };
        }
        catch (Exception ex)
        {
            Task.Run(async () =>
            {
                return CodeiumVSPackage.Instance.LogAsync(ex.ToString());
            });
        }
        return codeBlockInfo;
    }

}


[Command(PackageIds.RefactorSelectionCodeBlock)]
internal class CommandRefactorSelectionCodeBlock : BaseCommandCodeLens<CommandRefactorSelectionCodeBlock>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {

        try
        {
            LanguageServerController controller =
                (Package as CodeiumVSPackage).LanguageServer.Controller;

            CodeLensDescriptorContext ctx = null;
            ITextSnapshotLine snapshotLine;
            int startPos;
            if (e.InValue != null)
            {
                await CodeiumVSPackage.Instance.LogAsync(e.InValue.ToString());
                ctx = e.InValue as CodeLensDescriptorContext;
                startPos = ctx.ApplicableSpan.Value.Start;
                snapshotLine = docView.TextBuffer.CurrentSnapshot.GetLineFromPosition(startPos);
                int startLine = snapshotLine.LineNumber;
                TextBounds selectionLine = docView.TextView.TextViewLines.GetCharacterBounds(snapshotLine.Start);
                Point selectionScreenPos = docView.TextView.VisualElement.PointToScreen(
                    new Point(selectionLine.Left - docView.TextView.ViewportLeft,
                        selectionLine.Top - docView.TextView.ViewportTop));

                var start = docView.TextView.TextViewLines.GetCharacterBounds(snapshotLine.Start);

                // highlight the selected codeblock
                TextHighlighter? highlighter = TextHighlighter.GetInstance(docView.TextView);
                highlighter?.AddHighlight(snapshotLine.Extent);

                var dialog = RefactorCodeDialogWindow.GetOrCreate();
                string? prompt =
                    await dialog.ShowAndGetPromptAsync(languageInfo, selectionScreenPos.X, selectionScreenPos.Y);

                highlighter?.ClearAll();

                await ResolveCodeBlock(startLine);
                // user did not select any of the prompt
                if (prompt == null) return;
                if (functionInfo != null)
                {
                    controller.RefactorFunctionAsync(
                        prompt, docView.FilePath, functionInfo);
                }
                else
                {
                    if (classInfo == null) return;
                    CodeBlockInfo codeBlockInfo = ClassToCodeBlock(classInfo);

                    await controller.RefactorCodeBlockAsync(
                        prompt, docView.FilePath, languageInfo.Type, codeBlockInfo);
                }
            }
        }
        catch (Exception ex)
        {
            await CodeiumVSPackage.Instance.LogAsync(ex.ToString());
        }
    }
}

[Command(PackageIds.ExplainSelectionCodeBlock)]
internal class ExplainSelectionCodeBlock : BaseCommandCodeLens<ExplainSelectionCodeBlock>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {

        try
        {
            LanguageServerController controller =
                (Package as CodeiumVSPackage).LanguageServer.Controller;

            if (e.InValue != null)
            {
                await CodeiumVSPackage.Instance.LogAsync(e.InValue.ToString());
                var ctx = e.InValue as CodeLensDescriptorContext;
                int startPos = ctx.ApplicableSpan.Value.Start;
                ITextSnapshotLine line = docView.TextBuffer.CurrentSnapshot.GetLineFromPosition(startPos);
                int startLine = line.LineNumber;

                await ResolveCodeBlock(startLine);
                if (functionInfo != null)
                {
                    controller.ExplainFunctionAsync(docView.FilePath, functionInfo);
                }
                else
                {
                    if (classInfo == null) return;
                    CodeBlockInfo codeBlockInfo = ClassToCodeBlock(classInfo);
                    controller.ExplainCodeBlockAsync(
                        docView.FilePath, languageInfo.Type, codeBlockInfo);
                }
            }
        }
        catch (Exception ex)
        {
            await CodeiumVSPackage.Instance.LogAsync(ex.ToString());
        }
    }
}


[Command(PackageIds.GenerateSelectionFunctionDocstring)]
internal class GenerateSelectionFunctionDocstring : BaseCommandCodeLens<GenerateSelectionFunctionDocstring>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {

        try
        {
            LanguageServerController controller =
                (Package as CodeiumVSPackage).LanguageServer.Controller;

            if (e.InValue != null)
            {
                await CodeiumVSPackage.Instance.LogAsync(e.InValue.ToString());
                var ctx = e.InValue as CodeLensDescriptorContext;
                int startPos = ctx.ApplicableSpan.Value.Start;
                ITextSnapshotLine line = docView.TextBuffer.CurrentSnapshot.GetLineFromPosition(startPos);
                int startLine = line.LineNumber;

                await ResolveCodeBlock(startLine);
                if (functionInfo != null)
                {
                    controller.GenerateFunctionDocstringAsync(docView.FilePath, functionInfo);
                }
            }
        }
        catch (Exception ex)
        {
            await CodeiumVSPackage.Instance.LogAsync(ex.ToString());
        }
    }
}
[Command(PackageIds.GenerateFunctionUnitTest)]
internal class CommandGenerateFunctionUnitTest
    : BaseCommandContextMenu<CommandGenerateFunctionUnitTest>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        base.BeforeQueryStatus(e);
        Command.Visible = is_function;
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        LanguageServerController controller =
            (Package as CodeiumVSPackage).LanguageServer.Controller;
        FunctionInfo? functionInfo = await GetFunctionInfoAsync();

        if (functionInfo != null)
            await controller.GenerateFunctionUnitTestAsync(
                "Generate unit test", docView.Document.FilePath, functionInfo);
    }
}

[Command(PackageIds.GenerateFunctionDocstring)]
internal class CommandGenerateFunctionDocstring
    : BaseCommandContextMenu<CommandGenerateFunctionDocstring>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        base.BeforeQueryStatus(e);
        Command.Visible = is_function;
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        LanguageServerController controller =
            (Package as CodeiumVSPackage).LanguageServer.Controller;
        FunctionInfo? functionInfo = await GetFunctionInfoAsync();

        if (functionInfo != null)
            await controller.GenerateFunctionDocstringAsync(docView.Document.FilePath,
                                                            functionInfo);
    }
}
