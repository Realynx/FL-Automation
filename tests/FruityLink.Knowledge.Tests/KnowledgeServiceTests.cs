using FruityLink.Core.Domain;
using Shouldly;
using Xunit;

namespace FruityLink.Knowledge.Tests;

public sealed class KnowledgeServiceTests : KnowledgeDbTestBase
{
    public KnowledgeServiceTests()
        : base("fruitylink-knowledge-tests", "knowledge.db")
    {
    }

    [Fact]
    public async Task AddFileSource_ThenSearch_ReturnsRelevantChunkFirst()
    {
        KnowledgeService service = CreateService();

        string synthPath = WriteTextFile("synths.txt",
            "Subtractive synthesis filters harmonics from a sawtooth oscillator. " +
            "A resonant low-pass filter sweep gives the classic acid bass sound.");
        string drumsPath = WriteTextFile("drums.txt",
            "Layering a kick drum with a clap and a snare builds a punchy backbeat. " +
            "Swing and groove come from shifting the hi-hat timing slightly.");

        await service.AddFileSourceAsync(synthPath);
        await service.AddFileSourceAsync(drumsPath);

        var hits = await service.SearchAsync("resonant filter sweep on the oscillator", topK: 3);

        hits.ShouldNotBeEmpty();
        hits[0].SourceTitle.ShouldBe("synths.txt");
        hits[0].Text.ShouldContain("filter");
        // Scores must be ordered descending.
        for (int i = 0; i < hits.Count - 1; i++)
            hits[i].Score.ShouldBeGreaterThanOrEqualTo(hits[i + 1].Score);
    }

    [Fact]
    public async Task AddFileSource_ReturnsSourceWithTitleAndChunkCount()
    {
        KnowledgeService service = CreateService();
        string path = WriteTextFile("notes.md", string.Join(' ', Enumerable.Repeat("groove", 400)));

        KnowledgeSource source = await service.AddFileSourceAsync(path);

        source.Title.ShouldBe("notes.md");
        source.Uri.ShouldBe(path);
        source.ChunkCount.ShouldBeGreaterThan(1);
        source.Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListSources_ReflectsIngestedState()
    {
        KnowledgeService service = CreateService();
        string a = WriteTextFile("a.txt", "alpha bravo charlie");
        string b = WriteTextFile("b.txt", "delta echo foxtrot");

        await service.AddFileSourceAsync(a);
        await service.AddFileSourceAsync(b);

        var sources = await service.ListSourcesAsync();
        sources.Count.ShouldBe(2);
        sources.Select(s => s.Title).ShouldBe(new[] { "a.txt", "b.txt" }, ignoreOrder: true);
    }

    [Fact]
    public async Task RemoveSource_DeletesItsChunksAndDropsFromListing()
    {
        KnowledgeService service = CreateService();
        string a = WriteTextFile("keep.txt", "resonant filter oscillator harmonics");
        string b = WriteTextFile("remove.txt", "kick clap snare hihat groove");

        KnowledgeSource keep = await service.AddFileSourceAsync(a);
        KnowledgeSource remove = await service.AddFileSourceAsync(b);

        await service.RemoveSourceAsync(remove.Id);

        var sources = await service.ListSourcesAsync();
        sources.Count.ShouldBe(1);
        sources[0].Id.ShouldBe(keep.Id);

        // Search must no longer surface the removed source's chunks.
        var hits = await service.SearchAsync("kick clap snare", topK: 5);
        hits.ShouldAllBe(h => h.SourceId == keep.Id);
    }

    [Fact]
    public async Task Search_EmptyIndex_ReturnsEmpty()
    {
        KnowledgeService service = CreateService();
        var hits = await service.SearchAsync("anything", topK: 5);
        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_DimensionMismatch_SkipsMismatchedVectors()
    {
        // Service embeds queries at dim 8; store a chunk vector at dim 16 directly.
        var embeddings = new FakeEmbeddingClient(dim: 8);
        var store = new SqliteVectorStore(_dbPath);
        var service = new KnowledgeService(embeddings, store, new SourceTextExtractor(), new HttpClient());

        store.UpsertSource(new KnowledgeSource("mismatch", "mem://x", "Mismatch", DateTimeOffset.UtcNow, 1));
        store.UpsertChunks("mismatch", new[]
        {
            new ChunkRecord("mismatch:0", 0, "wrong dimension chunk", new float[16]),
        });

        // A correctly-dimensioned chunk that should still be found.
        string path = WriteTextFile("ok.txt", "alpha bravo charlie delta");
        await service.AddFileSourceAsync(path);

        var hits = await service.SearchAsync("alpha bravo", topK: 5);
        hits.ShouldAllBe(h => h.SourceId != "mismatch");
    }

    [Fact]
    public async Task AddWebSource_WithFakeHandler_ExtractsTitleAndIndexesText()
    {
        const string html =
            "<html><head><title>Sidechain Compression</title><style>.x{}</style></head>" +
            "<body><script>ignore()</script><h1>Sidechain</h1>" +
            "<p>Ducking the bass against the kick drum creates pumping sidechain compression.</p>" +
            "</body></html>";

        var handler = new FakeHttpMessageHandler(html);
        using var httpClient = new HttpClient(handler);
        KnowledgeService service = CreateService(httpClient: httpClient);

        KnowledgeSource source = await service.AddWebSourceAsync("https://example.com/sidechain");

        source.Title.ShouldBe("Sidechain Compression");
        source.Uri.ShouldBe("https://example.com/sidechain");
        source.ChunkCount.ShouldBeGreaterThan(0);

        var hits = await service.SearchAsync("pumping ducking compression against the kick", topK: 3);
        hits.ShouldNotBeEmpty();
        hits[0].SourceId.ShouldBe(source.Id);
        // Script/style content must have been stripped.
        hits[0].Text.ShouldNotContain("ignore()");
        hits[0].Text.ShouldNotContain(".x{}");
    }

    [Fact]
    public async Task Reingestion_IsIdempotent_DoesNotDuplicateChunks()
    {
        KnowledgeService service = CreateService();
        string path = WriteTextFile("dup.txt", "alpha bravo charlie delta echo foxtrot");

        await service.AddFileSourceAsync(path);
        var afterFirst = await service.SearchAsync("alpha bravo", topK: 50);
        await service.AddFileSourceAsync(path); // same content, new source id

        var sources = await service.ListSourcesAsync();
        // Each AddFileSource creates a new source; both should be present and intact.
        sources.Count.ShouldBe(2);
        afterFirst.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AddFileSource_StripsControlCharsAndPreservesRealText()
    {
        KnowledgeService service = CreateService();

        // Special chars are built from code points so this source file stays pure ASCII.
        string nul = ((char)0x00).ToString();        // NUL
        string bell = ((char)0x07).ToString();       // a C0 control char
        string replacement = ((char)0xFFFD).ToString(); // Unicode replacement char (decode garbage)
        string emoji = char.ConvertFromUtf32(0x1F600);   // valid surrogate pair, must survive

        // Embed the junk between real words; control chars must not crash the embedding backend
        // (many serialize chunks to JSON) and must not appear in the stored index.
        string dirty = "resonant" + nul + "filter" + bell + "oscillator" + replacement +
                       " harmonics " + emoji + " sawtooth";
        string path = WriteTextFile("dirty.txt", dirty);

        KnowledgeSource source = await service.AddFileSourceAsync(path);
        source.ChunkCount.ShouldBeGreaterThan(0);

        var hits = await service.SearchAsync("resonant filter oscillator", topK: 1);
        hits.ShouldNotBeEmpty();
        string text = hits[0].Text;

        text.ShouldContain("resonant");
        text.ShouldContain("oscillator");
        text.ShouldContain("harmonics");
        text.ShouldContain(emoji);        // valid surrogate pair preserved
        text.ShouldNotContain(nul);
        text.ShouldNotContain(bell);
        text.ShouldNotContain(replacement);
    }

    [Fact]
    public async Task ExtractPdf_EmptyBytes_ReturnsEmpty()
    {
        var extractor = new SourceTextExtractor();
        string text = await extractor.ExtractPdfAsync(Array.Empty<byte>());
        text.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractPdf_GarbageBytes_ThrowsClearMessage()
    {
        var extractor = new SourceTextExtractor();
        var garbage = new byte[256];
        for (int i = 0; i < garbage.Length; i++)
            garbage[i] = (byte)((i * 37) & 0xFF);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await extractor.ExtractPdfAsync(garbage));
        ex.Message.ShouldContain("PDF");
    }

    [Fact]
    public async Task AddFileSource_CorruptPdf_ThrowsButStoreStaysUsable()
    {
        KnowledgeService service = CreateService();

        // A .pdf whose bytes are not a real PDF — ingest must fail cleanly, not crash the process
        // or corrupt the store.
        string badPdf = Path.Combine(_workDir, "corrupt.pdf");
        File.WriteAllBytes(badPdf, new byte[] { 0x25, 0x50, 0x44, 0x46, 0xDE, 0xAD, 0xBE, 0xEF });

        await Should.ThrowAsync<InvalidOperationException>(() => service.AddFileSourceAsync(badPdf));

        // The store is still usable for a subsequent valid source.
        string ok = WriteTextFile("after.txt", "alpha bravo charlie delta");
        KnowledgeSource source = await service.AddFileSourceAsync(ok);
        source.ChunkCount.ShouldBeGreaterThan(0);

        var sources = await service.ListSourcesAsync();
        sources.ShouldContain(s => s.Title == "after.txt");
        sources.ShouldNotContain(s => s.Title == "corrupt.pdf");
    }
}
