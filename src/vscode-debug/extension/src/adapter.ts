/*
  Minimal debug adapter skeleton. For now, it simulates a debug session
  and terminates quickly. Next iterations will proxy to the C# debug host.
*/
import {
  LoggingDebugSession,
  InitializedEvent,
  TerminatedEvent,
  StoppedEvent,
  OutputEvent,
} from '@vscode/debugadapter';
import { DebugProtocol } from '@vscode/debugprotocol';
import { spawn, ChildProcessWithoutNullStreams } from 'node:child_process';
import * as path from 'node:path';
import * as fs from 'node:fs';
import * as readline from 'node:readline';
import * as vscode from 'vscode';

export class LuaCSharpDebugSession extends LoggingDebugSession {
  private configurationDone = false;
  private threadId = 1;
  private proc?: ChildProcessWithoutNullStreams;
  private nextId = 1;
  private pendingLaunchArgs?: {
    program: string;
    cwd?: string;
    stopOnEntry?: boolean;
  };
  private launched = false;
  private lastStopped?: { file?: string; line?: number };
  private pending = new Map<string, { resolve: (v: any) => void; reject: (e: any) => void }>();
  private nextVarRef = 1;
  private localsRef = 0;
  private globalsRef = 0;
  private panel?: vscode.WebviewPanel;
  private lastBytecodeChunk?: string;

  public constructor() {
    super();
    // make VS Code happy with absent breakpoints until configured
    this.setDebuggerLinesStartAt1(true);
    this.setDebuggerColumnsStartAt1(true);
  }

  protected initializeRequest(
    response: DebugProtocol.InitializeResponse,
    _args: DebugProtocol.InitializeRequestArguments
  ): void {
    response.body = response.body || {};
    response.body.supportsConfigurationDoneRequest = true;
    response.body.supportsRestartRequest = true;
    response.body.supportsTerminateRequest = true;
    this.sendResponse(response);
    this.sendEvent(new InitializedEvent());
  }

  protected configurationDoneRequest(
    response: DebugProtocol.ConfigurationDoneResponse,
    _args: DebugProtocol.ConfigurationDoneArguments
  ) {
    this.configurationDone = true;
    this.sendResponse(response);
    if (!this.launched && this.pendingLaunchArgs) {
      this.launched = true;
      this.rpcSend({ method: 'launch', params: this.pendingLaunchArgs });
    }
  }

  protected async launchRequest(
    response: DebugProtocol.LaunchResponse,
    args: DebugProtocol.LaunchRequestArguments & {
      program: string;
      cwd?: string;
      stopOnEntry?: boolean;
    }
  ) {
    // Spawn the C# debug host in stdio mode
    const cwd = args.cwd ?? process.cwd();
    const script = args.program;
    const projectPath = this.resolveDebugServerProject();
    const dotnetArgs = [
      'run',
      '--project',
      projectPath,
      '--',
      '--stdio',
      '--cwd',
      cwd,
      '--script',
      script,
    ];

    this.sendEvent(
      new OutputEvent(`[lua-csharp] Spawning: dotnet ${dotnetArgs.join(' ')}\n`)
    );

    this.proc = spawn('dotnet', dotnetArgs, { cwd });

    const rl = readline.createInterface({
      input: this.proc.stdout,
      crlfDelay: Infinity,
    });
    rl.on('line', (line) => this.handleHostLine(line));
    this.proc.stderr.on('data', (buf: Buffer) => {
      this.sendEvent(new OutputEvent(String(buf)));
    });
    this.proc.on('exit', () => {
      this.sendEvent(new TerminatedEvent());
    });

    // Kick the host with initialize; VS Code will send setBreakpoints, then configurationDone
    this.rpcSend({ method: 'initialize' });

    // Save for later (after configurationDone)
    this.pendingLaunchArgs = { program: script, cwd, stopOnEntry: args.stopOnEntry };

    this.sendResponse(response);
  }

  protected disconnectRequest(
    response: DebugProtocol.DisconnectResponse,
    _args: DebugProtocol.DisconnectArguments
  ): void {
    if (this.proc && !this.proc.killed) {
      this.rpcSend({ method: 'terminate' });
      this.proc.kill();
    }
    this.sendResponse(response);
  }

  protected continueRequest(
    response: DebugProtocol.ContinueResponse,
    _args: DebugProtocol.ContinueArguments
  ): void {
    this.rpcSend({ method: 'continue' });
    this.sendResponse(response);
  }

  protected nextRequest(
    response: DebugProtocol.NextResponse,
    _args: DebugProtocol.NextArguments
  ): void {
    this.rpcCall('next').finally(() => this.sendResponse(response));
  }

  protected stepInRequest(
    response: DebugProtocol.StepInResponse,
    _args: DebugProtocol.StepInArguments
  ): void {
    this.rpcCall('stepIn').finally(() => this.sendResponse(response));
  }

  protected stepOutRequest(
    response: DebugProtocol.StepOutResponse,
    _args: DebugProtocol.StepOutArguments
  ): void {
    this.rpcCall('stepOut').finally(() => this.sendResponse(response));
  }

  protected setBreakPointsRequest(
    response: DebugProtocol.SetBreakpointsResponse,
    args: DebugProtocol.SetBreakpointsArguments
  ): void {
    const lines = (args.breakpoints ?? []).map((b) => b.line);
    const sourcePath = args.source?.path ?? '';
    this.rpcSend({ method: 'setBreakpoints', params: { source: sourcePath, lines } });
    response.body = {
      breakpoints: lines.map((l) => ({ verified: true, line: l })),
    } as any;
    this.sendResponse(response);
  }

  private workspaceDir(): string | undefined {
    // Best-effort: Node process CWD is fine for now
    return process.cwd();
  }

  private handleHostLine(line: string) {
    try {
      const msg = JSON.parse(line);
      if (msg.type === 'event') {
        switch (msg.event) {
          case 'initialized':
            // Host's own initialized event is ignored; the adapter is the DAP endpoint
            break;
          case 'output': {
            const body = msg.body || {};
            const text = (body.output ?? '') as string;
            // category 
            // important|stdout|stderr|console
            this.sendEvent(new OutputEvent(text, body.category));
            break;
          }
          case 'stopped': {
            const reason = msg.body?.reason || 'breakpoint';
            this.lastStopped = { file: msg.body?.file, line: msg.body?.line };
            // Invalidate previous locals reference and generate a new one for this stop
            this.localsRef = ++this.nextVarRef;
            this.globalsRef = ++this.nextVarRef;
            this.sendEvent(new StoppedEvent(reason, this.threadId));
            // Update bytecode viewer
            this.ensurePanel();
            this.renderBytecode();
            break;
          }
          case 'terminated':
            this.sendEvent(new TerminatedEvent());
            break;
        }
      } else if (msg.type === 'response' && msg.id) {
        const handler = this.pending.get(String(msg.id));
        if (handler) {
          this.pending.delete(String(msg.id));
          if (msg.error) handler.reject(msg.error);
          else handler.resolve(msg.result);
        }
      }
    } catch (e) {
      this.sendEvent(
        new OutputEvent(`[lua-csharp] host message parse error: ${e}\n`)
      );
    }
  }

  private rpcSend(payload: { method: string; params?: any }) {
    if (!this.proc) return;
    const id = String(this.nextId++);
    const msg = JSON.stringify({ id, method: payload.method, params: payload.params ?? {} });
    this.proc.stdin.write(msg + '\n');
  }

  private rpcCall(method: string, params?: any): Promise<any> {
    if (!this.proc) return Promise.reject(new Error('no process'));
    const id = String(this.nextId++);
    const promise = new Promise<any>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
    });
    const msg = JSON.stringify({ id, method, params: params ?? {} });
    this.proc!.stdin.write(msg + '\n');
    return promise;
  }

  private resolveDebugServerProject(): string {
    // Compute absolute path to repo's C# debug server: <repo>/src/Lua.DebugServer
    // __dirname is <repo>/src/vscode-debug/extension/dist
    const repoRoot = path.resolve(__dirname, '../../../../');
    const candidate = path.join(repoRoot, 'src', 'Lua.DebugServer');
    return candidate;
  }

  // DAP: report a single thread so VS Code can display stop state
  protected threadsRequest(
    response: DebugProtocol.ThreadsResponse
  ): void {
    response.body = {
      threads: [{ id: this.threadId, name: 'Lua Main Thread' }],
    };
    this.sendResponse(response);
  }

  // Minimal stack frame based on last stopped location
  protected stackTraceRequest(
    response: DebugProtocol.StackTraceResponse,
    args: DebugProtocol.StackTraceArguments
  ): void {
    const sf: DebugProtocol.StackFrame[] = [];
    const file = this.lastStopped?.file;
    const line = this.lastStopped?.line ?? 1;
    if (file) {
      sf.push({
        id: 1,
        name: 'Lua',
        line,
        column: 1,
        source: { path: file, name: path.basename(file) },
      });
    } else {
      sf.push({ id: 1, name: 'Lua', line: line, column: 1 });
    }
    response.body = { stackFrames: sf, totalFrames: sf.length };
    this.sendResponse(response);
  }

  // Provide a single empty Locals scope for now
  protected scopesRequest(
    response: DebugProtocol.ScopesResponse,
    _args: DebugProtocol.ScopesArguments
  ): void {
    response.body = {
      scopes: [
        {
          name: 'Locals',
          variablesReference: this.localsRef,
          expensive: false,
        },
        {
          name: 'Globals',
          variablesReference: this.globalsRef,
          expensive: true,
        },
      ],
    };
    this.sendResponse(response);
  }

  protected variablesRequest(
    response: DebugProtocol.VariablesResponse,
    args: DebugProtocol.VariablesArguments
  ): void {
    if (args.variablesReference === this.localsRef) {
      this.rpcCall('getLocals')
        .then((res) => {
          const vars = (res?.variables ?? []) as { name: string; value: string }[];
          response.body = {
            variables: vars.map((v) => ({ name: v.name, value: v.value, variablesReference: 0 })),
          };
          this.sendResponse(response);
        })
        .catch((err) => {
          this.sendEvent(new OutputEvent(`[lua-csharp] getLocals error: ${err}\n`));
          response.body = { variables: [] };
          this.sendResponse(response);
        });
    } else if (args.variablesReference === this.globalsRef) {
      this.rpcCall('getGlobals')
        .then((res) => {
          const vars = (res?.variables ?? []) as { name: string; value: string }[];
          response.body = {
            variables: vars.map((v) => ({ name: v.name, value: v.value, variablesReference: 0 })),
          };
          this.sendResponse(response);
        })
        .catch((err) => {
          this.sendEvent(new OutputEvent(`[lua-csharp] getGlobals error: ${err}\n`));
          response.body = { variables: [] };
          this.sendResponse(response);
        });
    } else {
      response.body = { variables: [] };
      this.sendResponse(response);
    }
  }

  // Bytecode webview helpers
  private ensurePanel() {
    if (this.panel) {
      return;
    }
    this.panel = vscode.window.createWebviewPanel(
      'luaBytecode',
      'Lua Bytecode',
      vscode.ViewColumn.Beside,
      { enableFindWidget: true, enableScripts: true }
    );
    this.panel.onDidDispose(() => {
      this.panel = undefined;
    });
    this.panel.webview.onDidReceiveMessage(async (msg) => {
      if (!this.lastBytecodeChunk) return;
      if (msg && msg.cmd === 'toggleInstrBp') {
        const index = Number(msg.index);
        const has = !!msg.has;
        try {
          await this.rpcCall('setInstrBreakpoint', {
            chunk: this.lastBytecodeChunk,
            index,
            enabled: !has,
          });
          await this.renderBytecode();
        } catch (e) {
          this.sendEvent(new OutputEvent(`[lua-csharp] setInstrBreakpoint error: ${e}\n`));
        }
      }
    });
  }

  private async renderBytecode() {
    if (!this.panel) return;
    try {
      const res = await this.rpcCall('getBytecode');
      const chunk = (res?.chunk as string) || '';
      const pc = (res?.pc as number) ?? -1;
      const instr: { index: number; line: number; text: string }[] = res?.instructions ?? [];
      const constants: string[] = res?.constants ?? [];
      const locals: { name: string; startPc: number; endPc: number }[] = res?.locals ?? [];
      const upvalues: { name: string; isLocal: boolean; index: number }[] = res?.upvalues ?? [];
      this.lastBytecodeChunk = chunk;
      const bpsRes = await this.rpcCall('getInstrBreakpoints', { chunk });
      const indices: number[] = bpsRes?.breakpoints ?? [];
      const html = this.buildHtml(chunk, pc, instr, constants, locals, upvalues, new Set(indices));
      this.panel.title = `Lua Bytecode: ${path.basename(chunk || 'current')}`;
      this.panel.webview.html = html;
    } catch (err) {
      this.sendEvent(new OutputEvent(`[lua-csharp] getBytecode error: ${err}\n`));
    }
  }

  private buildHtml(
    chunk: string,
    pc: number,
    instr: { index: number; line: number; text: string }[],
    constants: string[],
    locals: { name: string; startPc: number; endPc: number }[],
    upvalues: { name: string; isLocal: boolean; index: number }[],
    bpSet: Set<number>
  ): string {
    const rows = instr
      .map((i) => {
        const has = bpSet.has(i.index);
        const cls = i.index === pc ? 'row current' : has ? 'row bp' : 'row';
        const ln = i.line ? String(i.line) : '';
        const idx = String(i.index);
        const text = this.escapeHtml(i.text);
        const marker = has ? '<span class="mark">●</span>' : '<span class="mark">○</span>';
        return `<div class="${cls}" data-idx="${idx}" data-has="${has ? 1 : 0}"><span class="col idx">${marker} [${idx}]</span><span class="col line">${ln}</span><span class="col text">${text}</span></div>`;
      })
      .join('');

    const constRows = constants
      .map((c, i) => {
        return `<div class="row"><span class="col idx">[${i}]</span><span class="col text" style="grid-column: span 2;">${this.escapeHtml(String(c))}</span></div>`;
      })
      .join('');

    const localsRows = locals
      .map((l, i) => {
        return `<div class="row"><span class="col idx">[${i}]</span><span class="col name">${this.escapeHtml(l.name)}</span><span class="col meta">${l.startPc} .. ${l.endPc}</span></div>`;
      })
      .join('');

    const upvalRows = upvalues
      .map((u, i) => {
        const il = u.isLocal ? 1 : 0;
        return `<div class="row"><span class="col idx">[${i}]</span><span class="col name">${this.escapeHtml(u.name)}</span><span class="col meta">${il} \t ${u.index}</span></div>`;
      })
      .join('');
    const head = this.escapeHtml(chunk || '');
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <style>
    body { font-family: var(--vscode-editor-font-family, ui-monospace, monospace); font-size: 12px; }
    .header { margin: 6px 0 8px; color: var(--vscode-foreground); }
    .grid { border-top: 1px solid var(--vscode-editorWidget-border); }
    .row { display: grid; grid-template-columns: 80px 60px 1fr; padding: 2px 6px; border-bottom: 1px solid var(--vscode-editorWidget-border); }
    .row:nth-child(even) { background: var(--vscode-editorInlayHint-background, transparent); }
    .row.current { background: var(--vscode-editor-findMatchHighlightBackground, #fffbcc); }
    .col.idx { color: var(--vscode-descriptionForeground); }
    .col.line { color: var(--vscode-descriptionForeground); }
    .col.text { white-space: pre; }
    .row .mark { display: inline-block; width: 10px; color: var(--vscode-charts-red, #c33); margin-right: 4px; }
    .row.bp .mark { color: var(--vscode-charts-red, #c33); }
    .row:not(.bp) .mark { color: var(--vscode-descriptionForeground); }
    .hint { color: var(--vscode-descriptionForeground); font-style: italic; margin: 6px; }
    .section { margin-top: 10px; }
    .title { margin: 8px 0 4px; font-weight: bold; color: var(--vscode-foreground); }
    .grid.consts .row { grid-template-columns: 80px 1fr; }
    .grid.locals .row { grid-template-columns: 80px 1fr 1fr; }
    .grid.upvals .row { grid-template-columns: 80px 1fr 1fr; }
  </style>
  <title>Lua Bytecode</title>
  </head>
<body>
  <div class="header">${head}</div>
  <div class="grid">${rows}</div>
  <div class="hint">Click a row to toggle an instruction breakpoint.</div>
  <div class="section">
    <div class="title">Constants</div>
    <div class="grid consts">${constRows}</div>
  </div>
  <div class="section">
    <div class="title">Local Variables</div>
    <div class="grid locals">${localsRows}</div>
  </div>
  <div class="section">
    <div class="title">UpValues</div>
    <div class="grid upvals">${upvalRows}</div>
  </div>
</body>
</html>` +
      `
<script>
  const vscode = acquireVsCodeApi();
  document.querySelectorAll('.grid .row').forEach((row) => {
    row.addEventListener('click', () => {
      const idxAttr = row.getAttribute('data-idx');
      if (idxAttr === null) return; // only instruction rows have data-idx
      const idxNum = Number(idxAttr);
      if (!Number.isFinite(idxNum)) return;
      const has = row.getAttribute('data-has') === '1';
      vscode.postMessage({ cmd: 'toggleInstrBp', index: idxNum, has });
    });
  });
</script>`;
  }

  private escapeHtml(s: string): string {
    return s
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
  }

  // New version that understands patched instructions
  private buildHtml2(
    chunk: string,
    pc: number,
    instr: { index: number; line: number; text: string; patched?: boolean; patchKind?: string; origText?: string }[]
  ): string {
    const rows = instr
      .map((i) => {
        const cls = i.index === pc ? 'row current' : 'row';
        const ln = i.line ? String(i.line) : '';
        const idx = String(i.index);
        const text = this.escapeHtml(i.text);
        const badge = i.patched
          ? `<span class="badge ${i.patchKind === 'step' ? 'step' : 'bp'}">${i.patchKind === 'step' ? '[STEP]' : '[BP]'}</span>`
          : '';
        const orig = i.patched && i.origText
          ? `<span class="orig"> → ${this.escapeHtml(i.origText)}</span>`
          : '';
        return `<div class="${cls}" data-idx="${idx}"><span class="col idx">[${idx}]</span><span class="col line">${ln}</span><span class="col text">${badge} ${text}${orig}</span></div>`;
      })
      .join('');
    const head = this.escapeHtml(chunk || '');
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <style>
    body { font-family: var(--vscode-editor-font-family, ui-monospace, monospace); font-size: 12px; }
    .header { margin: 6px 0 8px; color: var(--vscode-foreground); }
    .grid { border-top: 1px solid var(--vscode-editorWidget-border); }
    .row { display: grid; grid-template-columns: 80px 60px 1fr; padding: 2px 6px; border-bottom: 1px solid var(--vscode-editorWidget-border); }
    .row:nth-child(even) { background: var(--vscode-editorInlayHint-background, transparent); }
    .row.current { background: var(--vscode-editor-findMatchHighlightBackground, #fffbcc); }
    .col.idx { color: var(--vscode-descriptionForeground); }
    .col.line { color: var(--vscode-descriptionForeground); }
    .col.text { white-space: pre; }
    .badge { display: inline-block; margin-right: 6px; padding: 0 4px; border-radius: 3px; font-size: 10px; color: var(--vscode-editor-foreground); background: var(--vscode-editorInlayHint-background, #444); }
    .badge.bp { background: var(--vscode-charts-red, #c33); color: #fff; }
    .badge.step { background: var(--vscode-charts-blue, #36f); color: #fff; }
    .orig { color: var(--vscode-descriptionForeground); }
  </style>
  <title>Lua Bytecode</title>
  </head>
<body>
  <div class="header">${head}</div>
  <div class="grid">${rows}</div>
</body>
</html>`;
  }
}
