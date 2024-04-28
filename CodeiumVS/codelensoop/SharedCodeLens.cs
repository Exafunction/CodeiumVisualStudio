#nullable enable

using System.Collections.Generic;
using CodeiumVS.Packets;

namespace CodeiumVS
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    public static class PipeName
    {
        // Pipe needs to be scoped by PID so multiple VS instances don't compete for connecting CodeLenses.
        public static string Get(int pid) => $@"codeium\{pid}";
    }
    public static class ConfigureAwaitAlias
    {
        /// <summary>Alias for `ConfigureAwait(false)`.</summary>
        public static ConfiguredTaskAwaitable Caf(this Task t) => t.ConfigureAwait(false);

        /// <summary>Alias for `ConfigureAwait(false)`.</summary>
        public static ConfiguredTaskAwaitable<T> Caf<T>(this Task<T> t) => t.ConfigureAwait(false);
    }

    public interface IRemoteCodeLens
    {
        void Refresh();
    }

    public interface IRemoteVisualStudio
    {
        void RegisterCodeLensDataPoint(Guid id);
    }
        
    public interface ICodeLensListener
    {
        int GetVisualStudioPid();
        Task<FunctionInfo> LoadInstructions(Guid dataPointId, Guid projGuid, string filePath, int textStart, int textLen, CancellationToken ct);

    }

}