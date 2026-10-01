using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Worker.Contracts
{
    public interface IWorker
    {
        string Name { get; }

        Task<string> RunAsync(long seed, long iterations, IReadOnlyDictionary<string, string> args, IProgress<double> progress, CancellationToken token);
    }
}
