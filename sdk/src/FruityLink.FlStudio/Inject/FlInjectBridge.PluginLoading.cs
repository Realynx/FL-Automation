using System.Diagnostics;

namespace FruityLink.FlStudio.Inject;

// Plugin instantiation: the one load budget every generator/effect/preset load shares, and the
// first-load timeout recovery that turns "FL went quiet while the plugin initialised" into a note
// instead of a failure. Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs.
public sealed partial class FlInjectBridge
{
    /// <summary>The guard for a call that INSTANTIATES a plugin (generator load, mixer FX slot load,
    /// preset/state file load). Instantiating the FIRST plugin of a session blocks FL's UI thread for
    /// much longer than a struct read: live on FL 26.1.3.5570 a cold
    /// <c>add_mixer_effect(track: 20, slot: 0, "Fruity Parametric EQ 2")</c> outran the ordinary 5000 ms
    /// guard and an immediate retry then succeeded in well under a second, so the 5 s budget was
    /// measuring first-load latency, not a stuck dialog. 20 s covers a cold VST scan/instantiate;
    /// every other bridge call keeps its own (much shorter) guard, because they do not run plugin code.</summary>
    internal const int PluginInstantiationTimeoutMs = 20000;

    /// <summary>How long to let FL breathe before the single post-timeout re-inspection of the load
    /// target. The orphaned native load is still running on FL's UI thread when the guard expires, so
    /// an immediate peek would just time out as well.</summary>
    internal const int PluginInstantiationSettleMs = 1500;

    /// <summary>Runs one plugin instantiation under <see cref="PluginInstantiationTimeoutMs"/> and, when the
    /// guard expires, re-inspects the load target EXACTLY ONCE after <see cref="PluginInstantiationSettleMs"/>.
    /// Returns "" when the load completed inside the budget, or a note to append to the operation's
    /// verification text when the plugin did land after all. Only a target that still reads empty (or a
    /// follow-up inspection FL would not answer either) raises a <see cref="TimeoutException"/> — and its
    /// message says the plugin is still initialising instead of asserting a licence prompt, because the
    /// generic bridge text guessed "sign-in/licence dialog" for what was plain first-load latency.
    /// <paramref name="inspect"/> must return the plugin name loaded at the target, or "" when it is empty.</summary>
    internal static async Task<string> LoadPluginWithRecoveryAsync(string target, string pluginName,
        Func<Task> load, Func<Task<string>> inspect, Func<int, Task> settle)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await load().ConfigureAwait(false);
            return "";
        }
        catch (TimeoutException expired)
        {
            await settle(PluginInstantiationSettleMs).ConfigureAwait(false);
            string loaded;
            try { loaded = await inspect().ConfigureAwait(false); }
            catch (Exception probe) when (probe is TimeoutException or IOException)
            {
                throw PluginInstantiationTimeout(target, pluginName, $"FL did not answer the follow-up inspection of {target}", expired);
            }
            if (loaded.Length == 0)
                throw PluginInstantiationTimeout(target, pluginName, $"{target} is still empty", expired);
            return PluginInstantiationNote((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>The note a recovered first load appends to its verification text.</summary>
    internal static string PluginInstantiationNote(long elapsedMs)
        => $" (loaded after {elapsedMs} ms; FL's UI was blocked while the plugin initialised)";

    /// <summary>The instantiation timeout a caller actually sees: what was being loaded, what the target reads
    /// now, and the honest cause — still initialising, or waiting on a dialog — never an asserted licence prompt.</summary>
    internal static TimeoutException PluginInstantiationTimeout(string target, string pluginName, string state, Exception? inner)
        => new($"FL Studio did not respond within {PluginInstantiationTimeoutMs} ms while instantiating '{pluginName}' and {state}: " +
            "the plugin is still initialising or waiting on a dialog. Look at FL's GUI for a plugin window or message box, " +
            $"dismiss it if there is one, then retry and re-inspect {target} — a slow first load can still complete on its own.", inner);
}
