using System.ComponentModel;
using FruityLink.Agent.Manuals;
using FruityLink.Core.Abstractions;
using Microsoft.SemanticKernel;
using static FruityLink.Agent.Plugins.PluginSupport;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Retrieval home for the agent's background knowledge. Two sources:
/// (1) the user's ingested RAG base (FL Studio docs, music-theory references) via <c>search_knowledge</c>;
/// (2) the built-in plugin "man db" — curated, per-plugin manuals the AI reads BEFORE designing a sound
/// or an effect chain, then drives the plugin's params via the native_*_plugin_params tools.
/// <para><paramref name="manuals"/> is optional so the existing composition root (and tests) that
/// construct this plugin positionally keep compiling: it defaults to the process-wide
/// <see cref="ManualStore.Default"/>, which is self-contained (loads embedded manuals, no DI needed).</para>
/// </summary>
public sealed class KnowledgePlugin(IKnowledgeRetriever retriever, ManualStore? manuals = null)
{
    private readonly ManualStore _manuals = manuals ?? ManualStore.Default;

    [KernelFunction("search_manual")]
    [Description("Search the official FL Studio user manual for how a feature, window, tool or plugin works. Returns " +
                 "the most relevant excerpts with page titles and source URLs. Use before answering 'how do I' or " +
                 "'what does X do' questions.")]
    public Task<string> SearchManualAsync(
        [Description("Natural-language question or keywords, e.g. 'how to sidechain with Fruity Limiter'")] string query,
        [Description("Excerpts to return, 1-20 (default 6)")] int topK = 6,
        CancellationToken ct = default)
        => SearchAsync(query, topK == 0 ? 6 : topK,
            "no matching manual page found — try different keywords, or the manual DB may not be installed", ct);

    [KernelFunction("search_knowledge")]
    [Description("Search ingested FL Studio docs + music-theory references; returns top excerpts with sources.")]
    public Task<string> SearchKnowledgeAsync(
        [Description("What to look up")] string query,
        [Description("Excerpts to return, 1-20 (default 5)")] int topK = 5,
        CancellationToken ct = default)
        => SearchAsync(query, topK, "no matching knowledge found — the base may be empty", ct);

    /// <summary>Shared RAG lookup for both search tools (same retriever + envelope, different framing).</summary>
    private async Task<string> SearchAsync(string query, int topK, string emptyMessage, CancellationToken ct)
    {
        try
        {
            // Clamp: a weak model may pass a huge topK that would dump the whole base into context.
            topK = Math.Clamp(topK, 1, 20);
            var hits = await retriever.SearchAsync(query, topK, ct);
            if (hits.Count == 0)
                return Ok(emptyMessage);

            return Ok(string.Join("\n\n", hits.Select((h, i) =>
                $"[{i + 1}] ({h.SourceTitle}, score {h.Score:0.00})\n{h.Text}")));
        }
        catch (OperationCanceledException) { throw; }
        // Deliberately NOT PluginSupport.Run: the retriever is local (no FL bridge involved), so
        // BridgeError's pipe/timeout classification would mislead the model into "inject the bridge"
        // advice — surface the real cause under the shared ERR envelope instead.
        catch (Exception ex)
        {
            return Err($"knowledge search failed: {ex.Message}");
        }
    }

    [KernelFunction("list_plugin_manuals")]
    [Description("List available FL plugin manuals (name, type, one-line summary). Read one via get_plugin_manual before designing a sound or effect chain.")]
    public string ListPluginManuals()
    {
        var all = _manuals.All;
        if (all.Count == 0)
            return Ok("no plugin manuals installed yet");
        return Ok($"plugin manuals ({all.Count}):\n{_manuals.Catalog()}");
    }

    [KernelFunction("get_plugin_manual")]
    [Description("Get an FL plugin's manual: oscillators/params, sound-design recipes, effect chain placement. Fuzzy name match. Read it before driving params via native_list/set_channel/mixer_plugin_params.")]
    public string GetPluginManual(
        [Description("Plugin name, e.g. 3xOSC, Fruity Reverb 2, Serum")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Err($"provide a plugin name. Available: {string.Join(", ", _manuals.Names())}");

        PluginManual? manual = _manuals.Resolve(name);
        if (manual is null)
        {
            var names = _manuals.Names();
            return Err(names.Count == 0
                ? "no plugin manuals installed yet"
                : $"no manual for '{name}'. Did you mean: {string.Join(", ", names)}");
        }

        return Ok(manual.Body);
    }
}
