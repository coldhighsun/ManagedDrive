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
    /// An installer type and switches stated once at the manifest root are inherited by the
    /// installer entries.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_TypeAndSwitchesAtRoot_AreInheritedByTheInstaller()
    {
        var info = ReadManifest(
            "PackageIdentifier: Some.Package\nInstallerType: msi\nInstallerSwitches:\n  Silent: /qn\nInstallers:\n  - Architecture: x64\n");

        Assert.Equal("msi", info.InstallerType);
        Assert.Equal("/qn", info.SilentSwitches);
        Assert.Equal("Some.Package", info.PackageIdentifier);
    }

    /// <summary>
    /// With several installers listed, the one whose type matches the downloaded file's
    /// extension is used, not just the first entry.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_SeveralInstallers_PicksTheOneMatchingTheDownloadedFile()
    {
        const string yaml = "Installers:\n  - Architecture: x64\n    InstallerType: msi\n  - Architecture: x64\n    InstallerType: inno\n";

        var forExe = ReadManifest(yaml, installerFileName: "Setup.exe");
        var forMsi = ReadManifest(yaml, installerFileName: "Setup.msi");

        Assert.Equal("inno", forExe.InstallerType);
        Assert.Equal("msi", forMsi.InstallerType);
    }

    /// <summary>
    /// A requested architecture narrows the entries before the file type is matched.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_ArchitectureRequested_PicksThatArchitecturesEntry()
    {
        const string yaml =
            "Installers:\n  - Architecture: x86\n    InstallerType: inno\n    InstallerSwitches:\n      Silent: /x86\n" +
            "  - Architecture: x64\n    InstallerType: inno\n    InstallerSwitches:\n      Silent: /x64\n";

        var info = ReadManifest(yaml, installerFileName: "Setup.exe", architecture: "x64");

        Assert.Equal("/x64", info.SilentSwitches);
    }

    /// <summary>
    /// Several installers and none matching the downloaded file is a guess, so it is left to
    /// plain winget by throwing <see cref="NotSupportedException"/>.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_SeveralInstallersNoneMatchingTheFile_ThrowsNotSupportedException()
    {
        const string yaml = "Installers:\n  - InstallerType: msi\n  - InstallerType: wix\n";

        Assert.Throws<NotSupportedException>(() => ReadManifest(yaml, installerFileName: "Setup.exe"));
    }

    /// <summary>
    /// Several entries of the same file type with different switches (e.g. user and machine
    /// scope) can't be told apart by the downloaded file, so the choice is left to plain winget.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_SameTypeEntriesWithDifferentSwitches_ThrowsNotSupportedException()
    {
        const string yaml =
            "Installers:\n  - Scope: user\n    InstallerType: inno\n    InstallerSwitches:\n      Silent: /CURRENTUSER\n" +
            "  - Scope: machine\n    InstallerType: inno\n    InstallerSwitches:\n      Silent: /ALLUSERS\n";

        Assert.Throws<NotSupportedException>(() => ReadManifest(yaml, installerFileName: "Setup.exe"));
    }

    /// <summary>
    /// Entries that would run identically (only differing in, say, architecture or URL) still
    /// resolve to the first one.
    /// </summary>
    [Fact]
    public void ReadInstallerInfo_SameTypeEntriesWithSameSwitches_PicksTheFirst()
    {
        const string yaml =
            "Installers:\n  - Architecture: x86\n    InstallerType: inno\n    InstallerSwitches:\n      Silent: /S\n" +
            "  - Architecture: x64\n    InstallerType: inno\n    InstallerSwitches:\n      Silent: /S\n";

        var info = ReadManifest(yaml, installerFileName: "Setup.exe");

        Assert.Equal("/S", info.SilentSwitches);
    }

    /// <summary>
    /// Writes <paramref name="yaml"/> to a temporary manifest file and reads it.
    /// </summary>
    /// <param name="yaml">The manifest content.</param>
    /// <param name="installerFileName">The downloaded installer's file name, if known.</param>
    /// <param name="architecture">The requested architecture, if any.</param>
    /// <returns>The installer info read from the manifest.</returns>
    private static InstallerInfo ReadManifest(string yaml, string? installerFileName = null, string? architecture = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return WingetManifestReader.ReadInstallerInfo(path, installerFileName, architecture);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
