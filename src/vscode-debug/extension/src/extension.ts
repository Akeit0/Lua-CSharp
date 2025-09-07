import * as vscode from 'vscode';
import { LuaCSharpDebugSession } from './adapter';
import { panelHost } from './panelHost';

export function activate(context: vscode.ExtensionContext) {
  const type = 'lua-csharp';
  let statusItem: vscode.StatusBarItem | undefined;

  // Provide default debug configuration
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider(type, new LuaCSharpConfigProvider())
  );

  // Inline debug adapter implementation
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory(type, new InlineFactory())
  );

  // Command: Bytecode Viewer (toggle behavior)
  context.subscriptions.push(
    vscode.commands.registerCommand('lua-csharp.showBytecode', async () => {
      const ses = vscode.debug.activeDebugSession;
      if (!ses || ses.type !== type) {
        // Toggle placeholder when no session
        if (panelHost.isOpen()) panelHost.disposePanel();
        else panelHost.showPlaceholder();
        return;
      }
      // With active lua-csharp session, toggle live viewer via adapter
      try {
        await ses.customRequest('toggleBytecode');
      } catch (e: any) {
        vscode.window.showErrorMessage(`Bytecode Viewer toggle failed: ${e?.message ?? e}`);
      }
    })
  );

  // Command: Find prototype at cursor (does not auto-open viewer)
  context.subscriptions.push(
    vscode.commands.registerCommand('lua-csharp.findPrototypeAtCursor', async () => {
      const ses = vscode.debug.activeDebugSession;
      if (!ses || ses.type !== type) {
        vscode.window.showInformationMessage('Start a Lua-CSharp debug session to search prototypes.');
        return;
      }
      const editor = vscode.window.activeTextEditor;
      if (!editor) {
        vscode.window.showInformationMessage('Open a Lua file to use this command.');
        return;
      }
      const file = editor.document.uri.fsPath;
      const line = editor.selection.active.line + 1; // 1-based
      try {
        const res: any = await ses.customRequest('findPrototype', { file, line });
        if (!res || (res as any).error) {
          vscode.window.showInformationMessage('Prototype not found at this location.');
          return;
        }
        const chunk = (res as any).chunk || '(unknown)';
        const choice = await vscode.window.showInformationMessage(`Found function prototype in ${chunk}`, 'Open Bytecode');
        if (choice === 'Open Bytecode') {
          await ses.customRequest('showPrototypeAt', { file, line });
        }
      } catch (e: any) {
        vscode.window.showErrorMessage(`Find Prototype failed: ${e?.message ?? e}`);
      }
    })
  );

  // Command: Debugger Options (toggle StepOver granularity)
  context.subscriptions.push(
    vscode.commands.registerCommand('lua-csharp.debugOptions', async () => {
      const ses = vscode.debug.activeDebugSession;
      if (!ses || ses.type !== type) {
        vscode.window.showInformationMessage('Start a Lua-CSharp debug session to configure debugger options.');
        return;
      }
      try {
        const res: any = await ses.customRequest('getOptions');
        const current = String(res?.stepOverMode ?? 'Line');
        const items: vscode.QuickPickItem[] = [
          { label: current === 'Line' ? '$(check) Line' : '    Line', description: 'Step over per source line' },
          { label: current === 'Instruction' ? '$(check) Instruction' : '    Instruction', description: 'Step over per bytecode instruction' },
        ];
        const pick = await vscode.window.showQuickPick(items, { placeHolder: 'Step Over Granularity' });
        if (!pick) return;
        const mode = pick.label.includes('Instruction') ? 'Instruction' : 'Line';
        await ses.customRequest('setStepOverMode', { mode });
        vscode.window.showInformationMessage(`Step Over granularity set to ${mode}.`);
      } catch (e: any) {
        vscode.window.showErrorMessage(`Failed to update debugger options: ${e?.message ?? e}`);
      }
    })
  );

  // CodeLens: Offer clickable action above Lua function definitions
  const selector: vscode.DocumentSelector = [
    { language: 'lua', scheme: 'file' },
    { pattern: '**/*.lua' },
  ];
  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider(selector, new LuaFunctionCodeLensProvider())
  );

  // Auto-open the bytecode viewer when a Lua-CSharp debug session starts
  context.subscriptions.push(
    vscode.debug.onDidStartDebugSession(async (ses) => {
      if (ses.type !== type) return;
      // Only switch to live viewer if a panel is already open (placeholder or live)
      if (panelHost.isOpen()) {
        try { await ses.customRequest('showBytecode'); } catch {}
      }
    })
  );

  // Status bar toggle
  statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10000);
  statusItem.text = '$(list-unordered) Bytecode';
  statusItem.tooltip = 'Toggle Lua Bytecode Viewer';
  statusItem.command = 'lua-csharp.showBytecode';
  statusItem.show();
  context.subscriptions.push(statusItem);
}

export function deactivate() {}

class LuaCSharpConfigProvider implements vscode.DebugConfigurationProvider {
  resolveDebugConfiguration(
    _folder: vscode.WorkspaceFolder | undefined,
    config: vscode.DebugConfiguration
  ): vscode.ProviderResult<vscode.DebugConfiguration> {
    if (!config.type) config.type = 'lua-csharp';
    if (!config.name) config.name = 'Lua-CSharp: Launch';
    if (!config.request) config.request = 'launch';
    if (!config.program) {
      const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '';
      config.program = `${folder}/test.lua`;
    }
    if (!config.cwd) {
      const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '';
      config.cwd = `${folder}`;
    }
    if (config.stopOnEntry === undefined) config.stopOnEntry = true;
    return config;
  }
}

class InlineFactory implements vscode.DebugAdapterDescriptorFactory {
  createDebugAdapterDescriptor(
    _session: vscode.DebugSession
  ): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {
    return new vscode.DebugAdapterInlineImplementation(new LuaCSharpDebugSession());
  }
}

class LuaFunctionCodeLensProvider implements vscode.CodeLensProvider {
  private fnRegex = /^(\s*)(local\s+)?function\s+[a-zA-Z_][\w:]*/;
  onDidChangeCodeLenses?: vscode.Event<void> | undefined;

  provideCodeLenses(
    document: vscode.TextDocument,
    _token: vscode.CancellationToken
  ): vscode.ProviderResult<vscode.CodeLens[]> {
    const lenses: vscode.CodeLens[] = [];
    for (let i = 0; i < Math.min(document.lineCount, 5000); i++) {
      const line = document.lineAt(i);
      if (this.fnRegex.test(line.text)) {
        const range = new vscode.Range(i, 0, i, 0);
        lenses.push(
          new vscode.CodeLens(range, {
            title: 'Find Prototype',
            tooltip: 'Search running VM for this function\'s prototype',
            command: 'lua-csharp.findPrototypeAtCursor',
            arguments: [],
          })
        );
      }
    }
    return lenses;
  }
}
