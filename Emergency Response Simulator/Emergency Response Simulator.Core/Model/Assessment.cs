namespace Emergency_Response_Simulator.Core.Model;

/// <summary>The assessment rubric (Phase 11 M6).</summary>
public enum AssessmentRating
{
    Competent,
    Developing,
    NotYetCompetent,
}

/// <summary>
/// What an assessment note is about (Phase 12): the competency areas incident command training assesses, so a
/// participant's ratings can be read area by area as well as overall.
/// </summary>
public enum Competency
{
    /// <summary>Taking and keeping command: structure, delegation, span of control, handovers.</summary>
    CommandAndControl,

    /// <summary>Knowing what is going on, and what it is likely to do next.</summary>
    SituationalAwareness,

    /// <summary>Timely, sound decisions: priorities, resources, approvals.</summary>
    DecisionMaking,

    /// <summary>Radio discipline, orders and read-backs, briefings, the public and the media.</summary>
    Communication,

    /// <summary>Working with the team and the other agencies.</summary>
    Teamwork,

    /// <summary>Responder and public safety: zones, PARs, Maydays, protective actions.</summary>
    Safety,
}

public static class Rubric
{
    public static string Name(Competency area) => area switch
    {
        Competency.CommandAndControl => "Command & control",
        Competency.SituationalAwareness => "Situational awareness",
        Competency.DecisionMaking => "Decision making",
        Competency.Communication => "Communication",
        Competency.Teamwork => "Teamwork & liaison",
        _ => "Safety",
    };

    public static string Name(AssessmentRating rating) => rating switch
    {
        AssessmentRating.Competent => "Competent",
        AssessmentRating.Developing => "Developing",
        _ => "Not yet competent",
    };

    /// <summary>A rating as a score out of 100, to combine with the decision metrics.</summary>
    public static int Score(AssessmentRating rating) => rating switch
    {
        AssessmentRating.Competent => 100,
        AssessmentRating.Developing => 50,
        _ => 0,
    };

    /// <summary>A score out of 100 back on the rubric: 75 and over competent, 40 and over developing.</summary>
    public static AssessmentRating Level(int score) => score >= 75 ? AssessmentRating.Competent
        : score >= 40 ? AssessmentRating.Developing
        : AssessmentRating.NotYetCompetent;
}
