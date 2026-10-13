namespace Emergency_Response_Simulator.Simulation.Engine;

/// <summary>Identifies the running session; every event it produces carries this id.</summary>
public sealed record SimulationSession(Guid Id);

/// <summary>Bound from the "Simulation" configuration section.</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    /// <summary>Real time between engine ticks.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Simulation clock at session start. Defaults to the current time.</summary>
    public DateTimeOffset? StartTime { get; set; }

    public double InitialTimeScale { get; set; } = 1.0;

    public double MaxTimeScale { get; set; } = 60.0;

    /// <summary>Caps one tick's simulated time so a stalled machine does not make the world jump.</summary>
    public TimeSpan MaxTickDelta { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Scripted scenario to play: "barrow-street", or empty for none.</summary>
    public string? Scenario { get; set; }

    /// <summary>Start the clock as soon as the scenario is seeded (unattended demos, instructor setups).</summary>
    public bool AutoStart { get; set; }

    /// <summary>Presentation mode for scripted scenarios: the scenario makes the first dispatch decisions itself.</summary>
    public bool DemoAutoResponse { get; set; }

    /// <summary>
    /// Run the world simulation (hazards, civilians, casualties and hospitals, live traffic, power, weather drift and
    /// AI-run agencies). Off gives the scripted-only world of earlier phases.
    /// </summary>
    public bool WorldModels { get; set; } = true;

    /// <summary>
    /// Simulate communications (Phase 7): channel airtime, garbled and lost messages, coverage, batteries, the 999
    /// queue. Off, every message reaches command at once and intact.
    /// </summary>
    public bool CommsRealism { get; set; } = true;

    /// <summary>
    /// Simulate crews as people (Phase 8): fatigue, stress, shifts, rehab and relief, PARs, Maydays and collapses,
    /// qualifications. Off, crews never tire and nothing happens to them.
    /// </summary>
    public bool HumanFactors { get; set; } = true;

    /// <summary>
    /// Simulate the other agencies as joint partners (Phase 10): unified command, hoarding, information silos,
    /// authority disputes, EOC staffing, the media and conflicting messages. Off, agencies only do their own work.
    /// </summary>
    public bool MultiAgency { get; set; } = true;

    /// <summary>
    /// Politicians ring command about a serious incident (Phase 11 M5): the local TD, the council's chief executive, the
    /// minister's office, more often the worse it is, and higher up when nobody calls them back.
    /// </summary>
    public bool PoliticalPressure { get; set; } = true;

    /// <summary>
    /// The media cover the response, and public confidence rises and falls with how command keeps the public informed
    /// (Phase 12).
    /// </summary>
    public bool MediaCoverage { get; set; } = true;

    /// <summary>
    /// Let the Python hazard-model service drive real releases and keep command's projections current (Phase 13). Off,
    /// or with the service not running, the in-engine Gaussian plume models them instead.
    /// </summary>
    public bool DispersionService { get; set; } = true;
}
