using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Crews;

/// <summary>
/// Makes up the people on a unit (Design Document §12): names, roles, qualifications and how far into their shift they
/// are. Seeded from the call sign, so Engine 4 has the same crew every session.
/// </summary>
public static class CrewRoster
{
    private static readonly string[] FirstNames =
    [
        "Aoife", "Ciarán", "Niamh", "Seán", "Siobhán", "Darragh", "Orla", "Cian", "Gráinne", "Eoin", "Róisín", "Pádraig",
        "Clodagh", "Conor", "Sinéad", "Declan", "Aisling", "Fionn", "Méabh", "Tadhg", "Deirdre", "Ronan", "Laura", "Mark",
        "Emma", "David", "Sarah", "James", "Grace", "Paul", "Tomasz", "Ana",
    ];

    private static readonly string[] Surnames =
    [
        "Byrne", "Walsh", "Murphy", "Kelly", "O'Brien", "Ryan", "O'Sullivan", "Doyle", "McCarthy", "Gallagher", "Doherty",
        "Kennedy", "Lynch", "Murray", "Quinn", "Moore", "McLoughlin", "Carroll", "Connolly", "Daly", "Brennan", "Farrell",
        "Fitzgerald", "Nolan", "Whelan", "Dunne", "Kavanagh", "Keane", "Hayes", "Power", "Nowak", "Costa",
    ];

    /// <param name="lapsed">Qualifications whose certificate has run out for one member who holds them.</param>
    /// <param name="salt">Different people for the same unit (relief crews).</param>
    public static IReadOnlyList<CrewMemberInfo> Generate(string callsign, UnitType type, int crewSize, IReadOnlyCollection<string> capabilities,
        IReadOnlyCollection<string>? lapsed = null, int salt = 0)
    {
        var random = new Random(Seed(callsign) ^ (salt * 7919));
        var agency = ResourceGroups.AgencyFor(type);
        var size = crewSize > 0 ? crewSize : agency == AgencyType.Fire ? 4 : 2;
        var roles = Roles(type, agency, size, capabilities);

        var used = new HashSet<string>();
        var members = new List<(CrewMemberInfo Info, List<string> Held, List<string> Lapsed)>();
        foreach (var role in roles)
        {
            string name;
            do name = $"{FirstNames[random.Next(FirstNames.Length)]} {Surnames[random.Next(Surnames.Length)]}";
            while (!used.Add(name.Split(' ')[^1]));
            var held = Qualify(role, type, capabilities, random);
            members.Add((new CrewMemberInfo(Derive(callsign, salt, members.Count), name, role, held), held, []));
        }

        // Certificates that have run out: the first member holding each loses it.
        foreach (var qualification in lapsed ?? [])
        {
            if (members.FirstOrDefault(m => m.Held.Contains(qualification)) is { Info: not null } member)
            {
                member.Held.Remove(qualification);
                member.Lapsed.Add(qualification);
            }
        }

        return members.Select(m => m.Info with { Qualifications = m.Held.ToList(), Lapsed = m.Lapsed.Count > 0 ? m.Lapsed.ToList() : null })
            .ToList();
    }

    /// <summary>Length of a shift: fire brigade 10 h (day watch), ambulance 12 h, Garda 10 h, others 8 h.</summary>
    public static TimeSpan ShiftLength(UnitType type) => ResourceGroups.AgencyFor(type) switch
    {
        AgencyType.Fire or AgencyType.Police => TimeSpan.FromHours(10),
        AgencyType.Ems => TimeSpan.FromHours(12),
        _ => TimeSpan.FromHours(8),
    };

    /// <summary>How long the crew has already been on shift: anywhere from an hour in to near the end.</summary>
    public static TimeSpan DefaultOnShift(string callsign, UnitType type)
    {
        var fraction = 0.1 + 0.8 * new Random(Seed(callsign) ^ 0x5f3759df).NextDouble();
        return TimeSpan.FromMinutes(Math.Round(ShiftLength(type).TotalMinutes * fraction / 5) * 5);
    }

    private static List<CrewRole> Roles(UnitType type, AgencyType agency, int size, IReadOnlyCollection<string> capabilities)
    {
        var roles = new List<CrewRole>();
        switch (agency)
        {
            case AgencyType.Fire:
                roles.Add(CrewRole.Officer);
                if (size > 1) roles.Add(CrewRole.Driver);
                while (roles.Count < size) roles.Add(CrewRole.Firefighter);
                break;
            case AgencyType.Ems:
                var als = type is UnitType.AmbulanceAls or UnitType.MedicalSupervisor or UnitType.MedevacHelicopter
                          || capabilities.Contains(Qualifications.Als);
                roles.Add(als ? CrewRole.Paramedic : CrewRole.Emt);
                while (roles.Count < size) roles.Add(CrewRole.Emt);
                break;
            case AgencyType.Police:
                while (roles.Count < size) roles.Add(CrewRole.Garda);
                break;
            default:
                while (roles.Count < size) roles.Add(CrewRole.Technician);
                break;
        }
        return roles;
    }

    private static List<string> Qualify(CrewRole role, UnitType type, IReadOnlyCollection<string> capabilities, Random random)
    {
        var held = new List<string>();
        var fireground = role is CrewRole.Officer or CrewRole.Firefighter || (role == CrewRole.Driver && random.NextDouble() < 0.5);
        if (fireground && (capabilities.Contains("BreathingApparatus") || type is UnitType.Engine or UnitType.Ladder or UnitType.Rescue or UnitType.Hazmat))
            held.Add(Qualifications.BreathingApparatus);
        if (role != CrewRole.Driver && (type == UnitType.Hazmat || capabilities.Contains(Qualifications.Hazmat)) && role is not (CrewRole.Paramedic or CrewRole.Emt or CrewRole.Garda))
            held.Add(Qualifications.Hazmat);
        if (type == UnitType.SearchAndRescue
            || (role is CrewRole.Officer or CrewRole.Firefighter && (capabilities.Contains("Rescue") || type == UnitType.Rescue) && random.NextDouble() < 0.6)
            || (role == CrewRole.Firefighter && random.NextDouble() < 0.08))
            held.Add(Qualifications.Usar);
        if ((capabilities.Contains(Qualifications.Swiftwater) && role != CrewRole.Driver)
            || (role == CrewRole.Firefighter && random.NextDouble() < 0.12))
            held.Add(Qualifications.Swiftwater);
        if (role == CrewRole.Paramedic) held.Add(Qualifications.Als);
        if (role is CrewRole.Paramedic or CrewRole.Emt) held.Add(Qualifications.Bls);
        return held;
    }

    /// <summary>A stable id for the n-th member of a unit's crew.</summary>
    private static Guid Derive(string callsign, int salt, int index)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(Seed(callsign)).CopyTo(bytes, 0);
        BitConverter.GetBytes(salt).CopyTo(bytes, 4);
        BitConverter.GetBytes(index).CopyTo(bytes, 8);
        BitConverter.GetBytes(0x43524557).CopyTo(bytes, 12); // "CREW"
        return new Guid(bytes);
    }

    /// <summary>FNV-1a: string.GetHashCode changes between runs, this doesn't.</summary>
    private static int Seed(string text)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }
}
