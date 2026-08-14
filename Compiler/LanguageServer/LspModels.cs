using System.Text.Json.Serialization;

namespace ZV.Compiler.LanguageServer;

public class LspMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public object? Params { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public object? Error { get; set; }
}

public class InitializeParams
{
    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("rootUri")]
    public string? RootUri { get; set; }

    [JsonPropertyName("capabilities")]
    public object? Capabilities { get; set; }
}

public class InitializeResult
{
    [JsonPropertyName("capabilities")]
    public ServerCapabilities Capabilities { get; set; } = new();
}

public class ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")]
    public TextDocumentSyncOptions TextDocumentSync { get; set; } = new();

    [JsonPropertyName("referencesProvider")]
    public bool ReferencesProvider { get; set; } = true;

    [JsonPropertyName("definitionProvider")]
    public bool DefinitionProvider { get; set; } = true;
}

public class TextDocumentSyncOptions
{
    [JsonPropertyName("openClose")]
    public bool OpenClose { get; set; } = true;

    [JsonPropertyName("change")]
    public int Change { get; set; } = 1; // Full document sync
}

public class TextDocumentItem
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("languageId")]
    public string LanguageId { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class DidOpenTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentItem TextDocument { get; set; } = new();
}

public class DidChangeTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public VersionedTextDocumentIdentifier TextDocument { get; set; } = new();

    [JsonPropertyName("contentChanges")]
    public TextDocumentContentChangeEvent[] ContentChanges { get; set; } = [];
}

public class VersionedTextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; }
}

public class TextDocumentContentChangeEvent
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class DidCloseTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = new();
}

public class TextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
}

public class ReferenceParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = new();

    [JsonPropertyName("position")]
    public Position Position { get; set; } = new();

    [JsonPropertyName("context")]
    public ReferenceContext Context { get; set; } = new();
}

public class ReferenceContext
{
    [JsonPropertyName("includeDeclaration")]
    public bool IncludeDeclaration { get; set; } = true;
}

public class Location
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("range")]
    public Range Range { get; set; } = new();
}

public class PublishDiagnosticsParams
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("diagnostics")]
    public DiagnosticItem[] Diagnostics { get; set; } = [];
}

public class DiagnosticItem
{
    [JsonPropertyName("range")]
    public Range Range { get; set; } = new();

    [JsonPropertyName("severity")]
    public int Severity { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class Range
{
    [JsonPropertyName("start")]
    public Position Start { get; set; } = new();

    [JsonPropertyName("end")]
    public Position End { get; set; } = new();
}

public class Position
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }
}

public class DefinitionParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = new();

    [JsonPropertyName("position")]
    public Position Position { get; set; } = new();
}
