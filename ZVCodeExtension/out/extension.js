"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const cp = __importStar(require("child_process"));
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const node_1 = require("vscode-languageclient/node");
let client;
const outputChannel = vscode.window.createOutputChannel('ZV');
function activate(context) {
    const executablePath = resolveCompilerPath();
    if (!executablePath) {
        vscode.window.showErrorMessage('Could not find the ZV compiler on PATH. Set "zv.executablePath" in your settings.');
        return;
    }
    startLanguageClient(executablePath);
    const compileCommand = vscode.commands.registerCommand('zv.compileCurrentFile', () => {
        compileCurrentFile(executablePath);
    });
    context.subscriptions.push(compileCommand, vscode.workspace.onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration('zv.executablePath')) {
            vscode.window.showInformationMessage('Reload the window for the new ZV executable path to take effect.');
        }
    }));
}
function deactivate() {
    outputChannel.dispose();
    if (!client) {
        return undefined;
    }
    return client.stop();
}
function resolveCompilerPath() {
    const config = vscode.workspace.getConfiguration('zv');
    const configuredPath = config.get('executablePath', null);
    if (configuredPath) {
        if (fs.existsSync(configuredPath)) {
            return configuredPath;
        }
        vscode.window.showWarningMessage(`Configured ZV executable "${configuredPath}" was not found. Falling back to PATH.`);
    }
    return findExecutableOnPath('ZV');
}
function startLanguageClient(executablePath) {
    const serverOptions = {
        command: executablePath,
        args: ['--lsp'],
        transport: node_1.TransportKind.stdio
    };
    const clientOptions = {
        documentSelector: [{ scheme: 'file', language: 'zv' }],
        synchronize: {
            fileEvents: vscode.workspace.createFileSystemWatcher('**/*.zv')
        }
    };
    client = new node_1.LanguageClient('zvLanguageServer', 'ZV Language Server', serverOptions, clientOptions);
    client.start();
}
function compileCurrentFile(executablePath) {
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
        }
        else {
            outputChannel.appendLine(`Compilation failed with exit code ${code}.`);
        }
    });
    proc.on('error', (err) => {
        vscode.window.showErrorMessage(`Failed to start ZV compiler: ${err.message}`);
    });
}
function findExecutableOnPath(name) {
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
//# sourceMappingURL=extension.js.map