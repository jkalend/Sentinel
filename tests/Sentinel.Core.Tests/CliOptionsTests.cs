using Sentinel.Core.Cli;

namespace Sentinel.Core.Tests;

public class CliOptionsTests
{
    private static readonly Dictionary<string, bool> Spec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["db"] = true,
        ["apply"] = false
    };

    [Fact]
    public void Value_option_and_flag_parse_together()
    {
        var options = CliOptions.Parse(["--db", "store.db", "--apply"]);

        Assert.Equal("store.db", CliOptions.Get(options, "DB"));
        Assert.True(options.ContainsKey("apply"));
        Assert.Null(options["apply"]);
    }

    [Fact]
    public void Positional_argument_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() => CliOptions.Parse(["store.db"]));
        Assert.Contains("Unexpected argument 'store.db'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_option_fails_instead_of_being_ignored()
    {
        var options = CliOptions.Parse(["--tokn", "secret"]);
        var error = Assert.Throws<ArgumentException>(
            () => CliOptions.Validate("collector", options, Spec));

        Assert.Contains("--tokn", error.Message, StringComparison.Ordinal);
        Assert.Contains("collector", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_option_without_a_value_fails()
    {
        var options = CliOptions.Parse(["--db", "--apply"]);
        var error = Assert.Throws<ArgumentException>(
            () => CliOptions.Validate("collector", options, Spec));

        Assert.Contains("--db requires a value", error.Message, StringComparison.Ordinal);
        Assert.Null(CliOptions.Get(options, "db"));
        Assert.True(options.ContainsKey("apply"));
    }

    [Fact]
    public void Flag_with_a_value_fails()
    {
        var options = CliOptions.Parse(["--apply", "yes"]);
        var error = Assert.Throws<ArgumentException>(
            () => CliOptions.Validate("purge", options, Spec));

        Assert.Contains("--apply does not take a value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_options_validate()
    {
        var options = CliOptions.Parse(["--db", "store.db", "--apply"]);

        CliOptions.Validate("purge", options, Spec);
    }

    [Fact]
    public void Integer_option_rejects_non_numeric_value()
    {
        var options = CliOptions.Parse(["--limit", "many"]);

        var error = Assert.Throws<ArgumentException>(
            () => CliOptions.Integer(options, "limit", 10));
        Assert.Contains("--limit must be an integer", error.Message, StringComparison.Ordinal);
        Assert.Equal(10, CliOptions.Integer(options, "missing", 10));
    }
}
