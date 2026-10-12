namespace Emergency_Response_Simulator.Core.Model;

/// <summary>The assessment rubric (Phase 11 M6).</summary>
public enum AssessmentRating
{
    Competent,
    Developing,
    NotYetCompetent,
}

public static class Rubric
{
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
