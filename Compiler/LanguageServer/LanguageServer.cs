using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZV.Compiler.AST;
using ZvLexer = ZV.Compiler.Lexer.Lexer;
using ZvParser = ZV.Compiler.Parser.Parser;
using SystemTextJson = System.Text.Json;

namespace ZV.Compiler.LanguageServer;

public class LanguageServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Dictionary<string, DocumentState> _documents = new();
    private readonly Dictionary<string, HashSet<string>> _dependents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _documentIncludes = new();
    private bool _shutdownRequested;

    public LanguageServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
        {
            var message = await LspJsonRpc.ReadMessageAsync(_input, cancellationToken);
            if (message == null)
            {
                break;
            }

            await HandleMessageAsync(message);
        }
    }

    private async Task HandleMessageAsync(LspMessage message)
    {
        switch (message.Method)
        {
            case "initialize":
                await SendResponseAsync(message.Id, new InitializeResult
                {
                    Capabilities = new ServerCapabilities
                    {
                        TextDocumentSync = new TextDocumentSyncOptions
                        {
                            OpenClose = true,
                            Change = 1 // Full document sync
                        },
                        ReferencesProvider = true,
                        DefinitionProvider = true
                    }
                });
                break;

            case "initialized":
                // No response required.
                break;

            case "textDocument/didOpen":
                HandleDidOpen(message.Params);
                break;

            case "textDocument/didChange":
                HandleDidChange(message.Params);
                break;

            case "textDocument/didClose":
                HandleDidClose(message.Params);
                break;

            case "textDocument/references":
                await HandleReferencesAsync(message);
                break;

            case "textDocument/definition":
                await HandleDefinitionAsync(message);
                break;

            case "shutdown":
                _shutdownRequested = true;
                await SendResponseAsync(message.Id, null);
                break;

            case "exit":
                Environment.Exit(0);
                break;
        }
    }

    private void HandleDidOpen(object? paramsObj)
    {
        if (paramsObj is not SystemTextJson.JsonElement element)
        {
            return;
        }

        var doc = element.GetProperty("textDocument");
        var uri = doc.GetProperty("uri").GetString() ?? string.Empty;
        var text = doc.GetProperty("text").GetString() ?? string.Empty;

        var filePath = UriToFilePath(uri);
        var state = new DocumentState
        {
            Uri = uri,
            FilePath = filePath,
            Text = text,
            FileProvider = ResolveIncludedFile
        };

        _documents[uri] = state;
        state.Rebuild();
        UpdateIncludeGraph(uri, state);
        PublishDiagnostics(uri, state);
    }

    private void HandleDidChange(object? paramsObj)
    {
        if (paramsObj is not SystemTextJson.JsonElement element)
        {
            return;
        }

        var doc = element.GetProperty("textDocument");
        var uri = doc.GetProperty("uri").GetString() ?? string.Empty;
        var changes = element.GetProperty("contentChanges");

        if (!_documents.TryGetValue(uri, out var state))
        {
            var filePath = UriToFilePath(uri);
            state = new DocumentState
            {
                Uri = uri,
                FilePath = filePath,
                FileProvider = ResolveIncludedFile
            };
            _documents[uri] = state;
        }

        foreach (var change in changes.EnumerateArray())
        {
            if (change.TryGetProperty("text", out var textProp))
            {
                state.Text = textProp.GetString() ?? string.Empty;
            }
        }

        state.Rebuild();
        UpdateIncludeGraph(uri, state);
        PublishDiagnostics(uri, state);

        if (!string.IsNullOrEmpty(state.FilePath))
        {
            RebuildDependents(state.FilePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void HandleDidClose(object? paramsObj)
    {
        if (paramsObj is not SystemTextJson.JsonElement element)
        {
            return;
        }

        var doc = element.GetProperty("textDocument");
        var uri = doc.GetProperty("uri").GetString() ?? string.Empty;

        if (_documents.TryGetValue(uri, out _))
        {
            // Remove this document from the include graph before deleting it.
            UpdateIncludeGraph(uri, new DocumentState { IncludedFilePaths = new HashSet<string>() });
            _documents.Remove(uri);
        }

        PublishDiagnostics(uri, Array.Empty<DiagnosticItem>());
    }

    private async Task HandleReferencesAsync(LspMessage message)
    {
        if (message.Params is not SystemTextJson.JsonElement element)
        {
            await SendResponseAsync(message.Id, Array.Empty<Location>());
            return;
        }

        var textDocument = element.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString() ?? string.Empty;
        var position = element.GetProperty("position");
        var line = position.GetProperty("line").GetInt32();
        var character = position.GetProperty("character").GetInt32();

        bool includeDeclaration = true;
        if (element.TryGetProperty("context", out var contextElement))
        {
            if (contextElement.TryGetProperty("includeDeclaration", out var includeDeclElement))
            {
                includeDeclaration = includeDeclElement.GetBoolean();
            }
        }

        var locations = FindReferences(uri, new Position { Line = line, Character = character }, includeDeclaration);
        await SendResponseAsync(message.Id, locations);
    }

    private async Task HandleDefinitionAsync(LspMessage message)
    {
        if (message.Params is not SystemTextJson.JsonElement element)
        {
            await SendResponseAsync(message.Id, Array.Empty<Location>());
            return;
        }

        var textDocument = element.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString() ?? string.Empty;
        var position = element.GetProperty("position");
        var line = position.GetProperty("line").GetInt32();
        var character = position.GetProperty("character").GetInt32();

        var locations = FindDefinition(uri, new Position { Line = line, Character = character });
        await SendResponseAsync(message.Id, locations);
    }

    private List<Location> FindReferences(string uri, Position position, bool includeDeclaration)
    {
        if (!_documents.TryGetValue(uri, out var state))
        {
            return new List<Location>();
        }

        var best = FindBestOccurrenceAtPosition(state, position);
        if (best == null)
        {
            return new List<Location>();
        }

        var locations = new List<Location>();
        foreach (var occ in state.SymbolIndex.Occurrences)
        {
            if (!string.Equals(occ.Name, best.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (occ.IsType != best.IsType)
            {
                continue;
            }

            if (!includeDeclaration && occ.Kind == SymbolKind.Declaration)
            {
                continue;
            }

            locations.Add(CreateLocation(occ, uri));
        }

        return locations;
    }

    private List<Location> FindDefinition(string uri, Position position)
    {
        if (!_documents.TryGetValue(uri, out var state))
        {
            return new List<Location>();
        }

        // First check whether the cursor is on an #include path. If so, go to that file.
        var includeTarget = FindIncludeTargetAtPosition(state, position);
        if (includeTarget != null)
        {
            return new List<Location>
            {
                new Location
                {
                    Uri = FilePathToUri(includeTarget),
                    Range = new Range
                    {
                        Start = new Position { Line = 0, Character = 0 },
                        End = new Position { Line = 0, Character = 0 }
                    }
                }
            };
        }

        var best = FindBestOccurrenceAtPosition(state, position);
        if (best == null)
        {
            return new List<Location>();
        }

        var declaration = state.SymbolIndex.Occurrences
            .FirstOrDefault(occ => string.Equals(occ.Name, best.Name, StringComparison.Ordinal)
                                   && occ.IsType == best.IsType
                                   && occ.Kind == SymbolKind.Declaration);

        if (declaration == null)
        {
            return new List<Location>();
        }

        return new List<Location> { CreateLocation(declaration, uri) };
    }

    private string? FindIncludeTargetAtPosition(DocumentState state, Position position)
    {
        if (state.FilePath == null) return null;

        var lines = state.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (position.Line < 0 || position.Line >= lines.Length) return null;

        string line = lines[position.Line];
        var match = System.Text.RegularExpressions.Regex.Match(line, "^\\s*#include\\s*\"([^\"]+)\"|^\\s*#include\\s*<([^>]+)>");
        if (!match.Success) return null;

        string includePath;
        bool systemInclude;
        int pathStart;
        if (match.Groups[1].Success)
        {
            includePath = match.Groups[1].Value;
            systemInclude = false;
            pathStart = match.Groups[1].Index;
        }
        else
        {
            includePath = match.Groups[2].Value;
            systemInclude = true;
            pathStart = match.Groups[2].Index;
        }

        int pathEnd = pathStart + includePath.Length;
        if (position.Character < pathStart || position.Character > pathEnd)
        {
            return null;
        }

        return ZvLexer.ResolveIncludePath(includePath, systemInclude, state.FilePath, ZvLexer.GetDefaultSystemIncludePaths());
    }

    private SymbolOccurrence? FindBestOccurrenceAtPosition(DocumentState state, Position position)
    {
        if (state.SymbolIndex.Occurrences.Count == 0)
        {
            return null;
        }

        int targetLine = position.Line + 1;
        int targetColumn = position.Character + 1;
        string? targetFile = state.FilePath;

        SymbolOccurrence? best = null;
        int bestDistance = int.MaxValue;

        foreach (var occ in state.SymbolIndex.Occurrences)
        {
            if (targetFile != null &&
                !string.Equals(occ.Location.File, targetFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int lineDistance = Math.Abs(occ.Location.Line - targetLine);
            int columnDistance = Math.Abs(occ.Location.Column - targetColumn);
            int distance = lineDistance * 1000 + columnDistance;

            if (distance < bestDistance)
            {
                best = occ;
                bestDistance = distance;
            }
        }

        return best;
    }

    private Location CreateLocation(SymbolOccurrence occ, string defaultUri)
    {
        string targetUri = string.IsNullOrEmpty(occ.Location.File)
            ? defaultUri
            : FilePathToUri(occ.Location.File);

        int startLine = Math.Max(0, occ.Location.Line - 1);
        int startColumn = Math.Max(0, occ.Location.Column - 1);
        int endLine = startLine;
        int endColumn = Math.Max(startColumn, startColumn + occ.Name.Length - 1);

        return new Location
        {
            Uri = targetUri,
            Range = new Range
            {
                Start = new Position { Line = startLine, Character = startColumn },
                End = new Position { Line = endLine, Character = endColumn }
            }
        };
    }

    private string? ResolveIncludedFile(string fullPath)
    {
        string uri = FilePathToUri(fullPath);
        if (_documents.TryGetValue(uri, out var state))
        {
            return state.Text;
        }

        if (File.Exists(fullPath))
        {
            return File.ReadAllText(fullPath);
        }

        return null;
    }

    private void RebuildDependents(string changedFilePath, HashSet<string> visited)
    {
        if (!visited.Add(changedFilePath))
        {
            return;
        }

        if (!_dependents.TryGetValue(changedFilePath, out var dependentUris))
        {
            return;
        }

        foreach (var dependentUri in dependentUris.ToList())
        {
            if (!_documents.TryGetValue(dependentUri, out var dependentState))
            {
                continue;
            }

            dependentState.Rebuild();
            UpdateIncludeGraph(dependentUri, dependentState);
            PublishDiagnostics(dependentUri, dependentState);

            if (!string.IsNullOrEmpty(dependentState.FilePath))
            {
                RebuildDependents(dependentState.FilePath, visited);
            }
        }
    }

    private void UpdateIncludeGraph(string uri, DocumentState state)
    {
        if (_documentIncludes.TryGetValue(uri, out var oldIncludes))
        {
            foreach (var inc in oldIncludes)
            {
                if (_dependents.TryGetValue(inc, out var set))
                {
                    set.Remove(uri);
                    if (set.Count == 0)
                    {
                        _dependents.Remove(inc);
                    }
                }
            }
        }

        var newIncludes = new HashSet<string>(state.IncludedFilePaths, StringComparer.OrdinalIgnoreCase);
        _documentIncludes[uri] = newIncludes;

        foreach (var inc in newIncludes)
        {
            if (!_dependents.TryGetValue(inc, out var set))
            {
                set = new HashSet<string>();
                _dependents[inc] = set;
            }
            set.Add(uri);
        }
    }

    private void PublishDiagnostics(string uri, IReadOnlyList<DiagnosticItem> diagnostics)
    {
        _ = SendNotificationAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = diagnostics as DiagnosticItem[] ?? diagnostics.ToArray()
        });
    }

    private void PublishDiagnostics(string uri, DocumentState state)
    {
        var diagnostics = CompilationService.LintSource(state.Text, state.FilePath, ResolveIncludedFile);
        var diagnosticsByUri = new Dictionary<string, List<DiagnosticItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in diagnostics)
        {
            string targetUri;
            if (string.IsNullOrEmpty(d.File))
            {
                targetUri = uri;
            }
            else
            {
                string fullPath = Path.GetFullPath(d.File);
                targetUri = FilePathToUri(fullPath);
            }

            if (!diagnosticsByUri.TryGetValue(targetUri, out var items))
            {
                items = new List<DiagnosticItem>();
                diagnosticsByUri[targetUri] = items;
            }

            int line = Math.Max(0, d.Line - 1);
            int column = Math.Max(0, d.Column - 1);

            items.Add(new DiagnosticItem
            {
                Range = new Range
                {
                    Start = new Position { Line = line, Character = column },
                    End = new Position { Line = line, Character = column }
                },
                Severity = d.Severity == "error" ? 1 : 2,
                Message = d.Message
            });
        }

        foreach (var kvp in diagnosticsByUri)
        {
            PublishDiagnostics(kvp.Key, kvp.Value);
        }
    }

    private static string? UriToFilePath(string uri)
    {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new Uri(uri).LocalPath;
            }
            catch
            {
                var path = uri.Substring("file://".Length).TrimStart('/');
                return path.Replace('/', Path.DirectorySeparatorChar);
            }
        }

        return null;
    }

    private static string FilePathToUri(string filePath)
    {
        try
        {
            return new Uri(filePath).AbsoluteUri;
        }
        catch
        {
            return "file:///" + filePath.Replace('\\', '/');
        }
    }

    private async Task SendResponseAsync(object? id, object? result)
    {
        await LspJsonRpc.WriteMessageAsync(_output, new LspMessage
        {
            Id = id,
            Result = result
        });
    }

    private async Task SendNotificationAsync(string method, object parameters)
    {
        await LspJsonRpc.WriteMessageAsync(_output, new LspMessage
        {
            Method = method,
            Params = parameters
        });
    }

    private class DocumentState
    {
        public string Uri { get; set; } = "";
        public string? FilePath { get; set; }
        public string Text { get; set; } = "";
        public Func<string, string?>? FileProvider { get; set; }
        public HashSet<string> IncludedFilePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public SymbolIndex SymbolIndex { get; private set; } = SymbolIndex.Build(Enumerable.Empty<Statement>());

        public void Rebuild()
        {
            try
            {
                var includedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lexer = new ZvLexer(Text, FilePath, includedFiles, fileProvider: FileProvider, systemIncludePaths: ZvLexer.GetDefaultSystemIncludePaths());
                var tokens = lexer.ScanTokens();
                var parser = new ZvParser(tokens, FilePath);
                var statements = parser.Parse();
                SymbolIndex = SymbolIndex.Build(statements);
                IncludedFilePaths = new HashSet<string>(lexer.IncludedFiles, StringComparer.OrdinalIgnoreCase);
                if (FilePath != null)
                {
                    IncludedFilePaths.Remove(FilePath);
                }
            }
            catch
            {
                SymbolIndex = SymbolIndex.Build(Enumerable.Empty<Statement>());
                IncludedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
