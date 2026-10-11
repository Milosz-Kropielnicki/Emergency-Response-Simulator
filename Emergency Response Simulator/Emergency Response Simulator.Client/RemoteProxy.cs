using System.Reflection;
using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Simulation.Remote;

namespace Emergency_Response_Simulator.Client;

/// <summary>
/// Implements a service contract (<see cref="IC2Service"/>, <see cref="IIapService"/>) by calling the server: every
/// method of the interface, one for one, with no hand-written forwarding to drift out of step (Phase 11 M1).
/// When a command succeeds, the call returns only once this client's COP has applied the event it produced, so view
/// models that read the COP straight after a command (open an incident, then select it) behave as they do standalone.
/// </summary>
public class RemoteProxy : DispatchProxy
{
    /// <summary>How long a command waits for its own event to arrive before returning anyway.</summary>
    public static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(5);

    private static readonly MethodInfo CallTyped = typeof(RemoteProxy).GetMethod(nameof(CallAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private SimulationConnection _connection = null!;
    private string _service = null!;

    public static T Create<T>(SimulationConnection connection, string service) where T : class
    {
        var proxy = DispatchProxy.Create<T, RemoteProxy>();
        var remote = (RemoteProxy)(object)proxy;
        remote._connection = connection;
        remote._service = service;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (!typeof(Task).IsAssignableFrom(targetMethod.ReturnType))
            throw new NotSupportedException($"{targetMethod.Name} isn't available remotely.");

        var parameters = targetMethod.GetParameters();
        var wire = new List<JsonElement>();
        var cancellation = CancellationToken.None;
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type == typeof(CancellationToken)) cancellation = args?[i] is CancellationToken token ? token : CancellationToken.None;
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>)) continue; // not carried
            else wire.Add(JsonSerializer.SerializeToElement(args?[i], type, WireJson.Options));
        }

        var resultType = targetMethod.ReturnType.IsGenericType ? targetMethod.ReturnType.GetGenericArguments()[0] : typeof(object);
        var call = (Task)CallTyped.MakeGenericMethod(resultType).Invoke(this, [targetMethod.Name, wire.ToArray(), cancellation])!;
        return targetMethod.ReturnType.IsGenericType ? call : Discard(call);
    }

    private async Task<T?> CallAsync<T>(string method, JsonElement[] args, CancellationToken cancellationToken)
    {
        JsonElement? result;
        try
        {
            result = await _connection.CallAsync(_service, method, args, cancellationToken);
        }
        catch (Microsoft.AspNetCore.SignalR.HubException ex) when (typeof(T) == typeof(CommandResult))
        {
            // The session refused it (not started, ended, not this role's to give): a command that failed, as standalone.
            return (T)(object)CommandResult.Fail(RemoteErrors.Message(ex));
        }
        var value = result is { ValueKind: not JsonValueKind.Null } json ? json.Deserialize<T>(WireJson.Options) : default;
        if (value is CommandResult { Succeeded: true, EventSequence: { } sequence })
            await _connection.Mirror.WaitForAsync(sequence, EchoTimeout, cancellationToken);
        return value;
    }

    private static async Task Discard(Task call) => await call;
}
