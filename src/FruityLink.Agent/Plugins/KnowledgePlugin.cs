using System.ComponentModel;
using FruityLink.Core.Abstractions;
using Microsoft.SemanticKernel;
using static FruityLink.Agent.Plugins.PluginSupport;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// RAG tool: searches the user's ingested knowledge base (FL Studio docs, music-theory
/// references, etc.) for background relevant to the current question.
/// </summary>
public sealed class KnowledgePlugin(IKnowledgeRetriever retriever)
{
    [KernelFunction("search_knowledge")]
    [Description("Search ingested FL Studio docs + music-theory references; returns top excerpts with sources.")]
    public async Task<string> SearchKnowledgeAsync(
        [Description("What to look up")] string query,
        [Description("Excerpts to return, 1-20 (default 5)")] int topK = 5,
        CancellationToken ct = default)
    {
        try
        {
            // Clamp: a weak model may pass a huge topK that would dump the whole base into context.
            topK = Math.Clamp(topK, 1, 20);
            var hits = await retriever.SearchAsync(query, topK, ct);
            if (hits.Count == 0)
                return Ok("no matching knowledge found — the base may be empty (the user can add sources in the Knowledge panel)");

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
}
