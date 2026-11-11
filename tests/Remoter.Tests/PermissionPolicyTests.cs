using Remoter.App.OpenCode;

namespace Remoter.Tests;

public class PermissionPolicyTests
{
    [Fact]
    public void Default_AllowsEverythingByDefaultAndAsksForDangerousActions()
    {
        var rules = PermissionPolicy.Default();

        Assert.Contains(rules, rule => rule.Action == "*" && rule.Effect == "allow");

        foreach (var action in new[] { "read", "glob", "grep", "external_directory" })
            Assert.Contains(rules, rule => rule.Action == action && rule.Effect == "allow");

        foreach (var action in new[] { "edit", "shell", "webfetch", "websearch", "subagent" })
            Assert.Contains(rules, rule => rule.Action == action && rule.Effect == "ask");

        Assert.Contains(rules, rule => rule.Action == "question" && rule.Effect == "deny");
    }

    [Fact]
    public void Default_AllowsEnvExampleBeforeAskingForEnvFiles()
    {
        var rules = PermissionPolicy.Default().ToList();

        var allowExample = rules.FindIndex(rule => rule.Resource == "*.env.example" && rule.Effect == "allow");
        var askEnv = rules.FindIndex(rule => rule.Resource == "*.env" && rule.Effect == "ask");

        Assert.True(allowExample >= 0, "нет правила allow для *.env.example");
        Assert.True(askEnv >= 0, "нет правила ask для *.env");
        Assert.True(allowExample < askEnv, "*.env.example должен проверяться раньше *.env");
    }

    [Fact]
    public void AllowAll_AllowsEverythingExceptQuestion()
    {
        var rules = PermissionPolicy.AllowAll();

        Assert.Contains(rules, rule => rule.Action == "*" && rule.Resource == "*" && rule.Effect == "allow");
        Assert.Contains(rules, rule => rule.Action == "question" && rule.Effect == "deny");
        Assert.DoesNotContain(rules, rule => rule.Effect == "ask");
    }

    [Fact]
    public void For_SelectsPolicyByAutoApprove()
    {
        Assert.Contains(PermissionPolicy.For(true), rule => rule.Action == "*" && rule.Effect == "allow");
        Assert.DoesNotContain(PermissionPolicy.For(true), rule => rule.Effect == "ask");
        Assert.Contains(PermissionPolicy.For(false), rule => rule.Action == "edit" && rule.Effect == "ask");
    }

    [Fact]
    public void AreEqual_ComparesRulesInOrder()
    {
        var allowAll = PermissionPolicy.For(true);
        var same = PermissionPolicy.For(true);

        Assert.True(PermissionPolicy.AreEqual(allowAll, same));
        Assert.False(PermissionPolicy.AreEqual(allowAll, PermissionPolicy.For(false)));
        Assert.False(PermissionPolicy.AreEqual(null, allowAll));
        Assert.False(PermissionPolicy.AreEqual(allowAll.Reverse().ToArray(), allowAll));
    }

    [Fact]
    public void Describe_KnownActionsHaveLabels()
    {
        Assert.Contains("терминал", PermissionPolicy.Describe("shell"));
        Assert.Contains("Изменение", PermissionPolicy.Describe("edit"));
        Assert.Contains("shell-unknown", PermissionPolicy.Describe("shell-unknown"));
    }
}
