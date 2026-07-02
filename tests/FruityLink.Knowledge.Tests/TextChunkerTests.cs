using Shouldly;
using Xunit;

namespace FruityLink.Knowledge.Tests;

public sealed class TextChunkerTests
{
    private const int Max = 100;
    private const int Overlap = 20;

    [Fact]
    public void Chunk_EmptyOrWhitespace_ReturnsNothing()
    {
        var chunker = new TextChunker(Max, Overlap);
        chunker.Chunk(null).ShouldBeEmpty();
        chunker.Chunk("").ShouldBeEmpty();
        chunker.Chunk("    \n\t  ").ShouldBeEmpty();
    }

    [Fact]
    public void Chunk_ShortText_ReturnsSingleChunk()
    {
        var chunker = new TextChunker(Max, Overlap);
        var chunks = chunker.Chunk("A short sentence.");
        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBe("A short sentence.");
    }

    [Fact]
    public void Chunk_LongText_ProducesMultipleChunks_NoneExceedingMax()
    {
        var chunker = new TextChunker(Max, Overlap);
        string text = BuildLorem(60);

        var chunks = chunker.Chunk(text);

        chunks.Count.ShouldBeGreaterThan(1);
        foreach (string chunk in chunks)
            chunk.Length.ShouldBeLessThanOrEqualTo(Max);
    }

    [Fact]
    public void Chunk_DoesNotSplitMidWord()
    {
        var chunker = new TextChunker(Max, Overlap);
        string text = BuildLorem(80);

        var chunks = chunker.Chunk(text);

        // Every word in the original must appear intact in at least one chunk.
        var originalWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunkWords = chunks
            .SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet();

        foreach (string word in originalWords)
            chunkWords.ShouldContain(word);
    }

    [Fact]
    public void Chunk_HasOverlapBetweenConsecutiveChunks()
    {
        var chunker = new TextChunker(Max, Overlap);
        string text = BuildLorem(80);

        var chunks = chunker.Chunk(text);
        chunks.Count.ShouldBeGreaterThan(1);

        // The tail words of chunk N should reappear among the head words of chunk N+1.
        for (int i = 0; i < chunks.Count - 1; i++)
        {
            var tail = chunks[i].Split(' ').TakeLast(3).ToHashSet();
            var head = chunks[i + 1].Split(' ').Take(6).ToHashSet();
            tail.Overlaps(head).ShouldBeTrue($"chunk {i} and {i + 1} should share overlap words");
        }
    }

    [Fact]
    public void Chunk_VeryLongSingleWord_BreaksAtHardLimit()
    {
        var chunker = new TextChunker(Max, Overlap);
        string giant = new string('x', Max * 3);

        var chunks = chunker.Chunk(giant);

        chunks.Count.ShouldBeGreaterThan(1);
        foreach (string chunk in chunks)
            chunk.Length.ShouldBeLessThanOrEqualTo(Max);
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new TextChunker(0, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new TextChunker(100, 100));
        Should.Throw<ArgumentOutOfRangeException>(() => new TextChunker(100, -1));
    }

    private static string BuildLorem(int words)
    {
        string[] vocab =
        {
            "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf",
            "hotel", "india", "juliet", "kilo", "lima", "mike", "november",
        };
        var parts = new List<string>(words);
        for (int i = 0; i < words; i++)
        {
            string w = vocab[i % vocab.Length];
            // Sprinkle sentence boundaries to exercise sentence-aware breaking.
            parts.Add((i % 7 == 6) ? w + "." : w);
        }

        return string.Join(' ', parts);
    }
}
