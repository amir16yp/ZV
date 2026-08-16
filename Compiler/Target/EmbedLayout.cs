using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZV.Compiler.Target;

/// <summary>
/// Builds a self-contained binary table of embedded resources/files that can be
/// appended to any bare-metal kernel image regardless of ISA. The layout is:
///
///   [0..3]   magic "ZVEM" (little-endian)
///   [4..7]   entry count (uint32)
///   [8..N]   records (20 bytes each):
///              name_offset : uint32 (relative to start of layout)
///              name_length : uint32
///              data_offset : uint32
///              data_size   : uint32
///              kind        : uint32 (0 = resource, 1 = file)
///   names    null-terminated ASCII name pool
///   data     raw file/resource bytes
///
/// The table is designed to be read from real/protected/long mode as long as the
/// base address is known to the runtime.
/// </summary>
public static class EmbedLayout
{
    public const uint Magic = 0x4D455A5A; // "ZVEM" little-endian

    public static byte[] Build(IReadOnlyList<EmbedInfo> embeds)
    {
        if (embeds.Count == 0)
            return Array.Empty<byte>();

        // Pre-calculate sizes and collect data.
        var entries = new List<Entry>(embeds.Count);
        foreach (var embed in embeds)
        {
            byte[] fileBytes = File.ReadAllBytes(embed.SourcePath);
            string name = embed.Kind == EmbedKind.File
                ? (embed.DestinationPath ?? Path.GetFileName(embed.SourcePath))
                : Path.GetFileName(embed.SourcePath);
            byte[] nameAscii = Encoding.ASCII.GetBytes(name + '\0');
            entries.Add(new Entry(nameAscii, fileBytes, embed.Kind));
        }

        int headerSize = 8;
        int recordSize = 20;
        int recordsSize = entries.Count * recordSize;
        int namePoolSize = 0;
        int dataPoolSize = 0;
        foreach (var e in entries)
        {
            namePoolSize += e.Name.Length;
            dataPoolSize += e.Data.Length;
        }

        var result = new byte[headerSize + recordsSize + namePoolSize + dataPoolSize];

        // Header
        BitConverter.GetBytes(Magic).CopyTo(result, 0);
        BitConverter.GetBytes((uint)entries.Count).CopyTo(result, 4);

        int namePoolOffset = headerSize + recordsSize;
        int dataPoolOffset = namePoolOffset + namePoolSize;

        int currentNameOffset = 0;
        int currentDataOffset = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            int recordOffset = headerSize + (i * recordSize);

            WriteU32(result, recordOffset + 0, (uint)(namePoolOffset + currentNameOffset));
            WriteU32(result, recordOffset + 4, (uint)(e.Name.Length - 1)); // exclude null terminator
            WriteU32(result, recordOffset + 8, (uint)(dataPoolOffset + currentDataOffset));
            WriteU32(result, recordOffset + 12, (uint)e.Data.Length);
            WriteU32(result, recordOffset + 16, (uint)e.Kind);

            e.Name.CopyTo(result, namePoolOffset + currentNameOffset);
            e.Data.CopyTo(result, dataPoolOffset + currentDataOffset);

            currentNameOffset += e.Name.Length;
            currentDataOffset += e.Data.Length;
        }

        return result;
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset + 0] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private sealed record Entry(byte[] Name, byte[] Data, EmbedKind Kind);
}
