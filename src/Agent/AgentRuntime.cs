using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;
using Android.Provider;
using Worker.Contracts;

namespace PhoneFarm.Agent
{
    public sealed class AgentRuntime : IDisposable
    {
        static readonly Uri[] DispatcherCandidates =
        {
            new Uri("http://dgx-mz1.local:8080"),
            new Uri("http://192.168.68.128:8080"),
        };

        readonly Service context;
        readonly string filesDir;
        readonly string cacheDir;
        readonly Thread loopThread;
        readonly CancellationTokenSource cts = new();
        readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
        readonly AgentAssemblyLoader loader = new();
        readonly string agentId;
        Uri dispatcher;

        public AgentRuntime(Service context, string filesDir, string cacheDir)
        {
            this.context = context;
            this.filesDir = filesDir;
            this.cacheDir = cacheDir;
            agentId = Settings.Secure.GetString(context.ContentResolver, Settings.Secure.AndroidId) ?? "agent";
            loopThread = new Thread(LoopMain) { IsBackground = true, Name = "phonefarm-loop" };
            loopThread.Start();
            SaveStateJson("phase", "starting");
        }

        public static void SetConfigured(Context context, bool value)
        {
            var marker = Path.Combine(context.FilesDir.Path, "enabled");
            if (value) File.WriteAllText(marker, "1");
            else if (File.Exists(marker)) File.Delete(marker);
        }

        static bool IsConfigured(Context context) => File.Exists(Path.Combine(context.FilesDir.Path, "enabled"));

        public static string DescribeState(Context context)
        {
            var file = Path.Combine(context.FilesDir.Path, "agent_state.json");
            return File.Exists(file) ? File.ReadAllText(file) : "no state file";
        }

        void LoopMain()
        {
            Android.Util.Log.Info("phonefarm", "loop started");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    RunCycle();
                }
                catch (Exception e)
                {
                    Android.Util.Log.Warn("phonefarm", "cycle failed: " + e);
                    SetStatus("error: " + e.Message);
                }
                SleepQuiet(5000);
            }
        }

        void RunCycle()
        {
            if (!IsConfigured(context))
            {
                SetStatus("not configured (open the app and tap Start)");
                return;
            }
            dispatcher ??= FindDispatcherAsync().GetAwaiter().GetResult();
            if (dispatcher == null)
            {
                SetStatus("dispatcher unreachable");
                return;
            }

            var info = new
            {
                capacity = Math.Max(1, Environment.ProcessorCount),
                info = $"{Build.Manufacturer} {Build.Model}, Android API {Android.OS.Build.VERSION.SdkInt}, {Environment.ProcessorCount} cpus",
            };
            PostJson<object>("/api/shards/claim", new { agentId, jobId = (string)null, info });

            var jobs = GetJson<List<JobBrief>>("/api/jobs");
            var job = jobs?
                .Where(j => !j.Canceled && j.Shards != null && j.Shards.Any(s => s.State == "queued"))
                .OrderByDescending(j => j.CreatedAtUtc)
                .FirstOrDefault();
            if (job == null)
            {
                SetStatus("waiting for work");
                return;
            }

            int capacity = Math.Max(1, Environment.ProcessorCount);
            var shards = PostJson<List<ShardDto>>("/api/shards/claim", new { agentId, jobId = job.Id, info });
            if (shards == null || shards.Count == 0)
            {
                SetStatus($"job {job.Id}: nothing to claim");
                return;
            }

            SetStatus($"job {job.Id} ({job.Name}): running {shards.Count} shard(s)");
            byte[] assembly = GetBytes($"/api/workers/{job.AssemblySha}");
            SaveStateJson("jobId", job.Id);
            SaveStateJson("shards", shards.Count.ToString());

            var tasks = shards.Select(s => RunShardAsync(job, s, assembly)).ToArray();
            try
            {
                Task.WhenAll(tasks).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                SetStatus("shard batch error: " + e.Message);
            }
            SaveStateJson("phase", "idle");
        }

        async Task RunShardAsync(JobBrief job, ShardDto shard, byte[] assembly)
        {
            var ctsSource = new CancellationTokenSource();
            var progress = new Progress<double>(p =>
            {
                try
                {
                    var r = PostJson<CancelFlag>($"/api/shards/{job.Id}/{shard.Index}/heartbeat", new { agentId, progress = p });
                    if (r?.Cancel == true) ctsSource.Cancel();
                }
                catch
                {
                }
            });

            try
            {
                var worker = loader.Resolve(assembly, job.Entry);
                IReadOnlyDictionary<string, string> args = string.IsNullOrEmpty(shard.ArgsJson)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(shard.ArgsJson);

                string value = await worker.RunAsync(shard.Seed, shard.Iterations, args, progress, ctsSource.Token);
                PostJson<object>($"/api/shards/{job.Id}/{shard.Index}/result",
                    new { agentId, ok = true, value, error = (string)null, machine = Machine() });
            }
            catch (OperationCanceledException)
            {
                PostJson<object>($"/api/shards/{job.Id}/{shard.Index}/result",
                    new { agentId, ok = false, value = (string)null, error = "canceled", machine = Machine() });
            }
            catch (Exception e)
            {
                PostJson<object>($"/api/shards/{job.Id}/{shard.Index}/result",
                    new { agentId, ok = false, value = (string)null, error = e.ToString(), machine = Machine() });
            }
        }

        async Task<Uri> FindDispatcherAsync()
        {
            foreach (var candidate in DispatcherCandidates.Concat(GatewayCandidates()))
            {
                try
                {
                    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                    var body = await probe.GetStringAsync(new Uri(candidate, "/"));
                    Android.Util.Log.Info("phonefarm", "probe " + candidate + " -> " + (body != null && body.Contains("dispatcher") ? "ok" : "no-match"));
                    if (body != null && body.Contains("dispatcher"))
                    {
                        return candidate;
                    }
                }
                catch (Exception e)
                {
                    Android.Util.Log.Info("phonefarm", "probe " + candidate + " failed: " + e.Message);
                }
            }
            return null;
        }

        IEnumerable<Uri> GatewayCandidates()
        {
            string gateway = null;
            try
            {
                var wifi = (WifiManager)context.GetSystemService(Context.WifiService);
                var ip = wifi?.ConnectionInfo?.IpAddress;
                if (ip.HasValue)
                {
                    int value = ip.Value;
                    gateway = $"{value & 0xff}.{(value >> 8) & 0xff}.{(value >> 16) & 0xff}.1";
                }
            }
            catch
            {
            }
            var fallbacks = new List<string>();
            if (gateway != null) fallbacks.Add(gateway);
            fallbacks.Add("192.168.1.100");
            return fallbacks.Select(g => new Uri($"http://{g}:8080"));
        }

        T GetJson<T>(string path)
        {
            using var response = http.GetAsync(new Uri(dispatcher, path)).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<T>(text, JsonOpts);
        }

        byte[] GetBytes(string path)
        {
            using var response = http.GetAsync(new Uri(dispatcher, path)).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }

        T PostJson<T>(string path, object body)
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = http.PostAsync(new Uri(dispatcher, path), content).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return default;
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<T>(text, JsonOpts);
        }

        void PostMultipart(string path, MultipartFormDataContent content)
        {
            using var response = http.PostAsync(new Uri(dispatcher, path), content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
        }

        static string Machine() => $"{Build.Manufacturer} {Build.Model}";

        static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        void SetStatus(string status)
        {
            SaveStateJson("status", status);
        }

        void SaveStateJson(string key, string value)
        {
            try
            {
                var file = Path.Combine(filesDir, "agent_state.json");
                Dictionary<string, string> dict;
                try
                {
                    dict = File.Exists(file)
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                        : new Dictionary<string, string>();
                }
                catch
                {
                    dict = new Dictionary<string, string>();
                }
                dict[key] = value;
                dict["updatedUtc"] = DateTime.UtcNow.ToString("O");
                dict["agentId"] = agentId;
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn("phonefarm", "state save failed: " + e);
            }
        }

        void SleepQuiet(int ms)
        {
            try
            {
                cts.Token.WaitHandle.WaitOne(ms);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            try
            {
                loopThread.Join(2000);
            }
            catch
            {
            }
            http.Dispose();
        }

        class AgentAssemblyLoader
        {
            readonly Dictionary<string, (IWorker Worker, AssemblyLoadContext Context)> cache = new();

            public IWorker Resolve(byte[] assembly, string entry)
            {
                var cacheKey = Convert.ToHexString(SHA1.HashData(assembly)) + "|" + entry;
                lock (cache)
                {
                    if (cache.TryGetValue(cacheKey, out var hit)) return hit.Worker;
                }

                var loadContext = new CollectiblePluginContext(assembly);
                Assembly plugin = loadContext.LoadFromStream(new MemoryStream(assembly));
                var type = plugin.GetTypes().FirstOrDefault(t => typeof(IWorker).IsAssignableFrom(t) && !t.IsAbstract)
                    ?? throw new InvalidOperationException("no IWorker implementation found in assembly");
                var worker = (IWorker)Activator.CreateInstance(type);
                lock (cache)
                {
                    cache[cacheKey] = (worker, loadContext);
                }
                return worker;
            }
        }

        sealed class CollectiblePluginContext : AssemblyLoadContext
        {
            readonly byte[] assembly;

            public CollectiblePluginContext(byte[] assembly) : base("plugin", true)
            {
                this.assembly = assembly;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                if (string.Equals(assemblyName.Name, "Worker.Contracts", StringComparison.Ordinal))
                {
                    return typeof(IWorker).Assembly;
                }
                return null;
            }
        }
    }

    sealed class JobBrief
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Entry { get; set; }
        public string AssemblySha { get; set; }
        public bool Canceled { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public List<ShardStateDto> Shards { get; set; }
    }

    sealed class ShardStateDto
    {
        public string State { get; set; }
    }

    sealed class ClaimResponse
    {
        public List<ShardDto> Shards { get; set; }
    }

    sealed class ShardDto
    {
        public string JobId { get; set; }
        public int Index { get; set; }
        public string Entry { get; set; }
        public string AssemblySha { get; set; }
        public long Iterations { get; set; }
        public long Seed { get; set; }
        public string ArgsJson { get; set; }
    }

    sealed class CancelFlag
    {
        public bool Cancel { get; set; }
    }
}
