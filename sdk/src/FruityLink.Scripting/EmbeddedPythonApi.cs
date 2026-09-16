using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FruityLink.Scripting;

// These are public CPython C APIs, with pointer-sized Py_ssize_t and int64_t config values.
// The version-specific library and reverse callbacks remain loaded for the process lifetime.
internal sealed class EmbeddedPythonApi(nint library)
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint PointerCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IntCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint ObjectCall(nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void VoidObjectCall(nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IntObjectCall(nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint BinaryCall(nint first, nint second);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint TernaryCall(nint first, nint second, nint third);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint StringCall([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint AttributeCall(nint value, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint SizedStringCall(nint value, nint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint ReadStringCall(nint value, out nint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint IndexCall(nint value, nint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetItemCall(nint value, nint index, nint item);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint SizeCall(nint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint BoolCall(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ConfigIntCall(nint config, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ConfigStringCall(nint config, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ConfigListCall(nint config, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint count, nint values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ConfigErrorCall(nint config, out nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void VoidCall();

    internal readonly PointerCall ConfigCreate = Bind<PointerCall>(library, "PyInitConfig_Create");
    internal readonly VoidObjectCall ConfigFree = Bind<VoidObjectCall>(library, "PyInitConfig_Free");
    internal readonly ConfigIntCall ConfigSetInt = Bind<ConfigIntCall>(library, "PyInitConfig_SetInt");
    internal readonly ConfigStringCall ConfigSetString = Bind<ConfigStringCall>(library, "PyInitConfig_SetStr");
    internal readonly ConfigListCall ConfigSetList = Bind<ConfigListCall>(library, "PyInitConfig_SetStrList");
    internal readonly ConfigErrorCall ConfigError = Bind<ConfigErrorCall>(library, "PyInitConfig_GetError");
    internal readonly IntObjectCall Initialize = Bind<IntObjectCall>(library, "Py_InitializeFromInitConfig");
    internal readonly IntCall IsInitialized = Bind<IntCall>(library, "Py_IsInitialized");
    internal readonly PointerCall GetVersion = Bind<PointerCall>(library, "Py_GetVersion");
    internal readonly PointerCall SaveThread = Bind<PointerCall>(library, "PyEval_SaveThread");
    internal readonly VoidObjectCall RestoreThread = Bind<VoidObjectCall>(library, "PyEval_RestoreThread");
    internal readonly StringCall Import = Bind<StringCall>(library, "PyImport_ImportModule");
    internal readonly AttributeCall GetAttribute = Bind<AttributeCall>(library, "PyObject_GetAttrString");
    internal readonly BinaryCall Call = Bind<BinaryCall>(library, "PyObject_CallObject");
    internal readonly TernaryCall NewFunction = Bind<TernaryCall>(library, "PyCFunction_NewEx");
    internal readonly SizedStringCall NewString = Bind<SizedStringCall>(library, "PyUnicode_FromStringAndSize");
    internal readonly ReadStringCall ReadString = Bind<ReadStringCall>(library, "PyUnicode_AsUTF8AndSize");
    internal readonly SizeCall NewTuple = Bind<SizeCall>(library, "PyTuple_New");
    internal readonly IndexCall TupleGet = Bind<IndexCall>(library, "PyTuple_GetItem");
    internal readonly ObjectCall TupleSize = Bind<ObjectCall>(library, "PyTuple_Size");
    internal readonly SetItemCall TupleSet = Bind<SetItemCall>(library, "PyTuple_SetItem");
    internal readonly BoolCall NewBool = Bind<BoolCall>(library, "PyBool_FromLong");
    internal readonly VoidObjectCall DecRef = Bind<VoidObjectCall>(library, "Py_DecRef");
    internal readonly VoidObjectCall IncRef = Bind<VoidObjectCall>(library, "Py_IncRef");
    internal readonly PointerCall RaisedException = Bind<PointerCall>(library, "PyErr_GetRaisedException");
    internal readonly ObjectCall ObjectString = Bind<ObjectCall>(library, "PyObject_Str");
    internal readonly VoidCall ClearError = Bind<VoidCall>(library, "PyErr_Clear");

    internal static EmbeddedPythonApi Load(string directory)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Embedded Python requires Windows x64.");
        string path = Path.Combine(directory, "python314.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("The bundled CPython runtime is missing.", path);
        CheckExistingModule(path);
        nint library = LoadLibraryExW(path, 0, 0x00000100 | 0x00001000);
        if (library == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load the private CPython DLL.");
        var api = new EmbeddedPythonApi(library);
        string version = Marshal.PtrToStringUTF8(api.GetVersion()) ?? "unknown";
        if (!version.StartsWith("3.14.6 ", StringComparison.Ordinal))
            throw new InvalidOperationException($"Embedded Python requires the bundled CPython 3.14.6; found {version}.");
        if (api.IsInitialized() != 0)
            throw new InvalidOperationException("CPython 3.14 is already initialized by another owner. Restart FL with one interpreter owner.");
        return api;
    }

    internal nint Checked(nint value)
        => value != 0 ? value : throw new InvalidOperationException("Embedded Python failed: " + ErrorText());

    internal string String(nint value)
    {
        nint bytes = Checked(ReadString(Checked(value), out nint size));
        if (size > 4 * 1024 * 1024) throw new InvalidOperationException("Embedded Python string exceeds 4 MiB.");
        return Marshal.PtrToStringUTF8(bytes, checked((int)size))!;
    }

    internal nint String(string value)
    {
        nint bytes = Marshal.StringToCoTaskMemUTF8(value);
        try { return Checked(NewString(bytes, Encoding.UTF8.GetByteCount(value))); }
        finally { Marshal.FreeCoTaskMem(bytes); }
    }

    private string ErrorText()
    {
        nint error = RaisedException();
        if (error == 0) return "Native API returned no value.";
        nint message = ObjectString(error);
        try
        {
            if (message == 0) return "Unable to format Python exception.";
            nint bytes = ReadString(message, out nint size);
            return bytes == 0 ? "Python exception." : Marshal.PtrToStringUTF8(bytes, (int)Math.Min(size, 65536))!;
        }
        finally { if (message != 0) DecRef(message); DecRef(error); ClearError(); }
    }

    private static T Bind<T>(nint library, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static void CheckExistingModule(string path)
    {
        nint existing = GetModuleHandleW("python314.dll");
        if (existing == 0) return;
        var actual = new StringBuilder(32768);
        if (GetModuleFileNameW(existing, actual, actual.Capacity) == 0 ||
            !string.Equals(Path.GetFullPath(actual.ToString()), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A different python314.dll is already loaded. Restart FL before changing embedded runtimes.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint module, StringBuilder fileName, int size);
}
