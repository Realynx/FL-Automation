using System.IO;

namespace FruityLink.Probe;

/// <summary>
/// flprobe — FruityLink runtime + plugin dev harness.
///
/// FruityLink now ships as a transparent <b>proxy-DLL filesystem install</b>
/// (re/integration-pending-proxy.md): FL Studio loads our <c>version.dll</c> proxy at startup, which
/// boots the CoreCLR host in-process and loads <c>FlBridge.dll</c>. That bridge serves the SAME named
/// pipe <c>\\.\pipe\FruityLinkBridge</c>, so you talk to it WITHOUT injecting — just <c>attach</c>.
/// (Injection is the legacy/dev fallback only; in the install model the proxy OWNS the DLL lifecycle —
/// do not inject a second copy over it.)
///
///   flprobe attach                 connect to the proxy-loaded bridge over the pipe (no injection) → ping + info
///   flprobe bridge [cmd...]        pipe client: status | ping | info | peek &lt;hex&gt; &lt;len&gt; | call &lt;hex&gt; [args] | poke | key | …
///   flprobe plugin <sub> …         build / install / hot-reload plugins (list|install|update|enable|disable|reload|dev)
///   flprobe inject [dllPath]       (legacy/dev) inject the bridge into FL64
///   flprobe eject                  (legacy/dev) unload it cleanly
///   flprobe reload [dllPath]       (legacy/dev) eject + inject (the native-bridge rebuild loop)
///   flprobe proxy [port] [url]     OpenAI-compatible logging proxy (LLM debugging)
///
/// The plugin loop targets the host's plugins dir (re/integration-pending-plugin-host.md): the host
/// discovers plugins from &lt;host-dir&gt;\plugins\&lt;id&gt;\ and hot-reloads on file change (FileSystemWatcher +
/// shadow-copy), so overwriting the files there is enough to reload. flprobe additionally best-effort
/// calls the debug-pipe handlers (plugins_list / plugins_dir / plugin_enable|disable|reload) documented
/// in re/integration-pending-probe-debug.md.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var positional = new List<string>(args);
        string command = positional.Count > 0 ? positional[0].ToLowerInvariant() : "bridge";
        return command switch
        {
            "attach" => Inject.Attach(positional),
            "inject" => Inject.Run(positional),
            "eject" => Inject.Eject(),
            "reload" => Inject.Reload(positional),
            "bridge" => Inject.Bridge(positional),
            "plugin" or "plugins" => Plugin.Run(positional),
            "proxy" => await ProxyAsync(positional),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("flprobe — FruityLink runtime + plugin dev harness");
        Console.WriteLine();
        Console.WriteLine("  attach                          connect to the proxy-loaded bridge over the pipe (NO injection) -> ping + info");
        Console.WriteLine("  bridge [cmd...]                 pipe client (status | ping | info | peek <ghidraHex> <len> | call <hex> [args] | poke <hex> <bytes> | key <vkHex>)");
        Console.WriteLine();
        Console.WriteLine("  plugin list                     list installed plugins (pipe 'plugins_list')");
        Console.WriteLine("  plugin install <dirOrDll> [--id <id>] [--plugins-dir <dir>]   copy a publish closure into <pluginsDir>\\<id>\\, reload+enable");
        Console.WriteLine("  plugin update  <dirOrDll> [--id <id>] [--plugins-dir <dir>]   alias for install");
        Console.WriteLine("  plugin dev     <projDirOrCsproj> [--id <id>] [--plugins-dir <dir>]   dotnet publish -c Debug, then install (full build+debug loop)");
        Console.WriteLine("  plugin enable|disable|reload <id>   toggle/reload one plugin (pipe 'plugin_enable|disable|reload')");
        Console.WriteLine();
        Console.WriteLine("  inject [dllPath] | eject | reload [dllPath]   (legacy/dev) hot inject the native bridge");
        Console.WriteLine("  proxy [port] [targetUrl]        OpenAI-compatible logging proxy");
        return 1;
    }

    // OpenAI-compatible logging proxy — independent of FL; handy for debugging LLM round-trips.
    private static async Task<int> ProxyAsync(List<string> pos)
    {
        int port = pos.Count > 1 && int.TryParse(pos[1], out int p) ? p : 8400;
        string target = (pos.Count > 2 ? pos[2] : "http://localhost:11434").TrimEnd('/');
        string logPath = Path.Combine(Path.GetTempPath(), "flprobe-proxy.log");
        File.WriteAllText(logPath, $"flprobe proxy log — {DateTime.Now}\n");

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { Console.WriteLine($"[FAIL] cannot listen on {port}: {ex.Message}"); return 2; }

        Console.WriteLine($"Proxy: http://localhost:{port}/  ->  {target}");
        Console.WriteLine($"Logging to {logPath}. Ctrl+C to stop.\n");

        while (true)
        {
            System.Net.HttpListenerContext ctx = await listener.GetContextAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    System.Net.HttpListenerRequest req = ctx.Request;
                    string body;
                    using (var sr = new StreamReader(req.InputStream, req.ContentEncoding)) body = await sr.ReadToEndAsync();

                    string entry = $"\n=== {DateTime.Now:HH:mm:ss} {req.HttpMethod} {req.Url!.PathAndQuery} ===\n{body}\n";
                    Console.WriteLine(entry);
                    File.AppendAllText(logPath, entry);

                    var fwd = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod(req.HttpMethod), target + req.Url!.PathAndQuery);
                    if (body.Length > 0)
                        fwd.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, (req.ContentType ?? "application/json").Split(';')[0]);
                    string? auth = req.Headers["Authorization"];
                    if (auth is not null) fwd.Headers.TryAddWithoutValidation("Authorization", auth);

                    System.Net.Http.HttpResponseMessage resp = await http.SendAsync(fwd);
                    byte[] respBytes = await resp.Content.ReadAsByteArrayAsync();
                    File.AppendAllText(logPath, $"--> RESPONSE {(int)resp.StatusCode} ({respBytes.Length} bytes)\n");

                    ctx.Response.StatusCode = (int)resp.StatusCode;
                    ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
                    await ctx.Response.OutputStream.WriteAsync(respBytes);
                    ctx.Response.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("proxy error: " + ex.Message);
                    try { ctx.Response.StatusCode = 502; ctx.Response.Close(); } catch { }
                }
            });
        }
    }
}
