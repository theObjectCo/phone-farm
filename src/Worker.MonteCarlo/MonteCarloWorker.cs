using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Worker.Contracts;

namespace Worker.MonteCarlo
{
    public class MonteCarloWorker : IWorker
    {
        public string Name => "montecarlo-pi";

        public Task<string> RunAsync(long seed, long iterations, IReadOnlyDictionary<string, string> args, IProgress<double> progress, CancellationToken token)
        {
            long n = iterations;
            if (args != null && args.TryGetValue("iterations", out var s) && long.TryParse(s, out var overridden) && overridden > 0)
            {
                n = overridden;
            }

            return Task.Run(() =>
            {
                var rng = new Random((int)(seed & 0x7fffffff));
                long inside = 0;
                long reportStep = Math.Max(1, n / 20);
                for (long i = 0; i < n; i++)
                {
                    if (i % reportStep == 0)
                    {
                        progress?.Report((double)i / n);
                        token.ThrowIfCancellationRequested();
                    }
                    double x = rng.NextDouble();
                    double y = rng.NextDouble();
                    if (x * x + y * y <= 1.0)
                    {
                        inside++;
                    }
                }
                token.ThrowIfCancellationRequested();
                double pi = 4.0 * inside / n;
                return pi.ToString("E10", CultureInfo.InvariantCulture);
            }, token);
        }
    }
}
