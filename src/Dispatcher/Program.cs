using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

var dataPath = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataPath);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

var state = JobStore.Load(dataPath);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("PHONEFARM_URL") ?? "http://0.0.0.0:8080");
var app = builder.Build();

app.MapGet("/", () => Results.Text(
    $"phone-farm dispatcher\nagents: {state.Agents.Count}  jobs: {state.Jobs.Count}\n" +
    string.Join("\n", state.Jobs.Values.OrderByDescending(j => j.CreatedAtUtc).Select(j => j.Summary()))));

app.MapGet("/api/jobs", () => state.Jobs.Values.OrderByDescending(j => j.CreatedAtUtc).Select(j => j.ToDto()).ToArray());

app.MapGet("/api/jobs/{id}", (string id) =>
    state.Jobs.TryGetValue(id, out var job) ? Results.Json(job.ToDto(), jsonOptions) : Results.NotFound());

app.MapPost("/api/jobs/{id}/cancel", (string id) =>
{
    if (!state.Jobs.TryGetValue(id, out var job)) return Results.NotFound();
    job.Canceled = true;
    foreach (var shard in job.Shards.Where(s => s.State == ShardState.Queued)) shard.State = ShardState.Done;
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/jobs/{id}/shards/{index}/retry", (string id, int index) =>
{
    if (!state.Jobs.TryGetValue(id, out var job)) return Results.NotFound();
    var shard = job.Shards.ElementAtOrDefault(index);
    if (shard == null || shard.State is not (ShardState.Failed or ShardState.Done)) return Results.Conflict("not failed");
    shard.State = ShardState.Queued;
    shard.Error = null;
    shard.Result = null;
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/shards/claim", (ClaimRequest req) =>
{
    if (string.IsNullOrEmpty(req.AgentId)) return Results.BadRequest("agentId required");
    var agent = state.TouchAgent(req.AgentId, req.Info);
    if (agent.JobId != req.JobId)
    {
        state.JobForAgent(req.AgentId)?.ReleaseShardsOf(req.AgentId);
        agent.JobId = req.JobId;
    }
    if (string.IsNullOrEmpty(req.JobId) || !state.Jobs.TryGetValue(req.JobId, out var job)) return Results.Ok(Array.Empty<ShardDto>());
    int runningInJob = job.Shards.Count(s => s.State == ShardState.Running && s.AssignedTo == req.AgentId);
    int free = Math.Max(1, agent.Capacity) - runningInJob;
    if (job.Canceled || free <= 0)
    {
        return Results.Ok(Array.Empty<ShardDto>());
    }
    return Results.Ok(job.ClaimFor(req.AgentId, free));
});

app.MapPost("/api/shards/{jobId}/{index}/heartbeat", (string jobId, int index, ProgressRequest req) =>
{
    if (!state.TryShard(jobId, index, out var job, out var shard) || shard.State != ShardState.Running) return Results.NotFound();
    if (shard.AssignedTo != req.AgentId) return Results.Conflict("wrong agent");
    shard.Progress = req.Progress;
    shard.LastBeatUtc = DateTime.UtcNow;
    state.TouchAgent(req.AgentId, null);
    return Results.Ok(new { cancel = job.Canceled });
});

app.MapPost("/api/shards/{jobId}/{index}/result", (string jobId, int index, ResultRequest req) =>
{
    if (!state.TryShard(jobId, index, out var job, out var shard) || shard.State != ShardState.Running) return Results.NotFound();
    if (shard.AssignedTo != req.AgentId) return Results.Conflict("wrong agent");
    shard.Complete(req.Ok, req.Value, req.Error, req.Machine);
    state.TouchAgent(req.AgentId, null);
    state.Save();
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/agents", () => state.Agents.Values
    .OrderByDescending(a => a.LastSeenUtc)
    .Select(a => new { a.Id, a.Info, a.Capacity, a.JobId, a.LastSeenUtc })
    .ToArray());

app.MapPost("/api/agents/{id}/unregister", (string id) =>
{
    state.JobForAgent(id)?.ReleaseShardsOf(id);
    state.Agents.TryRemove(id, out _);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/workers/{sha}", (string sha) =>
{
    var path = Path.Combine(dataPath, "workers", sha + ".dll");
    return File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
});

app.MapPost("/api/jobs", async (HttpRequest http) =>
{
    var form = await ReadMultipartAsync(http);
    string Get(string key) => form.TryGetValue(key, out var v) ? Encoding.UTF8.GetString(v).Trim() : null;

    var name = Get("name");
    var entry = Get("entry");
    var shardsStr = Get("shards");
    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(entry) || !int.TryParse(shardsStr, out var shards) || shards < 1)
    {
        return Results.BadRequest("name, entry, shards required");
    }
    if (!form.TryGetValue("assembly", out var dll) || dll.Length == 0) return Results.BadRequest("assembly file required");

    string sha;
    using (var sha256 = SHA256.Create())
    {
        sha = Convert.ToHexString(sha256.ComputeHash(dll)).ToLowerInvariant();
    }
    var workersDir = Path.Combine(dataPath, "workers");
    Directory.CreateDirectory(workersDir);
    var dllPath = Path.Combine(workersDir, sha + ".dll");
    if (!File.Exists(dllPath))
    {
        await File.WriteAllBytesAsync(dllPath, dll);
    }

    var iterationsStr = Get("iterations");
    long iterations = long.TryParse(iterationsStr, out var it) && it > 0 ? it : 20_000_000;

    var job = new Job
    {
        Name = name,
        Entry = entry,
        AssemblySha = sha,
        Iterations = iterations,
        ArgsJson = Get("argsJson"),
    };
    for (int i = 0; i < shards; i++)
    {
        job.Shards.Add(new Shard { Index = i });
    }
    state.Jobs[job.Id] = job;
    state.Save();
    return Results.Json(job.ToDto(), jsonOptions);
});

app.MapGet("/api/jobs/{id}/results", (string id) =>
    state.Jobs.TryGetValue(id, out var job)
        ? Results.Text(string.Join("\n", job.Shards.Select(s => $"{s.Index}\t{s.State}\t{s.Result ?? s.Error ?? string.Empty}")), "text/plain")
        : Results.NotFound());

app.Run();

static async Task<Dictionary<string, byte[]>> ReadMultipartAsync(HttpRequest http)
{
    var result = new Dictionary<string, byte[]>();
    if (!http.HasFormContentType)
    {
        using var body = await JsonDocument.ParseAsync(http.Body);
        foreach (var prop in body.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? Encoding.UTF8.GetBytes(prop.Value.GetString())
                : Encoding.UTF8.GetBytes(prop.Value.ToString());
        }
        return result;
    }
    var form = await http.ReadFormAsync();
    foreach (var f in form.Files)
    {
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms);
        result[f.Name] = ms.ToArray();
    }
    foreach (var kv in form)
    {
        result[kv.Key] = Encoding.UTF8.GetBytes(kv.Value);
    }
    return result;
}

sealed class JobStore
{
    readonly string dataPath;

    JobStore(string dataPath) => this.dataPath = dataPath;

    public readonly ConcurrentDictionary<string, AgentInfo> Agents = new();
    public readonly ConcurrentDictionary<string, Job> Jobs = new();

    public static JobStore Load(string dataPath)
    {
        var store = new JobStore(dataPath);
        var file = Path.Combine(dataPath, "state.json");
        if (File.Exists(file))
        {
            var loaded = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file));
            if (loaded != null)
            {
                foreach (var job in loaded.Jobs)
                {
                    foreach (var shard in job.Shards.Where(s => s.State == ShardState.Running))
                    {
                        shard.State = ShardState.Queued;
                        shard.AssignedTo = null;
                    }
                    store.Jobs[job.Id] = job;
                }
            }
        }
        return store;
    }

    public AgentInfo TouchAgent(string id, AgentInfo incoming)
    {
        var agent = Agents.GetOrAdd(id, _ => new AgentInfo { Id = id });
        agent.LastSeenUtc = DateTime.UtcNow;
        if (incoming != null)
        {
            agent.Info = incoming.Info;
            if (incoming.Capacity > 0) agent.Capacity = incoming.Capacity;
        }
        return agent;
    }

    public Job JobForAgent(string agentId) => Jobs.Values.FirstOrDefault(j => j.Shards.Any(s => s.AssignedTo == agentId));

    public bool TryShard(string jobId, int index, out Job job, out Shard shard)
    {
        job = null;
        shard = null;
        if (!Jobs.TryGetValue(jobId, out job)) return false;
        shard = index >= 0 && index < job.Shards.Count ? job.Shards[index] : null;
        return shard != null;
    }

    public void Save()
    {
        var file = Path.Combine(dataPath, "state.json");
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(file, JsonSerializer.Serialize(new Snapshot { Jobs = Jobs.Values.ToList() }));
    }
}

sealed class Snapshot
{
    public List<Job> Jobs { get; set; } = new();
}

sealed class AgentInfo
{
    public string Id { get; set; }
    public int Capacity { get; set; } = 1;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string JobId { get; set; }
    public string Info { get; set; }
}

sealed class Job
{
    readonly object gate = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; }
    public string Entry { get; set; }
    public string AssemblySha { get; set; }
    public long Iterations { get; set; } = 20_000_000;
    public string ArgsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Canceled { get; set; }
    public List<Shard> Shards { get; set; } = new();

    public string Summary()
    {
        int done = Shards.Count(s => s.State == ShardState.Done);
        int failed = Shards.Count(s => s.State == ShardState.Failed);
        int running = Shards.Count(s => s.State == ShardState.Running);
        return $"{Id}  {Name,-16} {done}/{Shards.Count} done, {failed} failed, {running} running";
    }

    public JobDto ToDto()
    {
        lock (gate)
        {
            return new JobDto
            {
                Id = Id,
                Name = Name,
                Entry = Entry,
                AssemblySha = AssemblySha,
                Canceled = Canceled,
                CreatedAtUtc = CreatedAtUtc,
                Shards = Shards.Select(s => s.ToDto()).ToList(),
            };
        }
    }

    public List<ShardDto> ClaimFor(string agentId, int count)
    {
        lock (gate)
        {
            var granted = new List<ShardDto>();
            foreach (var shard in Shards.Where(s => s.State == ShardState.Queued).Take(count))
            {
                shard.State = ShardState.Running;
                shard.AssignedTo = agentId;
                shard.LastBeatUtc = DateTime.UtcNow;
                granted.Add(new ShardDto
                {
                    JobId = Id,
                    Index = shard.Index,
                    Entry = Entry,
                    AssemblySha = AssemblySha,
                    Iterations = Iterations,
                    Seed = StableSeed(Id, shard.Index),
                    ArgsJson = ArgsJson,
                });
            }
            return granted;
        }
    }

    public void ReleaseShardsOf(string agentId)
    {
        lock (gate)
        {
            foreach (var shard in Shards.Where(s => s.State == ShardState.Running && s.AssignedTo == agentId))
            {
                shard.State = ShardState.Queued;
                shard.AssignedTo = null;
            }
        }
    }

    static long StableSeed(string jobId, int index)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{jobId}:{index}"));
        return Math.Abs(BitConverter.ToInt64(hash, 0));
    }
}

sealed class Shard
{
    public int Index { get; set; }
    public ShardState State { get; set; } = ShardState.Queued;
    public string AssignedTo { get; set; }
    public double Progress { get; set; }
    public DateTime LastBeatUtc { get; set; }
    public string Result { get; set; }
    public string Error { get; set; }
    public string Machine { get; set; }

    public void Complete(bool ok, string value, string error, string machine)
    {
        State = ok ? ShardState.Done : ShardState.Failed;
        Result = value;
        Error = error;
        Machine = machine;
        AssignedTo = null;
        Progress = ok ? 1.0 : Progress;
    }

    public ShardDto ToDto() => new()
    {
        Index = Index,
        State = State.ToString().ToLowerInvariant(),
        AssignedTo = AssignedTo,
        Progress = Progress,
        Result = Result,
        Error = Error,
        Machine = Machine,
    };
}

enum ShardState { Queued, Running, Done, Failed }

sealed class ClaimRequest
{
    public string AgentId { get; set; }
    public string JobId { get; set; }
    public AgentInfo Info { get; set; }
}

sealed class ProgressRequest
{
    public string AgentId { get; set; }
    public double Progress { get; set; }
}

sealed class ResultRequest
{
    public string AgentId { get; set; }
    public bool Ok { get; set; }
    public string Value { get; set; }
    public string Error { get; set; }
    public string Machine { get; set; }
}

sealed class JobDto
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Entry { get; set; }
    public string AssemblySha { get; set; }
    public bool Canceled { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<ShardDto> Shards { get; set; } = new();
}

sealed class ShardDto
{
    public string JobId { get; set; }
    public int Index { get; set; }
    public string State { get; set; }
    public string AssignedTo { get; set; }
    public double Progress { get; set; }
    public string Result { get; set; }
    public string Error { get; set; }
    public string Machine { get; set; }
    public string Entry { get; set; }
    public string AssemblySha { get; set; }
    public long Iterations { get; set; }
    public long Seed { get; set; }
    public string ArgsJson { get; set; }
}
