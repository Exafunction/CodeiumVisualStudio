using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Text.Tagging;
using System.ComponentModel.Composition;

namespace CodeiumVS.Utilities;

internal class MefProvider
{
    private static MefProvider _instance;
    private static IComponentModel _compositionService;

    internal static MefProvider Instance { get { return _instance ??= new MefProvider(); } }

    // disabled because not needed right now
    //[Import] internal IPeekBroker PeekBroker { get; private set; }
    //[Import] internal ICompletionBroker CompletionBroker { get; private set; }
    //[Import] internal IAsyncCompletionBroker AsyncCompletionBroker { get; private set; }
    //[Import] internal ITextEditorFactoryService TextEditorFactoryService { get; private set; }
    //[Import] internal ITextDocumentFactoryService TextDocumentFactoryService { get; private set; }
    //[Import] internal IDifferenceBufferFactoryService2 DifferenceBufferFactory { get; private set; }
    //[Import] internal IWpfDifferenceViewerFactoryService DifferenceViewerFactory { get; private set; }
    [Import] internal IViewTagAggregatorFactoryService TagAggregatorFactoryService { get; private set; }
    //[Import] internal IVsEditorAdaptersFactoryService EditorAdaptersFactoryService { get; private set; }
    //[Import] internal IProjectionBufferFactoryService ProjectionBufferFactoryService { get; private set; }
    //[Import] internal IEditorCommandHandlerServiceFactory EditorCommandHandlerService { get; private set; }

    //internal IOleServiceProvider OleServiceProvider { get; private set; }
    //internal IVsRunningDocumentTable RunningDocumentTable { get; private set; }
    //internal IVsUIShellOpenDocument DocumentOpeningService { get; private set; }

    private MefProvider()
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            Assumes.Null(_compositionService);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // sastify mef import
            _compositionService = Requires.NotNull(await ServiceProvider.GetGlobalServiceAsync(typeof(SComponentModel)) as IComponentModel);
            _compositionService.DefaultCompositionService.SatisfyImportsOnce(this);

            // disabled because not needed right now
            //OleServiceProvider = await ServiceProvider.GetGlobalServiceAsync(typeof(IOleServiceProvider)) as IOleServiceProvider;
            //RunningDocumentTable = await ServiceProvider.GetGlobalServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            //DocumentOpeningService = await ServiceProvider.GetGlobalServiceAsync(typeof(SVsUIShellOpenDocument)) as IVsUIShellOpenDocument;

            //Assumes.NotNull(OleServiceProvider);
            //Assumes.NotNull(RunningDocumentTable);
            //Assumes.NotNull(DocumentOpeningService);
        });
    }
}