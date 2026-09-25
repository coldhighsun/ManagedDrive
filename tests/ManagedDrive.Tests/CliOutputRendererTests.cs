using ManagedDrive.Cli;
using ManagedDrive.Cli.Core;
using Spectre.Console;

namespace ManagedDrive.Tests;

public sealed class CliOutputRendererTests : IDisposable
{
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _output = new();

    public CliOutputRendererTests()
    {
        _originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new()
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(_output),
        });
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        _output.Dispose();
    }

    [Fact]
    public void Render_DiskListMountPointWithMarkupBrackets_RendersItLiterally()
    {
        var mountPoint = Path.Join("C:", "mnt", "[build]");
        var response = new CliResponse(true, string.Empty, [new(mountPoint, "Data", 0, 1024 * 1024)], 0);

        var exitCode = CliOutputRenderer.Render(response);

        Assert.Equal(0, exitCode);
        Assert.Contains("[build]", _output.ToString());
    }

    [Fact]
    public void Render_DiskListLabelWithMarkupBrackets_RendersItLiterally()
    {
        var response = new CliResponse(true, string.Empty, [new("R:", "[red]x", 0, 1024 * 1024)], 0);

        var exitCode = CliOutputRenderer.Render(response);

        Assert.Equal(0, exitCode);
        Assert.Contains("[red]x", _output.ToString());
    }
}
