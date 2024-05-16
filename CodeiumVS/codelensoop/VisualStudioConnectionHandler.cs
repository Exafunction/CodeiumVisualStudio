using System;
using System.IO.Pipes;
using System.Threading.Tasks;

using StreamJsonRpc;

namespace CodeiumVS
{

    public class VisualStudioConnectionHandler : IRemoteCodeLens, IDisposable
    {
        private readonly CodeiumDataPoint dataPoint;
        private readonly NamedPipeClientStream stream;
        private JsonRpc? rpc;

        public async static Task<VisualStudioConnectionHandler> Create(CodeiumDataPoint owner, int vspid)
        {
            var handler = new VisualStudioConnectionHandler(owner, vspid);
            await handler.Connect().Caf();
            return handler;
        }

        public VisualStudioConnectionHandler(CodeiumDataPoint dataPoint, int vspid)
        {
            this.dataPoint = dataPoint;
            stream = new NamedPipeClientStream(
                serverName: ".",
                PipeName.Get(vspid),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
        }

        public void Dispose() => stream.Dispose();

        public async Task Connect()
        {
            await stream.ConnectAsync().Caf();
            rpc = JsonRpc.Attach(stream, this);
            await rpc.InvokeAsync(nameof(IRemoteVisualStudio.RegisterCodeLensDataPoint), dataPoint.id).Caf();
        }

        public void Refresh() => dataPoint.Refresh();
    }
}