using FruityLink.Core.Domain;
using Shouldly;
using Xunit;

namespace FruityLink.Knowledge.Tests;

/// <summary>
/// Tests the incremental / idempotent-by-URI ingestion used by the manual-corpus builder:
/// unchanged content is skipped (no re-embed), changed content is re-embedded in place, the same
/// URI never duplicates, and pruning drops sources no longer present.
/// </summary>
public sealed class IncrementalIngestTests : KnowledgeDbTestBase
{
    public IncrementalIngestTests()
        : base("fruitylink-incremental-tests", "manual.db")
    {
    }

    [Fact]
    public async Task AddText_UnchangedContent_ReusesEmbeddingsAndDoesNotDuplicate()
    {
        var fake = new FakeEmbeddingClient();
        KnowledgeService svc = CreateService(fake);
        const string uri = "https://manual/x.htm";
        const string text = "Subtractive synthesis filters harmonics from a sawtooth oscillator.";

        TextIngestResult first = await svc.AddTextSourceAsync(uri, "X", text);
        int callsAfterFirst = fake.CallCount;

        TextIngestResult second = await svc.AddTextSourceAsync(uri, "X", text);

        first.Reused.ShouldBeFalse();
        second.Reused.ShouldBeTrue();
        fake.CallCount.ShouldBe(callsAfterFirst);            // no embedding call the second time
        second.ChunkCount.ShouldBe(first.ChunkCount);
        (await svc.ListSourcesAsync()).Count.ShouldBe(1);     // same URI -> one source, not two
    }

    [Fact]
    public async Task AddText_ChangedContent_ReembedsInPlaceAndReplacesChunks()
    {
        var fake = new FakeEmbeddingClient();
        KnowledgeService svc = CreateService(fake);
        const string uri = "https://manual/y.htm";

        await svc.AddTextSourceAsync(uri, "Y", "alpha bravo charlie the original oscillator text");
        int callsAfterFirst = fake.CallCount;

        TextIngestResult changed = await svc.AddTextSourceAsync(uri, "Y", "delta echo foxtrot the replacement reverb text");

        changed.Reused.ShouldBeFalse();
        fake.CallCount.ShouldBeGreaterThan(callsAfterFirst);  // re-embedded
        (await svc.ListSourcesAsync()).Count.ShouldBe(1);      // still one source (replaced, not added)

        var hits = await svc.SearchAsync("replacement reverb", topK: 5);
        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(h => h.Text.Contains("replacement")); // old chunks gone
    }

    [Fact]
    public async Task PruneSourcesNotIn_RemovesSourcesNoLongerPresent()
    {
        var fake = new FakeEmbeddingClient();
        KnowledgeService svc = CreateService(fake);

        await svc.AddTextSourceAsync("https://manual/a.htm", "A", "alpha alpha alpha");
        await svc.AddTextSourceAsync("https://manual/b.htm", "B", "bravo bravo bravo");

        int pruned = await svc.PruneSourcesNotInAsync(new[] { "https://manual/a.htm" });

        pruned.ShouldBe(1);
        var sources = await svc.ListSourcesAsync();
        sources.Count.ShouldBe(1);
        sources[0].Title.ShouldBe("A");
    }

    [Fact]
    public async Task PruneSourcesNotIn_KeepingAll_RemovesNothing()
    {
        var fake = new FakeEmbeddingClient();
        KnowledgeService svc = CreateService(fake);
        await svc.AddTextSourceAsync("https://manual/a.htm", "A", "alpha");
        await svc.AddTextSourceAsync("https://manual/b.htm", "B", "bravo");

        int pruned = await svc.PruneSourcesNotInAsync(new[] { "https://manual/a.htm", "https://manual/b.htm" });

        pruned.ShouldBe(0);
        (await svc.ListSourcesAsync()).Count.ShouldBe(2);
    }
}
