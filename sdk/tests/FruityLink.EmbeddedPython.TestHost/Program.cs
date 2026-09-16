using FruityLink.Scripting;
using FruityLink.EmbeddedPython.TestHost;

if (args.Length < 2) throw new ArgumentException("Expected runtime directory and Python package path.");
var options = new EmbeddedPythonOptions(args[0], args[1])
{
    ExtensionPackagePaths = args.Skip(3).ToArray(),
};
await EmbeddedChecks.RunAsync(options, args.ElementAtOrDefault(2) ?? "execution");
Console.WriteLine("Embedded Python integration passed.");
