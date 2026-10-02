const vscode = require('vscode');
const crypto = require('crypto');
const fs = require('fs');

let output;
let statusItem;
let jobsProvider;
let phonesProvider;
let refreshTimer;
let refreshing = false;

const state = {
    ok: false,
    error: '',
    agents: [],
    jobs: [],
    updatedAt: null,
};

function activate(context) {
    output = vscode.window.createOutputChannel('PhoneFarm');
    statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    jobsProvider = new TreeProvider('phoneFarm.jobs');
    phonesProvider = new TreeProvider('phoneFarm.phones');

    context.subscriptions.push(
        output,
        statusItem,
        vscode.window.registerTreeDataProvider('phoneFarm.jobs', jobsProvider),
        vscode.window.registerTreeDataProvider('phoneFarm.phones', phonesProvider),
        vscode.commands.registerCommand('phoneFarm.refresh', () => refresh({ notify: true })),
        vscode.commands.registerCommand('phoneFarm.connect', connect),
        vscode.commands.registerCommand('phoneFarm.openDispatcher', openDispatcher),
        vscode.commands.registerCommand('phoneFarm.runCachedWorker', runCachedWorker),
        vscode.commands.registerCommand('phoneFarm.submitWorkerAssembly', submitWorkerAssembly),
        vscode.commands.registerCommand('phoneFarm.showJob', showJob),
        vscode.commands.registerCommand('phoneFarm.cancelJob', cancelJob),
        vscode.commands.registerCommand('phoneFarm.retryFailedShards', retryFailedShards),
        vscode.commands.registerCommand('phoneFarm.unregisterAgent', unregisterAgent),
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('phoneFarm')) {
                schedulePolling();
                refresh();
            }
        }),
        { dispose: () => { if (refreshTimer) clearInterval(refreshTimer); } },
    );

    schedulePolling();
    refresh();
}

function deactivate() {
    if (refreshTimer) {
        clearInterval(refreshTimer);
        refreshTimer = undefined;
    }
}

module.exports = { activate, deactivate };

function schedulePolling() {
    if (refreshTimer) {
        clearInterval(refreshTimer);
        refreshTimer = undefined;
    }

    const intervalMs = Math.max(1000, Number(config('pollIntervalMs', 3000)) || 3000);
    refreshTimer = setInterval(() => refresh({ notify: false }), intervalMs);
}

async function refresh(options = {}) {
    if (refreshing) {
        return;
    }

    refreshing = true;
    try {
        const [agents, jobs] = await Promise.all([
            httpJson('GET', '/api/agents'),
            httpJson('GET', '/api/jobs'),
        ]);

        state.ok = true;
        state.error = '';
        state.agents = Array.isArray(agents) ? agents : [];
        state.jobs = Array.isArray(jobs) ? jobs : [];
        state.updatedAt = new Date().toISOString();

        if (options.notify) {
            const online = state.agents.filter(isOnline).length;
            vscode.window.setStatusBarMessage(`PhoneFarm: refreshed, ${online}/${state.agents.length} agents online`, 3000);
        }
    } catch (error) {
        state.ok = false;
        state.error = errorMessage(error);
        output.appendLine(`${new Date().toISOString()} refresh failed: ${state.error}`);
    } finally {
        refreshing = false;
        updateStatus();
        jobsProvider.refresh();
        phonesProvider.refresh();
    }
}

function config(key, fallback) {
    return vscode.workspace.getConfiguration('phoneFarm').get(key, fallback);
}

function dispatcherUrl() {
    const value = String(config('dispatcherUrl', 'http://localhost:8080') || '').trim();
    return (value || 'http://localhost:8080').replace(/\/+$/, '');
}

function apiUrl(path) {
    return new URL(path, `${dispatcherUrl()}/`).toString();
}

async function httpJson(method, path) {
    const timeoutMs = Math.max(1000, Number(config('requestTimeoutMs', 8000)) || 8000);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    try {
        const response = await fetch(apiUrl(path), {
            method,
            headers: { accept: 'application/json' },
            signal: controller.signal,
        });

        const text = await response.text();
        if (!response.ok) {
            throw new Error(`${method} ${path} -> HTTP ${response.status} ${text.slice(0, 180)}`);
        }

        if (!text) {
            return null;
        }

        try {
            return JSON.parse(text);
        } catch {
            throw new Error(`Invalid JSON from ${path}: ${text.slice(0, 180)}`);
        }
    } finally {
        clearTimeout(timer);
    }
}

async function getBinary(path) {
    const timeoutMs = Math.max(1000, Number(config('requestTimeoutMs', 8000)) || 8000);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    try {
        const response = await fetch(apiUrl(path), { method: 'GET', signal: controller.signal });
        if (!response.ok) {
            throw new Error(`GET ${path} -> HTTP ${response.status}`);
        }

        return Buffer.from(await response.arrayBuffer());
    } finally {
        clearTimeout(timer);
    }
}

async function postMultipart(path, fields, file) {
    const boundary = `----phonefarm${crypto.randomBytes(16).toString('hex')}`;
    const parts = [];

    for (const [key, value] of Object.entries(fields)) {
        if (value === undefined || value === null || value === '') {
            continue;
        }

        parts.push(Buffer.from(
            `--${boundary}\r\nContent-Disposition: form-data; name="${key}"\r\n\r\n${String(value)}\r\n`,
            'utf8',
        ));
    }

    parts.push(Buffer.from(
        `--${boundary}\r\nContent-Disposition: form-data; name="assembly"; filename="${file.name}"\r\nContent-Type: application/octet-stream\r\n\r\n`,
        'utf8',
    ));
    parts.push(file.bytes);
    parts.push(Buffer.from(`\r\n--${boundary}--\r\n`, 'utf8'));

    const body = Buffer.from(Buffer.concat(parts));
    const timeoutMs = Math.max(10000, Number(config('requestTimeoutMs', 8000)) || 8000);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    try {
        const response = await fetch(apiUrl(path), {
            method: 'POST',
            headers: { 'content-type': `multipart/form-data; boundary=${boundary}` },
            body,
            signal: controller.signal,
        });

        const text = await response.text();
        if (!response.ok) {
            throw new Error(`POST ${path} -> HTTP ${response.status} ${text.slice(0, 180)}`);
        }

        try {
            return text ? JSON.parse(text) : null;
        } catch {
            throw new Error(`Invalid JSON from ${path}: ${text.slice(0, 180)}`);
        }
    } finally {
        clearTimeout(timer);
    }
}

async function submitJob({ name, entry, assemblyName, bytes, shards, iterations, argsJson }) {
    const fields = { name, entry, shards: String(shards) };
    if (iterations !== undefined && iterations !== null) {
        fields.iterations = String(iterations);
    }
    if (argsJson) {
        fields.argsJson = argsJson;
    }

    return postMultipart('/api/jobs', fields, { name: assemblyName, bytes });
}

function updateStatus() {
    if (!state.ok) {
        statusItem.text = '$(error) PhoneFarm offline';
        statusItem.tooltip = `PhoneFarm dispatcher is unreachable:\n${state.error}`;
    } else {
        const online = state.agents.filter(isOnline).length;
        const active = state.jobs.filter((job) => {
            const summary = summarize(job);
            return summary.status === 'running' || summary.status === 'queued';
        }).length;

        statusItem.text = `$(device-mobile) PhoneFarm: ${online}/${state.agents.length} agents`;
        statusItem.tooltip = `PhoneFarm\n${active} active job(s)\n${dispatcherUrl()}\nLast refresh: ${state.updatedAt || 'unknown'}`;
    }

    statusItem.command = { command: 'workbench.view.extension.phonefarm', title: 'PhoneFarm' };
    statusItem.show();
}

function isOnline(agent) {
    const seenAt = Date.parse(agent?.lastSeenUtc || '');
    if (Number.isNaN(seenAt)) {
        return false;
    }

    const thresholdMs = Math.max(5000, Number(config('onlineThresholdSeconds', 15)) || 15) * 1000;
    return Math.abs(Date.now() - seenAt) <= thresholdMs;
}

function summarize(job) {
    const summary = { total: 0, done: 0, failed: 0, running: 0, queued: 0, canceled: !!job.canceled };
    for (const shard of job.shards || []) {
        const status = String(shard.state || '').toLowerCase();
        summary.total += 1;
        if (status === 'done') summary.done += 1;
        else if (status === 'failed') summary.failed += 1;
        else if (status === 'running') summary.running += 1;
        else if (status === 'queued') summary.queued += 1;
    }

    summary.percent = summary.total ? Math.floor((summary.done / summary.total) * 100) : 0;
    if (summary.canceled) {
        summary.status = 'canceled';
    } else if (summary.failed > 0) {
        summary.status = 'failed';
    } else if (summary.running > 0) {
        summary.status = 'running';
    } else if (summary.queued > 0) {
        summary.status = 'queued';
    } else {
        summary.status = 'done';
    }

    return summary;
}

function statusIcon(status) {
    const names = {
        done: 'pass',
        failed: 'error',
        running: 'debug-alt',
        queued: 'clock',
        canceled: 'circle-slash',
        offline: 'circle-slash',
        online: 'device-mobile',
    };

    return new vscode.ThemeIcon(names[status] || 'circle-large-outline');
}

function treeMessage(label, description, status, command) {
    const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
    item.description = description;
    item.iconPath = statusIcon(status);
    if (command) {
        item.command = command;
    }
    return item;
}

function sortedJobs() {
    return state.jobs.slice().sort((a, b) => {
        const sa = summarize(a);
        const sb = summarize(b);
        const activeA = sa.status === 'running' || sa.status === 'queued' || sa.status === 'failed';
        const activeB = sb.status === 'running' || sb.status === 'queued' || sb.status === 'failed';
        if (activeA !== activeB) {
            return activeA ? -1 : 1;
        }

        return (b.createdAtUtc || '').localeCompare(a.createdAtUtc || '');
    });
}

function collectWorkers() {
    const workers = new Map();
    for (const job of state.jobs) {
        const entry = job.entry || '';
        const sha = job.assemblySha || '';
        if (!entry || !sha) {
            continue;
        }

        const key = `${entry}|${sha}`;
        const shard = (job.shards || [])[0] || {};
        const current = workers.get(key) || {
            key,
            name: job.name || entry,
            entry,
            assemblySha: sha,
            iterations: shard.iterations,
            argsJson: shard.argsJson || '',
            runs: 0,
            lastSeen: job.createdAtUtc || '',
        };

        current.runs += 1;
        if ((job.createdAtUtc || '') > current.lastSeen) {
            current.lastSeen = job.createdAtUtc;
            current.name = job.name || current.name;
        }
        if (current.iterations === undefined && shard.iterations !== undefined) {
            current.iterations = shard.iterations;
        }

        workers.set(key, current);
    }

    return Array.from(workers.values()).sort((a, b) => (b.lastSeen || '').localeCompare(a.lastSeen || ''));
}

async function connect() {
    const value = await vscode.window.showInputBox({
        title: 'PhoneFarm Dispatcher',
        prompt: 'PhoneFarm dispatcher base URL',
        value: dispatcherUrl(),
        ignoreFocusOut: true,
        validateInput: (text) => (text.trim().startsWith('http://') || text.trim().startsWith('https://') ? undefined : 'Use an http:// or https:// URL'),
    });

    if (value === undefined) {
        return;
    }

    const target = vscode.workspace.workspaceFolders
        ? vscode.ConfigurationTarget.Workspace
        : vscode.ConfigurationTarget.Global;

    await vscode.workspace.getConfiguration('phoneFarm').update('dispatcherUrl', value.trim(), target);
    await refresh({ notify: true });
}

function openDispatcher() {
    vscode.env.openExternal(vscode.Uri.parse(dispatcherUrl()));
}

async function runCachedWorker() {
    const workers = collectWorkers();
    if (workers.length === 0) {
        const choice = await vscode.window.showWarningMessage(
            'No cached workers were found. Submit a worker assembly first.',
            'Submit Worker Assembly',
        );
        if (choice === 'Submit Worker Assembly') {
            await vscode.commands.executeCommand('phoneFarm.submitWorkerAssembly');
        }
        return;
    }

    const picked = await vscode.window.showQuickPick(
        workers.map((worker) => ({
            label: worker.name,
            description: worker.entry,
            detail: `${worker.assemblySha.slice(0, 12)}... · ${worker.runs} run(s) · last seen ${formatAgo(worker.lastSeen)}`,
            worker,
        })),
        {
            title: 'Run a cached PhoneFarm worker',
            matchOnDescription: true,
            matchOnDetail: true,
        },
    );

    if (!picked) {
        return;
    }

    const worker = picked.worker;
    const defaults = jobNameDefaults(worker.name);
    const name = await vscode.window.showInputBox({ title: 'Job name', prompt: 'Job name', value: defaults.name, ignoreFocusOut: true });
    if (!name) {
        return;
    }

    const shardsText = await vscode.window.showInputBox({
        title: 'Shards',
        prompt: 'Number of shards',
        value: String(config('defaultShards', 16)),
        ignoreFocusOut: true,
        validateInput: (text) => (isPositiveInteger(text) ? undefined : 'Enter a positive integer'),
    });
    if (!shardsText) {
        return;
    }

    const iterationsText = await vscode.window.showInputBox({
        title: 'Iterations',
        prompt: 'Iterations per shard, or 0 for non-counting workers',
        value: String(worker.iterations === undefined ? config('defaultIterations', 20000000) : worker.iterations),
        ignoreFocusOut: true,
        validateInput: (text) => (isNonNegativeInteger(text) ? undefined : 'Enter a non-negative integer'),
    });
    if (!iterationsText) {
        return;
    }

    const argsJson = await vscode.window.showInputBox({
        title: 'Arguments',
        prompt: 'Optional argsJson object for the worker',
        value: worker.argsJson || '{}',
        ignoreFocusOut: true,
    });
    if (argsJson === undefined) {
        return;
    }

    try {
        const bytes = await getBinary(`/api/workers/${encodeURIComponent(worker.assemblySha)}`);
        const job = await submitJob({
            name,
            entry: worker.entry,
            assemblyName: `${worker.assemblySha.slice(0, 12)}.dll`,
            bytes,
            shards: Number(shardsText),
            iterations: Number(iterationsText),
            argsJson,
        });

        vscode.window.setStatusBarMessage(`PhoneFarm: submitted ${job?.id || name}`, 5000);
        await vscode.commands.executeCommand('workbench.view.extension.phonefarm');
        await refresh();
    } catch (error) {
        vscode.window.showErrorMessage(`PhoneFarm: ${errorMessage(error)}`);
    }
}

async function submitWorkerAssembly() {
    const picked = await vscode.window.showOpenDialog({
        canSelectMany: false,
        openLabel: 'Select worker DLL',
        filters: { 'NET assembly': ['dll'] },
    });

    if (!picked || picked.length === 0) {
        return;
    }

    const uri = picked[0];
    const fileName = uri.fsPath.split(/[\\/]/).pop() || 'worker.dll';
    const entry = await vscode.window.showInputBox({
        title: 'Worker entry',
        prompt: 'Entry point in the form Namespace.Class, AssemblyName',
        value: `${fileName.replace(/\.dll$/i, '')}.MyWorker, ${fileName.replace(/\.dll$/i, '')}`,
        ignoreFocusOut: true,
    });
    if (!entry) {
        return;
    }

    const name = await vscode.window.showInputBox({
        title: 'Job name',
        prompt: 'Job name',
        value: fileName.replace(/\.dll$/i, ''),
        ignoreFocusOut: true,
    });
    if (!name) {
        return;
    }

    const shardsText = await vscode.window.showInputBox({
        title: 'Shards',
        prompt: 'Number of shards',
        value: String(config('defaultShards', 16)),
        ignoreFocusOut: true,
        validateInput: (text) => (isPositiveInteger(text) ? undefined : 'Enter a positive integer'),
    });
    if (!shardsText) {
        return;
    }

    const iterationsText = await vscode.window.showInputBox({
        title: 'Iterations',
        prompt: 'Iterations per shard, or 0 for non-counting workers',
        value: String(config('defaultIterations', 20000000)),
        ignoreFocusOut: true,
        validateInput: (text) => (isNonNegativeInteger(text) ? undefined : 'Enter a non-negative integer'),
    });
    if (!iterationsText) {
        return;
    }

    const argsJson = await vscode.window.showInputBox({
        title: 'Arguments',
        prompt: 'Optional argsJson object for the worker',
        value: '{}',
        ignoreFocusOut: true,
    });
    if (argsJson === undefined) {
        return;
    }

    try {
        const bytes = await fs.promises.readFile(uri.fsPath);
        const job = await submitJob({
            name,
            entry,
            assemblyName: fileName,
            bytes,
            shards: Number(shardsText),
            iterations: Number(iterationsText),
            argsJson,
        });

        vscode.window.setStatusBarMessage(`PhoneFarm: submitted ${job?.id || name}`, 5000);
        await vscode.commands.executeCommand('workbench.view.extension.phonefarm');
        await refresh();
    } catch (error) {
        vscode.window.showErrorMessage(`PhoneFarm: ${errorMessage(error)}`);
    }
}

async function showJob(arg) {
    const jobId = await resolveJobId(arg, 'Show Job');
    if (!jobId) {
        return;
    }

    const job = state.jobs.find((candidate) => candidate.id === jobId);
    if (!job) {
        vscode.window.showWarningMessage(`Job ${jobId} is no longer in the dispatcher snapshot. Refresh first.`);
        return;
    }

    const document = await vscode.workspace.openTextDocument({
        language: 'json',
        content: JSON.stringify(job, null, 2),
    });

    await vscode.window.showTextDocument(document, { preview: false });
}

async function cancelJob(arg) {
    const jobId = await resolveJobId(arg, 'Cancel Job');
    if (!jobId) {
        return;
    }

    try {
        await httpJson('POST', `/api/jobs/${encodeURIComponent(jobId)}/cancel`);
        vscode.window.setStatusBarMessage(`PhoneFarm: canceled ${jobId}`, 4000);
        await refresh();
    } catch (error) {
        vscode.window.showErrorMessage(`PhoneFarm: ${errorMessage(error)}`);
    }
}

async function retryFailedShards(arg) {
    const jobId = await resolveJobId(arg, 'Retry Failed Shards');
    if (!jobId) {
        return;
    }

    const job = state.jobs.find((candidate) => candidate.id === jobId);
    const failed = (job?.shards || []).filter((shard) => String(shard.state || '').toLowerCase() === 'failed');
    if (failed.length === 0) {
        vscode.window.showInformationMessage('No failed shards found in this job.');
        return;
    }

    try {
        for (const shard of failed) {
            await httpJson('POST', `/api/jobs/${encodeURIComponent(jobId)}/shards/${shard.index}/retry`);
        }

        vscode.window.setStatusBarMessage(`PhoneFarm: queued ${failed.length} shard(s) for retry`, 5000);
        await refresh();
    } catch (error) {
        vscode.window.showErrorMessage(`PhoneFarm: ${errorMessage(error)}`);
    }
}

async function unregisterAgent(arg) {
    let agentId = typeof arg === 'string' ? arg : arg?.id;
    if (!agentId) {
        const picked = await vscode.window.showQuickPick(
            state.agents.map((agent) => ({
                label: agent.info || agent.id,
                description: agent.id,
                detail: isOnline(agent) ? 'online' : 'offline',
                id: agent.id,
            })),
            { title: 'Forget PhoneFarm agent' },
        );

        if (!picked) {
            return;
        }

        agentId = picked.id;
    }

    const confirmation = await vscode.window.showWarningMessage(
        `Forget agent ${agentId}? If the agent is still online, it will reappear after its next claim.`,
        { modal: true },
        'Forget',
    );

    if (confirmation !== 'Forget') {
        return;
    }

    try {
        await httpJson('POST', `/api/agents/${encodeURIComponent(agentId)}/unregister`);
        vscode.window.setStatusBarMessage(`PhoneFarm: forgot ${agentId}`, 4000);
        await refresh();
    } catch (error) {
        vscode.window.showErrorMessage(`PhoneFarm: ${errorMessage(error)}`);
    }
}

async function resolveJobId(arg, title) {
    if (typeof arg === 'string') {
        return arg;
    }

    if (arg?.type === 'job' && arg.id) {
        return arg.id;
    }

    const picked = await vscode.window.showQuickPick(
        sortedJobs().map((job) => {
            const summary = summarize(job);
            return {
                label: job.name || job.id,
                description: `${job.id} · ${summary.status} ${summary.done}/${summary.total}`,
                detail: `${dispatcherUrl()}/api/jobs/${job.id}`,
                id: job.id,
            };
        }),
        { title },
    );

    return picked?.id;
}

function jobNameDefaults(baseName) {
    const now = new Date();
    const stamp = [
        now.getUTCFullYear(),
        String(now.getUTCMonth() + 1).padStart(2, '0'),
        String(now.getUTCDate()).padStart(2, '0'),
        '-',
        String(now.getUTCHours()).padStart(2, '0'),
        String(now.getUTCMinutes()).padStart(2, '0'),
        String(now.getUTCSeconds()).padStart(2, '0'),
    ].join('');

    return { name: `${baseName}-${stamp}` };
}

function isPositiveInteger(text) {
    return /^\d+$/.test(String(text || '').trim()) && Number(text) > 0;
}

function isNonNegativeInteger(text) {
    return /^\d+$/.test(String(text || '').trim());
}

function formatAgo(value) {
    const timestamp = Date.parse(value || '');
    if (Number.isNaN(timestamp)) {
        return 'unknown';
    }

    const seconds = Math.max(0, Math.floor((Date.now() - timestamp) / 1000));
    if (seconds < 60) {
        return `${seconds}s ago`;
    }

    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) {
        return `${minutes}m ago`;
    }

    const hours = Math.floor(minutes / 60);
    if (hours < 24) {
        return `${hours}h ago`;
    }

    return `${Math.floor(hours / 24)}d ago`;
}

function errorMessage(error) {
    return String(error?.message || error || 'unknown error');
}

class TreeProvider {
    constructor(viewId) {
        this.viewId = viewId;
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
    }

    refresh() {
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element) {
        return element;
    }

    async getChildren(element) {
        if (this.viewId === 'phoneFarm.jobs') {
            return getJobChildren(element);
        }

        return getPhoneChildren(element);
    }
}

function getJobChildren(element) {
    if (element?.type === 'job') {
        const job = state.jobs.find((candidate) => candidate.id === element.id);
        if (!job) {
            return [];
        }

        return (job.shards || []).map((shard) => {
            const item = new vscode.TreeItem(`#${shard.index} ${shard.state}`, vscode.TreeItemCollapsibleState.None);
            item.description = shard.result || shard.error || shard.machine || '';
            item.iconPath = statusIcon(String(shard.state || '').toLowerCase());
            item.tooltip = JSON.stringify(shard, null, 2);
            return item;
        });
    }

    if (!state.ok) {
        return [treeMessage('Dispatcher unreachable', state.error, 'failed', {
            command: 'phoneFarm.refresh',
            title: 'Refresh PhoneFarm',
        })];
    }

    if (state.jobs.length === 0) {
        return [treeMessage('No jobs', 'run a cached worker or submit an assembly', 'offline', {
            command: 'phoneFarm.runCachedWorker',
            title: 'Run Cached Worker',
        })];
    }

    return sortedJobs().map((job) => {
        const summary = summarize(job);
        const item = new vscode.TreeItem(job.name || job.id, vscode.TreeItemCollapsibleState.Collapsed);
        item.id = `job:${job.id}`;
        item.description = `${summary.status} ${summary.done}/${summary.total} · ${formatAgo(job.createdAtUtc)}`;
        item.tooltip = `${job.name || job.id}\n${job.id}\n${job.entry || ''}\n${dispatcherUrl()}/api/jobs/${job.id}`;
        item.iconPath = statusIcon(summary.status);
        item.contextValue = `phoneFarm.job.${summary.status}`;
        item.command = { command: 'phoneFarm.showJob', title: 'Show Job', arguments: [{ type: 'job', id: job.id }] };
        item.type = 'job';
        item.jobId = job.id;
        return item;
    });
}

function getPhoneChildren(element) {
    if (element) {
        return [];
    }

    if (!state.ok) {
        return [treeMessage('Dispatcher unreachable', state.error, 'failed', {
            command: 'phoneFarm.refresh',
            title: 'Refresh PhoneFarm',
        })];
    }

    if (state.agents.length === 0) {
        return [treeMessage('No registered agents', 'start the PhoneFarm Android agent', 'offline', {
            command: 'phoneFarm.refresh',
            title: 'Refresh PhoneFarm',
        })];
    }

    const runningByAgent = new Map();
    const jobById = new Map(state.jobs.map((job) => [job.id, job]));

    for (const job of state.jobs) {
        for (const shard of job.shards || []) {
            if (String(shard.state || '').toLowerCase() !== 'running' || !shard.assignedTo) {
                continue;
            }

            const current = runningByAgent.get(shard.assignedTo) || { count: 0, jobs: new Set() };
            current.count += 1;
            current.jobs.add(job.id);
            runningByAgent.set(shard.assignedTo, current);
        }
    }

    return state.agents.map((agent) => {
        const online = isOnline(agent);
        const item = new vscode.TreeItem(agent.info || agent.id, vscode.TreeItemCollapsibleState.None);
        item.id = `agent:${agent.id}`;

        const running = runningByAgent.get(agent.id);
        const parts = [
            online ? 'online' : 'offline',
            `${agent.capacity || 1} slots`,
            formatAgo(agent.lastSeenUtc),
        ];

        if (running?.jobs?.size) {
            const jobNames = Array.from(running.jobs).map((id) => jobById.get(id)?.name || id);
            parts.push(`${running.count} shard(s) · ${jobNames.join(', ')}`);
        }

        item.description = parts.join(' · ');
        item.tooltip = `Agent ID: ${agent.id}\n${agent.info || ''}\nCapacity: ${agent.capacity || 1}\nLast seen: ${formatAgo(agent.lastSeenUtc)}\nJob: ${agent.jobId || 'none'}`;
        item.iconPath = statusIcon(online ? 'online' : 'offline');
        item.contextValue = online ? 'phoneFarm.agent.online' : 'phoneFarm.agent.offline';
        item.command = { command: 'phoneFarm.showJob', title: 'Show Jobs' };
        return item;
    });
}
