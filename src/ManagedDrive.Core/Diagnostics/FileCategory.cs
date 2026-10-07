namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// Coarse kind of a file, derived from its extension, used to colour the space-usage treemap.
/// </summary>
public enum FileCategory
{
    /// <summary>
    /// Anything not recognised below, including files without an extension.
    /// </summary>
    Other = 0,

    /// <summary>
    /// Pictures.
    /// </summary>
    Image,

    /// <summary>
    /// Video.
    /// </summary>
    Video,

    /// <summary>
    /// Sound.
    /// </summary>
    Audio,

    /// <summary>
    /// Archives and disk images.
    /// </summary>
    Archive,

    /// <summary>
    /// Office documents, PDFs and plain text.
    /// </summary>
    Document,

    /// <summary>
    /// Source code and data formats such as JSON.
    /// </summary>
    Code,

    /// <summary>
    /// Programs and libraries.
    /// </summary>
    Executable,

    /// <summary>
    /// Logs, caches, temporary and database files.
    /// </summary>
    Cache,
}

/// <summary>
/// Maps file extensions to <see cref="FileCategory"/>.
/// </summary>
public static class FileCategories
{
    /// <summary>
    /// Extension (lower case, with the dot) to category.
    /// </summary>
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    /// <summary>
    /// Returns the category of a file name or extension.
    /// </summary>
    /// <param name="fileNameOrExtension">A file name such as <c>a.PNG</c>, or just an extension such as <c>.png</c>.</param>
    /// <returns>The category, <see cref="FileCategory.Other"/> when unknown.</returns>
    public static FileCategory Of(string fileNameOrExtension)
    {
        var extension = System.IO.Path.GetExtension(fileNameOrExtension);
        return extension.Length > 0 && ByExtension.TryGetValue(extension.ToLowerInvariant(), out var category)
            ? category
            : FileCategory.Other;
    }

    /// <summary>
    /// Builds the extension table.
    /// </summary>
    /// <returns>The table, keyed case-insensitively.</returns>
    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);

        void Add(FileCategory category, string extensions)
        {
            foreach (var extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                map[extension] = category;
            }
        }

        Add(FileCategory.Image, ".png .jpg .jpeg .gif .bmp .webp .tif .tiff .ico .svg .heic .raw .psd");
        Add(FileCategory.Video, ".mp4 .mkv .avi .mov .wmv .flv .webm .m4v .mpg .mpeg");
        Add(FileCategory.Audio, ".mp3 .wav .flac .aac .ogg .m4a .wma .opus");
        Add(FileCategory.Archive, ".zip .7z .rar .tar .gz .bz2 .xz .zst .iso .vhd .vhdx .mdr .cab .nupkg");
        Add(FileCategory.Document, ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .rtf .csv .odt .epub");
        Add(FileCategory.Code, ".cs .csproj .sln .js .jsx .ts .tsx .py .java .cpp .c .h .hpp .go .rs .json .xml .xaml .yml .yaml .html .css .ps1 .sh .bat .cmd .ini .toml");
        Add(FileCategory.Executable, ".exe .dll .sys .msi .lib .obj .pdb .so .dylib");
        Add(FileCategory.Cache, ".log .tmp .temp .cache .bak .db .sqlite .etl .dmp .pyc .map");

        return map;
    }
}
