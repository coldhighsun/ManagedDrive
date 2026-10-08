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
    /// Archives, disk images and packages such as JAR, APK, wheel and VSIX.
    /// </summary>
    Archive,

    /// <summary>
    /// Office documents, PDFs, plain text, notebooks and fonts.
    /// </summary>
    Document,

    /// <summary>
    /// Source code, project and configuration files, and data formats such as JSON.
    /// </summary>
    Code,

    /// <summary>
    /// Programs, libraries and compiled code such as object files, Java classes and WebAssembly.
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
                if (!map.TryAdd(extension, category))
                {
                    throw new InvalidOperationException($"Extension '{extension}' is listed for both {map[extension]} and {category}.");
                }
            }
        }

        Add(FileCategory.Image, ".png .jpg .jpeg .gif .bmp .webp .tif .tiff .ico .svg .heic .heif .avif .raw .psd .cr2 .nef .arw .dng");
        Add(FileCategory.Video, ".mp4 .mkv .avi .mov .wmv .flv .webm .m4v .mpg .mpeg .3gp .vob");
        Add(FileCategory.Audio, ".mp3 .wav .flac .aac .ogg .m4a .wma .opus .mid .midi .aiff .ape");
        Add(FileCategory.Archive, ".zip .7z .rar .tar .gz .tgz .bz2 .xz .zst .lz4 .lzma .iso .img .dmg .vhd .vhdx .vmdk .mdr .cab .nupkg .jar .war .apk .whl .vsix");
        Add(FileCategory.Document, ".pdf .doc .docx .docm .dotx .xls .xlsx .xlsm .xlsb .ppt .pptx .pptm .txt .md .rtf .csv .tsv .odt .ods .odp .epub .ipynb .ttf .otf .woff .woff2");
        Add(FileCategory.Code, ".cs .csx .csproj .vbproj .fsproj .props .targets .sln .slnx .vb .fs .js .jsx .mjs .cjs .ts .tsx .vue .svelte .py .java .kt .kts .swift .rb .php .lua .dart .scala .cpp .cc .cxx .c .h .hpp .go .rs .json .jsonc .xml .xaml .axaml .razor .cshtml .yml .yaml .html .htm .css .scss .sass .less .sql .ps1 .psm1 .sh .bat .cmd .ini .toml .config .editorconfig .gradle .proto");
        Add(FileCategory.Executable, ".exe .dll .sys .msi .msix .lib .obj .o .a .pdb .so .dylib .node .class .scr .ocx .drv .wasm");
        Add(FileCategory.Cache, ".log .tmp .temp .cache .bak .old .swp .db .sqlite .sqlite3 .mdb .ldb .etl .dmp .mdmp .pyc .pyo .map");

        return map;
    }
}
