// Resume the three bounded R5 Anti tasks after the reported quota reset.
// This supervisor only starts writers after checking the saved source snapshot
// and live bridge/agent processes. The bridges publish review notifications.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn, execFileSync } = require('node:child_process');

const workspace = path.resolve(__dirname, '../..');
const evidence = path.join(workspace, 'build/v4-migration');
const retry = process.argv.includes('--retry');
const attempt = retry ? 'r5-deferred-retry' : 'r5-deferred-resume';
const statusFile = path.join(evidence, `${attempt}.status.json`);
const stopFile = path.join(evidence, `${attempt}.stop`);
const baselineFile = path.join(evidence, retry
  ? 'R5-after-anti-source-hashes.json' : 'R5-quota-partial-source-hashes.json');
const resumeAt = Date.parse(retry
  ? '2026-10-07T11:06:00Z' // 19:06 Asia/Shanghai
  : '2026-10-07T07:36:00Z'); // 15:36 Asia/Shanghai
const tasks = (retry ? [
  { task: 'R5GAME', conversation: 'f6280b44-e8f7-481d-a843-0387ce2fb55e' },
  { task: 'R5ROOTFIX', conversation: '8be97dc1-2960-44ac-a7dd-526412d80cf8' },
] : [
  { task: 'R5COMMON', conversation: '1f4413ca-1657-4f46-bb36-ac8b0eac3a96' },
  { task: 'R5GAME', conversation: 'f6280b44-e8f7-481d-a843-0387ce2fb55e' },
  { task: 'R5ROOTFIX', conversation: '8be97dc1-2960-44ac-a7dd-526412d80cf8' },
]);
const state = { supervisorPid: process.pid, resumeAt: new Date(resumeAt).toISOString(),
  updated: new Date().toISOString(), state: 'waiting', tasks: tasks.map(x => ({ ...x, state: 'waiting' })) };

function save() {
  state.updated = new Date().toISOString();
  fs.mkdirSync(evidence, { recursive: true });
  const temporary = `${statusFile}.${process.pid}.tmp`;
  fs.writeFileSync(temporary, JSON.stringify(state, null, 2));
  fs.renameSync(temporary, statusFile);
}

function sourceCheck() {
  const baseline = JSON.parse(fs.readFileSync(baselineFile, 'utf8').replace(/^\uFEFF/, ''));
  const expected = new Map(baseline.map(item => [item.path.replace(/\\/g, '/'), item.sha256.toUpperCase()]));
  const changed = [];
  for (const [relative, hash] of expected) {
    const absolute = path.join(workspace, relative);
    if (!fs.existsSync(absolute)) { changed.push(`${relative}: missing`); continue; }
    const actual = crypto.createHash('sha256').update(fs.readFileSync(absolute)).digest('hex').toUpperCase();
    if (actual !== hash) changed.push(`${relative}: changed`);
  }
  const roots = ['engine', 'protocol', 'client', 'server', 'presentation'];
  const found = [];
  function walk(directory) {
    if (!fs.existsSync(directory)) return;
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        if (entry.name !== 'build') walk(absolute);
      } else if (/\.(kt|java)$/i.test(entry.name)) {
        found.push(path.relative(workspace, absolute).replace(/\\/g, '/'));
      }
    }
  }
  for (const root of roots) walk(path.join(workspace, root, 'src'));
  walk(path.join(workspace, 'tools/architecture/v4-review'));
  for (const relative of found) if (!expected.has(relative)) changed.push(`${relative}: new`);
  return { expected: expected.size, found: found.length, changed };
}

function activeWriters() {
  const command = '$p = Get-CimInstance Win32_Process | Where-Object { '
    + '$_.Name -in @("agy.exe", "claude.exe", "node.exe") -and '
    + '$_.CommandLine -match "v4-bridge\\.cjs|v4-claude-bridge\\.cjs|R5COMMON|R5GAME|R5ROOTFIX" '
    + '} | Select-Object ProcessId, Name, CommandLine; '
    + '@($p) | ConvertTo-Json -Compress';
  const raw = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command],
    { cwd: workspace, encoding: 'utf8', timeout: 30000 }).trim();
  if (!raw || raw === 'null' || raw === '[]') return [];
  const rows = JSON.parse(raw);
  return (Array.isArray(rows) ? rows : [rows]).filter(row => row.ProcessId !== process.pid)
    .map(row => ({ pid: row.ProcessId, name: row.Name }));
}

function preflight() {
  const source = sourceCheck();
  const writers = activeWriters();
  return { source, writers, stopped: fs.existsSync(stopFile) };
}

function launch(taskState) {
  const output = fs.openSync(path.join(evidence, `${attempt}-${taskState.task}.stdout.log`), 'a');
  const error = fs.openSync(path.join(evidence, `${attempt}-${taskState.task}.stderr.log`), 'a');
  const child = spawn(process.execPath,
    [path.join(__dirname, 'v4-bridge.cjs'), taskState.task, taskState.conversation],
    { cwd: workspace, shell: false, windowsHide: true, stdio: ['ignore', output, error] });
  fs.closeSync(output);
  fs.closeSync(error);
  taskState.pid = child.pid;
  taskState.state = 'running';
  save();
  return new Promise(resolve => {
    child.once('error', err => {
      taskState.state = 'launch_failed'; taskState.error = err.message; save(); resolve();
    });
    child.once('close', code => {
      taskState.state = code === 0 ? 'delivered_requires_review' : 'failed_requires_review';
      taskState.exitCode = code; save(); resolve();
    });
  });
}

async function main() {
  if (process.argv.includes('--verify-only')) {
    console.log(JSON.stringify(preflight()));
    return;
  }
  save();
  while (Date.now() < resumeAt) {
    if (fs.existsSync(stopFile)) { state.state = 'cancelled'; save(); return; }
    await new Promise(resolve => setTimeout(resolve, Math.min(30000, resumeAt - Date.now())));
  }
  const check = preflight();
  state.preflight = check;
  if (check.stopped || check.writers.length || check.source.changed.length ||
      check.source.expected !== check.source.found) {
    state.state = check.stopped ? 'cancelled' : 'preflight_blocked';
    save();
    return;
  }
  state.state = 'running'; save();
  await Promise.all(state.tasks.map(launch));
  state.state = 'finished_requires_review'; save();
}

main().catch(error => { state.state = 'error'; state.error = error.message; save(); process.exitCode = 1; });
