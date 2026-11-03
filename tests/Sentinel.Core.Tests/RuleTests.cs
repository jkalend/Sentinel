using Sentinel.Core.Rules;

namespace Sentinel.Core.Tests;

public class RuleTests
{
    private const string EncodedPowerShell = "sentinel.process.encoded-powershell";
    private const string TempExecutable = "sentinel.process.temp-executable";
    private const string ScriptHostEgress = "sentinel.network.script-host-nonstandard-egress";

    [Fact]
    public void Loads_three_versioned_rules()
    {
        var rules = TestSupport.Rules();
        Assert.Equal(3, rules.Rules.Count);
        Assert.Equal("2", rules.Rules.Single(rule => rule.Id == ScriptHostEgress).Version);
        Assert.All(
            rules.Rules.Where(rule => rule.Id != ScriptHostEgress),
            rule => Assert.Equal("1", rule.Version));
    }

    [Theory]
    [InlineData("pwsh.exe")]
    [InlineData("cscript.exe")]
    public void Script_host_aliases_match(string processName)
    {
        var fixture = TestSupport.Fixture();
        var baseline = TestSupport.LocalEvent(fixture, 1002);
        var local = baseline with
        {
            Process = baseline.Process! with { Name = processName }
        };

        Assert.Contains(
            RuleEvaluator.Evaluate(local, TestSupport.Rules()),
            alert => alert.RuleId == ScriptHostEgress);
    }

    [Theory]
    [InlineData(1001, EncodedPowerShell, true)]
    [InlineData(1003, EncodedPowerShell, false)]
    [InlineData(1005, EncodedPowerShell, false)]
    [InlineData(1008, EncodedPowerShell, false)]
    [InlineData(1005, TempExecutable, true)]
    [InlineData(1001, TempExecutable, false)]
    [InlineData(1003, TempExecutable, false)]
    [InlineData(1008, TempExecutable, false)]
    [InlineData(1002, ScriptHostEgress, true)]
    [InlineData(1004, ScriptHostEgress, false)]
    [InlineData(1006, ScriptHostEgress, false)]
    [InlineData(1007, ScriptHostEgress, false)]
    [InlineData(1010, ScriptHostEgress, false)]
    public void Truth_table_matches_fixture(long recordId, string ruleId, bool expected)
    {
        var fixture = TestSupport.Fixture();
        var local = TestSupport.LocalEvent(fixture, recordId);
        var matches = RuleEvaluator
            .Evaluate(local, TestSupport.Rules())
            .Any(alert => alert.RuleId == ruleId);

        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("pwsh.exe", "pwsh.exe -ec SQBFAFgA", true)]
    [InlineData("powershell.exe", "powershell.exe -enc SQBFAFgA", true)]
    [InlineData("powershell.exe", "powershell.exe -e SQBFAFgA", true)]
    [InlineData("powershell.exe", "powershell.exe -ExecutionPolicy Bypass -File x.ps1", false)]
    public void Encoded_powershell_aliases_match_without_execution_policy_false_positive(
        string processName,
        string commandLine,
        bool expected)
    {
        var fixture = TestSupport.Fixture();
        var baseline = TestSupport.LocalEvent(fixture, 1001);
        var local = baseline with
        {
            Process = baseline.Process! with
            {
                Name = processName,
                CommandLine = commandLine
            }
        };

        var matches = RuleEvaluator
            .Evaluate(local, TestSupport.Rules())
            .Any(alert => alert.RuleId == EncodedPowerShell);

        Assert.Equal(expected, matches);
    }

    [Fact]
    public void Exists_operator_accepts_yaml_boolean()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                id: test.exists
                version: 1
                title: Exists operator
                detection:
                  selection:
                    process.cmd_line:
                      exists: true
                  condition: selection
                """);

            var rule = RuleLoader.LoadFile(path);
            var condition = Assert.Single(rule.Selection);
            Assert.Equal("exists", condition.Operator);
            Assert.True(condition.Exists);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unsupported_operator_fails_closed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                id: test.unsupported
                version: 1
                title: Unsupported operator
                detection:
                  selection:
                    process.name:
                      regex: '.*'
                  condition: selection
                """);

            Assert.Throws<UnsupportedRuleException>(() => RuleLoader.LoadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unsupported_condition_fails_closed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                id: test.correlation
                version: 1
                title: Unsupported condition
                detection:
                  selection:
                    process.name: powershell.exe
                  condition: selection and selection
                """);

            Assert.Throws<UnsupportedRuleException>(() => RuleLoader.LoadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Duplicate_rule_identity_is_rejected()
    {
        var rule = RuleLoader.LoadFile(Path.Combine(TestSupport.RulesPath, "encoded-powershell.yaml"));
        Assert.Throws<UnsupportedRuleException>(() => new RuleSet([rule, rule]));
    }
}
