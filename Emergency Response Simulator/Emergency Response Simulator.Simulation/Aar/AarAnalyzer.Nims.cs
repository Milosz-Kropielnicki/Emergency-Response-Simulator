using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// NIMS/ICS compliance in the review (Phase 20): the check run on the COP as it stood at the end. Gaps are findings to
/// work on; a high score with requirements met is a strength.
/// </summary>
public sealed partial class AarAnalyzer
{
    /// <summary>How many NIMS gaps are listed among the findings; the rest are in the NIMS section.</summary>
    private const int NimsGapsListed = 4;

    private static IEnumerable<AarFinding> NimsFindings(NimsReport nims)
    {
        if (nims.Findings.Count == 0) yield break;
        foreach (var gap in nims.Findings.Where(f => f.Severity == NimsSeverity.Gap).Take(NimsGapsListed))
            yield return new AarFinding(false, $"NIMS/ICS: {gap.Message}");
        if (nims.Count(NimsSeverity.Gap) == 0 && nims.Count(NimsSeverity.Met) >= 3)
            yield return new AarFinding(true, $"NIMS/ICS: {nims.Count(NimsSeverity.Met)} requirements met and no gaps (score {nims.Score}).");
    }
}
