using Sentinel.Core.Model;
using Sentinel.Core.Rules;

namespace Sentinel.Core.Tests;

public class SigmaImporterTests
{
    [Fact]
    public void Supported_sigma_rule_imports_and_evaluates()
    {
        var input = NewDirectory();
        var output = NewDirectory();
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllText(
                Path.Combine(input, "powershell.yml"),
                """
                title: PowerShell Execution
                id: test.powershell
                status: experimental
                author: test
                detection:
                  selection:
                    Image|endswith: '\powershell.exe'
                  condition: selection
                """);

            var results = SigmaImporter.ImportDirectory(input, output);

            var imported = Assert.Single(results);
            Assert.Equal("sigma.test.powershell", imported.RuleId);
            var rules = RuleLoader.LoadDirectory(output);
            var fixture = TestSupport.Fixture();
            var alerts = fixture.Records
                .Select(record => TestSupport.ToLocal(fixture, record))
                .SelectMany(value => RuleEvaluator.Evaluate(value, rules))
                .ToList();
            Assert.NotEmpty(alerts);
            Assert.All(alerts, alert => Assert.Equal("sigma.test.powershell", alert.RuleId));
        }
        finally
        {
            Directory.Delete(input, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Unsupported_condition_is_rejected()
    {
        var error = Assert.Throws<UnsupportedRuleException>(() => SigmaImporter.ConvertRule(
            """
            title: Unsupported
            id: test.unsupported
            detection:
              selection:
                Image|endswith: '\cmd.exe'
              condition: selection and not filter
            """,
            "unsupported.yml"));

        Assert.Contains("condition", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_field_is_rejected()
    {
        var error = Assert.Throws<UnsupportedRuleException>(() => SigmaImporter.ConvertRule(
            """
            title: Unsupported
            id: test.unsupported
            detection:
              selection:
                ParentImage|endswith: '\cmd.exe'
              condition: selection
            """,
            "unsupported.yml"));

        Assert.Contains("ParentImage", error.Message, StringComparison.Ordinal);
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"sentinel-sigma-{Guid.NewGuid():N}");
}
