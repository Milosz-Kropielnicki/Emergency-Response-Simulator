using System.Reflection;
using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Microsoft.AspNetCore.SignalR;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// Runs a client's call on the server's own service (Phase 11 M1). Only methods declared on the contract interfaces
/// can be reached, so the hub mirrors <see cref="IC2Service"/>, <see cref="IIapService"/>, <see cref="ISimulationControl"/>,
/// <see cref="IAarService"/>, the GIS, plume and integration services (Phase 20) and the rest one for one, and a method added
/// to a contract is callable without touching the hub. The same table serves the REST API (Phase 20).
/// The services validate every command against the server's live COP exactly as they do in a standalone session.
/// Arguments and results travel as JSON in the <see cref="WireJson"/> format; cancellation tokens are the connection's,
/// and progress reporters are not carried.
/// </summary>
public sealed class CommandDispatcher
{
    public static readonly IReadOnlyDictionary<string, Type> Contracts = new Dictionary<string, Type>
    {
        [SimulationHubProtocol.Services.C2] = typeof(IC2Service),
        [SimulationHubProtocol.Services.Iap] = typeof(IIapService),
        [SimulationHubProtocol.Services.Control] = typeof(ISimulationControl),
        [SimulationHubProtocol.Services.Aar] = typeof(IAarService),
        [SimulationHubProtocol.Services.Instructor] = typeof(IInstructorService),
        [SimulationHubProtocol.Services.Assessment] = typeof(IAssessmentService),
        [SimulationHubProtocol.Services.Gis] = typeof(IGisService),
        [SimulationHubProtocol.Services.Plume] = typeof(IPlumeService),
        [SimulationHubProtocol.Services.Integration] = typeof(IIntegrationService),
    };

    private static readonly Dictionary<(string Service, string Method, int Args), MethodInfo> Methods = BuildTable();

    /// <summary>Every callable method, for tests and diagnostics.</summary>
    public static IEnumerable<(string Service, MethodInfo Method)> Callable => Methods.Select(m => (m.Key.Service, m.Value));

    /// <summary>The arguments a client sends: everything except cancellation tokens and progress reporters.</summary>
    public static IEnumerable<ParameterInfo> WireParameters(MethodInfo method) =>
        method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken) && !IsProgress(p.ParameterType));

    /// <summary>
    /// Methods a client can reach: asynchronous ones whose result can travel. Synchronous members (properties) come from
    /// the clock push or the local COP; <see cref="IAarService.ReplayToAsync"/> runs on the client's own copy of the stream.
    /// </summary>
    public static bool IsRemotable(MethodInfo method) =>
        !method.IsSpecialName && typeof(Task).IsAssignableFrom(method.ReturnType)
        && !(method.ReturnType.IsGenericType && method.ReturnType.GetGenericArguments()[0] is { IsInterface: true } result
             && !typeof(System.Collections.IEnumerable).IsAssignableFrom(result)); // a live service can't travel; a list can

    private static bool IsProgress(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>);

    private static Dictionary<(string, string, int), MethodInfo> BuildTable()
    {
        var table = new Dictionary<(string, string, int), MethodInfo>();
        foreach (var (name, contract) in Contracts)
            foreach (var method in contract.GetMethods().Where(IsRemotable))
                table[(name, method.Name, WireParameters(method).Count())] = method;
        return table;
    }


    /// <summary>
    /// Runs a participant's call in their session (Phase 11 M2): on that session's services, as that participant (so the
    /// events it records carry them as the actor), and, for C2 and IAP commands, through the session's command gate, so
    /// each is checked against a COP that already holds every earlier command's result.
    /// </summary>
    public async Task<JsonElement?> InvokeAsync(SessionRuntime session, ParticipantRecord participant, string service, string method,
        JsonElement[] args, CancellationToken cancellationToken)
    {
        // Optional parameters may be left off the end, so look for the longest signature that fits.
        var target = Methods.Where(m => m.Key.Service == service && m.Key.Method == method && m.Key.Args >= args.Length)
            .OrderBy(m => m.Key.Args).Select(m => m.Value).FirstOrDefault()
            ?? throw new HubException($"Unknown call {service}.{method} with {args.Length} argument(s).");

        var truth = participant.Truth;
        CommandAuthorizer.Authorise(session, participant, service, method);

        // Starting the clock for the first time is the instructor starting the exercise.
        if (service == SimulationHubProtocol.Services.Control && method == nameof(ISimulationControl.StartAsync))
        {
            await session.BeginAsync(participant);
            return null;
        }

        var instance = session.Services.GetService(Contracts[service])
                       ?? throw new HubException($"This server has no {service} service: {(service == SimulationHubProtocol.Services.Gis ? "it has no GIS database" : "it isn't configured")}.");
        var values = new List<object?>();
        var index = 0;
        foreach (var parameter in target.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken)) values.Add(cancellationToken);
            else if (IsProgress(parameter.ParameterType)) values.Add(null);
            else if (index < args.Length) values.Add(Restrict(service, method, parameter, args[index++].Deserialize(parameter.ParameterType, WireJson.Options), truth));
            else if (parameter.HasDefaultValue) values.Add(Restrict(service, method, parameter, parameter.DefaultValue, truth));
            else throw new HubException($"{service}.{method} needs '{parameter.Name}'.");
        }

        async Task<Task> Run()
        {
            using (CommandContext.As(participant.Actor))
            {
                try
                {
                    var task = (Task)target.Invoke(instance, [.. values])!;
                    await task;
                    await TakePositionAsync(session, participant, target, values, task);
                    return task;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    throw new HubException(ex.InnerException.Message);
                }
            }
        }

        var done = service is SimulationHubProtocol.Services.C2 or SimulationHubProtocol.Services.Iap or SimulationHubProtocol.Services.Instructor
                   or SimulationHubProtocol.Services.Integration
            ? await session.Gate.RunAsync(Run, cancellationToken)
            : await Run();

        if (!target.ReturnType.IsGenericType) return null;
        var resultType = target.ReturnType.GetGenericArguments()[0];
        var result = done.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(done);
        return JsonSerializer.SerializeToElement(result, resultType, WireJson.Options);
    }

    /// <summary>
    /// A participant put in an ICS position (Phase 11 M3) takes that role in the session, so the function stops falling to
    /// the IC. The service has already checked they are present and the role is free; this runs inside the command gate.
    /// </summary>
    private static async Task TakePositionAsync(SessionRuntime session, ParticipantRecord actor, MethodInfo target, List<object?> values, Task done)
    {
        if (target.Name != nameof(IC2Service.AssignIcsPositionAsync) || done is not Task<CommandResult> { Result.Succeeded: true }) return;
        var parameters = target.GetParameters();
        var position = (IcsRole)values[Array.FindIndex(parameters, p => p.Name == "role")]!;
        if (values[Array.FindIndex(parameters, p => p.Name == "userId")] is Guid userId)
            await session.TakePositionAsync(actor, userId, Roles.Of(position));
    }

    /// <summary>A trainee connection never gets truth events, even by asking for the timeline with them.</summary>
    private static object? Restrict(string service, string method, ParameterInfo parameter, object? value, bool truth) =>
        service == SimulationHubProtocol.Services.Aar && method == nameof(IAarService.GetTimelineAsync) && parameter.Name == "includeTruth" && !truth
            ? false
            : value;
}
