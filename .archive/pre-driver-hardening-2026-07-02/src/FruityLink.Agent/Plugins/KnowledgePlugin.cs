using System.ComponentModel;
using FruityLink.Core.Abstractions;
using Microsoft.SemanticKernel;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// RAG tool: searches the user's ingested knowledge base (FL Studio docs, music-theory
/// references, etc.) for background relevant to the current question.
/// </summary>
public sealed class KnowledgePlugin(IKnowledgeRetriever retriever)
{
    [KernelFunction("search_knowledge")]
    [Description("Search the user's knowledge base (ingested FL Studio docs + music-theory references); returns the most relevant excerpts with sources. Use when you need background facts on FL features or music theory before answering.")]
    public async Task<string> SearchKnowledgeAsync(
        [Description("What to look up")] string query,
        [Description("Excerpts to return (default 5)")] int topK = 5,
        CancellationToken ct = default)
    {
        try
        {
            // Clamp: a weak model may pass a huge topK that would dump the whole base into context.
            topK = Math.Clamp(topK, 1, 20);
            var hits = await retriever.SearchAsync(query, topK, ct);
            if (hits.Count == 0)
                return "No matching knowledge found. The knowledge base may be empty — the user can add sources in the Knowledge panel.";

            return string.Join("\n\n", hits.Select((h, i) =>
                $"[{i + 1}] ({h.SourceTitle}, score {h.Score:0.00})\n{h.Text}"));
        }
        catch (Exception ex)
        {
            return $"Knowledge search failed: {ex.Message}";
        }
    }
}
