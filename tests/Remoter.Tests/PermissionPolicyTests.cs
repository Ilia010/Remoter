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
    public void Describe_KnownActionsHaveLabels()
    {
        Assert.Contains("терминал", PermissionPolicy.Describe("shell"));
        Assert.Contains("Изменение", PermissionPolicy.Describe("edit"));
        Assert.Contains("shell-unknown", PermissionPolicy.Describe("shell-unknown"));
    }
}
