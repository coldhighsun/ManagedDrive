using ManagedDrive.WingetExtension;
using YamlDotNet.Core;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="WingetManifestReader"/>, pinning the exceptions
/// <see cref="SilentInstaller"/> relies on to decide between falling back and failing.
/// </summary>
public sealed class WingetManifestReaderTests
{
    /// <summary>
    /// A manifest that doesn't specify switches gets winget's defaults for a known installer
    /// type.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_KnownTypeWithoutSwitches_UsesDefaultSwitches()
    {
        var info = ReadManifest("Installers:\n  - InstallerType: inno\n");

        Assert.Equal("inno", info.InstallerType);
        Assert.Equal("/VERYSILENT /NORESTART", info.SilentSwitches);
        Assert.Equal("/SILENT /NORESTART", info.SilentWithProgressSwitches);
    }

    /// <summary>
    /// A manifest that doesn't specify switches for an installer type without known defaults
    /// throws <see cref="NotSupportedException"/>, which makes wingetx fall back to a plain
    /// <c>winget install</c>.
    /// </summary>
    /// <param name="installerType">The manifest's installer type.</param>
    [Theory]
    [InlineData("exe")]
    [InlineData("zip")]
    public void ReadInstallerInfo_TypeWithoutDefaultSwitches_ThrowsNotSupportedException(string installerType)
    {
        Assert.Throws<NotSupportedException>(() => ReadManifest($"Installers:\n  - InstallerType: {installerType}\n"));
    }

    /// <summary>
    /// A malformed manifest throws <see cref="YamlException"/>, which wingetx reports as a
    /// failure.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_MalformedYaml_ThrowsYamlException()
    {
        Assert.ThrowsAny<YamlException>(() => ReadManifest("Installers: [unclosed\n"));
    }

    /// <summary>
    /// Writes <paramref name="yaml"/> to a temporary manifest file and reads it.
    /// </summary>
    /// <param name="yaml">The manifest content.</param>
    /// <returns>The installer info read from the manifest.</returns>
    private static InstallerInfo ReadManifest(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return WingetManifestReader.ReadInstallerInfo(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
