using System.Text.Json.Serialization;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Tests;

public class EventJsonTests
{
    [Fact]
    public void Every_event_type_is_registered_for_polymorphic_json()
    {
        var registered = typeof(DomainEvent)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();

        var all = typeof(DomainEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DomainEvent)) && !t.IsAbstract);

        Assert.All(all, t => Assert.Contains(t, registered));
    }

    [Fact]
    public void Events_round_trip_with_type_discriminator_and_readable_enums()
    {
        DomainEvent original = new ZoneDeclared(
            Guid.NewGuid(), ZoneType.HotZone, "Hot zone A",
            [new GeoPoint(53.1, -6.1), new GeoPoint(53.2, -6.1), new GeoPoint(53.2, -6.2)], null);

        var json = EventJson.Serialize(original);
        var copy = (ZoneDeclared)EventJson.Deserialize(json);

        Assert.Contains("\"$type\":\"ZoneDeclared\"", json);
        Assert.Contains("\"HotZone\"", json);
        Assert.Equal(original, copy with { Boundary = ((ZoneDeclared)original).Boundary });
        Assert.Equal(((ZoneDeclared)original).Boundary, copy.Boundary);
    }
}
