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
                logsource:
                  category: process_creation
                  product: windows
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
            Assert.Empty(RuleEvaluator.Evaluate(TestSupport.LocalEvent(fixture, 1002), rules));
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

    [Fact]
    public void Duplicate_output_names_are_rejected()
    {
        var input = NewDirectory();
        var output = NewDirectory();
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllText(Path.Combine(input, "a.yml"), Rule("a/b"));
            File.WriteAllText(Path.Combine(input, "b.yml"), Rule("a-b"));

            var error = Assert.Throws<UnsupportedRuleException>(
                () => SigmaImporter.ImportDirectory(input, output));
            Assert.Contains("same output file", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(output) && Directory.EnumerateFiles(output).Any());
        }
        finally
        {
            Directory.Delete(input, recursive: true);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public void Repeated_mapped_field_and_wildcard_patterns_are_rejected()
    {
        var duplicateField = """
        title: Duplicate field
        id: test.dup
        logsource:
          category: process_creation
          product: windows
        detection:
          selection:
            Image|endswith: '\powershell.exe'
            Image|startswith: 'C:\Windows'
          condition: selection
        """;
        Assert.Throws<UnsupportedRuleException>(
            () => SigmaImporter.ConvertRule(duplicateField, "dup.yml"));

        var wildcard = Rule("test.wild").Replace("'\\powershell.exe'", "'*\\powershell.exe'");
        Assert.Throws<UnsupportedRuleException>(
            () => SigmaImporter.ConvertRule(wildcard, "wild.yml"));

        var cidr = Rule("test.cidr")
            .Replace("Image|endswith: '\\powershell.exe'", "DestinationIp: '10.0.0.0/8'");
        Assert.Throws<UnsupportedRuleException>(
            () => SigmaImporter.ConvertRule(cidr, "cidr.yml"));
    }

    [Fact]
    public void List_prefix_and_suffix_modifiers_are_supported()
    {
        var rule = Rule("test.list").Replace(
            "Image|endswith: '\\powershell.exe'",
            "Image|endswith: ['\\powershell.exe', '\\cmd.exe']");
        var (yaml, ruleId) = SigmaImporter.ConvertRule(rule, "list.yml");
        Assert.Equal("sigma.test.list", ruleId);
        Assert.Contains("ends_with", yaml, StringComparison.Ordinal);
        Assert.Contains("cmd.exe", yaml, StringComparison.Ordinal);
    }


    [Fact]
    public void Empty_alternatives_and_logsource_service_are_rejected()
    {
        var empty = Rule("test.empty").Replace(
            "Image|endswith: '\\powershell.exe'",
            "Image|endswith: ['', '\\cmd.exe']");
        Assert.Throws<UnsupportedRuleException>(
            () => SigmaImporter.ConvertRule(empty, "empty.yml"));

        var service = Rule("test.service").Replace(
            "  category: process_creation",
            "  category: process_creation\n  service: security");
        Assert.Throws<UnsupportedRuleException>(
            () => SigmaImporter.ConvertRule(service, "service.yml"));
    }

    private static string Rule(string id) =>
        $"""
        title: Test rule {id}
        id: {id}
        logsource:
          category: process_creation
          product: windows
        detection:
          selection:
            Image|endswith: '\powershell.exe'
          condition: selection
        """;
}
