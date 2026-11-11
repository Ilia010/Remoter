using Remoter.App.Telegram;

namespace Remoter.Tests;

public class AccessPolicyTests
{
    [Fact]
    public void EmptyWhitelist_AllowsNobody()
    {
        Assert.False(AccessPolicy.IsAllowed(123, []));
        Assert.True(AccessPolicy.IsSetupMode([]));
    }

    [Fact]
    public void Whitelist_AllowsOnlyListedUsers()
    {
        long[] allowed = [123, 456];

        Assert.True(AccessPolicy.IsAllowed(123, allowed));
        Assert.True(AccessPolicy.IsAllowed(456, allowed));
        Assert.False(AccessPolicy.IsAllowed(789, allowed));
        Assert.False(AccessPolicy.IsSetupMode(allowed));
    }
}
