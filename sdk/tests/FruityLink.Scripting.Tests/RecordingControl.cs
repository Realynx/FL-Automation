using FruityLink.Core.Abstractions;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace FruityLink.Scripting.Tests;

public interface ICompleteControl : INativeFlControl, IFlStructuredQuery, IFlSymbolResolution { }

public class RecordingControl : DispatchProxy
{
    public ConcurrentQueue<(string Method, object?[] Arguments)> Calls { get; } = new();
    public Func<MethodInfo, object?[], object?>? Handler { get; set; }
    public FlSymbolStatus? Status { get; set; }

    public static (T Control, RecordingControl Recorder) Create<T>() where T : class
    {
        T control = Create<T, RecordingControl>();
        return (control, (RecordingControl)(object)control);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        var arguments = args ?? Array.Empty<object?>();
        if (method.Name == nameof(IFlSymbolResolution.GetSymbolStatusAsync)) return Task.FromResult(Status);
        Calls.Enqueue((method.Name, arguments));
        if (Handler is { } handler) return handler(method, arguments);
        return DefaultReturn(method);
    }

    public static object DefaultReturn(MethodInfo method)
    {
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        Type result = method.ReturnType.GenericTypeArguments[0];
        object? value = method.Name switch
        {
            nameof(INativeFlControl.IsAvailableAsync) => true,
            nameof(INativeFlControl.GetTempoAsync) => 120.0,
            nameof(INativeFlControl.GetPpqAsync) => 96,
            _ => result == typeof(string) ? "" : result.IsValueType ? Activator.CreateInstance(result) : null
        };
        return typeof(RecordingControl).GetMethod(nameof(ResultTask), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(result).Invoke(null, new[] { value })!;
    }

    private static Task<T> ResultTask<T>(T value) => Task.FromResult(value);
    internal static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
