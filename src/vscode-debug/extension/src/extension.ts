import * as vscode from 'vscode';
import { LuaCSharpDebugSession } from './adapter';

export function activate(context: vscode.ExtensionContext) {
  const type = 'lua-csharp';

  // Provide default debug configuration
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider(type, new LuaCSharpConfigProvider())
  );

  // Inline debug adapter implementation
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory(type, new InlineFactory())
  );

  // Command: Show Bytecode Viewer
  context.subscriptions.push(
    vscode.commands.registerCommand('lua-csharp.showBytecode', async () => {
      const ses = vscode.debug.activeDebugSession;
      if (!ses || ses.type !== type) {
        vscode.window.showInformationMessage('Start a Lua-CSharp debug session to show bytecode.');
        return;
      }
      try {
        await ses.customRequest('showBytecode');
      } catch (e: any) {
        vscode.window.showErrorMessage(`Show Bytecode failed: ${e?.message ?? e}`);
      }
    })
  );
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
