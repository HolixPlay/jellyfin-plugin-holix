using System;
using System.Collections.Generic;
using System.Reflection;

namespace Jellyfin.Plugin.Holix.Tests;

/// <summary>Minimal interface fake: methods are answered by name; unknown methods return default.</summary>
/// <typeparam name="T">The interface.</typeparam>
public class Fake<T> : DispatchProxy
    where T : class
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = new(StringComparer.Ordinal);

    public static T Create(params (string Method, Func<object?[], object?> Handler)[] handlers)
    {
        var proxy = Create<T, Fake<T>>();
        var fake = (Fake<T>)(object)proxy;
        foreach (var (method, handler) in handlers)
        {
            fake._handlers[method] = handler;
        }

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_handlers.TryGetValue(targetMethod.Name, out var handler))
        {
            return handler(args ?? []);
        }

        var type = targetMethod.ReturnType;
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
