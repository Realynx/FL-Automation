using Shouldly;
using Xunit;

namespace FruityLink.Knowledge.Tests;

public sealed class VectorMathTests
{
    [Fact]
    public void Cosine_IdenticalVectors_IsOne()
    {
        float[] a = { 1f, 2f, 3f, 4f };
        VectorMath.Cosine(a, a).ShouldBe(1d, 1e-9);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_IsZero()
    {
        float[] a = { 1f, 0f };
        float[] b = { 0f, 1f };
        VectorMath.Cosine(a, b).ShouldBe(0d, 1e-9);
    }

    [Fact]
    public void Cosine_OppositeVectors_IsMinusOne()
    {
        float[] a = { 1f, 2f, 3f };
        float[] b = { -1f, -2f, -3f };
        VectorMath.Cosine(a, b).ShouldBe(-1d, 1e-9);
    }

    [Fact]
    public void Cosine_ZeroVector_IsZero()
    {
        float[] a = { 0f, 0f, 0f };
        float[] b = { 1f, 2f, 3f };
        VectorMath.Cosine(a, b).ShouldBe(0d);
    }

    [Fact]
    public void Cosine_LengthMismatch_Throws()
    {
        float[] a = { 1f, 2f };
        float[] b = { 1f, 2f, 3f };
        Should.Throw<ArgumentException>(() => VectorMath.Cosine(a, b));
    }
}
