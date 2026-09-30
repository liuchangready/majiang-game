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
if (!['status', 'A', 'B', 'C', 'D'].includes(task)) throw new Error('Expected status|A|B|C|D');
const logs = path.join(workspace, 'build/architecture-v2');
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
  const prompt = `用户授权你负责 D:/majiang 的架构重构生产实现，Codex 负责设计、测试和最终审查。你是任务 ${task} 的唯一开发者。重要执行约束：本次 headless 会话的 command 权限未授权，绝对不要调用 RunCommand/终端/shell，包括 pwd、Get-Content、rg、mkdir、gradle。请仅使用内置文件读取、目录列表、搜索、写入和编辑工具完成开发。构建和测试由 Codex 执行；若文件工具也无法使用，明确报告阻塞，不要改权限或绕过限制。先完整阅读 doc/architecture-v2/DESIGN.md、TASKS.md、ACCEPTANCE.md，再完成 TASKS.md 中任务 ${task} 的全部生产实现。严格遵守独占文件范围；A/B/C 同时在同一工作区执行，禁止修改其他任务的文件。不要修改 core/src/test、设计文件、Gradle/依赖版本，不要删除或禁用测试。不要调用其他代理，不要替 Codex 写验收测试。现有 ArchitectureSafetyRegressionTest 是有意暴露旧缺陷的 RED 基线。第一批模块允许因为待集成接口而暂时无法全量编译，但必须完成真实实现，不要用 TODO、空操作或伪会话代替。公共新类型与其他任务有依赖时按 DESIGN.md 使用，不要抢占定义；若契约缺失，在交付里明确报告。实现完成后写 doc/architecture-v2/delivery/${task}.md，列修改、接口、偏差、自检、限制，并在最终回复给出摘要。不要改 TASKS.md 状态。开始执行，无需再次确认。`;
  const reviewFile = path.join(workspace, `doc/architecture-v2/review/${task}.md`);
  const review = fs.existsSync(reviewFile) ? `\n\n这是已交付模块的审查修订轮。保留已经实现的代码，优先完成以下 Codex 审查要求；修复完成后更新 delivery/${task}.md。不要把已有实现从头重写。\n${fs.readFileSync(reviewFile, 'utf8')}` : '';
  console.log(`${new Date().toISOString()} bridge ${task} started`);
  const result = await server.handleMessage({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: {
    name: task === 'status' ? 'query_agent_mesh_status' : 'invoke_antigravity',
    arguments: task === 'status' ? {} : { prompt: prompt + review + '\n\n必须读取 TASKS.md 最后“第二轮并行返修”章节，以它为本轮文件所有权边界。D 不要修改本轮属于 B/C 的文件。修复当前实现，不要从头实现已完成模块。INTEGRATION.md 固定了测试构造器和会话契约。所有测试由 Codex 执行，禁止终端和改测试。', workspacePath: workspace, mode: 'accept-edits' }
  }});
  fs.writeFileSync(path.join(logs, `bridge-${task}.json`), JSON.stringify(result, null, 2));
  console.log(`${new Date().toISOString()} bridge ${task} finished; result=build/architecture-v2/bridge-${task}.json`);
  if (result.error || result.result?.isError) process.exitCode = 1;
  if (task === 'status') console.log(JSON.stringify(result));
}
main().catch(e => { console.error(e.message); process.exitCode = 1; });
