using System.Net.Http.Headers;
using System.Text;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Swaps an <see cref="HttpResponseMessage"/>'s (possibly already-consumed) content for a fresh,
/// re-readable UTF-8 buffer, copying the original content headers minus the ones that describe the
/// OLD bytes. This is the single owner of that header-exclusion list, shared by
/// <see cref="LlmToolCallRepairHandler"/> and <see cref="LlmLoggingHandler"/>:
/// <list type="bullet">
///   <item><c>Content-Length</c> — the new body has a different length; StringContent computes its own.</item>
///   <item><c>Content-Type</c> — re-emitted below with the correct charset for the new bytes.</item>
///   <item><c>Content-Encoding</c> — the transport already decompressed the response; carrying the
///     original value forward makes a downstream reader try to un-gzip plaintext.</item>
///   <item><c>Content-MD5</c> — a checksum of bytes that no longer exist.</item>
/// </list>
/// </summary>
internal static class HttpContentRebuffer
{
    /// <summary>Replaces <paramref name="response"/>'s content with <paramref name="newBody"/>.</summary>
    /// <param name="response">Response whose content is replaced in place.</param>
    /// <param name="newBody">Replacement body text (buffered; downstream can read it repeatedly).</param>
    /// <param name="mediaType">Media type to stamp on the new content. Null preserves the original
    /// response's media type (falling back to <c>application/json</c> when the server sent none) —
    /// used by the logging handler so an HTML error page isn't mislabeled as JSON. The repair
    /// handler passes <c>application/json</c> explicitly because its output is always JSON.</param>
    internal static void Replace(HttpResponseMessage response, string newBody, string? mediaType = null)
    {
        HttpContentHeaders oldHeaders = response.Content.Headers;
        string effectiveType = mediaType ?? oldHeaders.ContentType?.MediaType ?? "application/json";

        var content = new StringContent(newBody, Encoding.UTF8, effectiveType);
        foreach (KeyValuePair<string, IEnumerable<string>> header in oldHeaders)
        {
            if (DescribesOldBytes(header.Key)) continue;
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = content;
    }

    private static bool DescribesOldBytes(string name) =>
        string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Content-MD5", StringComparison.OrdinalIgnoreCase);
}
