using System.Runtime.CompilerServices;

// Expose internal types (TextChunker, SqliteVectorStore, ISourceTextExtractor, the test-only
// KnowledgeService constructor, etc.) to the Knowledge test project.
[assembly: InternalsVisibleTo("FruityLink.Knowledge.Tests")]
