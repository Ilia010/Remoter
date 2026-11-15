using Remoter.App.Telegram;

namespace Remoter.Tests;

public class BotMenusTests
{
    [Fact]
    public void MainMenu_AllButtonsCarryMenuCallbacks()
    {
        var buttons = BotMenus.MainMenu().InlineKeyboard.SelectMany(row => row).ToArray();

        Assert.Equal(12, buttons.Length);
        Assert.All(buttons, button =>
        {
            Assert.NotNull(button.CallbackData);
            Assert.True(CallbackData.TryParseMenu(button.CallbackData!, out _));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(button.CallbackData!) <= 64);
        });
    }

    [Fact]
    public void Browser_PaginatesEntriesAndKeepsGlobalIndexes()
    {
        var page = Page(13, generation: 5);
        FileBrowser.SetPage(page, 1);

        var buttons = BotMenus.Browser(page, null).InlineKeyboard.SelectMany(row => row).ToArray();
        var first = buttons.First(button => button.CallbackData?.Contains(":e") == true);

        Assert.True(CallbackData.TryParseFiles(first.CallbackData!, out var generation, out var token));
        Assert.Equal(5, generation);
        Assert.Equal("e12", token);
        Assert.Contains("entry-12", first.Text);

        var nav = buttons
            .Where(button => button.CallbackData?.Contains(":p") == true)
            .Select(button => button.CallbackData!)
            .ToArray();
        Assert.Equal(2, nav.Length);
        Assert.All(nav, data => Assert.True(CallbackData.TryParseFiles(data, out _, out _)));
        Assert.Contains(CallbackData.FilesNav(5, "p0"), nav);
        Assert.Contains(CallbackData.FilesNav(5, "p2"), nav);
    }

    [Fact]
    public void Browser_Drives_DoNotOfferNewSession()
    {
        var page = new BrowserPage
        {
            Generation = 3,
            Drives = true,
            Entries = [new BrowserEntry { Name = "C:\\", FullPath = "C:\\", IsDirectory = true, IsDrive = true }],
        };

        var buttons = BotMenus.Browser(page, null).InlineKeyboard.SelectMany(row => row).ToArray();
        var tokens = buttons
            .Select(button =>
            {
                Assert.True(CallbackData.TryParseFiles(button.CallbackData!, out var generation, out var token));
                Assert.Equal(3, generation);
                return token;
            })
            .ToArray();

        Assert.DoesNotContain("n", tokens);
        Assert.DoesNotContain("u", tokens);
        Assert.Contains("h", tokens);
        Assert.Contains("c", tokens);
        Assert.Contains("R", tokens);
        Assert.Contains("m", tokens);
    }

    [Fact]
    public void Browser_Directory_OffersUpDrivesAndNewSession()
    {
        var page = Page(1, generation: 9);

        var tokens = BotMenus.Browser(page, null)
            .InlineKeyboard
            .SelectMany(row => row)
            .Select(button =>
            {
                Assert.True(CallbackData.TryParseFiles(button.CallbackData!, out var generation, out var token));
                Assert.Equal(9, generation);
                return token;
            })
            .ToArray();

        Assert.Contains("u", tokens);
        Assert.Contains("r", tokens);
        Assert.Contains("h", tokens);
        Assert.Contains("c", tokens);
        Assert.Contains("n", tokens);
        Assert.Contains("R", tokens);
        Assert.Contains("m", tokens);
    }

    [Fact]
    public void ReplyMenu_IsPersistentAndEveryButtonMapsToAnAction()
    {
        var markup = BotMenus.ReplyMenu();

        Assert.True(markup.ResizeKeyboard);
        Assert.True(markup.IsPersistent);

        var buttons = markup.Keyboard.SelectMany(row => row).ToArray();
        Assert.Equal(12, buttons.Length);

        var actions = new HashSet<string>();
        foreach (var button in buttons)
        {
            Assert.True(BotMenus.TryParseReply(button.Text, out var action), button.Text);
            Assert.True(actions.Add(action), $"Действие повторяется: {action}");
        }

        Assert.Contains(BotMenus.Actions.Files, actions);
        Assert.Contains(BotMenus.Actions.New, actions);
        Assert.Contains(BotMenus.Actions.Sessions, actions);
        Assert.Contains(BotMenus.Actions.Model, actions);
        Assert.Contains(BotMenus.Actions.Agent, actions);
        Assert.Contains(BotMenus.Actions.Status, actions);
        Assert.Contains(BotMenus.Actions.Screen, actions);
        Assert.Contains(BotMenus.Actions.Diff, actions);
        Assert.Contains(BotMenus.Actions.Cancel, actions);
        Assert.Contains(BotMenus.Actions.Restart, actions);
        Assert.Contains(BotMenus.Actions.Help, actions);
        Assert.Contains(BotMenus.Actions.Main, actions);
    }

    [Fact]
    public void TryParseReply_IgnoresRegularText()
    {
        Assert.False(BotMenus.TryParseReply("посмотри ошибку в логе", out _));
        Assert.False(BotMenus.TryParseReply("/menu", out _));
        Assert.True(BotMenus.TryParseReply($"  {BotMenus.Reply.Menu}  ", out var action));
        Assert.Equal(BotMenus.Actions.Main, action);
    }

    [Fact]
    public void BrowserText_NotesCurrentDirectory()
    {
        var page = Page(1, generation: 1, path: @"C:\work");

        var text = BotMenus.BrowserText(page, @"C:\work\");

        Assert.Contains("Это текущая рабочая папка", text);
        Assert.Contains("C:\\work", text);
    }

    private static BrowserPage Page(int count, int generation, string path = @"C:\temp") => new()
    {
        Generation = generation,
        Path = path,
        Entries = Enumerable.Range(0, count)
            .Select(index => new BrowserEntry
            {
                Name = $"entry-{index:D2}",
                FullPath = $@"{path}\entry-{index:D2}",
                IsDirectory = index % 2 == 0,
            })
            .ToArray(),
    };
}
