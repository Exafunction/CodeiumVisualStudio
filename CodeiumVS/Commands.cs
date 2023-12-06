using EnvDTE;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextTemplating;
using System.ComponentModel.Composition;
using System.ComponentModel.Design;
using System;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using System.IO.Packaging;
using System.Reflection;

namespace CodeiumVS;

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
        await CodeiumVSPackage.Instance.langServer.SignInAsync();
    }
}

[Command(PackageIds.SignOut)]
internal sealed class CommandSignOut : BaseCommand<CommandSignOut>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await CodeiumVSPackage.Instance.langServer.SignOutAsync();
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

[Command(PackageIds.DebugButton)]
internal sealed class CommandDebugButton : BaseCommand<CommandDebugButton>
{
    private static async Task<DTE> GetDTE2Async()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
    }
    private async Task<IWpfTextViewHost> GetCurrentViewHostAsync()
    {
        // code to get access to the editor's currently selected text cribbed from
        // http://msdn.microsoft.com/en-us/library/dd884850.aspx
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        IVsTextManager txtMgr = (IVsTextManager)ServiceProvider.GlobalProvider.GetService(typeof(SVsTextManager));
        IVsTextView vTextView = null;
        int mustHaveFocus = 1;
        txtMgr.GetActiveView(mustHaveFocus, null, out vTextView);
        IVsUserData userData = vTextView as IVsUserData;
        if (userData == null)
        {
            return null;
        }
        else
        {
            IWpfTextViewHost viewHost;
            object holder;
            Guid guidViewHost = DefGuidList.guidIWpfTextViewHost;
            userData.GetData(ref guidViewHost, out holder);
            viewHost = (IWpfTextViewHost)holder;
            return viewHost;
        }
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        // somehow OpenViaProjectAsync doesn't work... at least for me
        await VS.Documents.OpenAsync("D:\\source\\repos\\TestCPP\\TestSDKCS2\\main.cpp");
        DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
        if (docView?.TextView == null) return;

        ITextSelection selection = docView.TextView.Selection;
        selection.Select(new SnapshotSpan(new SnapshotPoint(docView.TextBuffer.CurrentSnapshot, 300), 500), false);
        docView.TextView.Caret.MoveTo(new SnapshotPoint(docView.TextBuffer.CurrentSnapshot, 300));
        docView.TextView.Caret.EnsureVisible();
    }
}


internal abstract class BaseCommandSelectedCode<T> : BaseCommand<T> where T : class, new()
{
    protected DocumentView docView;

    protected override async Task InitializeCompletedAsync()
    {
        Command.BeforeQueryStatus += delegate (object s, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.Run(UpdateVisibilityAsync);
        };

        await base.InitializeCompletedAsync();
    }

    private async Task UpdateVisibilityAsync()
    {
        Command.Visible = false;
        if (!CodeiumVSPackage.Instance.langServer.IsReady()) return;

        docView = await VS.Documents.GetActiveDocumentViewAsync();
        if (docView?.TextView == null) return;

        ITextSelection selection = docView.TextView.Selection;

        // has no selection
        if (selection.SelectedSpans.Count == 0 || selection.SelectedSpans[0].Span.Start == selection.SelectedSpans[0].Span.End)
            return;

        Command.Visible = IsVisible();
    }

    protected virtual bool IsVisible()
    {
        return true;
    }

    protected abstract string GetText();


    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        if (this.GetType().GetCustomAttribute(typeof(CommandAttribute)) is not CommandAttribute attr)
        {
            throw new InvalidOperationException($"No [Command(GUID, ID)] attribute was added to {typeof(T).Name}");
        }

        await CodeiumVSPackage.Instance.langServer.controller.RefactorCodeBlockAsync(docView, GetText());
    }
}

[Command(PackageIds.ExplainCodeBlock)]
internal sealed class CommandExplainCodeBlock : BaseCommandSelectedCode<CommandExplainCodeBlock>
{
    protected override string GetText() { return ""; }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {

        await CodeiumVSPackage.Instance.langServer.controller.ExplainCodeBlockAsync(docView);
    }
}


[Command(PackageIds.RefactorAddCommentsAndDocString)]
internal sealed class CommandRefactorAddCommentsAndDocString : BaseCommandSelectedCode<CommandRefactorAddCommentsAndDocString>
{
    protected override string GetText() { return "Add comments and docstrings to the code."; }
}

[Command(PackageIds.RefactorAddLoggingStatements)]
internal sealed class CommandRefactorAddLoggingStatements : BaseCommandSelectedCode<CommandRefactorAddLoggingStatements>
{
    protected override string GetText()
    {
        var lang = Languages.Mapper.GetLanguage(docView);
        return lang.Type switch
        {
            Packets.Language.LANGUAGE_C          => "Add `printf()` statements so that it can be easily debugged.",
            Packets.Language.LANGUAGE_CPP        => "Add `std::cout` statements so that it can be easily debugged.",
            Packets.Language.LANGUAGE_JAVASCRIPT => "Add `console.log()` statements so that it can be easily debugged.",
            Packets.Language.LANGUAGE_PYTHON     => "Add `print()` statements so that it can be easily debugged.",
            Packets.Language.LANGUAGE_CSHARP     => "Add `Console.WriteLine()` statements so that it can be easily debugged.",
            _                                    => "Add logging statements so that it can be easily debugged.",
        };
    }
}

[Command(PackageIds.RefactorAddTypeAnnotations)]
internal sealed class CommandRefactorAddTypeAnnotations : BaseCommandSelectedCode<CommandRefactorAddTypeAnnotations>
{
    protected override bool IsVisible()
    {
        var lang = Languages.Mapper.GetLanguage(docView);
        return lang.Type switch
        {
            Packets.Language.LANGUAGE_CSHARP or
            Packets.Language.LANGUAGE_TYPESCRIPT or
            Packets.Language.LANGUAGE_PYTHON => true,
            _ => false,
        };
    }

    protected override string GetText()
    {
        return "Add type annotations to this code block, including the function arguments and return type. Modify the docstring to reflect the types.";
    }
}

[Command(PackageIds.RefactorCleanupThisCode)]
internal sealed class CommandRefactorCleanupThisCode : BaseCommandSelectedCode<CommandRefactorCleanupThisCode>
{
    protected override string GetText()
    {
        return "Clean up this code by standardizing variable names, removing debugging statements, improving readability, and more. Explain what you did to clean it up in a short and concise way.";
    }
}

[Command(PackageIds.RefactorCheckForBugsAndNullPointers)]
internal sealed class CommandRefactorCheckForBugsAndNullPointers : BaseCommandSelectedCode<CommandRefactorCheckForBugsAndNullPointers>
{
    protected override string GetText()
    {
        return "Check for bugs such as null pointer references, unhandled exceptions, and more. If you don't see anything obvious, reply that things look good and that the user can reply with a stack trace to get more information.";
    }
}

[Command(PackageIds.RefactorImplementCodeForTODOComment)]
internal sealed class CommandRefactorImplementCodeForTODOComment : BaseCommandSelectedCode<CommandRefactorImplementCodeForTODOComment>
{
    protected override string GetText() { return "Implement the code for the TODO comment."; }
}

[Command(PackageIds.RefactorFixMyPyAndPylint)]
internal sealed class CommandRefactorFixMyPyAndPylint : BaseCommandSelectedCode<CommandRefactorFixMyPyAndPylint>
{
    protected override bool IsVisible()
    {
        var lang = Languages.Mapper.GetLanguage(docView);
        return lang.Type == Packets.Language.LANGUAGE_PYTHON;
    }

    protected override string GetText() { return "Fix mypy and pylint errors and warnings."; }
}

[Command(PackageIds.RefactorMakeThisCodeStronglyTyped)]
internal sealed class CommandRefactorMakeThisCodeStronglyTyped : BaseCommandSelectedCode<CommandRefactorMakeThisCodeStronglyTyped>
{
    protected override string GetText()
    {
        return "Make this code strongly typed, including the function arguments and return type. Modify the docstring to reflect the types.";
    }
}

[Command(PackageIds.RefactorMakeThisCodeFaster)]
internal sealed class CommandRefactorMakeThisCodeFaster : BaseCommandSelectedCode<CommandRefactorMakeThisCodeFaster>
{
    protected override string GetText() { return "Make this faster and more efficient"; }
}

[Command(PackageIds.RefactorMakeThisCodeAFuntionalReactComponent)]
internal sealed class CommandRefactorMakeThisCodeAFuntionalReactComponent : BaseCommandSelectedCode<CommandRefactorMakeThisCodeAFuntionalReactComponent>
{
    protected override bool IsVisible()
    {
        var lang = Languages.Mapper.GetLanguage(docView);
        return lang.Type switch
        {
            Packets.Language.LANGUAGE_JAVASCRIPT or
            Packets.Language.LANGUAGE_TSX => true,
            _ => false,
        };
    }

    protected override string GetText() { return "Make this code a functional React component."; }
}

[Command(PackageIds.RefactorCreateTypeScriptInterfaceToDefineTheComponentGroup)]
internal sealed class CommandRefactorCreateTypeScriptInterfaceToDefineTheComponentGroup : BaseCommandSelectedCode<CommandRefactorCreateTypeScriptInterfaceToDefineTheComponentGroup>
{
    protected override bool IsVisible()
    {
        var lang = Languages.Mapper.GetLanguage(docView);
        return lang.Type switch
        {
            Packets.Language.LANGUAGE_JAVASCRIPT or
            Packets.Language.LANGUAGE_TSX => true,
            _ => false,
        };
    }

    protected override string GetText() { return "Create a Typescript interface to define the component props."; }
}

[Command(PackageIds.RefactorUseAsyncAwaitInsteadOfPromises)]
internal sealed class CommandRefactorUseAsyncAwaitInsteadOfPromises : BaseCommandSelectedCode<CommandRefactorUseAsyncAwaitInsteadOfPromises>
{
    protected override bool IsVisible()
    {
        var lang = Languages.Mapper.GetLanguage(docView);
        return lang.Type switch
        {
            Packets.Language.LANGUAGE_TYPESCRIPT or
            Packets.Language.LANGUAGE_JAVASCRIPT or
            Packets.Language.LANGUAGE_TSX => true,
            _ => false,
        };
    }

    protected override string GetText() { return "Use async / await instead of promises."; }
}

[Command(PackageIds.RefactorVerboselyCommentThisCode)]
internal sealed class CommandRefactorVerboselyCommentThisCode : BaseCommandSelectedCode<CommandRefactorVerboselyCommentThisCode>
{
    protected override string GetText() { return "Verbosely comment this code so that I can understand what's going on."; }
}
