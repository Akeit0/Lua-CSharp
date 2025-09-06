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
import * as net from 'node:net';

export class LuaCSharpDebugSession extends LoggingDebugSession {
  private configurationDone = false;
  private threadId = 1;
  private proc?: ChildProcessWithoutNullStreams;
  private socket?: net.Socket;
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
  private upvaluesRef = 0;
  private panel?: vscode.WebviewPanel;
  private lastBytecodeChunk?: string;
  private pendingBps = new Map<string, { line: number; condition?: string; hitCondition?: string; logMessage?: string }[]>();
  private initializedSent = false;
  private currentFrameId = 1;
  private pauseToken = 0;
  private bytecodeCache = new Map<number, any>();
  private localsRefByFrame = new Map<number, number>();
  private upvaluesRefByFrame = new Map<number, number>();
  private frameByVarRef = new Map<number, { scope: 'locals' | 'upvalues'; frameId: number }>();

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
    response.body.supportsConditionalBreakpoints = true;
    response.body.supportsHitConditionalBreakpoints = true;
    response.body.supportsLogPoints = true;
    response.body.supportsSetVariable = true;
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
    this.initializedSent = true;
    this.flushPendingBreakpoints();

    // Save for later (after configurationDone)
    this.pendingLaunchArgs = { program: script, cwd, stopOnEntry: args.stopOnEntry };

    this.sendResponse(response);
  }

  protected async attachRequest(
    response: DebugProtocol.AttachResponse,
    args: DebugProtocol.AttachRequestArguments & { host: string; port: number; program?: string; cwd?: string; stopOnEntry?: boolean }
  ) {
    // Connect to running server via TCP
    const host = (args as any).host || '127.0.0.1';
    const port = Number((args as any).port || 4711);

    this.sendEvent(new OutputEvent(`[lua-csharp] Attaching to ${host}:${port}\n`));

    await new Promise<void>((resolve, reject) => {
      const sock = net.createConnection({ host, port }, () => {
        this.socket = sock;
        resolve();
      });
      sock.on('error', reject);
      const rl = readline.createInterface({ input: sock, crlfDelay: Infinity });
      rl.on('line', (line) => this.handleHostLine(line));
      sock.on('close', () => this.sendEvent(new TerminatedEvent()));
    });

    // Initialize the server side
    this.rpcSend({ method: 'initialize' });
    this.initializedSent = true;
    this.flushPendingBreakpoints();

    // If attach includes a program, defer launching until configurationDone
    const program = (args as any).program as string | undefined;
    const cwd = (args as any).cwd as string | undefined;
    const stopOnEntry = (args as any).stopOnEntry as boolean | undefined;
    if (program && program.length > 0) {
      this.pendingLaunchArgs = { program, cwd, stopOnEntry } as any;
      this.launched = false;
    }
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
    if (this.socket) {
      try { this.rpcSend({ method: 'terminate' }); } catch {}
      this.socket.end();
      this.socket.destroy();
      this.socket = undefined;
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
    const breakpoints = (args.breakpoints ?? []).map((b) => ({
      line: b.line,
      condition: (b as any).condition,
      hitCondition: (b as any).hitCondition,
      logMessage: (b as any).logMessage,
    }));
    const sourcePath = args.source?.path ?? '';
    this.queueOrSendBreakpoints(sourcePath, breakpoints);
    response.body = {
      breakpoints: breakpoints.map((bp) => ({ verified: true, line: bp.line })),
    } as any;
    this.sendResponse(response);
  }
  private queueOrSendBreakpoints(
    source: string,
    breakpoints: { line: number; condition?: string; hitCondition?: string; logMessage?: string }[]
  ) {
    if ((this.proc || this.socket) && this.initializedSent) {
      this.rpcSend({ method: 'setBreakpoints', params: { source, breakpoints } });
    } else {
      this.pendingBps.set(source, breakpoints);
    }
  }

  private flushPendingBreakpoints() {
    if (!(this.proc || this.socket) || !this.initializedSent) return;
    for (const [source, bps] of this.pendingBps) {
      this.rpcSend({ method: 'setBreakpoints', params: { source, breakpoints: bps } });
    }
    this.pendingBps.clear();
  }

  private workspaceDir(): string | undefined {
    // Best-effort: Node process CWD is fine for now
    return process.cwd();
  }

  // Config
  private shouldAutoOpenPanel(): boolean {
    try {
      return !!vscode.workspace.getConfiguration('luaCsharp').get('bytecodeViewer.openOnStop', false);
    } catch {
      return false;
    }
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
          this.upvaluesRef = ++this.nextVarRef;
          // New pause: reset frame selection and bytecode cache
          this.currentFrameId = 1;
          this.pauseToken++;
          this.bytecodeCache.clear();
          this.localsRefByFrame.clear();
          this.upvaluesRefByFrame.clear();
          this.frameByVarRef.clear();
          this.sendEvent(new StoppedEvent(reason, this.threadId));
            // Update bytecode viewer if open; otherwise open if configured
            if (this.panel) {
              this.renderBytecode();
            } else if (this.shouldAutoOpenPanel()) {
              this.ensurePanel();
              this.renderBytecode();
            }
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

  // Handle custom requests from VS Code commands
  protected customRequest(
    command: string,
    response: DebugProtocol.Response,
    _args: any
  ): void {
    if (command === 'showBytecode') {
      this.ensurePanel();
      this.renderBytecode();
      this.sendResponse(response);
      return;
    } else if (command === 'findPrototype') {
      // Proxy to server and return raw snapshot
      this.rpcCall('findPrototype', { file: _args?.file, line: _args?.line })
        .then((res) => {
          (response as any).body = res || {};
          this.sendResponse(response);
        })
        .catch((err) => {
          (response as any).body = { error: String(err) };
          this.sendResponse(response);
        });
      return;
    } else if (command === 'showPrototypeAt') {
      // Find and render prototype snapshot for file+line
      this.ensurePanel();
      this.rpcCall('findPrototype', { file: _args?.file, line: _args?.line })
        .then(async (res) => {
          if (!res) { this.sendResponse(response); return; }
          const stackRes = await this.rpcCall('getStack');
          const frames: { id: number; name: string; file: string; line: number }[] = stackRes?.frames ?? [];
          const chunk = (res?.chunk as string) || '';
          const pc = (res?.pc as number) ?? -1;
          const instr: { index: number; line: number; text: string; childIndex?: number }[] = res?.instructions ?? [];
          const constants: string[] = res?.constants ?? [];
          const locals: { name: string; startPc: number; endPc: number }[] = res?.locals ?? [];
          const upvalues: { name: string; isLocal: boolean; index: number }[] = res?.upvalues ?? [];
          this.lastBytecodeChunk = chunk;
          const bpsRes = await this.rpcCall('getInstrBreakpoints', { chunk });
          const indices: number[] = bpsRes?.breakpoints ?? [];
          const html = this.buildHtml(chunk, pc, instr, constants, locals, upvalues, new Set(indices));
          this.panel!.title = `Lua Bytecode: ${path.basename(chunk || 'function')}`;
          this.panel!.webview.html = html;
          this.sendResponse(response);
        })
        .catch((err) => {
          this.sendEvent(new OutputEvent(`[lua-csharp] showPrototypeAt error: ${err}\n`));
          this.sendResponse(response);
        });
      return;
    }
    super.customRequest(command, response, _args);
  }

  private rpcSend(payload: { method: string; params?: any }) {
    const id = String(this.nextId++);
    const msg = JSON.stringify({ id, method: payload.method, params: payload.params ?? {} }) + '\n';
    if (this.proc) this.proc.stdin.write(msg);
    else if (this.socket) this.socket.write(msg);
  }

  private rpcCall(method: string, params?: any): Promise<any> {
    if (!this.proc && !this.socket) return Promise.reject(new Error('no transport'));
    const id = String(this.nextId++);
    const promise = new Promise<any>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
    });
    const msg = JSON.stringify({ id, method, params: params ?? {} }) + '\n';
    if (this.proc) this.proc.stdin.write(msg);
    else this.socket!.write(msg);
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
    this.sendEvent(new OutputEvent(`[lua-csharp] stackTraceRequest start\n`));
    this.rpcCall('getStack')
      .then((res) => {
        const frames = (res?.frames ?? []) as { id: number; name: string; file: string; line: number }[];
        const sfs: DebugProtocol.StackFrame[] = frames.map((f) => ({
          id: f.id,
          name: f.name || 'Lua',
          line: f.line || 1,
          column: 1,
          source: f.file ? { path: f.file, name: path.basename(f.file) } : undefined,
        }));
        response.body = { stackFrames: sfs, totalFrames: sfs.length };
        this.sendResponse(response);
        this.sendEvent(new OutputEvent(`[lua-csharp] stackTraceRequest done: ${sfs.length} frames\n`));
      })
      .catch((err) => {
        this.sendEvent(new OutputEvent(`[lua-csharp] getStack error: ${err}\n`));
        // Fallback to last stopped location
        const sf: DebugProtocol.StackFrame[] = [];
        const file = this.lastStopped?.file;
        const line = this.lastStopped?.line ?? 1;
        if (file) {
          sf.push({ id: 1, name: 'Lua', line, column: 1, source: { path: file, name: path.basename(file) } });
        } else {
          sf.push({ id: 1, name: 'Lua', line: line, column: 1 });
        }
        response.body = { stackFrames: sf, totalFrames: sf.length };
        this.sendResponse(response);
        
      });
  }

  // Provide a single empty Locals scope for now
  protected scopesRequest(
    response: DebugProtocol.ScopesResponse,
    _args: DebugProtocol.ScopesArguments
  ): void {
    // Track selected frameId and allocate per-frame variable references
    const fid = ((_args as any)?.frameId as number) || 1;
    this.currentFrameId = fid;
    this.sendEvent(new OutputEvent(`[lua-csharp] scopesRequest frameId=${fid}\n`));

    let lref = this.localsRefByFrame.get(fid);
    if (!lref) {
      lref = ++this.nextVarRef;
      this.localsRefByFrame.set(fid, lref);
      this.frameByVarRef.set(lref, { scope: 'locals', frameId: fid });
    }
    let uref = this.upvaluesRefByFrame.get(fid);
    if (!uref) {
      uref = ++this.nextVarRef;
      this.upvaluesRefByFrame.set(fid, uref);
      this.frameByVarRef.set(uref, { scope: 'upvalues', frameId: fid });
    }
    response.body = {
      scopes: [
        {
          name: 'Locals',
          variablesReference: lref,
          expensive: false,
        },
        {
          name: 'Upvalues',
          variablesReference: uref,
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
    const ref = args.variablesReference;
    const mapping = this.frameByVarRef.get(ref);
    // Legacy top-frame references when VS Code didn't request new scopes
    if (ref === this.localsRef) {
      this.currentFrameId = 1;
      this.sendEvent(new OutputEvent(`[lua-csharp] variablesRequest locals (legacy top) frameId=1\n`));
      this.rpcCall('getLocals', { frameId: 1 })
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
      return;
    }
    if (ref === this.upvaluesRef) {
      this.currentFrameId = 1;
      this.sendEvent(new OutputEvent(`[lua-csharp] variablesRequest upvalues (legacy top) frameId=1\n`));
      this.rpcCall('getUpvalues', { frameId: 1 })
        .then((res) => {
          const vars = (res?.variables ?? []) as { name: string; value: string }[];
          response.body = {
            variables: vars.map((v) => ({ name: v.name, value: v.value, variablesReference: 0 })),
          };
          this.sendResponse(response);
          
        })
        .catch((err) => {
          this.sendEvent(new OutputEvent(`[lua-csharp] getUpvalues error: ${err}\n`));
          response.body = { variables: [] };
          this.sendResponse(response);
        });
      return;
    }

    if (mapping && mapping.scope === 'locals') {
      this.currentFrameId = mapping.frameId;
      this.sendEvent(new OutputEvent(`[lua-csharp] variablesRequest locals frameId=${mapping.frameId}\n`));
      this.rpcCall('getLocals', { frameId: mapping.frameId })
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
      // Treat globals as referring to the top frame when scopesRequest is skipped
      this.currentFrameId = 1;
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
    } else if (mapping && mapping.scope === 'upvalues') {
      this.currentFrameId = mapping.frameId;
      this.sendEvent(new OutputEvent(`[lua-csharp] variablesRequest upvalues frameId=${mapping.frameId}\n`));
      this.rpcCall('getUpvalues', { frameId: mapping.frameId })
        .then((res) => {
          const vars = (res?.variables ?? []) as { name: string; value: string }[];
          response.body = {
            variables: vars.map((v) => ({ name: v.name, value: v.value, variablesReference: 0 })),
          };
          this.sendResponse(response);
          if (this.panel) this.renderBytecode();
        })
        .catch((err) => {
          this.sendEvent(new OutputEvent(`[lua-csharp] getUpvalues error: ${err}\n`));
          response.body = { variables: [] };
          this.sendResponse(response);
        });
    } else {
      response.body = { variables: [] };
      this.sendResponse(response);
      
    }
  }

  protected setVariableRequest(
    response: DebugProtocol.SetVariableResponse,
    args: DebugProtocol.SetVariableArguments
  ): void {
    const name = args.name;
    const value = args.value ?? '';
    if (args.variablesReference === this.localsRef) {
      this.rpcCall('setLocal', { name, value })
        .then((res) => {
          response.body = { value: String(res?.value ?? value) } as any;
          this.sendResponse(response);
        })
        .catch((err) => {
          this.sendEvent(new OutputEvent(`[lua-csharp] setLocal error: ${err}\n`));
          response.body = { value } as any;
          this.sendResponse(response);
        });
      return;
    }
    if (args.variablesReference === this.upvaluesRef) {
      this.rpcCall('setUpvalue', { name, value })
        .then((res) => {
          response.body = { value: String(res?.value ?? value) } as any;
          this.sendResponse(response);
        })
        .catch((err) => {
          this.sendEvent(new OutputEvent(`[lua-csharp] setUpvalue error: ${err}\n`));
          response.body = { value } as any;
          this.sendResponse(response);
        });
      return;
    }
    // Not supported for other scopes
    response.body = { value } as any;
    this.sendResponse(response);
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
      if (msg && msg.cmd === 'toggleInstrBp') {
        if (!this.lastBytecodeChunk) return;
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
      } else if (msg && msg.cmd === 'selectFrame') {
        const fid = Number(msg.frameId);
        if (Number.isFinite(fid) && fid > 0) {
          this.currentFrameId = fid;
          await this.renderBytecode();
        }
      }
    });
  }

  private async renderBytecode() {
    if (!this.panel) return;
    try {
      const fid = this.currentFrameId || 1;
      console .log(`Rendering bytecode for frame ${fid}`);
      let res = this.bytecodeCache.get(fid);
      if (!res) {
        res = await this.rpcCall('getBytecode', { frameId: fid });
        this.bytecodeCache.set(fid, res);
      }
      const stackRes = await this.rpcCall('getStack');
      const frames: { id: number; name: string; file: string; line: number }[] = stackRes?.frames ?? [];
      const chunk = (res?.chunk as string) || '';
      const pc = (res?.pc as number) ?? -1;
      const instr: { index: number; line: number; text: string; childIndex?: number }[] = res?.instructions ?? [];
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
    instr: { index: number; line: number; text: string; childIndex?: number }[],
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
  const framesEl = document.getElementById('frames');
  if (framesEl) {
    framesEl.addEventListener('click', (ev) => {
      const t = ev.target as HTMLElement;
      if (!t || !t.classList || !t.classList.contains('frame')) return;
      const fidAttr = t.getAttribute('data-fid');
      if (!fidAttr) return;
      const fid = Number(fidAttr);
      if (!Number.isFinite(fid)) return;
      vscode.postMessage({ cmd: 'selectFrame', frameId: fid });
    });
  }
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
      `
<script>
  // Auto-scroll current instruction into view
  (function() {
    const scrollToCurrent = () => {
      const el = document.querySelector('.grid .row.current');
      if (el && el.scrollIntoView) {
        try { el.scrollIntoView({ block: 'center', behavior: 'smooth' }); }
        catch { el.scrollIntoView(); }
      }
    };
    if (document.readyState === 'complete' || document.readyState === 'interactive') {
      setTimeout(scrollToCurrent, 0);
    } else {
      window.addEventListener('DOMContentLoaded', scrollToCurrent, { once: true });
    }
  })();
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
