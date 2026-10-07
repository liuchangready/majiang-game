// Run the user's configured GLM through Claude Code; Codex owns builds and review.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawn } = require('node:child_process');
const { publishReviewNotification } = require('./v4-review-notification.cjs');
const workspace = path.resolve(__dirname, '../..');
const task = process.argv[2];
if (!/^[A-Z][A-Z0-9_-]*$/.test(task || '')) throw new Error('Invalid task id');
const resumeSession = process.argv[3];
if (resumeSession && !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(resumeSession)) throw new Error('Invalid session id');
const config = JSON.parse(fs.readFileSync(path.join(os.homedir(), '.claude/settings.json'), 'utf8').replace(/^\uFEFF/, ''));
const model = config.env?.ANTHROPIC_DEFAULT_SONNET_MODEL;
if (!model || !/^glm-/i.test(model)) throw new Error('Expected an explicitly configured GLM model');
const taskFile = path.join(workspace, `doc/v4-migration/TASK-GLM-${task}.md`);
const prompt = `The user explicitly switched V4 production development from Anti to Claude Code using GLM. You implement task ${task}; Codex supervises, runs builds and independently accepts it. Read AGENTS.md and the task below, then implement it. Preserve all existing working tree changes. Only edit production and module tests within the task ownership and your delivery doc. Do not edit tools/architecture review tests, settings, credentials, or unrelated files. Use built-in file tools only; no shells, other agents, commits, test deletion/skipping or weakened valid assertions. Do not claim tests ran: Codex runs them. Finish a bounded implementation, not advice. Write doc/v4-migration/delivery-GLM-${task}.md with changes, remaining risks, and tests proposed.\n\n${fs.readFileSync(taskFile, 'utf8')}`;
const logs = path.join(workspace, 'build/v4-migration');
fs.mkdirSync(logs, { recursive: true });
const prefix = path.join(logs, `GLM-${task}-${Date.now()}`);
const executable = path.join(process.env.APPDATA, 'npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe');
const args = ['-p', '--output-format', 'stream-json', '--verbose', '--model', model,
  '--permission-mode', 'acceptEdits', '--tools', 'Read,Edit,Write,Glob,Grep',
  '--allowedTools', 'Read,Edit,Write,Glob,Grep', '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}'];
if (resumeSession) args.push('--resume', resumeSession);
const child = spawn(executable, args, { cwd: workspace, shell: false, windowsHide: true,
  env: { ...process.env, ...config.env } });
const out = fs.createWriteStream(`${prefix}.jsonl`);
const err = fs.createWriteStream(`${prefix}.stderr.log`);
let pending = '', result;
child.stdout.on('data', data => {
  out.write(data); pending += data.toString();
  let newline;
  while ((newline = pending.indexOf('\n')) >= 0) {
    const line = pending.slice(0, newline); pending = pending.slice(newline + 1);
    try {
      const row = JSON.parse(line);
      if (row.type === 'system' && row.subtype === 'init') console.log(JSON.stringify({ event: 'initialized', model: row.model, session: row.session_id }));
      if (row.type === 'result') result = row;
    } catch {}
  }
});
child.stderr.on('data', data => err.write(data));
child.stdin.end(prompt);
fs.writeFileSync(`${prefix}.run.json`, JSON.stringify({ task, model, pid: child.pid, started: new Date().toISOString() }, null, 2));
console.log(JSON.stringify({ event: 'started', task, model, pid: child.pid, log: `${prefix}.jsonl` }));
const timer = setTimeout(() => child.kill(), 2700000);
child.on('error', error => { console.error(error.message); process.exitCode = 1; });
child.on('close', code => {
  clearTimeout(timer); out.end(); err.end();
  fs.writeFileSync(`${prefix}.result.json`, JSON.stringify({ code, result }, null, 2));
  console.log(JSON.stringify({ event: 'finished', task, code, isError: result?.is_error, resultFile: `${prefix}.result.json` }));
  const eventFile = publishReviewNotification({ task, run: path.basename(prefix), provider: 'Claude/GLM',
    outcome: code === 0 && result && !result.is_error ? 'delivered' : 'failed',
    resultFile: `${prefix}.result.json`, deliveryFile: path.join(workspace, `doc/v4-migration/delivery-GLM-${task}.md`) });
  console.log(JSON.stringify({ event: 'review_required', task, eventFile }));
  if (code !== 0 || !result || result.is_error) process.exitCode = 1;
});
