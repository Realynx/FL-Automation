using System.Runtime.InteropServices;

namespace FruityLink.Scripting;

internal static class EmbeddedPythonConfiguration
{
    internal static void Initialize(EmbeddedPythonApi api, EmbeddedPythonOptions options)
    {
        string stdlib = Path.Combine(options.RuntimeDirectory, "python314.zip");
        if (!File.Exists(stdlib)) throw new FileNotFoundException("The bundled Python standard library is missing.", stdlib);
        if (!File.Exists(options.PythonPackagePath) && !Directory.Exists(options.PythonPackagePath))
            throw new FileNotFoundException("The FruityLink Python package is missing.", options.PythonPackagePath);
        nint config = api.ConfigCreate();
        if (config == 0) throw new OutOfMemoryException("Unable to allocate CPython initialization configuration.");
        try
        {
            SetPaths(api, config, options, stdlib);
            foreach (string option in new[] { "site_import", "use_environment", "install_signal_handlers", "write_bytecode", "parse_argv" })
                Check(api, config, api.ConfigSetInt(config, option, 0));
            Check(api, config, api.Initialize(config));
        }
        finally { api.ConfigFree(config); }
    }

    private static void SetPaths(EmbeddedPythonApi api, nint config, EmbeddedPythonOptions options, string stdlib)
    {
        foreach (string option in new[] { "home", "prefix", "base_prefix", "exec_prefix", "base_exec_prefix" })
            Check(api, config, api.ConfigSetString(config, option, options.RuntimeDirectory));
        foreach (string option in new[] { "executable", "base_executable", "program_name" })
            Check(api, config, api.ConfigSetString(config, option, Path.Combine(options.RuntimeDirectory, "python.exe")));
        SetList(api, config, "module_search_paths", BuildModuleSearchPaths(options, stdlib));
    }

    internal static string[] BuildModuleSearchPaths(EmbeddedPythonOptions options, string stdlib) =>
        [stdlib, options.RuntimeDirectory, options.PythonPackagePath, .. options.ExtensionPackagePaths];

    private static void SetList(EmbeddedPythonApi api, nint config, string name, string[] values)
    {
        nint[] strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        nint pointers = Marshal.AllocCoTaskMem(strings.Length * IntPtr.Size);
        try
        {
            Marshal.Copy(strings, 0, pointers, strings.Length);
            Check(api, config, api.ConfigSetList(config, name, (nuint)strings.Length, pointers));
        }
        finally
        {
            foreach (nint value in strings) Marshal.FreeCoTaskMem(value);
            Marshal.FreeCoTaskMem(pointers);
        }
    }

    private static void Check(EmbeddedPythonApi api, nint config, int status)
    {
        if (status >= 0) return;
        api.ConfigError(config, out nint message);
        throw new InvalidOperationException("Unable to initialize embedded Python: " + Marshal.PtrToStringUTF8(message));
    }
}
