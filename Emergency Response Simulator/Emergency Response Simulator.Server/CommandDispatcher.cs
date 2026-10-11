using System.Reflection;
using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Simulation.Remote;
using Microsoft.AspNetCore.SignalR;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// Runs a client's call on the server's own service (Phase 11 M1). Only methods declared on the contract interfaces
/// can be reached, so the hub mirrors <see cref="IC2Service"/>, <see cref="IIapService"/>, <see cref="ISimulationControl"/>
/// and <see cref="IAarService"/> one for one, and a method added to a contract is callable without touching the hub.
/// The services validate every command against the server's live COP exactly as they do in a standalone session.
/// Arguments and results travel as JSON in the <see cref="WireJson"/> format; cancellation tokens are the connection's,
/// and progress reporters are not carried.
/// </summary>
public sealed class CommandDispatcher(IServiceProvider services)
{
    public static readonly IReadOnlyDictionary<string, Type> Contracts = new Dictionary<string, Type>
    {
        [SimulationHubProtocol.Services.C2] = typeof(IC2Service),
        [SimulationHubProtocol.Services.Iap] = typeof(IIapService),
        [SimulationHubProtocol.Services.Control] = typeof(ISimulationControl),
        [SimulationHubProtocol.Services.Aar] = typeof(IAarService),
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

    public async Task<JsonElement?> InvokeAsync(string service, string method, JsonElement[] args, bool truth, CancellationToken cancellationToken)
    {
        // Optional parameters may be left off the end, so look for the longest signature that fits.
        var target = Methods.Where(m => m.Key.Service == service && m.Key.Method == method && m.Key.Args >= args.Length)
            .OrderBy(m => m.Key.Args).Select(m => m.Value).FirstOrDefault()
            ?? throw new HubException($"Unknown call {service}.{method} with {args.Length} argument(s).");

        Authorise(service, method, truth);
        var instance = services.GetRequiredService(Contracts[service]);
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

        Task task;
        try
        {
            task = (Task)target.Invoke(instance, [.. values])!;
            await task;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new HubException(ex.InnerException.Message);
        }

        if (!target.ReturnType.IsGenericType) return null;
        var resultType = target.ReturnType.GetGenericArguments()[0];
        var result = task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
        return JsonSerializer.SerializeToElement(result, resultType, WireJson.Options);
    }

    /// <summary>
    /// The review reveals ground truth, so until roles arrive (Phase 11 M3) only the instructor may build it or re-run
    /// branches from a client.
    /// </summary>
    private static void Authorise(string service, string method, bool truth)
    {
        if (service == SimulationHubProtocol.Services.Aar && !truth
            && method is nameof(IAarService.BuildReportAsync) or nameof(IAarService.CompareBranchesAsync))
            throw new HubException("Only the instructor can open the after-action review during a networked session.");
    }

    /// <summary>A trainee connection never gets truth events, even by asking for the timeline with them.</summary>
    private static object? Restrict(string service, string method, ParameterInfo parameter, object? value, bool truth) =>
        service == SimulationHubProtocol.Services.Aar && method == nameof(IAarService.GetTimelineAsync) && parameter.Name == "includeTruth" && !truth
            ? false
            : value;
}
