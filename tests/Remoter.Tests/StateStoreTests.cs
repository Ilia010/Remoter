using Remoter.App.Storage;

namespace Remoter.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "remoter-tests-" + Guid.NewGuid().ToString("n"));

    private string StatePath => Path.Combine(_directory, "state.json");

    [Fact]
    public void Update_PersistsAcrossInstances()
    {
        var store = new StateStore(StatePath);

        store.Update(42, chat =>
        {
            chat.SessionID = "ses_1";
            chat.Directory = @"C:\work\demo";
            chat.ModelID = "deepseek-v4.1-flash-free";
            chat.ModelProvider = "opencode";
            chat.Agent = "build";
        });

        var reloaded = new StateStore(StatePath);
        var chat = reloaded.Get(42);

        Assert.Equal("ses_1", chat.SessionID);
        Assert.Equal(@"C:\work\demo", chat.Directory);
        Assert.Equal("deepseek-v4.1-flash-free", chat.ModelID);
        Assert.Equal("opencode", chat.ModelProvider);
        Assert.Equal("build", chat.Agent);
        Assert.True(chat.Updated > 0);
    }

    [Fact]
    public void Get_CreatesMissingChat()
    {
        var store = new StateStore(StatePath);

        var chat = store.Get(7);

        Assert.Null(chat.SessionID);
        Assert.Null(chat.Directory);
    }

    [Fact]
    public void Load_CorruptFileStartsWithEmptyState()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, "{ this is not json");

        var store = new StateStore(StatePath);

        Assert.Null(store.Get(1).SessionID);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Ñ‚ÐµÑÑ‚Ð¾Ð²Ñ‹Ð¹ Ð¼ÑƒÑÐ¾Ñ€ Ð½Ðµ Ð´Ð¾Ð»Ð¶ÐµÐ½ Ñ€Ð¾Ð½ÑÑ‚ÑŒ Ð¿Ñ€Ð¾Ð³Ð¾Ð½
        }
    }
}
