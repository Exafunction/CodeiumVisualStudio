#nullable enable

using System.Collections.Generic;
using CodeiumVS.Packets;

namespace CodeiumVS
{
    using System;
    using System.Diagnostics;
    using System.IO.Pipes;
    using System.Linq;
    using System.Threading.Tasks;

    using StreamJsonRpc;

    using CodeLensConnections = System.Collections.Concurrent.ConcurrentDictionary<System.Guid, CodeLensConnectionHandler>;
    using CodeLensDetails = System.Collections.Concurrent.ConcurrentDictionary<System.Guid, FunctionInfo>;

    public class CodeLensConnectionHandler : IRemoteVisualStudio, IDisposable
    {
        private static readonly CodeLensConnections connections = new CodeLensConnections();
        private static readonly CodeLensDetails detailsData = new CodeLensDetails();

        private JsonRpc? rpc;
        private Guid? dataPointId;

        public static async Task AcceptCodeLensConnections()
        {
            try
            {
                while (true)
                {
                    var stream = new NamedPipeServerStream(
                        PipeName.Get(Process.GetCurrentProcess().Id),
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await stream.WaitForConnectionAsync().Caf();
                    _ = HandleConnection(stream);
                }
            }
            catch (Exception ex)
            {
                throw;
            }

            static async Task HandleConnection(NamedPipeServerStream stream)
            {
                try
                {
                    var handler = new CodeLensConnectionHandler();
                    var rpc = JsonRpc.Attach(stream, handler);
                    handler.rpc = rpc;
                    await rpc.Completion;
                    handler.Dispose();
                    stream.Dispose();
                }
                catch (Exception ex)
                {
                    CodeiumVSPackage.Instance.LogAsync("Handle Connection Error");
                }
            }
        }

        public void Dispose()
        {
            if (dataPointId.HasValue)
            {
                _ = connections.TryRemove(dataPointId.Value, out var _);
                _ = detailsData.TryRemove(dataPointId.Value, out var _);
            }
        }

        // Called from each CodeLensDataPoint via JSON RPC.
        public void RegisterCodeLensDataPoint(Guid id)
        {
            dataPointId = id;
            connections[id] = this;
        }

        public static void StoreDetailsData(Guid id, FunctionInfo closestFunction) => detailsData[id] = closestFunction;

        public static FunctionInfo GetDetailsData(Guid id) => detailsData[id];

        public static async Task RefreshCodeLensDataPoint(Guid id)
        {
            if (!connections.TryGetValue(id, out var conn))
                throw new InvalidOperationException($"CodeLens data point {id} was not registered.");

            Debug.Assert(conn.rpc != null);
            await conn.rpc!.InvokeAsync(nameof(IRemoteCodeLens.Refresh)).Caf();
        }

        public static async Task RefreshAllCodeLensDataPoints()
            => await Task.WhenAll(connections.Keys.Select(RefreshCodeLensDataPoint)).Caf();
    }
}
