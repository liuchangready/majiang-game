// Observe the authorized parallel tasks; switch only explicit Anti quota failures to GLM.
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const workspace = path.resolve(__dirname, '../..');

function quotaFailure(bridge, stderr) {
  let failed = Boolean(bridge.error || bridge.result?.isError);
  for (const item of bridge.result?.content || []) {
    try { const result = JSON.parse(item.text); failed ||= result.status === 'ERROR' || Boolean(result.error); } catch {}
  }
  if (!failed) return false;
  return stderr.split(/\r?\n/).some(line => {
    if (!line.startsWith('AGY_ERROR: ')) return false;
    try {
      const error = JSON.parse(line.slice('AGY_ERROR: '.length));
      return error.status === 'RESOURCE_EXHAUSTED' && /quota|usage limit|使用上限|配额/i.test(error.short_error || '');
    } catch { return false; }
  });
}

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function alive(pid) { try { process.kill(pid, 0); return true; } catch { return false; } }

async function main() {
  const manifest = path.resolve(workspace, process.argv[2] || '');
  const logRoot = path.join(workspace, 'build/v4-migration') + path.sep;
  if (!manifest.startsWith(logRoot) || !manifest.endsWith('.json')) throw new Error('Manifest must be a migration log JSON file');
  const tasks = JSON.parse(fs.readFileSync(manifest, 'utf8').replace(/^\uFEFF/, ''));
  const statusPath = manifest.replace(/\.json$/, '.status.json');
  const stopPath = manifest.replace(/\.json$/, '.stop');
  const lockPath = manifest.replace(/\.json$/, '.lock');
  const lock = fs.openSync(lockPath, 'wx');
  fs.writeFileSync(lock, String(process.pid));
  const states = tasks.map(task => ({ ...task, provider: 'Anti', status: 'running' }));
  const save = () => fs.writeFileSync(statusPath, JSON.stringify({ watcherPid: process.pid, updated: new Date().toISOString(), stopPath, tasks: states }, null, 2));
  const stopped = () => fs.existsSync(stopPath);
  save();
  try {
    await Promise.all(states.map(async state => {
      if (!/^[A-Z][A-Z0-9_-]*$/.test(state.task) || !/^[A-Z0-9_-]+-\d+$/.test(state.run) || !Number.isInteger(state.pid)) throw new Error('Invalid task entry');
      const prefix = path.join(logRoot, state.run);
      const deadline = Date.now() + 55 * 60 * 1000;
      while (!fs.existsSync(`${prefix}.bridge.json`) || alive(state.pid)) {
        if (stopped() || Date.now() > deadline) {
          state.status = stopped() ? 'watch_stopped' : 'watch_timeout'; save(); return;
        }
        await delay(2000);
      }
      if (stopped()) { state.status = 'watch_stopped'; save(); return; }
      const bridge = JSON.parse(fs.readFileSync(`${prefix}.bridge.json`, 'utf8'));
      const stderr = fs.readFileSync(`${prefix}.stderr.log`, 'utf8');
      if (!quotaFailure(bridge, stderr)) {
        state.status = 'anti_finished_requires_review'; save(); return;
      }
      const original = fs.readFileSync(path.join(workspace, `doc/v4-migration/TASK-ANTI-${state.task}.md`), 'utf8');
      const note = `# Authorized quota fallback to Claude Code / GLM\n\nAnti has exited with an explicit RESOURCE_EXHAUSTED quota error. The user authorized this fallback. Continue the SAME bounded task from actual files, preserving all partial changes. Other developers run in parallel: respect the ownership below exactly. Codex owns tests/builds/review. Before final delivery read doc/v4-migration/FEEDBACK-${state.task}.md if present. Write delivery-GLM-${state.task}.md (the older Anti delivery name below is superseded). No shell/builds/other agents/settings changes or reviewer edits.\n\n`;
      fs.writeFileSync(path.join(workspace, `doc/v4-migration/TASK-GLM-${state.task}.md`), note + original);
      // Recheck cancellation immediately before creating a new writer.
      if (stopped()) { state.status = 'watch_stopped'; save(); return; }
      const child = spawn(process.execPath, [path.join(__dirname, 'v4-claude-bridge.cjs'), state.task],
        { cwd: workspace, shell: false, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
      state.provider = 'Claude/GLM'; state.status = 'fallback_running'; state.bridgePid = child.pid; save();
      console.log(JSON.stringify({ task: state.task, event: 'quota_fallback_started', bridgePid: child.pid }));
      let buffered = '';
      child.stdout.on('data', data => {
        buffered += data.toString();
        let newline;
        while ((newline = buffered.indexOf('\n')) >= 0) {
          const line = buffered.slice(0, newline); buffered = buffered.slice(newline + 1);
          try {
            const event = JSON.parse(line);
            if (event.event === 'started') { state.pid = event.pid; state.fallbackLog = event.log; }
            if (event.event === 'initialized') state.session = event.session;
            save();
          } catch {}
        }
      });
      const fallbackError = fs.createWriteStream(`${prefix}.fallback-stderr.log`);
      child.stderr.pipe(fallbackError);
      await new Promise(resolve => {
        child.on('error', error => { state.status = 'fallback_launch_failed'; state.error = error.message; save(); resolve(); });
        child.on('close', code => { state.status = code === 0 ? 'glm_finished_requires_review' : 'glm_failed_requires_review'; state.exitCode = code; fallbackError.end(); save(); resolve(); });
      });
    }));
  } finally {
    fs.closeSync(lock);
    fs.unlinkSync(lockPath);
  }
}

module.exports = { quotaFailure };
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
