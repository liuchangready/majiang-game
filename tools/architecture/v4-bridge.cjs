// V4 task adapter using the installed Agent Mesh Bridge and its normal permission mode.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawn } = require('node:child_process');
const { publishReviewNotification } = require('./v4-review-notification.cjs');
const root = path.join(os.homedir(), '.vscode/extensions/local-dev.agent-mesh-bridge-1.0.0/dist/bridge');
const { AgentDetector } = require(path.join(root, 'agent-detector.js'));
const { AgentExecutor } = require(path.join(root, 'agent-executor.js'));
const { MCPServer } = require(path.join(root, 'mcp-server.js'));
const workspace = path.resolve(__dirname, '../..');
const task = process.argv[2];
if (!/^[A-Z][A-Z0-9_-]*$/.test(task || '')) throw new Error('Invalid task id');
const conversation = process.argv[3];
if (conversation && !/^[0-9a-f-]{36}$/i.test(conversation)) throw new Error('Invalid conversation id');
const taskFile = path.join(workspace, 'doc/v4-migration', `TASK-ANTI-${task}.md`);
if (!fs.existsSync(taskFile)) throw new Error('Task file does not exist');
const logs = path.join(workspace, 'build/v4-migration');
fs.mkdirSync(logs, { recursive: true });
const run = `${task}-${Date.now()}`;

class WindowsExecutor extends AgentExecutor {
  async runAgentProcess(target, req, stack) {
    if (req.targetAgent !== 'antigravity') throw new Error('Antigravity only');
    return new Promise((resolve, reject) => {
      const args = ['-p', req.prompt, '--output-format', 'json', '--mode', req.mode || 'accept-edits'];
      if (conversation) args.push('--conversation', conversation);
      const child = spawn(target.executablePath, args,
        { cwd: workspace, shell: false, windowsHide: true,
          env: { ...process.env, AGENT_BRIDGE_CALLER: 'codex',
            AGENT_BRIDGE_STACK: stack.join(','), AGENT_BRIDGE_DEPTH: String(stack.length) } });
      const out = fs.createWriteStream(path.join(logs, `${run}.stdout.log`));
      const err = fs.createWriteStream(path.join(logs, `${run}.stderr.log`));
      let stdout = '', stderr = '';
      child.stdout.on('data', b => { stdout += b; out.write(b); });
      child.stderr.on('data', b => { stderr += b; err.write(b); });
      const timer = setTimeout(() => { child.kill(); reject(new Error('Task exceeded 45 minutes; inspect partial edits before retrying')); }, 2700000);
      child.on('error', e => { clearTimeout(timer); out.end(); err.end(); reject(e); });
      child.on('close', code => {
        clearTimeout(timer); out.end(); err.end();
        if (code !== 0) return reject(new Error(`Anti exited ${code}; inspect ${run} logs`));
        let parsedJson;
        try { parsedJson = JSON.parse(stdout); } catch { }
        resolve({ rawText: stdout || stderr, parsedJson });
      });
    });
  }
}

async function main() {
  const detector = new AgentDetector();
  const executor = new WindowsExecutor(detector, { executionTimeoutSeconds: 2700 });
  const server = new MCPServer(executor, detector);
  server.setCallerAgent('codex');
  const prompt = `You are Antigravity (Anti), the user's designated production developer for V4 migration in D:/majiang. Codex owns architecture decisions, independent tests, builds and acceptance. Implement task ${task} fully. First read AGENTS.md, doc/v4-migration/PLAN.md, doc/v4-migration/CONTRACT.md and doc/v4-migration/TASK-ANTI-${task}.md. These current instructions supersede historical V3 compatibility requirements and old T1-T7 ownership for your assigned files. Use built-in file read/search/write/edit tools ONLY. Do not run shell, RunCommand, terminals, builds, other agents, commits, or change permissions/settings. If file tools cannot work, report the real blocker. Preserve other workspace changes. Do not change review-owned tests under tools/architecture/v4-review. Do not delete or weaken valid rules tests. Work only within this task's file ownership. Before finalizing, reread your task file and check doc/v4-migration/FEEDBACK-${task}.md if it exists: Codex may leave current compiler/test evidence there while you work. Address applicable feedback without weakening assertions. Write doc/v4-migration/delivery-ANTI-${task}.md listing files and unresolved items, and explicitly state tests are not run by you. No approval question needed for assigned edits. Do not stop at a plan; implement. No V3 API/entry compatibility is required. Do not jump to later stages.`;
  console.log(`${new Date().toISOString()} Anti ${task} started; logs=${run}`);
  const result = await server.handleMessage({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: {
    name: 'invoke_antigravity', arguments: { prompt, workspacePath: workspace, mode: 'accept-edits' }
  }});
  fs.writeFileSync(path.join(logs, `${run}.bridge.json`), JSON.stringify(result, null, 2));
  console.log(`${new Date().toISOString()} Anti ${task} finished; inspect delivery and ${run}.bridge.json`);
  let agentError = false;
  for (const item of result.result?.content || []) {
    if (item.type !== 'text') continue;
    try {
      const output = JSON.parse(item.text);
      if (output.status === 'ERROR' || output.error) {
        agentError = true;
        console.error(`Anti reported ${output.status || 'ERROR'}: ${output.error || 'inspect task log'}`);
      }
    } catch { }
  }
  const failed = Boolean(result.error || result.result?.isError || agentError);
  const eventFile = publishReviewNotification({ task, run, provider: 'Anti', outcome: failed ? 'failed' : 'delivered',
    resultFile: path.join(logs, `${run}.bridge.json`),
    deliveryFile: path.join(workspace, `doc/v4-migration/delivery-ANTI-${task}.md`) });
  console.log(JSON.stringify({ event: 'review_required', task, eventFile }));
  if (failed) process.exitCode = 1;
}
main().catch(e => {
  console.error(e.message); process.exitCode = 1;
  publishReviewNotification({ task, run, provider: 'Anti', outcome: 'failed',
    resultFile: path.join(logs, `${run}.stderr.log`),
    deliveryFile: path.join(workspace, `doc/v4-migration/delivery-ANTI-${task}.md`) });
});
