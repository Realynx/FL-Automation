using FruityLink.Core.Abstractions;

namespace FruityLink.Knowledge.Tests;

/// <summary>
/// Deterministic test embedding client: produces a fixed-dimension hashed term-frequency
/// (bag-of-words) vector, then L2-normalises it. Texts sharing vocabulary therefore land in
/// similar directions and score higher under cosine similarity, with no network involved.
/// </summary>
internal sealed class FakeEmbeddingClient : IEmbeddingClient
{
    private readonly int _dim;

    public FakeEmbeddingClient(int dim = 64) => _dim = dim;

    /// <summary>Number of <see cref="EmbedAsync(string, CancellationToken)"/> calls observed.</summary>
    public int CallCount { get; private set; }

    public Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(Embed(input));
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var result = new List<float[]>(inputs.Count);
        foreach (string input in inputs)
        {
            CallCount++;
            result.Add(Embed(input));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(result);
    }

    private float[] Embed(string text)
    {
        var vector = new float[_dim];
        foreach (string token in Tokenize(text))
        {
            int bucket = (int)(Hash(token) % (uint)_dim);
            vector[bucket] += 1f;
        }

        // L2-normalise so cosine reflects term overlap, not document length.
        double magnitude = 0d;
        foreach (float v in vector)
            magnitude += v * v;
        magnitude = Math.Sqrt(magnitude);
        if (magnitude > 0d)
        {
            for (int i = 0; i < vector.Length; i++)
                vector[i] = (float)(vector[i] / magnitude);
        }

        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (string raw in text.Split(
                     new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '-', '"', '\'' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.ToLowerInvariant();
            if (token.Length > 0)
                yield return token;
        }
    }

    // Deterministic FNV-1a hash (independent of runtime string-hash randomisation).
    private static uint Hash(string token)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (char c in token)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash;
    }
}
