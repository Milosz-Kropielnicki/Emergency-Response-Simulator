using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// Anything command can allocate: personnel, equipment, supplies, facilities, teams.
/// Mobile apparatus are modelled by the <see cref="Unit"/> subclass.
/// </summary>
public class Resource
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public ResourceKind Kind { get; set; }
    public int Quantity { get; set; } = 1;

    public Point? Location { get; set; }

    public Guid? AgencyId { get; set; }
    public Agency? Agency { get; set; }
}
