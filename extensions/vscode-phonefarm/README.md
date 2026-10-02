# PhoneFarm VS Code Extension

This extension adds a lightweight PhoneFarm control surface to VS Code.

The first version intentionally uses only the existing dispatcher API, so no backend changes are required.

## Features

- PhoneFarm activity-bar view.
- Jobs tree with status, progress, shard states, cancel, and retry failed shards.
- Phones tree based on currently registered dispatcher agents.
- Status-bar indicator for dispatcher and agent health.
- `PhoneFarm: Run Cached Worker` command to rerun a worker assembly already stored by the dispatcher.
- `PhoneFarm: Submit Worker Assembly` command for manually selecting a worker DLL.

## Requirements

- A running PhoneFarm dispatcher.
- A compatible dispatcher URL reachable from the VS Code UI window.
- No local .NET SDK is required for `Run Cached Worker`.

## Development

Open this folder in VS Code:

```bash
code phone-farm/extensions/vscode-phonefarm
```

Press `F5` to start the Extension Development Host.

If the extension lives inside a monorepo workspace, set:

```json
{
  "phoneFarm.dispatcherUrl": "http://dgx-mz1.local:8080"
}
```

## Commands

```text
PhoneFarm: Refresh
PhoneFarm: Connect Dispatcher
PhoneFarm: Open Dispatcher
PhoneFarm: Run Cached Worker
PhoneFarm: Submit Worker Assembly
PhoneFarm: Show Job JSON
PhoneFarm: Cancel Job
PhoneFarm: Retry Failed Shards
PhoneFarm: Forget Agent
```

## Current scope

This MVP avoids features that require dispatcher changes:

- no per-device target selection,
- no device battery or temperature display,
- no named worker registry,
- no worker build integration,
- no CodeLens run buttons yet.

Those are planned next once the extension and dispatcher APIs evolve.

## Security

The dispatcher API has no authentication. Only use this extension on trusted networks.
