using System.Text.Json;
using FruityLink.Core.Diagnostics;
using FruityLink.Core.Hosting;

namespace FruityLink.SessionHost;

// A local SDK broker, independent of MCP. EOF/parent death closes the kill-on-close job.
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main()
    {
        FlStudioProcessLease? lease = null;
        try
        {
            string? line = await Console.In.ReadLineAsync();
            if (line is null) return 0;
            var request = Deserialize<StartRequest>(line);
            lease = FlStudioProcessLauncher.Start(new FlStudioLaunchOptions(request.Executable)
            {
                Arguments = request.Arguments,
                Environment = request.Environment,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(request.Executable)),
                Mode = request.Background ? FlStudioLaunchMode.PrivateDesktop : FlStudioLaunchMode.Interactive,
                StartupTimeout = TimeSpan.FromSeconds(request.StartupTimeoutSeconds),
            });
            Reply(Status(lease));
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                var command = Deserialize<Command>(line);
                if (command.Method == "stop") break;
                if (command.Method == "ready") lease.CompleteStartup();
                Reply(command.Method switch
                {
                    "status" => Status(lease),
                    "ready" => Status(lease),
                    "windows" => Windows(lease),
                    _ => throw new ArgumentException("Unknown session-host method."),
                });
            }
            return 0;
        }
        catch (Exception error)
        {
            Reply(new { error = error.Message });
            return 1;
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    private static T Deserialize<T>(string line)
    {
        if (line.Length > 1_048_576) throw new ArgumentException("Session-host request is too large.");
        return JsonSerializer.Deserialize<T>(line, Json) ?? throw new ArgumentException("Empty session-host request.");
    }

    private static object Status(FlStudioProcessLease lease) => new
    {
        processId = lease.ProcessId, background = lease.Mode == FlStudioLaunchMode.PrivateDesktop,
        desktopName = lease.DesktopName, hasExited = lease.HasExited, exitCode = lease.ExitCode,
    };

    private static object Windows(FlStudioProcessLease lease)
    {
        var windows = lease.ReadWindows(CancellationToken.None);
        return new
        {
            windows = windows.Select(window => new
            {
                window = (long)window.Window, window.ProcessId, window.ClassName, window.Title,
                window.Visible, window.Enabled, owner = (long)window.Owner,
                modal = window.Visible && window.Enabled && (window.ClassName == "#32770" ||
                    windows.Any(owner => owner.Window == window.Owner && !owner.Enabled)),
                body = window.ClassName == "TMsgForm"
                    ? FlDialogInspector.TryInspect(lease.ProcessId, window.Window)?.Body : null,
            }).ToArray(),
        };
    }

    private static void Reply(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
        Console.Out.Flush();
    }

    private sealed record Command(string Method);
    private sealed record StartRequest(string Executable, string[] Arguments,
        Dictionary<string, string?> Environment, bool Background = true, double StartupTimeoutSeconds = 60);
}
