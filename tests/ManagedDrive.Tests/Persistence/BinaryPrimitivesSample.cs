namespace ManagedDrive.Tests;

/// <summary>
/// A fixed sequence of <see cref="BinaryWriter"/> primitives (strings with 1- and 2-byte length
/// prefixes, fixed-width integers, a byte run) for exercising a stream's small reads — the
/// <c>Read(Span&lt;byte&gt;)</c> and <c>ReadByte</c> paths <see cref="BinaryReader"/> uses — across
/// chunk boundaries.
/// </summary>
internal static class BinaryPrimitivesSample
{
    private const int Repetitions = 50;

    private static readonly string LongString = new('x', 300);

    internal static void Write(BinaryWriter writer)
    {
        for (var i = 0; i < Repetitions; i++)
        {
            writer.Write($"\\dir{i}\\file.txt");
            writer.Write((uint)i);
            writer.Write((ulong)i * 0x0102_0304_0506_0708UL);
            writer.Write((byte)i);
            writer.Write(LongString);
            writer.Write(-i);
            writer.Write(new byte[i % 7]);
        }
    }

    internal static void ReadAndAssert(BinaryReader reader)
    {
        for (var i = 0; i < Repetitions; i++)
        {
            Assert.Equal($"\\dir{i}\\file.txt", reader.ReadString());
            Assert.Equal((uint)i, reader.ReadUInt32());
            Assert.Equal((ulong)i * 0x0102_0304_0506_0708UL, reader.ReadUInt64());
            Assert.Equal((byte)i, reader.ReadByte());
            Assert.Equal(LongString, reader.ReadString());
            Assert.Equal(-i, reader.ReadInt32());
            Assert.Equal(new byte[i % 7], reader.ReadBytes(i % 7));
        }
    }
}
