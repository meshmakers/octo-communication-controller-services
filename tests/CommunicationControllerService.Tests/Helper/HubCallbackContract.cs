using System.Reflection;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
///     N8 — checks a controller → client SignalR send against the callback interface the client
///     binds its handlers from (<c>IAdapterHubCallbacks</c>, <c>IAdapterPoolHubCallbacks</c>,
///     <c>IOperatorHubCallbacks</c>).
/// </summary>
/// <remarks>
///     The hubs are untyped (<c>IHubContext&lt;THub&gt;</c>), so <c>SendAsync(name, args…)</c> compiles
///     with any argument list. The clients register <c>On&lt;T1,…,Tn&gt;(nameof(…), handler)</c> from the
///     same interface. A send whose arguments do not bind to that handler is <b>dropped silently on
///     the client</b> — no exception on either side, the controller even logs success. That is how
///     <c>DrainAsync</c> went out without its <c>reason</c> for the whole life of AB#4924. This checker
///     is what a typed hub would have given us at compile time: the method exists, the argument count
///     matches, and every argument is assignable to its parameter.
/// </remarks>
internal static class HubCallbackContract
{
    public static IReadOnlyList<string> Violations(Type callbacksInterface, string methodName, object?[] args)
    {
        var candidates = callbacksInterface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == methodName)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [$"{callbacksInterface.Name} has no method '{methodName}' — the client registers no handler for it"];
        }

        if (candidates.Length > 1)
        {
            return [$"{callbacksInterface.Name}.{methodName} is overloaded — SignalR binds handlers by name only"];
        }

        var parameters = candidates[0].GetParameters();
        if (parameters.Length != args.Length)
        {
            return
            [
                $"{callbacksInterface.Name}.{methodName} takes {parameters.Length} argument(s) " +
                $"({string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"))}) " +
                $"but the controller sent {args.Length}"
            ];
        }

        var violations = new List<string>();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;
            var arg = args[i];
            if (arg is null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                {
                    violations.Add($"{callbacksInterface.Name}.{methodName}: argument {i} " +
                                   $"('{parameters[i].Name}') is null but the parameter is {parameterType.Name}");
                }

                continue;
            }

            if (!parameterType.IsInstanceOfType(arg))
            {
                violations.Add($"{callbacksInterface.Name}.{methodName}: argument {i} ('{parameters[i].Name}') " +
                               $"is {arg.GetType().Name}, the parameter is {parameterType.Name}");
            }
        }

        return violations;
    }
}

/// <summary>
///     A client proxy that records every send instead of transporting it. Cheaper to read than
///     NSubstitute's <c>ReceivedCalls()</c> and independent of which <c>SendAsync</c> overload the
///     production code happened to use — they all end in <see cref="SendCoreAsync" />.
/// </summary>
internal sealed class RecordingClientProxy : ISingleClientProxy
{
    private readonly List<(string Method, object?[] Args)> _sent = [];

    public IReadOnlyList<(string Method, object?[] Args)> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        lock (_sent)
        {
            _sent.Add((method, args));
        }

        return Task.CompletedTask;
    }

    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The controller does not invoke client results on these hubs");
}
