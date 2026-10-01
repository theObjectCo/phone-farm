# PhoneFarm

PhoneFarm is a lightweight distributed job runner for Linux hosts and Android devices. A central dispatcher publishes jobs, Android agents claim shards over the local network, download a worker assembly, execute it, and report results back to the dispatcher.

The current implementation includes two sample workers:

- `Worker.MonteCarlo`: estimates pi using Monte Carlo sampling.
- `Worker.Audit`: collects non-sensitive device inventory data through Android system services.

## Components

```text
src/Dispatcher          ASP.NET Core dispatcher and HTTP API
src/Agent               Android foreground service that claims and runs shards
src/Worker.Contracts    Shared IWorker interface used by worker plugins
src/Worker.MonteCarlo   Sample Monte Carlo pi worker
src/Worker.Audit        Read-only Android audit worker
```

## How it works

1. A client submits a job to the dispatcher with a worker entry point and worker assembly.
2. The dispatcher stores the worker assembly by SHA-256 and creates job shards.
3. Android agents poll the dispatcher and claim available shards.
4. Each agent downloads the worker assembly for its claimed shards.
5. The worker assembly is loaded in memory and `IWorker.RunAsync` is executed.
6. The agent sends heartbeats and final results to the dispatcher.

Worker assemblies are stored on the dispatcher under `bin/Dispatcher/data/workers/<sha256>.dll`.

## Requirements

- .NET SDK matching the project target frameworks.
- Android workload for building the agent APK.
- Android SDK and JDK for Android packaging.
- `adb` for installing and starting the agent on phones.

The `deploy.sh` script is tuned for a Linux ARM64 build host. On other platforms, build the components directly with `dotnet` and install the APK manually.

## Build

Build the dispatcher:

```bash
dotnet publish src/Dispatcher/Dispatcher.csproj -c Release -o bin/Dispatcher
```

Build the sample workers:

```bash
dotnet publish src/Worker.MonteCarlo/Worker.MonteCarlo.csproj -c Release -o bin/worker-montecarlo
dotnet publish src/Worker.Audit/Worker.Audit.csproj -c Release -o bin/worker-audit
```

Build the Android agent:

```bash
dotnet build src/Agent/Agent.csproj -c Debug
```

The signed APK is generated under:

```text
src/Agent/bin/Debug/net10.0-android/pl.mz1.phonefarm.agent-Signed.apk
```

## Start the dispatcher

```bash
PHONEFARM_URL=http://0.0.0.0:8080 DOTNET_ROLL_FORWARD=LatestMajor ./bin/Dispatcher/Dispatcher
```

The dispatcher exposes status and API endpoints on port `8080` by default.

## Deploy agents

Use `adb` to install and start the agent on each connected phone:

```bash
adb install -r src/Agent/bin/Debug/net10.0-android/pl.mz1.phonefarm.agent-Signed.apk
adb shell am force-stop pl.mz1.phonefarm.agent
adb shell am start-foreground-service \
  -n pl.mz1.phonefarm.agent/pl.mz1.phonefarm.agent.AgentService \
  --es enable 1
```

The agent looks for the dispatcher through `DispatcherCandidates` in `src/Agent/AgentRuntime.cs`. Replace those candidates with your dispatcher URL before building a production agent.

## Submit jobs

Monte Carlo pi:

```bash
PHONEFARM_URL=http://localhost:8080 ./submit.sh 24 20000000
```

Device audit:

```bash
curl -sf \
  -F name=phone-audit \
  -F entry='Worker.Audit.AuditWorker, Worker.Audit' \
  -F assembly=@bin/worker-audit/Worker.Audit.dll \
  -F shards=8 \
  -F iterations=0 \
  http://localhost:8080/api/jobs
```

Check job status:

```bash
curl http://localhost:8080/
curl http://localhost:8080/api/jobs
curl http://localhost:8080/api/jobs/<job-id>
curl http://localhost:8080/api/jobs/<job-id>/results
curl http://localhost:8080/api/agents
```

## Custom workers

A worker is a `netstandard2.0` assembly that implements `Worker.Contracts.IWorker`.

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Worker.Contracts;

public sealed class MyWorker : IWorker
{
    public string Name => "my-worker";

    public Task<string> RunAsync(
        long seed,
        long iterations,
        IReadOnlyDictionary<string, string> args,
        IProgress<double> progress,
        CancellationToken token)
    {
        progress.Report(1.0);
        return Task.FromResult($"seed={seed}, iterations={iterations}");
    }
}
```

Submit it with `entry` set to `Namespace.Class, AssemblyName`, for example:

```text
MyApp.Workers.MyWorker, MyApp.Workers
```

## HTTP API

Core endpoints:

```text
GET  /
GET  /api/agents
POST /api/agents/{id}/unregister
GET  /api/jobs
GET  /api/jobs/{id}
GET  /api/jobs/{id}/results
POST /api/jobs/{id}/cancel
POST /api/jobs/{id}/shards/{index}/retry
POST /api/shards/claim
POST /api/shards/{jobId}/{index}/heartbeat
POST /api/shards/{jobId}/{index}/result
GET  /api/workers/{sha}
```

## Security notes

PhoneFarm currently assumes a trusted local network:

- The dispatcher has no authentication.
- Worker assemblies are executed on client devices.
- Agents can claim and run jobs if they can reach the dispatcher.
- The project is not intended for exposure to untrusted networks.

## License

This project is licensed under the MIT License. See `LICENSE` for details.
