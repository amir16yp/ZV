using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ZV.Compiler.Target;

/// <summary>
/// Builds a raw bare-metal disk image from a boot sector, optional kernel binary,
/// and any embedded image files. The layout is intentionally simple:
///
///   Sector 0          : boot sector
///   Sectors 1..K      : kernel (padded to sector boundary)
///   File table        : array of ImageFileEntry records
///   File data         : concatenated raw file contents
///
/// The file table is terminated by an entry whose first byte is 0x00. The boot
/// code is responsible for locating files by a stable compile-time identifier.
/// </summary>
public sealed class ImageBuilder
{
    public const int SectorSize = BootSectorGenerator.SectorSize;
    public const int MaxImagePathBytes = 16;

    private readonly List<ImageFileEntry> _imageFiles = new();
    private byte[]? _kernel;
    private byte[]? _bootSector;

    public void SetBootSector(byte[] bootSector)
    {
        if (bootSector.Length != SectorSize)
            throw new ArgumentException($"Boot sector must be {SectorSize} bytes.", nameof(bootSector));
        _bootSector = bootSector;
    }

    public void SetKernel(byte[] kernel)
    {
        _kernel = kernel;
    }

    public void AddImageFile(string destinationPath, byte[] data)
    {
        var pathBytes = Encoding.ASCII.GetBytes(destinationPath);
        if (pathBytes.Length == 0 || pathBytes.Length >= MaxImagePathBytes)
            throw new ArgumentException($"Image file path must be 1..{MaxImagePathBytes - 1} ASCII bytes.", nameof(destinationPath));

        _imageFiles.Add(new ImageFileEntry(destinationPath, data));
    }

    public byte[] BuildImage()
    {
        if (_bootSector == null)
            throw new InvalidOperationException("No boot sector set.");

        var image = new List<byte>();
        image.AddRange(_bootSector);

        byte[] kernel = _kernel ?? Array.Empty<byte>();
        image.AddRange(kernel);
        int kernelPadding = PadToSector(kernel.Length);
        for (int i = 0; i < kernelPadding; i++)
            image.Add(0);

        int kernelSectorCount = (kernel.Length + kernelPadding) / SectorSize;
        int fileTableOffset = (1 + kernelSectorCount) * SectorSize;

        // File table records are fixed-size; the boot sector can compute the table
        // location from the kernel size recorded in the image header (future work).
        // For now we append the table and data sequentially.
        var fileData = new List<byte>();
        var table = new List<byte>();
        foreach (var entry in _imageFiles)
        {
            int dataOffset = fileTableOffset + (_imageFiles.Count * ImageFileEntry.Size) + fileData.Count;
            table.AddRange(entry.Serialize(dataOffset, fileData.Count));
            fileData.AddRange(entry.Data);
        }
        table.AddRange(ImageFileEntry.Terminator());

        image.AddRange(table);
        image.AddRange(fileData);

        return image.ToArray();
    }

    private static int PadToSector(int length)
    {
        int remainder = length % SectorSize;
        return remainder == 0 ? 0 : SectorSize - remainder;
    }
}

public sealed record ImageFileEntry(string DestinationPath, byte[] Data)
{
    public const int Size = ImageBuilder.MaxImagePathBytes + 4 + 4; // path + offset + size

    public byte[] Serialize(int absoluteOffset, int dataOffsetWithinFileArea)
    {
        // The offset stored is the absolute byte offset in the image.
        _ = dataOffsetWithinFileArea; // reserved for future relative-offset forms

        var pathBytes = Encoding.ASCII.GetBytes(DestinationPath);
        var result = new List<byte>(Size);
        result.AddRange(pathBytes);
        while (result.Count < ImageBuilder.MaxImagePathBytes)
            result.Add(0);

        result.AddRange(BitConverter.GetBytes((uint)absoluteOffset));
        result.AddRange(BitConverter.GetBytes((uint)Data.Length));
        return result.ToArray();
    }

    public static byte[] Terminator()
    {
        return new byte[Size]; // zero-filled; first path byte == 0 marks end of table
    }
}
