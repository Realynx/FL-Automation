using System.Runtime.InteropServices;
using System.Text.Json;

namespace FruityLink.Scripting;

internal sealed class EmbeddedPythonSession
{
    private readonly EmbeddedPythonApi _api;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly EmbeddedPythonApi.BinaryCall _requestCallback;
    private readonly EmbeddedPythonApi.BinaryCall _cancelCallback;
    private readonly nint _request;
    private readonly nint _cancel;
    private readonly nint _execute;
    private volatile EmbeddedPythonJob? _active;

    internal EmbeddedPythonSession(EmbeddedPythonApi api)
    {
        _api = api;
        _requestCallback = Request;
        _cancelCallback = Cancelled;
        _request = Function("fruitylink_request", _requestCallback);
        _cancel = Function("fruitylink_cancelled", _cancelCallback);
        nint module = api.Checked(api.Import("fruitylink.embedding"));
        try
        {
            InstallGuards(module);
            _execute = api.Checked(api.GetAttribute(module, "execute_json"));
        }
        finally { api.DecRef(module); }
    }

    private void InstallGuards(nint module)
    {
        nint install = _api.Checked(_api.GetAttribute(module, "install_host_guards"));
        try { _api.DecRef(_api.Checked(_api.Call(install, 0))); }
        finally { _api.DecRef(install); }
    }

    internal JsonElement Execute(EmbeddedPythonJob job)
    {
        _active = job;
        nint args = _api.Checked(_api.NewTuple(4));
        try
        {
            SetItem(args, 0, _api.String(job.Code));
            SetItem(args, 1, _api.String(job.Scope));
            _api.IncRef(_request);
            SetItem(args, 2, _request);
            _api.IncRef(_cancel);
            SetItem(args, 3, _cancel);
            nint response = _api.Checked(_api.Call(_execute, args));
            try
            {
                using var json = JsonDocument.Parse(_api.String(response));
                return json.RootElement.Clone();
            }
            finally { _api.DecRef(response); }
        }
        finally { _active = null; _api.DecRef(args); }
    }

    private nint Request(nint self, nint args)
    {
        try { return _api.String(RequestJson(args)); }
        catch (Exception error)
        {
            // Never unwind a managed exception through CPython's C stack.
            try { return _api.String(JsonSerializer.Serialize(new { error = ScriptingError.FromException(error) }, ScriptingJson.Options)); }
            catch (Exception) { return 0; }
        }
    }

    private string RequestJson(nint args)
    {
        if (_api.TupleSize(args) != 3) throw new ScriptingException("invalid_request", "Invalid embedded callback arguments.");
        var job = Active(_api.String(_api.TupleGet(args, 0)));
        string method = _api.String(_api.TupleGet(args, 1));
        using var json = JsonDocument.Parse(_api.String(_api.TupleGet(args, 2)));
        job.Token.ThrowIfCancellationRequested();
        // Native FL operations may marshal to the UI thread. Release Python's GIL
        // while managed work drains, then restore this exact owning thread state.
        nint threadState = _api.SaveThread();
        object? value;
        try { value = job.Handler!(method, json.RootElement, job.Token).GetAwaiter().GetResult(); }
        finally { _api.RestoreThread(threadState); }
        return JsonSerializer.Serialize(new { result = value }, ScriptingJson.Options);
    }

    private EmbeddedPythonJob Active(string scope)
    {
        if (Environment.CurrentManagedThreadId != _owner || _active is not { } job || job.Scope != scope)
            throw new ScriptingException("unavailable", "The embedded FL connection is outside its active script scope.");
        return job;
    }

    private nint Cancelled(nint self, nint args)
    {
        try
        {
            bool cancelled = _api.TupleSize(args) != 1 || _active is not { } job ||
                job.Scope != _api.String(_api.TupleGet(args, 0)) || job.Token.IsCancellationRequested;
            return _api.NewBool(cancelled ? 1 : 0);
        }
        catch (Exception) { _api.ClearError(); return _api.NewBool(1); }
    }

    private void SetItem(nint args, int index, nint value)
    {
        if (_api.TupleSet(args, index, value) < 0) _api.Checked(0);
    }

    private nint Function(string name, EmbeddedPythonApi.BinaryCall callback)
    {
        // CPython borrows the method definition and its strings. These two tiny
        // definitions intentionally share the resident interpreter's lifetime.
        var definition = new MethodDefinition
        {
            Name = Marshal.StringToCoTaskMemUTF8(name),
            Method = Marshal.GetFunctionPointerForDelegate(callback),
            Flags = 1 // METH_VARARGS
        };
        nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<MethodDefinition>());
        Marshal.StructureToPtr(definition, memory, false);
        return _api.Checked(_api.NewFunction(memory, 0, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MethodDefinition
    {
        internal nint Name;
        internal nint Method;
        internal int Flags;
        internal nint Doc;
    }
}
