using Microsoft.VisualStudio.GraphModel.CodeSchema;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Newtonsoft.Json;
using ProtoBuf;
using StreamJsonRpc;
using System.IO;
using System.Linq;
using CodeiumVS.Packets;
using WebSocketSharp;

namespace CodeiumVS;
public class LanguageServerController
{
    readonly CodeiumVSPackage package;
    public WebSocket? ws = null;
    public LanguageServerController()
    {
        package = CodeiumVSPackage.Instance;
    }

    public async Task ConnectAsync()
    {

#pragma warning disable VSTHRD103 // Call async methods when in an async method
        void OnOpen(object sender, EventArgs e)
        {
            WebSocket ws = sender as WebSocket;
            package.Log($"Connected to {ws.Url}");
        }

        void OnClose(object sender, EventArgs e)
        {
            WebSocket ws = sender as WebSocket;
            package.Log($"Disconnected from {ws.Url}");
        }

        void OnMessage(object sender, EventArgs e)
        {
            MessageEventArgs msg = e as MessageEventArgs;
            if (!msg.IsBinary) return;

            using MemoryStream stream = new(msg.RawData);
            WebServerResponse request = Serializer.Deserialize<Packets.WebServerResponse>(stream);
            string text = JsonConvert.SerializeObject(request);

            package.Log($"OnMessage: {text}");

            if (request.ShouldSerializeopen_file_pointer())
            {
                var data = request.open_file_pointer;
                OpenSelection(data.file_path, data.start_line, data.start_col, data.end_line, data.end_col);
            }
            else if (request.ShouldSerializeinsert_at_cursor())
            {
                var data = request.insert_at_cursor;
                InsertText(data.text);
            }

        }

        void OnError(object sender, EventArgs e)
        {
            package.Log("OnError\n");
        }
#pragma warning restore VSTHRD103 // Call async methods when in an async method

        GetProcessesResponse? result = await package.langServer.GetProcessesAsync();

        ws = new WebSocket($"ws://127.0.0.1:{result.chat_web_server_port}/connect/ide");
        ws.OnOpen    += OnOpen;
        ws.OnClose   += OnClose;
        ws.OnMessage += OnMessage;
        ws.OnError   += OnError;
        ws.ConnectAsync();
    }

    static string RandomString(int length)
    {
        Random random = new Random();
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Repeat(chars, length)
          .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    private void InsertText(string text)
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView == null) return; //not a text window

            ITextSelection selection = docView.TextView.Selection;

            // has no selection
            if (selection.SelectedSpans.Count == 0 || selection.SelectedSpans[0].Span.Start == selection.SelectedSpans[0].Span.End)
            {
                // Inserts text at the caret
                SnapshotPoint position = docView.TextView.Caret.Position.BufferPosition;
                docView.TextBuffer?.Insert(position, text);
            }
            else
            {
                // Inserts text in the selection
                docView.TextBuffer?.Replace(selection.SelectedSpans[0].Span, text);
            }
        });
    }

    private void OpenSelection(string filePath, int start_line, int start_col, int end_line, int end_col)
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // somehow OpenViaProjectAsync doesn't work... at least for me
            DocumentView? docView = await VS.Documents.OpenAsync(filePath);
            if (docView?.TextView == null) return;

            var snapshot = docView.TextView.TextSnapshot;
            ITextSelection selection = docView.TextView.Selection;

            var lineStart = docView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(start_line - 1);
            var lineEnd = docView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(end_line - 1);

            int start = lineStart.Start.Position + start_col - 1;
            int end = lineEnd.Start.Position + end_col - 1;

            docView.TextView.Selection.Select(new SnapshotSpan(new SnapshotPoint(snapshot, start), end - start), false);
            docView.TextView.Caret.MoveTo(new SnapshotPoint(snapshot, end));
            docView.TextView.Caret.EnsureVisible();
        });
    }
    private void AddIntentCodeBlockExplain(ref WebServerRequest request, string filepath, string text, Language language, int start_line, int start_col, int end_line, int end_col)
    {
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            explain_code_block = new()
            {
                code_block_info = new()
                {
                    end_col = end_col,
                    end_line = end_line,
                    start_col = start_col,
                    start_line = start_line,
                    raw_source = text
                },
                file_path = filepath,
                language = language
            }
        };
    }

    private void AddIntentCodeBlockRefactor(ref WebServerRequest request, string filepath, string text, Language language, int start_line, int start_col, int end_line, int end_col, string prompt)
    {
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            code_block_refactor = new()
            {
                code_block_info = new()
                {
                    end_col = end_col,
                    end_line = end_line,
                    start_col = start_col,
                    start_line = start_line,
                    raw_source = text
                },
                file_path = filepath,
                language = language,
                refactor_description = prompt
            }
        };
    }

    private WebServerRequest NewWebServerRequest()
    {
        WebServerRequest request = new()
        {
            get_chat_message_request = new()
            {
                context_inclusion_type = Packets.ContextInclusionType.CONTEXT_INCLUSION_TYPE_UNSPECIFIED,
                experiment_config = new(),

                metadata = package.langServer.GetMetadata(),
                prompt = ""
            }
        };

        request.get_chat_message_request.chat_messages.Add(new()
        {
            conversation_id = RandomString(32),
            in_progress = false,
            message_id = $"user-{RandomString(32)}",
            source = Packets.ChatMessageSource.CHAT_MESSAGE_SOURCE_USER,
            timestamp = DateTime.UtcNow
        });

        return request;
    }

    public async Task ExplainCodeBlockAsync(DocumentView docView)
    {
        ITextSelection selection = docView.TextView.Selection;
        ITextSnapshotLine selectionStart = selection.Start.Position.GetContainingLine();
        ITextSnapshotLine selectionEnd = selection.End.Position.GetContainingLine();

        int lineStart = selectionStart.LineNumber + 1;
        int lineEnd = selectionEnd.LineNumber + 1;

        int colStart = selection.Start.Position - selectionStart.Start.Position + 1;
        int colEnd = selection.End.Position - selectionEnd.Start.Position + 1;

        string filePath = docView.FilePath;
        string text = docView.TextBuffer.CurrentSnapshot.GetText(selection.SelectedSpans[0].Span);
        var language = Languages.Mapper.GetLanguage(docView.TextBuffer.ContentType, Path.GetExtension(docView.FilePath)?.Trim('.'));

        var request = NewWebServerRequest();
        AddIntentCodeBlockExplain(ref request, filePath, text, language.Type, lineStart, colStart, lineEnd, colEnd);

        using MemoryStream memoryStream = new();
        Serializer.Serialize(memoryStream, request);

        ws.SendAsync(memoryStream.ToArray(), delegate (bool e) {} );
        await package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, package.DisposalToken);
    }

    public async Task RefactorCodeBlockAsync(DocumentView docView, string prompt)
    {
        ITextSelection selection = docView.TextView.Selection;
        ITextSnapshotLine selectionStart = selection.Start.Position.GetContainingLine();
        ITextSnapshotLine selectionEnd = selection.End.Position.GetContainingLine();

        int lineStart = selectionStart.LineNumber + 1;
        int lineEnd = selectionEnd.LineNumber + 1;

        int colStart = selection.Start.Position - selectionStart.Start.Position + 1;
        int colEnd = selection.End.Position - selectionEnd.Start.Position + 1;

        string filePath = docView.FilePath;
        string text = docView.TextBuffer.CurrentSnapshot.GetText(selection.SelectedSpans[0].Span);
        var language = Languages.Mapper.GetLanguage(docView.TextBuffer.ContentType, Path.GetExtension(docView.FilePath)?.Trim('.'));

        var request = NewWebServerRequest();
        AddIntentCodeBlockRefactor(ref request, filePath, text, language.Type, lineStart, colStart, lineEnd, colEnd, prompt);

        using MemoryStream memoryStream = new();
        Serializer.Serialize(memoryStream, request);

        ws.SendAsync(memoryStream.ToArray(), delegate (bool e) {} );
        await package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, package.DisposalToken);
    }

    public void Disconnect()
    {
        if (ws == null) return;
        ws.Close();
        ws = null;
    }
}
