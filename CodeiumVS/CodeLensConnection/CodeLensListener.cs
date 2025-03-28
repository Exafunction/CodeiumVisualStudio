#nullable enable

using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Utilities;
using CodeiumVS.Packets;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Debugger.Interop;

namespace CodeiumVS
{

    [Export(typeof(ICodeLensCallbackListener))]
    [ContentType("C/C++")]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [ContentType("vbscript")]
    [ContentType("TypeScript")]
    [ContentType("JavaScript")]
    [ContentType("html")]
    public class CodeLensListener : ICodeLensCallbackListener, ICodeLensListener
    {

        public int GetVisualStudioPid() => Process.GetCurrentProcess().Id;

        public bool IsCodeiumCodeLensActive() => CodeiumVSPackage.Instance != null && CodeiumVSPackage.Instance.SettingsPage.EnableCodeLens;
        FunctionInfo GetClosestFunction(IList<Packets.FunctionInfo>? functions, int line)
        {
            
            FunctionInfo minFunction = null;
            int minDistance = int.MaxValue;
            foreach (var f in functions)
            {
                var distance = Math.Abs(f.DefinitionLine - line);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    minFunction = f;
                }
            }

            return minFunction;
        }

        public async Task<FunctionInfo> LoadInstructions(
            Guid dataPointId,
            Guid projGuid,
            string filePath,
            int textStart,
            int textLen,
            CancellationToken ct)
        {
            try
            {

                ITextDocument _document;
                TextViewListener.Instance.documentDictionary.TryGetValue(filePath.ToLower(), out _document);
                var key = typeof(CodeiumCompletionHandler);
                var props = _document.TextBuffer.Properties;
                CodeiumCompletionHandler handler;
                if (props.ContainsProperty(key))
                {
                    handler = props.GetProperty<CodeiumCompletionHandler>(key);
                }
                else
                {
                    handler = null;
                    return null;
                }

                IList<FunctionInfo>? functions =
                    await CodeiumVSPackage.Instance.LanguageServer.GetFunctionsAsync(
                        _document.FilePath,
                        _document.TextBuffer.CurrentSnapshot.GetText(),
                        handler.GetLanguage(),
                        0,
                ct);

                var line = new Span(textStart, textLen);
                var snapshotLine = _document.TextBuffer.CurrentSnapshot.GetLineFromPosition(line.Start);
                var lineN = snapshotLine.LineNumber;
                FunctionInfo closestFunction = GetClosestFunction(functions, lineN);
                CodeLensConnectionHandler.StoreDetailsData(dataPointId, closestFunction);

                return closestFunction;
            }
            catch (Exception ex)
            {
                CodeiumVSPackage.Instance.LogAsync(ex.ToString());

                return null;
            }
        }

    }
}
