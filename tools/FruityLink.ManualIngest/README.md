# FruityLink.ManualIngest

Offline data-prep that turns the **FL Studio online manual** into the RAG vector database the
FL Automate plugin searches at runtime (`search_manual` tool).

Pipeline (three stages, each independently runnable and idempotent):

```
crawl    →  FL Studio online manual/html/**        (raw HTML mirror + manifest.json)
convert  →  FL Studio online manual/markdown/**     (ReverseMarkdown, main content only)
build    →  FL Studio online manual/fl-manual.db     (chunk + embed → SQLite vector store)
```

The `build` stage embeds each chunk with **Azure OpenAI `text-embedding-3-large`** (3072-dim). At
runtime the plugin embeds the search query with the *same* model **through the AI gateway**
(`POST /v1/embeddings`, metered per tier), so the query and corpus share one vector space.

## Usage

```bash
# 1) Crawl (courtesy >= 1s/page; BFS from the TOC, follows internal html/ links)
dotnet run --project tools/FruityLink.ManualIngest -- crawl

# 2) HTML -> Markdown
dotnet run --project tools/FruityLink.ManualIngest -- convert

# 3) Embed -> fl-manual.db  (needs the Azure key)
dotnet run --project tools/FruityLink.ManualIngest -- build --key <AZURE_KEY>
#   or:  MANUAL_EMBED_KEY / AZURE_OPENAI_API_KEY env var instead of --key

# everything in one go:
dotnet run --project tools/FruityLink.ManualIngest -- all --key <AZURE_KEY>
```

Flags: `--root DIR` (default `FL Studio online manual`), `--delay-ms 1000`, `--max N`,
`--endpoint URL`, `--model M`, `--dimensions 3072`, `--fresh` (force a clean rebuild).

Defaults target `https://obsidianfox-ai.services.ai.azure.com/openai/v1` /
`text-embedding-3-large`.

## Incremental updates (when FL revises the manual)

Re-run the same three stages **in place** (or copy the previous corpus folder first). Everything is
incremental:

- **crawl** replays each page's stored `ETag`/`Last-Modified` as `If-None-Match`/`If-Modified-Since`,
  so unchanged pages come back `304 Not Modified` (cheap) and are reported `new` / `changed` /
  `unchanged` / `removed`.
- **build** fingerprints each page's markdown; a page whose content is unchanged **keeps its existing
  embeddings** (no Azure call). Only new/changed pages are re-embedded; pages removed upstream are
  pruned from the DB. This also makes the first (full) build **resumable** — re-run after a failure
  and it skips everything already embedded.

Use `--fresh` to discard the DB and re-embed everything from scratch.

## Shipping the DB

Stage the built `fl-manual.db` where the plugin looks for it (first match wins):

1. `%APPDATA%\FLAutomate\fl-manual.db`
2. `<plugin dir>\knowledge\fl-manual.db` — drop it in
   `src/FruityLink.Plugins.FlAgent/knowledge/fl-manual.db` and it is copied to the plugin output
   automatically (`FruityLink.Plugins.FlAgent.csproj`).
3. `<plugin dir>\fl-manual.db`

If none is present the plugin runs normally and `search_manual` simply returns no hits.
