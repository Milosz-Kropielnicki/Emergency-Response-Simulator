using System.Windows;
using System.Windows.Media;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels.Iap;

/// <summary>One box on the ICS organisation chart.</summary>
public sealed record OrgNode(
    double X,
    double Y,
    double Width,
    double Height,
    string Title,
    string Name,
    IReadOnlyList<OrgMember> Members,
    bool IsVacant,
    string? Span,
    bool IsOverloaded);

/// <summary>A unit listed in a group box; flagged when the COP says it can't do the job.</summary>
public sealed record OrgMember(string Text, bool HasProblem);

/// <summary>A drawn ICS organisation chart (Design Document §8.1, ICS 207).</summary>
public sealed record OrgChart(IReadOnlyList<OrgNode> Nodes, IReadOnlyList<PointCollection> Lines, double Width, double Height)
{
    public static OrgChart Empty { get; } = new([], [], 0, 0);
}

/// <summary>
/// Lays out the chart the plan describes: the IC at the top, command staff off the trunk, the four
/// sections below, and the groups and divisions under Operations with their units.
/// </summary>
public static class OrgChartLayout
{
    private const double BoxWidth = 172;
    private const double BoxHeight = 44;
    private const double MemberHeight = 15;
    private const double Gap = 18;
    private const double Drop = 34;
    private const double Margin = 12;

    public static OrgChart Build(IapContent plan, Func<Guid, Unit?> findUnit)
    {
        var command = IapCompliance.ToCommand(plan);
        var assigned = plan.Assignments.Select(a => a.UnitId).Distinct().ToList();
        var spans = SpanOfControl.Assess(command, assigned).ToDictionary(s => s.Supervisor);
        var nodes = new List<OrgNode>();
        var lines = new List<PointCollection>();

        OrgMember Member(IapAssignment a)
        {
            var unit = findUnit(a.UnitId);
            var lost = unit is null || unit.Status == UnitStatus.OutOfService;
            var status = unit is null ? "not on roster" : unit.Status == UnitStatus.OutOfService ? "OUT OF SERVICE" : null;
            var task = string.IsNullOrWhiteSpace(a.Assignment) ? "" : $" — {a.Assignment}";
            return new OrgMember($"{a.Callsign}{task}" + (status is null ? "" : $" ({status})"), lost);
        }

        // Children of Operations: each group or division, plus single resources reporting directly.
        var children = new List<(string Title, string Name, List<OrgMember> Members, string? SpanKey)>();
        foreach (var group in plan.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
        {
            var members = plan.Assignments
                .Where(a => string.Equals(a.Group?.Trim(), group.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(Member).ToList();
            children.Add((group.Name, group.Supervisor ?? "No supervisor", members, group.Name));
        }
        var direct = plan.Assignments.Where(a => string.IsNullOrWhiteSpace(a.Group)).Select(Member).ToList();
        if (direct.Count > 0)
            children.Add(("Single resources", "Report to Operations", direct, null));

        double ChildHeight(int members) => BoxHeight + members * MemberHeight + (members > 0 ? 4 : 0);
        var childrenWidth = children.Count == 0 ? 0 : children.Count * BoxWidth + (children.Count - 1) * Gap;

        // General staff row: Operations (as wide as its children), Planning, Logistics, Finance/Admin.
        IcsRole[] sections = [IcsRole.Operations, IcsRole.Planning, IcsRole.Logistics, IcsRole.FinanceAdmin];
        var widths = sections.Select(r => r == IcsRole.Operations ? Math.Max(BoxWidth, childrenWidth) : BoxWidth).ToArray();
        var rowWidth = widths.Sum() + (sections.Length - 1) * Gap;

        // Command staff hang off the right of the trunk, so leave room for them.
        IcsRole[] staff = [IcsRole.Safety, IcsRole.Liaison, IcsRole.PublicInformation];
        var width = Math.Max(rowWidth, BoxWidth * 2 + 40) + Margin * 2;
        var trunkX = Margin + rowWidth / 2;
        if (trunkX + 30 + BoxWidth > width - Margin)
            width = trunkX + 30 + BoxWidth + Margin;

        string? SpanText(string supervisor) =>
            spans.TryGetValue(supervisor, out var span) && span.DirectReports > 0 ? $"{span.DirectReports} direct report{(span.DirectReports == 1 ? "" : "s")}" : null;
        bool Over(string supervisor) => spans.TryGetValue(supervisor, out var span) && span.IsOverloaded;

        // Incident Commander.
        var icName = plan.NameFor(IcsRole.IncidentCommander);
        nodes.Add(new OrgNode(trunkX - BoxWidth / 2, Margin, BoxWidth, BoxHeight, "Incident Commander", icName ?? "Vacant", [],
            icName is null, SpanText("Incident Commander"), Over("Incident Commander")));
        var y = Margin + BoxHeight + 14;

        foreach (var role in staff)
        {
            var name = plan.NameFor(role);
            nodes.Add(new OrgNode(trunkX + 30, y, BoxWidth, BoxHeight - 6, IapDocument.RoleName(role), name ?? "Vacant (IC retains)", [],
                name is null, null, false));
            lines.Add([new Point(trunkX, y + (BoxHeight - 6) / 2), new Point(trunkX + 30, y + (BoxHeight - 6) / 2)]);
            y += BoxHeight - 6 + 8;
        }

        var sectionY = y + Drop;
        var barY = sectionY - Drop / 2;
        lines.Add([new Point(trunkX, Margin + BoxHeight), new Point(trunkX, barY)]);

        var x = Margin;
        var centres = new List<double>();
        for (var i = 0; i < sections.Length; i++)
        {
            var role = sections[i];
            var centre = x + widths[i] / 2;
            centres.Add(centre);
            var name = plan.NameFor(role);
            var supervisor = role == IcsRole.Operations ? "Operations" : "";
            nodes.Add(new OrgNode(centre - BoxWidth / 2, sectionY, BoxWidth, BoxHeight, IapDocument.RoleName(role), name ?? "Vacant (IC retains)", [],
                name is null, role == IcsRole.Operations ? SpanText(supervisor) : null, role == IcsRole.Operations && Over(supervisor)));
            lines.Add([new Point(centre, barY), new Point(centre, sectionY)]);
            x += widths[i] + Gap;
        }
        lines.Add([new Point(centres[0], barY), new Point(centres[^1], barY)]);

        var height = sectionY + BoxHeight + Margin;
        if (children.Count > 0)
        {
            var opsCentre = centres[0];
            var childY = sectionY + BoxHeight + Drop;
            var childBar = childY - Drop / 2;
            lines.Add([new Point(opsCentre, sectionY + BoxHeight), new Point(opsCentre, childBar)]);

            var childX = opsCentre - childrenWidth / 2;
            var childCentres = new List<double>();
            foreach (var (title, name, members, spanKey) in children)
            {
                var h = ChildHeight(members.Count);
                nodes.Add(new OrgNode(childX, childY, BoxWidth, h, title, name, members, false,
                    spanKey is null ? null : SpanText(spanKey), spanKey is not null && Over(spanKey)));
                childCentres.Add(childX + BoxWidth / 2);
                lines.Add([new Point(childX + BoxWidth / 2, childBar), new Point(childX + BoxWidth / 2, childY)]);
                childX += BoxWidth + Gap;
                height = Math.Max(height, childY + h + Margin);
            }
            lines.Add([new Point(childCentres[0], childBar), new Point(childCentres[^1], childBar)]);
        }

        foreach (var line in lines)
            line.Freeze();
        return new OrgChart(nodes, lines, width, height);
    }
}
