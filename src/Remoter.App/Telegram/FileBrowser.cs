namespace Remoter.App.Telegram;

public sealed class BrowserEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public long Size { get; init; }

    public string Icon => IsDrive ? "💾" : IsDirectory ? "📁" : "📄";
}

/// <summary>Одна страница файлового браузера. Индекс элемента не зависит от страницы.</summary>
public sealed class BrowserPage
{
    public const int PageSize = 12;

    public int Generation { get; init; }
    public string? Path { get; init; }
    public bool Drives { get; init; }
    public IReadOnlyList<BrowserEntry> Entries { get; init; } = [];
    public bool Truncated { get; init; }
    public int Page { get; set; }

    public int PageCount => Math.Max(1, (Entries.Count + PageSize - 1) / PageSize);
    public int DirectoryCount => Entries.Count(e => e.IsDirectory);
    public int FileCount => Entries.Count(e => !e.IsDirectory);

    public IReadOnlyList<BrowserEntry> CurrentEntries
    {
        get
        {
            var start = Math.Clamp(Page, 0, PageCount - 1) * PageSize;
            return Entries.Skip(start).Take(PageSize).ToArray();
        }
    }
}

/// <summary>
/// Состояние файлового браузера на чат. Каждая навигация получает новое поколение,
/// чтобы нажатия на старые кнопки не открывали чужую папку.
/// </summary>
public sealed class FileBrowser
{
    private const int MaxRetainedPages = 8;
    private const int MaxDirectories = 200;
    private const int MaxFiles = 200;

    private static readonly EnumerationOptions ListOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly Dictionary<int, BrowserPage> _pages = new();

    // Случайный старт, чтобы кнопки из истории чата после перезапуска
    // не совпали с новыми страницами и не открыли чужую папку.
    private int _lastGeneration = Random.Shared.Next(1, 1_000_000);

    public BrowserPage Open(string? path)
    {
        var generation = Interlocked.Increment(ref _lastGeneration);
        var page = path is null ? BuildDrives(generation) : BuildDirectory(generation, path);

        _pages[generation] = page;
        Trim();

        return page;
    }

    public bool TryGet(int generation, out BrowserPage page) =>
        _pages.TryGetValue(generation, out page!);

    public static BrowserPage SetPage(BrowserPage page, int index)
    {
        page.Page = Math.Clamp(index, 0, page.PageCount - 1);
        return page;
    }

    public static string? ParentOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Directory.GetParent(path)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static BrowserPage BuildDirectory(int generation, string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
            throw new DirectoryNotFoundException($"Папка не найдена: {path}");

        var directories = new List<BrowserEntry>();
        var files = new List<BrowserEntry>();
        var truncated = false;

        try
        {
            foreach (var directory in info.EnumerateDirectories("*", ListOptions))
            {
                if (directories.Count >= MaxDirectories)
                {
                    truncated = true;
                    break;
                }

                directories.Add(new BrowserEntry
                {
                    Name = directory.Name,
                    FullPath = directory.FullName,
                    IsDirectory = true,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать папки {path}: {ex.Message}");
        }

        try
        {
            foreach (var file in info.EnumerateFiles("*", ListOptions))
            {
                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }

                long size = 0;
                try { size = file.Length; } catch { /* нет доступа к размеру */ }

                files.Add(new BrowserEntry
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    Size = size,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать файлы {path}: {ex.Message}");
        }

        directories.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        files.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var entries = new List<BrowserEntry>(directories.Count + files.Count);
        entries.AddRange(directories);
        entries.AddRange(files);

        return new BrowserPage
        {
            Generation = generation,
            Path = info.FullName,
            Entries = entries,
            Truncated = truncated,
        };
    }

    private static BrowserPage BuildDrives(int generation)
    {
        var entries = new List<BrowserEntry>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                    continue;

                entries.Add(new BrowserEntry
                {
                    Name = drive.Name,
                    FullPath = drive.Name,
                    IsDirectory = true,
                    IsDrive = true,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось получить список дисков: {ex.Message}");
        }

        entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        return new BrowserPage
        {
            Generation = generation,
            Path = null,
            Drives = true,
            Entries = entries,
        };
    }

    private void Trim()
    {
        if (_pages.Count <= MaxRetainedPages) return;

        var oldest = _pages.Keys.OrderBy(key => key).First();
        _pages.Remove(oldest);
    }
}
