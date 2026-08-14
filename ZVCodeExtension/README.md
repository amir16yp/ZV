# ZV Language Support

A VS Code extension for the [ZV programming language](https://github.com/amir16yp/ZV).

## Features

- Syntax highlighting for `.zv` files
- Language server diagnostics powered by the ZV compiler
- Configurable path to the ZV compiler executable
- Command palette / editor context menu action to compile the current `.zv` file
- Find All References (right-click an identifier) across the current file

## Requirements

- The ZV compiler executable must be on your `PATH` or configured via the `zv.executablePath` setting.
- The language server is started with `ZV --lsp` (stdio).

## Extension Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `zv.executablePath` | `string \| null` | `null` | Absolute path to the ZV compiler executable. When `null`, the extension searches `PATH` for `ZV` (or `ZV.exe` on Windows). |

## Development

```bash
cd ZVCodeExtension
npm install
npm run compile
```

To test locally, open this folder in VS Code and press `F5` to launch a new Extension Development Host window.

### Packaging

Build a `.vsix` installable archive:

```bash
npm run package
```

Install it from the command line with:

```bash
code --install-extension zvcode-0.1.0.vsix
```
