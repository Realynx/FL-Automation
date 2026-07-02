namespace FruityLink.Knowledge;

/// <summary>Small vector helpers used by the brute-force similarity search.</summary>
public static class VectorMath
{
    /// <summary>
    /// Computes the cosine similarity between two equal-length vectors. Returns a value in
    /// <c>[-1, 1]</c> (1 = identical direction, 0 = orthogonal, -1 = opposite). Returns
    /// <c>0</c> when either vector has zero magnitude.
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector (must be the same length as <paramref name="a"/>).</param>
    /// <exception cref="ArgumentException">The vectors have different lengths.</exception>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vector length mismatch: {a.Length} vs {b.Length}.", nameof(b));

        double dot = 0d, magA = 0d, magB = 0d;
        for (int i = 0; i < a.Length; i++)
        {
            double x = a[i];
            double y = b[i];
            dot += x * y;
            magA += x * x;
            magB += y * y;
        }

        if (magA <= 0d || magB <= 0d)
            return 0d;

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
