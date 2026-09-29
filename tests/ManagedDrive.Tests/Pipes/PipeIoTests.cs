using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

public sealed class PipeIoTests
{
    [Fact(Timeout = 5_000)]
    public async Task ReadLineWithTimeoutAsync_CancellationTokenFires_ReturnsNullWithoutWaitingForTimeout()
    {
        using var reader = new NeverCompletingTextReader();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await PipeIo.ReadLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(30), cts.Token);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadLineWithTimeoutAsync_LineAvailable_ReturnsLine()
    {
        using var reader = new StringReader("hello\n");

        var result = await PipeIo.ReadLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact(Timeout = 5_000)]
    public async Task ReadLineWithTimeoutAsync_NoDataWithinTimeout_ReturnsNullWithoutHanging()
    {
        using var reader = new NeverCompletingTextReader();

        var result = await PipeIo.ReadLineWithTimeoutAsync(
            reader, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadLineWithTimeoutAsync_StreamEndsWithoutALine_ReturnsNull()
    {
        using var reader = new StringReader(string.Empty);

        var result = await PipeIo.ReadLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact(Timeout = 5_000)]
    public async Task WriteLineWithTimeoutAsync_CancellationTokenFires_ReturnsWithoutThrowing()
    {
        await using var writer = new NeverCompletingTextWriter();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await PipeIo.WriteLineWithTimeoutAsync(writer, "hello", TimeSpan.FromSeconds(30), cts.Token);
    }

    [Fact(Timeout = 5_000)]
    public async Task WriteLineWithTimeoutAsync_WriteNeverCompletesWithinTimeout_ReturnsWithoutThrowing()
    {
        await using var writer = new NeverCompletingTextWriter();

        await PipeIo.WriteLineWithTimeoutAsync(
            writer, "hello", TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WriteLineWithTimeoutAsync_WriterAcceptsWrite_WritesLine()
    {
        await using var writer = new StringWriter();
        writer.NewLine = "\n";

        await PipeIo.WriteLineWithTimeoutAsync(writer, "hello", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("hello\n", writer.ToString());
    }

    private sealed class NeverCompletingTextReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    private sealed class NeverCompletingTextWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
