using Sentinel.Core.Fixture;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;
using Sentinel.Core.Store;
using Sentinel.Core.Timeline;

namespace Sentinel.Core.Tests;

public class ProcessTerminateTests
{
    [Fact]
    public void Event_id_5_maps_to_process_terminate_and_validates()
    {
        var fixture = FixtureSet.Load(TestSupport.TerminateFixturePath);
        var record = Assert.Single(fixture.Records);
        var mapped = OcsfMapper.Map(
            record,
            new MappingContext(
                fixture.SensorId!,
                fixture.HostId!,
                fixture.IncarnationId,
                DateTimeOffset.Parse("2026-11-16T10:01:00Z")),
            NetworkOutcome.Unknown);

        Assert.Equal(RecordKind.Data, mapped.Kind);
        Assert.Equal(OcsfMapper.ProcessActivityClassUid, mapped.ClassUid);
        Assert.Equal(OcsfMapper.ProcessTerminateActivityId, mapped.ActivityId);
        Assert.Equal("notepad.exe", mapped.Process?.Name);
        Assert.Equal("WS1\\alice", mapped.UserName);

        var ocsf = System.Text.Json.Nodes.JsonNode.Parse(mapped.OcsfJson!)!.AsObject();
        Assert.Equal(2, ocsf["activity_id"]!.GetValue<int>());
        Assert.Equal("Terminate", ocsf["activity_name"]!.GetValue<string>());
        Assert.Equal(100702, ocsf["type_uid"]!.GetValue<int>());
        Assert.Equal(
            "Process Activity: Terminate",
            ocsf["type_name"]!.GetValue<string>());

        var validator = new OcsfSchemaValidator(TestSupport.SchemaPath);
        Assert.Empty(validator.Validate(ocsf, "events/system/process_activity.json"));
    }

    [Fact]
    public void Terminate_fixture_ingests_without_alerts_and_renders()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var fixture = FixtureSet.Load(TestSupport.TerminateFixturePath);
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var report = new IngestionService(store, sensorId, hostId).Ingest(
                fixture.Records,
                TestSupport.Rules(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            Assert.Equal(1, report.Accepted);
            Assert.Equal(0, report.Alerts);
            Assert.Single(store.ReadEvents());
            Assert.Contains("notepad.exe", TimelineBuilder.Render(store.ReadEvents(), store.ReadPurgedRanges()));
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }
}
