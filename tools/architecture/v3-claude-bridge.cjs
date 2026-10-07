// Workspace adapter for the installed MCP bridge. Uses the user's Claude Code GLM configuration.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawn } = require('node:child_process');
const bridgeRoot = path.join(os.homedir(), '.vscode/extensions/local-dev.agent-mesh-bridge-1.0.0/dist/bridge');
const { AgentDetector } = require(path.join(bridgeRoot, 'agent-detector.js'));
const { AgentExecutor } = require(path.join(bridgeRoot, 'agent-executor.js'));
const { MCPServer } = require(path.join(bridgeRoot, 'mcp-server.js'));
const workspace = path.resolve(__dirname, '../..');
const task = process.argv[2];
if (!/^[A-Z][A-Z0-9_-]*$/.test(task || '')) throw new Error('Invalid task id');
const taskFile = path.resolve(workspace, process.argv[3]);
if (!taskFile.startsWith(workspace + path.sep)) throw new Error('Task must be within workspace');
const logs = path.join(workspace, 'build/v3-migration');
fs.mkdirSync(logs, { recursive: true });
const executable = path.join(process.env.APPDATA, 'npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe');
const config = JSON.parse(fs.readFileSync(path.join(os.homedir(), '.claude/settings.json'), 'utf8').replace(/^\uFEFF/, ''));
const model = config.env?.ANTHROPIC_DEFAULT_SONNET_MODEL;
if (!model || !/^glm-/i.test(model)) throw new Error('Expected an explicitly configured GLM model');

class ClaudeExecutor extends AgentExecutor {
  async runAgentProcess(target, req, stack) {
    if (req.targetAgent !== 'claude_code') throw new Error('Claude Code only');
    const args = ['-p', '--output-format', 'stream-json', '--verbose', '--model', model,
      '--permission-mode', 'acceptEdits', '--tools', 'Read,Edit,Write,Glob,Grep',
      '--allowedTools', 'Read,Edit,Write,Glob,Grep'];
    return new Promise((resolve, reject) => {
      const child = spawn(executable, args, { cwd: workspace, shell: false, windowsHide: true,
        env: { ...process.env, AGENT_BRIDGE_CALLER: 'codex', AGENT_BRIDGE_STACK: stack.join(',') } });
      const out = fs.createWriteStream(path.join(logs, `claude-${task}.jsonl`));
      const err = fs.createWriteStream(path.join(logs, `claude-${task}.stderr.log`));
      let stdout = '';
      child.stdout.on('data', b => { stdout += b; out.write(b); });
      child.stderr.on('data', b => err.write(b));
      child.stdin.end(req.prompt);
      const timer = setTimeout(() => { child.kill(); reject(new Error('Claude task exceeded 45 minutes')); }, 2700000);
      child.on('error', e => { clearTimeout(timer); out.end(); err.end(); reject(e); });
      child.on('close', code => {
        clearTimeout(timer); out.end(); err.end();
        const rows = stdout.split(/\r?\n/).filter(Boolean).map(line => { try { return JSON.parse(line); } catch { return null; } }).filter(Boolean);
        const result = rows.findLast(row => row.type === 'result');
        if (code !== 0 || result?.is_error || !result) return reject(new Error(`Claude ${task} failed (exit=${code}); inspect task logs`));
        resolve({ rawText: result.result || 'Completed', parsedJson: result });
      });
    });
  }
}

async function main() {
  const detector = new AgentDetector();
  const executor = new ClaudeExecutor(detector, { executionTimeoutSeconds: 2700 });
  const server = new MCPServer(executor, detector);
  server.setCallerAgent('codex');
  const prompt = `You are Claude Code using the user's GLM model, production developer for task ${task} in D:/majiang. The user explicitly delegates remaining V3 development to you. Codex owns design, tests, build execution and review. Other tasks run concurrently; modify ONLY your task ownership. Use Read/Edit/Write/Glob/Grep; no shell, other agents, commits, settings changes, test deletions or test weakening. Existing working tree modifications belong to this authorized migration: preserve them. Read actual declarations before coding. Do not claim builds/tests passed: Codex executes them. Write doc/v3-migration/delivery-${task}.md with changes and limits. Complete the implementation, not just advice. The old core domain source paths in earlier task docs have moved to engine; GameClient/sync moved to client; Screen/presentation stay core.\n\n${fs.readFileSync(taskFile, 'utf8')}`;
  console.log(`${new Date().toISOString()} Claude ${task} started model=${model}`);
  const result = await server.handleMessage({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: {
    name: 'invoke_claude_code', arguments: { prompt, workspacePath: workspace, timeoutSeconds: 2700 }
  }});
  fs.writeFileSync(path.join(logs, `claude-${task}-bridge.json`), JSON.stringify(result, null, 2));
  console.log(`${new Date().toISOString()} Claude ${task} finished`);
  if (result.error || result.result?.isError) process.exitCode = 1;
}
main().catch(e => { console.error(e.message); process.exitCode = 1; });
