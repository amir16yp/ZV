import * as cp from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    Executable,
    TransportKind
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;
const outputChannel = vscode.window.createOutputChannel('ZV');

export function activate(context: vscode.ExtensionContext): void {
    const executablePath = resolveCompilerPath();

    if (!executablePath) {
        vscode.window.showErrorMessage(
            'Could not find the ZV compiler on PATH. Set "zv.executablePath" in your settings.'
        );
        return;
    }

    startLanguageClient(executablePath);

    const compileCommand = vscode.commands.registerCommand('zv.compileCurrentFile', () => {
        compileCurrentFile(executablePath);
    });

    context.subscriptions.push(
        compileCommand,
        vscode.workspace.onDidChangeConfiguration((e) => {
            if (e.affectsConfiguration('zv.executablePath')) {
                vscode.window.showInformationMessage(
                    'Reload the window for the new ZV executable path to take effect.'
                );
            }
        })
    );
}

export function deactivate(): Thenable<void> | undefined {
    outputChannel.dispose();
    if (!client) {
        return undefined;
    }
    return client.stop();
}

function resolveCompilerPath(): string | undefined {
    const config = vscode.workspace.getConfiguration('zv');
    const configuredPath: string | null | undefined = config.get<string | null>('executablePath', null);

    if (configuredPath) {
        if (fs.existsSync(configuredPath)) {
            return configuredPath;
        }
        vscode.window.showWarningMessage(
            `Configured ZV executable "${configuredPath}" was not found. Falling back to PATH.`
        );
    }

    return findExecutableOnPath('ZV');
}

function startLanguageClient(executablePath: string): void {
    const serverOptions: ServerOptions = {
        command: executablePath,
        args: ['--lsp'],
        transport: TransportKind.stdio
    } as Executable;

    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'zv' }],
        synchronize: {
            fileEvents: vscode.workspace.createFileSystemWatcher('**/*.zv')
        }
    };

    client = new LanguageClient(
        'zvLanguageServer',
        'ZV Language Server',
        serverOptions,
        clientOptions
    );

    client.start();
}

function compileCurrentFile(executablePath: string): void {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== 'zv') {
        vscode.window.showWarningMessage('Open a .zv file to compile.');
        return;
    }

    const filePath = editor.document.fileName;
    const workspaceFolder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
    const cwd = workspaceFolder ? workspaceFolder.uri.fsPath : path.dirname(filePath);

    outputChannel.clear();
    outputChannel.appendLine(`> ZV "${filePath}"`);
    outputChannel.show(true);

    const proc = cp.spawn(executablePath, [filePath], { cwd });

    proc.stdout.on('data', (data) => {
        outputChannel.append(data.toString());
    });

    proc.stderr.on('data', (data) => {
        outputChannel.append(data.toString());
    });

    proc.on('close', (code) => {
        if (code === 0) {
            outputChannel.appendLine('Compilation succeeded.');
        } else {
            outputChannel.appendLine(`Compilation failed with exit code ${code}.`);
        }
    });

    proc.on('error', (err) => {
        vscode.window.showErrorMessage(`Failed to start ZV compiler: ${err.message}`);
    });
}

function findExecutableOnPath(name: string): string | undefined {
    const candidates = process.platform === 'win32'
        ? [`${name}.exe`, `${name}.cmd`, `${name}.bat`]
        : [name];

    const paths = (process.env.PATH || '').split(path.delimiter);

    for (const p of paths) {
        for (const candidate of candidates) {
            const fullPath = path.join(p, candidate);
            if (fs.existsSync(fullPath)) {
                return fullPath;
            }
        }
    }

    return undefined;
}
