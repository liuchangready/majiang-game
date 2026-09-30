// Local adapter for the installed Agent Mesh Bridge; does not modify the plugin.
// Its MCP dispatcher/agent detection/loop guards are retained. Process transport
// uses normal Windows argument escaping, windowsHide and the requested CLI mode.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawn } = require('node:child_process');
const root = path.join(os.homedir(), '.vscode/extensions/local-dev.agent-mesh-bridge-1.0.0/dist/bridge');
const { AgentDetector } = require(path.join(root, 'agent-detector.js'));
const { AgentExecutor } = require(path.join(root, 'agent-executor.js'));
const { MCPServer } = require(path.join(root, 'mcp-server.js'));
const workspace = path.resolve(__dirname, '../..');
const task = process.argv[2];
if (!['M', 'U', 'F'].includes(task)) throw new Error('Expected M|U|F');
const logs = path.join(workspace, 'build/meld-choice');
fs.mkdirSync(logs, { recursive: true });

class WindowsExecutor extends AgentExecutor {
  async runAgentProcess(target, req, stack) {
    if (req.targetAgent !== 'antigravity') throw new Error('This adapter only delegates to Antigravity');
    const prompt = `[Cross-Agent Bridge Request]\nFrom Agent: codex\nCall Chain: ${stack.join(' -> ')}\nWorkspace: ${workspace}\n\n${req.prompt}`;
    const args = ['-p', prompt, '--output-format', 'json', '--mode', req.mode || 'accept-edits'];
    return new Promise((resolve, reject) => {
      const child = spawn(target.executablePath, args, {
        cwd: workspace, shell: false, windowsHide: true,
        env: { ...process.env, AGENT_BRIDGE_CALLER: 'codex', AGENT_BRIDGE_STACK: stack.join(','), AGENT_BRIDGE_DEPTH: String(stack.length) }
      });
      const out = fs.createWriteStream(path.join(logs, `bridge-${task}.stdout.log`));
      const err = fs.createWriteStream(path.join(logs, `bridge-${task}.stderr.log`));
      let stdout = '', stderr = '';
      child.stdout.on('data', b => { stdout += b; out.write(b); });
      child.stderr.on('data', b => { stderr += b; err.write(b); });
      const timer = setTimeout(() => { child.kill(); reject(new Error('Antigravity task exceeded 45 minutes; inspect partial files before retrying')); }, 45 * 60 * 1000);
      child.on('error', e => { clearTimeout(timer); out.end(); err.end(); reject(e); });
      child.on('close', code => {
        clearTimeout(timer); out.end(); err.end();
        if (code !== 0) return reject(new Error(`Antigravity exited ${code}; inspect bridge-${task} logs`));
        let parsedJson;
        try { parsedJson = JSON.parse(stdout); } catch { /* Raw output retained. */ }
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
  const instructions = fs.readFileSync(path.join(workspace, 'doc/meld-choice/DESIGN.md'), 'utf8');
  const followup = process.argv[3] ? fs.readFileSync(path.resolve(workspace, process.argv[3]), 'utf8') : '';
  const prompt = `You are Antigravity, production developer for task ${task} in D:/majiang. Codex designs, writes tests, builds, and reviews. Implement your task fully using built-in file read/search/edit tools ONLY. Do not use RunCommand, shell, terminals, or other agents: command permissions are unavailable in this headless session. Do not change permissions. Do not modify tests, Gradle/dependencies, or files owned by the other concurrent task. Read the relevant production files and follow this contract. Write delivery-${task}.md under doc/meld-choice. No need to ask approval.\n${instructions}\n${followup}`;
  console.log(`${new Date().toISOString()} bridge ${task} started`);
  const result = await server.handleMessage({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: {
    name: 'invoke_antigravity', arguments: { prompt, workspacePath: workspace, mode: 'accept-edits' }
  }});
  fs.writeFileSync(path.join(logs, `bridge-${task}.json`), JSON.stringify(result, null, 2));
  console.log(`${new Date().toISOString()} bridge ${task} finished`);
  if (result.error || result.result?.isError) process.exitCode = 1;
}
main().catch(e => { console.error(e.message); process.exitCode = 1; });