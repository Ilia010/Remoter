using Remoter.App.Telegram;

namespace Remoter.Tests;

public sealed class FileBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remoter-tests", Guid.NewGuid().ToString("N"));

    public FileBrowserTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // тестовый мусор не должен ронять прогон
        }
    }

    [Fact]
    public void Open_Null_ShowsDrives()
    {
        var browser = new FileBrowser();

        var page = browser.Open(null);

        Assert.True(page.Drives);
        Assert.Null(page.Path);
        Assert.True(page.Generation > 0);
        Assert.All(page.Entries, entry =>
        {
            Assert.True(entry.IsDirectory);
            Assert.True(entry.IsDrive);
            Assert.EndsWith("\\", entry.FullPath);
        });
    }

    [Fact]
    public void Open_Directory_ListsDirectoriesFirstThenFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        File.WriteAllText(Path.Combine(_root, "beta.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "Alpha.txt"), "a");

        var browser = new FileBrowser();
        var page = browser.Open(_root);

        Assert.False(page.Drives);
        Assert.Equal(Path.GetFullPath(_root), page.Path);
        Assert.Equal(["Alpha", "beta", "Alpha.txt", "beta.txt"], page.Entries.Select(e => e.Name));

        Assert.Equal([true, true, false, false], page.Entries.Select(e => e.IsDirectory));
        Assert.Equal(2, page.DirectoryCount);
        Assert.Equal(2, page.FileCount);
        Assert.All(page.Entries, entry => Assert.StartsWith(_root, entry.FullPath));
    }

    [Fact]
    public void Open_Directory_SkipsHiddenAndSystemEntries()
    {
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "v");
        File.WriteAllText(Path.Combine(_root, "hidden.txt"), "h");
        File.WriteAllText(Path.Combine(_root, "system.txt"), "s");
        File.SetAttributes(Path.Combine(_root, "hidden.txt"), FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(_root, "system.txt"), FileAttributes.System);

        var browser = new FileBrowser();
        var page = browser.Open(_root);

        Assert.Equal(["visible.txt"], page.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Open_MissingDirectory_ThrowsDirectoryNotFound()
    {
        var browser = new FileBrowser();
        var missing = Path.Combine(_root, "nope", "deeper");

        var error = Assert.Throws<DirectoryNotFoundException>(() => browser.Open(missing));
        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void Open_CreatesNewGenerationAndKeepsOldPagesForCallbacks()
    {
        var browser = new FileBrowser();

        var first = browser.Open(_root);
        var second = browser.Open(null);

        Assert.NotEqual(first.Generation, second.Generation);
        Assert.True(browser.TryGet(first.Generation, out var storedFirst));
        Assert.True(browser.TryGet(second.Generation, out var storedSecond));
        Assert.Same(first, storedFirst);
        Assert.Same(second, storedSecond);
        Assert.False(browser.TryGet(999, out _));
    }

    [Fact]
    public void Open_KeepsOnlyEightMostRecentGenerations()
    {
        var browser = new FileBrowser();
        var generations = Enumerable.Range(0, 10)
            .Select(_ => browser.Open(_root).Generation)
            .ToArray();

        Assert.False(browser.TryGet(generations[0], out _));
        Assert.False(browser.TryGet(generations[1], out _));
        foreach (var generation in generations.Skip(2))
            Assert.True(browser.TryGet(generation, out _));
    }

    [Fact]
    public void ParentOf_ReturnsParentAndStopsAtRoot()
    {
        var child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);

        Assert.Equal(Path.GetFullPath(_root), FileBrowser.ParentOf(child));
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(_root)), FileBrowser.ParentOf(_root));

        var root = Path.GetPathRoot(Path.GetFullPath(_root));
        Assert.NotNull(root);
        Assert.Null(FileBrowser.ParentOf(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParentOf_EmptyPath_ReturnsNull(string? path)
    {
        Assert.Null(FileBrowser.ParentOf(path));
    }

    [Fact]
    public void SetPage_ClampsToValidRange()
    {
        CreateDirectories(25);
        var browser = new FileBrowser();
        var page = browser.Open(_root);

        Assert.Equal(3, page.PageCount);

        Assert.Equal(0, FileBrowser.SetPage(page, -5).Page);
        Assert.Equal(2, FileBrowser.SetPage(page, 99).Page);
        Assert.Equal(1, FileBrowser.SetPage(page, 1).Page);
    }

    [Fact]
    public void CurrentEntries_PaginatesWithoutLosingEntries()
    {
        CreateDirectories(25);
        var browser = new FileBrowser();
        var page = browser.Open(_root);

        Assert.Equal(12, page.CurrentEntries.Count);
        Assert.Equal("dir00", page.CurrentEntries[0].Name);

        FileBrowser.SetPage(page, 1);
        Assert.Equal(12, page.CurrentEntries.Count);
        Assert.Equal("dir12", page.CurrentEntries[0].Name);

        FileBrowser.SetPage(page, 2);
        Assert.Single(page.CurrentEntries);
        Assert.Equal("dir24", page.CurrentEntries[0].Name);
    }

    private void CreateDirectories(int count)
    {
        for (var i = 0; i < count; i++)
            Directory.CreateDirectory(Path.Combine(_root, $"dir{i:D2}"));
    }
}
