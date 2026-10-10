using System.Text.Json;
using Emergency_Response_Simulator.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Emergency_Response_Simulator.Data;

/// <summary>
/// PostgreSQL + PostGIS schema (Design Document §26). Geometries are stored as WGS84 (SRID 4326)
/// with GiST indexes for spatial queries. Enums are stored as text so the data stays readable.
/// </summary>
public class ErsDbContext(DbContextOptions<ErsDbContext> options) : DbContext(options)
{
    public DbSet<Agency> Agencies => Set<Agency>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<GisLayer> GisLayers => Set<GisLayer>();
    public DbSet<GisFeature> GisFeatures => Set<GisFeature>();
    public DbSet<OperationalPeriod> OperationalPeriods => Set<OperationalPeriod>();
    public DbSet<IncidentActionPlan> IncidentActionPlans => Set<IncidentActionPlan>();
    public DbSet<EventRecord> Events => Set<EventRecord>();

    private const string PointColumn = "geometry(Point,4326)";
    private const string GeometryColumn = "geometry(Geometry,4326)";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        var enumTypes = typeof(Incident).Assembly.GetTypes()
            .Concat(typeof(Core.Events.EventVisibility).Assembly.GetTypes())
            .Where(t => t.IsEnum)
            .Distinct();

        foreach (var enumType in enumTypes)
            configurationBuilder.Properties(enumType).HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");

        modelBuilder.Entity<Agency>(b =>
        {
            b.Property(a => a.Name).HasMaxLength(200);
            b.Property(a => a.ShortName).HasMaxLength(40);
            b.Ignore(a => a.AiControlled); // set by AgencyRegistered events
        });

        modelBuilder.Entity<User>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(200);
            b.HasOne(u => u.Agency).WithMany(a => a.Users).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Resource>(b =>
        {
            b.HasDiscriminator<string>("resource_type")
                .HasValue<Resource>("resource")
                .HasValue<Unit>("unit");
            b.Property(r => r.Name).HasMaxLength(200);
            b.Property(r => r.Location).HasColumnType(PointColumn);
            b.HasIndex(r => r.Location).HasMethod("gist");
            b.HasOne(r => r.Agency).WithMany(a => a.Resources).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Unit>(b =>
        {
            b.Property(u => u.Callsign).HasMaxLength(60);
            b.Property(u => u.HomeStation).HasMaxLength(200);
            b.HasIndex(u => u.Callsign);
            b.HasIndex(u => u.Status);
            // Route tracking is live COP state rebuilt from events; it is not persisted.
            b.Ignore(u => u.PlannedRoute);
            b.Ignore(u => u.RouteDistanceMeters);
            b.Ignore(u => u.Tasking);
            b.HasOne(u => u.AssignedIncident).WithMany(i => i.AssignedUnits)
                .HasForeignKey(u => u.AssignedIncidentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Incident>(b =>
        {
            b.Property(i => i.Number).HasMaxLength(40);
            b.HasIndex(i => i.Number).IsUnique();
            b.Property(i => i.Name).HasMaxLength(200);
            b.Property(i => i.Address).HasMaxLength(400);
            b.Property(i => i.Location).HasColumnType(PointColumn);
            b.HasIndex(i => i.Location).HasMethod("gist");
            b.HasOne(i => i.IncidentCommander).WithMany().OnDelete(DeleteBehavior.SetNull);
            b.Ignore(i => i.Command); // live ICS structure, rebuilt from events
        });

        modelBuilder.Entity<Report>(b =>
        {
            b.Property(r => r.SourceName).HasMaxLength(200);
            b.Property(r => r.Location).HasColumnType(PointColumn);
            b.HasIndex(r => r.ReceivedAt);
            b.HasOne(r => r.Incident).WithMany(i => i.Reports).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Alert>(b =>
        {
            b.Property(a => a.Title).HasMaxLength(200);
            b.HasIndex(a => a.RaisedAt);
            b.HasOne(a => a.Incident).WithMany(i => i.Alerts).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(a => a.Unit).WithMany().OnDelete(DeleteBehavior.SetNull);
            b.HasOne(a => a.AcknowledgedBy).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Zone>(b =>
        {
            b.Property(z => z.Name).HasMaxLength(200);
            b.Property(z => z.Area).HasColumnType(GeometryColumn);
            b.HasIndex(z => z.Area).HasMethod("gist");
            b.HasOne(z => z.Incident).WithMany(i => i.Zones).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<GisLayer>(b =>
        {
            b.Property(l => l.Key).HasMaxLength(60);
            b.HasIndex(l => l.Key).IsUnique();
            b.Property(l => l.Name).HasMaxLength(200);
            b.Property(l => l.Group).HasMaxLength(100);
            b.Property(l => l.Source).HasMaxLength(400);
        });

        modelBuilder.Entity<GisFeature>(b =>
        {
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.Geometry).HasColumnType(GeometryColumn);
            b.HasIndex(f => f.Geometry).HasMethod("gist");
            b.Property(f => f.Properties)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new(),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                        v => v.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key, kv.Value)),
                        v => new Dictionary<string, string>(v)));
            b.HasOne(f => f.Layer).WithMany(l => l.Features).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OperationalPeriod>(b =>
        {
            b.HasIndex(p => new { p.IncidentId, p.Number }).IsUnique();
            b.HasOne(p => p.Incident).WithMany(i => i.OperationalPeriods).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IncidentActionPlan>(b =>
        {
            b.HasIndex(p => new { p.IncidentId, p.OperationalPeriodId, p.Version }).IsUnique();
            // The whole document is one jsonb value, the same shape that travels in the IAP events.
            b.Property(p => p.Content)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<IapContent>(v, (JsonSerializerOptions?)null) ?? new IapContent(),
                    new ValueComparer<IapContent>(
                        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                        v => v.Clone()));
            b.Property(p => p.PreparedBy).HasMaxLength(200);
            b.Property(p => p.SubmittedBy).HasMaxLength(200);
            b.Property(p => p.ApprovedBy).HasMaxLength(200);
            b.Property(p => p.ReturnedBy).HasMaxLength(200);
            b.HasOne(p => p.Incident).WithMany().OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.OperationalPeriod).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EventRecord>(b =>
        {
            b.ToTable("events");
            b.HasKey(e => e.Sequence);
            b.Property(e => e.Sequence).UseIdentityAlwaysColumn();
            b.Property(e => e.Source).HasMaxLength(40);
            b.Property(e => e.Type).HasMaxLength(80);
            b.Property(e => e.Payload).HasColumnType("jsonb");
            b.HasIndex(e => new { e.SessionId, e.Sequence });
            b.HasIndex(e => new { e.SessionId, e.SimTime });
        });
    }
}
