using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CodeiumVS.Packets;

namespace CodeiumVS.Commands;

[Command(PackageIds.OpenChatWindow)]
internal sealed class CommandOpenChatWindow : BaseCommand<CommandOpenChatWindow>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        ToolWindowPane toolWindowPane = await CodeiumVSPackage.Instance.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, CodeiumVSPackage.Instance.DisposalToken);
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

[Command(PackageIds.EnterAuthToken)]
internal sealed class CommandEnterAuthToken : BaseCommand<CommandEnterAuthToken>
{
    protected override void Execute(object sender, EventArgs e)
    {
        new EnterTokenDialogWindow().ShowDialog();
    }
}

//[Command(PackageIds.DebugButton)]
//internal sealed class CommandDebugButton : BaseCommand<CommandDebugButton>
//{

//    //protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
//    //{
//    //}
//}

// this class provide the code context for the 4 right-click menu commands
// otherwise, those commands will need to do the same thing repeatedly
// this is rather ugly, but it works
internal class BaseCommandContextMenu<T> : BaseCommand<T> where T : class, new()
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
        is_visible = Command.Visible = ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            is_function = false;
            docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView == null) return false;

            languageInfo = Languages.Mapper.GetLanguage(docView);
            ITextSelection selection = docView.TextView.Selection;

            start_position = selection.Start.Position;
            end_position = selection.End.Position;

            // if there is no selection, attempt to get the code block at the caret
            if (selection.SelectedSpans.Count == 0 || start_position == end_position)
            {
                Span blockSpan = TextHighlighter.Instance.GetBlockSpan(out var tag, selection.Start.Position.Position, selection.Start.Position.Snapshot);
                if (tag == null) return false;

                start_position = blockSpan.Start;
                end_position = blockSpan.End;

                // "Nonstructural" == nothing; Namespace" == Namespace
                // "Type" == class/struct/enum; "Expression" == lambda; "Member" == function
                is_function = tag?.Type == "Member";
            }

            ITextSnapshotLine selectionStart = docView.TextBuffer.CurrentSnapshot.GetLineFromPosition(start_position);
            ITextSnapshotLine selectionEnd = docView.TextBuffer.CurrentSnapshot.GetLineFromPosition(end_position);

            start_line = selectionStart.LineNumber + 1;
            end_line = selectionEnd.LineNumber + 1;
            start_col = start_position - selectionStart.Start.Position + 1;
            end_col = end_position - selectionEnd.Start.Position + 1;

            text = docView.TextBuffer.CurrentSnapshot.GetText(start_position, end_position - start_position);

            return true;
        });
    }

    // Get the current function name and parameters, "current" being the at the the block caret is currently in
    protected bool GetCurrentFunctionInfo(out string? functionName, out string? parameters)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        functionName = null;
        parameters = null;

        IVsTextView vsTextView = VsShellUtilities.GetTextView(docView.WindowFrame);
        if (vsTextView == null) return false;
        if (vsTextView.GetBuffer(out var vsTextLines) != 0) return false;
        if (vsTextLines.GetLanguageServiceID(out var languageServiceID) != 0) return false;

        ServiceProvider.GlobalProvider.QueryService(languageServiceID, out var languageService);
        IVsLanguageBlock vsLanguageBlock = languageService as IVsLanguageBlock;
        if (vsLanguageBlock == null) return false;

        TextSpan[] spans = [new TextSpan()];
        vsLanguageBlock.GetCurrentBlock(vsTextLines, start_line, start_col, spans, out string desc, out int avail);
        if (avail == 0) return false;

        var splits = desc.Split('(');
        functionName = splits[0].Split(' ').Last();
        parameters = splits[1].Substring(0, splits[1].LastIndexOf(')'));
        return true;
    }

    protected async Task<FunctionInfo?> GetFunctionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!GetCurrentFunctionInfo(out string? functionName, out string? functionParams))
        {
            await CodeiumVSPackage.Instance.LogAsync("Error: Could not get function info");
            return null;
        }

        return new()
        {
            raw_source = text,
            clean_function = text,
            node_name = functionName,
            @params = functionParams,
            definition_line = start_line,
            start_line = start_line,
            end_line = end_line,
            start_col = start_col,
            end_col = end_col,
            language = languageInfo.Type,
        };
    }

    protected CodeBlockInfo GetCodeBlockInfo()
    {
        return new()
        {
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
        if (Command.Visible) Command.Text = is_function ? "Codeium: Explain Function" : "Codeium: Explain Code block";
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        LanguageServerController controller = (Package as CodeiumVSPackage).LanguageServer.Controller;

        if (is_function)
        {
            FunctionInfo functionInfo = await GetFunctionInfoAsync();
            await controller.ExplainFunctionAsync(docView.Document.FilePath, functionInfo);
        }
        else
        {
            CodeBlockInfo codeBlockInfo = GetCodeBlockInfo();
            await controller.ExplainCodeBlockAsync(docView.Document.FilePath, languageInfo.Type, codeBlockInfo);
        }
    }
}

[Command(PackageIds.RefactorCodeBlock)]
internal class CommandRefactorCodeBlock : BaseCommandContextMenu<CommandRefactorCodeBlock>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        base.BeforeQueryStatus(e);
        if (Command.Visible) Command.Text = is_function ? "Codeium: Refactor Function" : "Codeium: Refactor Code block";
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        // get the caret screen position and create the dialog at that position
        TextBounds caretLine = docView.TextView.TextViewLines.GetCharacterBounds(docView.TextView.Caret.Position.BufferPosition);
        Point caretScreenPos = docView.TextView.VisualElement.PointToScreen(
            new Point(caretLine.Left - docView.TextView.ViewportLeft, caretLine.Top - docView.TextView.ViewportTop)
        );

        // highlight the selected codeblock
        TextHighlighter.Instance?.AddHighlight(start_position, end_position - start_position, docView.TextView.TextSnapshot);

        var dialog = RefactorCodeDialogWindow.GetOrCreate();
        string? prompt = await dialog.ShowAndGetPromptAsync(languageInfo, caretScreenPos.X, caretScreenPos.Y);

        TextHighlighter.Instance?.ClearAll();

        // user did not select any of the prompt
        if (prompt == null) return;

        LanguageServerController controller = (Package as CodeiumVSPackage).LanguageServer.Controller;

        if (is_function)
        {
            FunctionInfo functionInfo = await GetFunctionInfoAsync();
            await controller.RefactorFunctionAsync(prompt, docView.Document.FilePath, functionInfo);
        }
        else
        {
            CodeBlockInfo codeBlockInfo = GetCodeBlockInfo();
            await controller.RefactorCodeBlockAsync(prompt, docView.Document.FilePath, languageInfo.Type, codeBlockInfo);
        }
    }
}

[Command(PackageIds.GenerateFunctionUnitTest)]
internal class CommandGenerateFunctionUnitTest : BaseCommandContextMenu<CommandGenerateFunctionUnitTest>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        base.BeforeQueryStatus(e);
        Command.Visible = is_function;
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        LanguageServerController controller = (Package as CodeiumVSPackage).LanguageServer.Controller;
        FunctionInfo functionInfo = await GetFunctionInfoAsync();
        await controller.GenerateFunctionUnitTestAsync("Generate unit test", docView.Document.FilePath, functionInfo);
    }
}

[Command(PackageIds.GenerateFunctionDocstring)]
internal class CommandGenerateFunctionDocstring : BaseCommandContextMenu<CommandGenerateFunctionDocstring>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        base.BeforeQueryStatus(e);
        Command.Visible = is_function;
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        LanguageServerController controller = (Package as CodeiumVSPackage).LanguageServer.Controller;
        FunctionInfo functionInfo = await GetFunctionInfoAsync();
        await controller.GenerateFunctionDocstringAsync(docView.Document.FilePath, functionInfo);
    }
}