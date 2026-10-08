using ManagedDrive.App.Services;
using Microsoft.Win32;

namespace ManagedDrive.Tests;

/// <summary>
/// In-memory stand-in for the registry's per-user environment, shared by the tests that need an
/// <see cref="IUserEnvironment"/>.
/// </summary>
internal sealed class FakeUserEnvironment : IUserEnvironment
{
    /// <summary>
    /// Gets the variables currently set; names are compared ignoring case, as Windows does.
    /// </summary>
    public Dictionary<string, UserTempValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the number of change broadcasts sent.
    /// </summary>
    public int Broadcasts { get; private set; }

    /// <inheritdoc />
    public UserTempValue? Read(string name) => Values.GetValueOrDefault(name);

    /// <inheritdoc />
    public void Write(string name, string value, RegistryValueKind kind) => Values[name] = new(value, kind);

    /// <inheritdoc />
    public void Delete(string name) => Values.Remove(name);

    /// <inheritdoc />
    public void Broadcast() => Broadcasts++;
}
