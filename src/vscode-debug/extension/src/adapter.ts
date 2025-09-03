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
            this.sendEvent(new OutputEvent(text));
            break;
          }
          case 'stopped': {
            const reason = msg.body?.reason || 'breakpoint';
            this.lastStopped = { file: msg.body?.file, line: msg.body?.line };
            // Invalidate previous locals reference and generate a new one for this stop
            this.localsRef = ++this.nextVarRef;
            this.globalsRef = ++this.nextVarRef;
            this.sendEvent(new StoppedEvent(reason, this.threadId));
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
}
