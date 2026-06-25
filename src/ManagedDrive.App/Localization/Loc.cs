using System.Windows;

namespace ManagedDrive.App.Localization;

public static class Loc
{
    public static string Get(string key) =>
        Application.Current?.Resources[key] is string value ? value : key;

    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);
}
