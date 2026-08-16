using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using ZV.Compiler.Lexer;
using ZV.Compiler.Target;

namespace ZV.Compiler.Tests;

public class EmbedLayoutTests
{
    [Fact]
    public void EmptyListProducesEmptyLayout()
    {
        Assert.Empty(EmbedLayout.Build(Array.Empty<EmbedInfo>()));
    }

    [Fact]
    public void LayoutStartsWithMagicAndCount()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[] { 1, 2, 3 });
            var embeds = new[]
            {
                new EmbedInfo(Path.Combine(dir, "a.bin"), EmbedKind.Resource, null, new SourceLocation(null, 0, 0, 0), 3)
            };

            var layout = EmbedLayout.Build(embeds);
            Assert.True(layout.Length > 8);
            Assert.Equal(0x5A, layout[0]); // 'Z'
            Assert.Equal(0x5A, layout[1]); // 'V' reversed due to little-endian
            Assert.Equal(0x45, layout[2]); // 'E'
            Assert.Equal(0x4D, layout[3]); // 'M'
            Assert.Equal(1u, BitConverter.ToUInt32(layout, 4));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RoundTripsResourceAndFileData()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "res.bin"), new byte[] { 0xAB, 0xCD });
            File.WriteAllBytes(Path.Combine(dir, "f.txt"), Encoding.ASCII.GetBytes("hi"));
            var embeds = new[]
            {
                new EmbedInfo(Path.Combine(dir, "res.bin"), EmbedKind.Resource, null, new SourceLocation(null, 0, 0, 0), 2),
                new EmbedInfo(Path.Combine(dir, "f.txt"), EmbedKind.File, "dest/f.txt", new SourceLocation(null, 0, 0, 0), 2)
            };

            var layout = EmbedLayout.Build(embeds);
            var text = Encoding.ASCII.GetString(layout);
            Assert.Contains("res.bin", text);
            Assert.Contains("dest/f.txt", text);
            Assert.Contains("hi", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
