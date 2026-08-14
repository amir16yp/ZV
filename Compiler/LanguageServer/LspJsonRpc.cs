using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZV.Compiler.LanguageServer;

public static class LspJsonRpc
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<LspMessage?> ReadMessageAsync(Stream input, CancellationToken cancellationToken = default)
    {
        int? contentLength = null;

        while (true)
        {
            var headerLine = await ReadHeaderLineAsync(input, cancellationToken);
            if (headerLine == null)
            {
                return null;
            }

            if (headerLine == string.Empty)
            {
                break;
            }

            const string contentLengthPrefix = "Content-Length: ";
            if (headerLine.StartsWith(contentLengthPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = headerLine.Substring(contentLengthPrefix.Length).Trim();
                if (int.TryParse(value, out int length))
                {
                    contentLength = length;
                }
            }
        }

        if (contentLength == null)
        {
            return null;
        }

        byte[] buffer = new byte[contentLength.Value];
        int read = 0;

        while (read < contentLength.Value)
        {
            int r = await input.ReadAsync(buffer.AsMemory(read, contentLength.Value - read), cancellationToken);
            if (r == 0)
            {
                return null;
            }
            read += r;
        }

        var json = Encoding.UTF8.GetString(buffer);
        return JsonSerializer.Deserialize<LspMessage>(json, Options);
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream input, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        char prev = '\0';

        while (true)
        {
            byte[] buffer = new byte[1];
            int r = await input.ReadAsync(buffer, cancellationToken);
            if (r == 0)
            {
                return sb.Length == 0 ? null : sb.ToString();
            }

            char c = (char)buffer[0];
            if (c == '\n' && prev == '\r')
            {
                return sb.ToString(0, sb.Length - 1);
            }

            sb.Append(c);
            prev = c;
        }
    }

    public static async Task WriteMessageAsync(Stream output, LspMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);

        await output.WriteAsync(headerBytes, cancellationToken);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
