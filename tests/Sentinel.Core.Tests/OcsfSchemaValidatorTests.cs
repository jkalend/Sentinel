using System.Text.Json.Nodes;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;

namespace Sentinel.Core.Tests;

public class OcsfSchemaValidatorTests
{
    private static readonly MappingContext Context = new(
        "sensor-test",
        "host-test",
        "incarnation-test",
        DateTimeOffset.Parse("2026-11-02T11:00:00Z"));

    private static JsonObject Map(long recordId)
    {
        var fixture = TestSupport.Fixture();
        var record = fixture.Records.Single(value => value.EventRecordId == recordId);
        var outcome = fixture.GroundTruth.TryGetValue(recordId, out var value)
            ? value
            : NetworkOutcome.Unknown;
        return JsonNode.Parse(OcsfMapper.Map(record, Context, outcome).OcsfJson!)!.AsObject();
    }

    [Fact]
    public void Missing_process_is_rejected()
    {
        var ocsf = Map(1001);
        ocsf.Remove("process");

        var errors = new OcsfSchemaValidator(TestSupport.SchemaPath)
            .Validate(ocsf, "events/system/process_activity.json");

        Assert.Contains(errors, error => error.Contains("process", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_device_is_rejected()
    {
        var ocsf = Map(1001);
        ocsf.Remove("device");

        var errors = new OcsfSchemaValidator(TestSupport.SchemaPath)
            .Validate(ocsf, "events/system/process_activity.json");

        Assert.Contains(errors, error => error.Contains("device", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_metadata_version_is_rejected()
    {
        var ocsf = Map(1001);
        ocsf["metadata"]!.AsObject().Remove("version");

        var errors = new OcsfSchemaValidator(TestSupport.SchemaPath)
            .Validate(ocsf, "events/system/process_activity.json");

        Assert.Contains(errors, error => error.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_connection_direction_is_rejected()
    {
        var ocsf = Map(1002);
        ocsf["connection_info"]!.AsObject().Remove("direction_id");

        var errors = new OcsfSchemaValidator(TestSupport.SchemaPath)
            .Validate(ocsf, "events/network/network_activity.json");

        Assert.Contains(errors, error => error.Contains("direction_id", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_type_uid_is_rejected()
    {
        var ocsf = Map(1001);
        ocsf["type_uid"] = 100702;

        var errors = new OcsfSchemaValidator(TestSupport.SchemaPath)
            .Validate(ocsf, "events/system/process_activity.json");

        Assert.Contains(errors, error => error.Contains("type_uid", StringComparison.Ordinal));
    }
}
