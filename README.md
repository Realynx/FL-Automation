# FruityLink

An AI co-pilot for **FL Studio (FruityLoops)**. FruityLink is a WPF desktop app that hosts a
Large-Language-Model agent (via **Microsoft Semantic Kernel**) which can understand, query, and
control your FL Studio project on demand — build chord progressions, melodies, and drums; inspect
and adjust the channel rack, mixer, effects, plugins, patterns, transport, and tempo; and pull
background knowledge from your own ingested docs. You stay in creative control (pick samples,
audition, steer); the agent does the tedious parts faster.

> Status: **Phase 1 vertical slice complete and green.** Backend (all layers) + a working chat that
> drives the FL tools and stages notes, with a light/dark black-orange-white theme. See
> [Roadmap](#roadmap) for what's wired vs. still UI-pending.

## How control works (the hybrid bridge)

FL Studio has **no single automation API**. Control runs through sandboxed Python *inside* FL,
split across two systems, so FruityLink uses a hybrid bridge (the same approach as the community
*Flapi* and *music-copilot* projects):

| Plane | Path | Used for |
|---|---|---|
| **Control / query** | App ⇄ **JSON request/response files** ⇄ a **MIDI controller script** (`device_FruityLink.py`) polling in `OnIdle` — **no virtual MIDI port, no driver, no install** | channel rack, mixer + FX, plugin params, patterns, transport, tempo, project info |
| **Note content** | App writes a **note-spec JSON**; a **piano-roll script** (`FruityLink.pyscript`) inserts the exact notes | chords, melodies, drums (one click: **Tools ▸ FruityLink Apply**) |

FL's controller-script interpreter blocks sockets/`subprocess`, but it **can do file I/O** (`open()`),
so the default transport needs **no virtual MIDI port**: the script polls a request file in `OnIdle`
and writes a response file. The only requirement is that the script is assigned to an **enabled MIDI
device** in FL so it runs (your keyboard still plays — notes pass through). Note *authoring* isn't
possible from the controller script, hence the separate piano-roll script. Writing `.flp` files
directly was rejected as undocumented and fragile.

## Architecture

```
src/
  FruityLink.Core         domain + music theory + ALL interfaces + SysEx protocol codec   (net9.0, no deps)
  FruityLink.FlStudio     file-bridge (FileRpc) + note-spec writer; legacy SysEx transport  (net9.0-windows)
  FruityLink.Knowledge    RAG: HTML/PDF/MD ingest, chunk, embed, SQLite + cosine search    (net9.0)
  FruityLink.Llm          Semantic Kernel factory + OpenAI-compatible embeddings client    (net9.0)
  FruityLink.Agent        SK plugins (the FL "tools") + FlAgent orchestrator + audit        (net9.0)
  FruityLink.Persistence  chats, version tree, settings, DPAPI-encrypted secrets           (net9.0-windows)
  FruityLink.App          WPF UI: DI host, theme, chat panel                                (net9.0-windows)
tools/
  FruityLink.Probe        flprobe — CLI debugger for the file bridge (doctor/ping/call/watch) (net9.0)
fl-bridge/                device_FruityLink.py + FruityLink.pyscript + install notes        (Python, inside FL)
tests/                    xUnit suites (Core, FlStudio, Knowledge, Llm, Persistence)
```

All abstractions live in `Core`; concretes are wired only in `App`'s composition root
(`ServiceConfiguration.cs`). Pure layers (`Core`, `Knowledge`, `Llm`, `Agent`) are OS-agnostic.

## Prerequisites

- **.NET SDK 9** (or 10) with the Windows desktop workload — `dotnet --list-sdks`.
- **Ollama** (default backend; local *or* a cloud-proxied endpoint — same OpenAI-compatible API):
  install it, then pull a **tool-capable** chat model and an embedding model:
  ```
  ollama pull qwen2.5          # or llama3.1 — must support tool/function calling
  ollama pull nomic-embed-text # embeddings for the knowledge base
  ```
  OpenAI, Azure OpenAI, and Anthropic backends are pluggable behind the same interface.
- **FL Studio 2025** with the two helper scripts installed (the app's **Connect FL** panel installs them for you). FruityLink talks to FL through files — **no virtual MIDI port and no driver** — you just assign the FruityLink controller to an enabled MIDI device in FL so the script runs.

## Build & run

```powershell
dotnet build FruityLink.slnx
dotnet test  FruityLink.slnx          # Core/FlStudio/Knowledge/Llm/Persistence suites
dotnet run --project src/FruityLink.App
```

The app opens to a chat. With Ollama running you can immediately ask for music:

> *"Build a I–vi–IV–V progression in D Dorian."*

The agent calls its music-theory tool, stages the notes, and tells you to run **Tools ▸ FruityLink
Apply** in the target pattern's Piano Roll. With FL connected (Connect FL panel), you can also ask
*"what's on my channel rack?"*, *"set the tempo to 128"*, etc.

## FL Studio setup (one time)

Open the app's **Connect FL** panel and click **Install FL scripts** (or do it manually — see
[`fl-bridge/README.md`](fl-bridge/README.md)). Then in FL: **Options ▸ MIDI Settings**, enable any MIDI
input device and set its **Controller type** to **FruityLink**. That's it — the bridge uses files, so
no virtual MIDI port is needed; your keyboard still plays because the script passes notes through.

## Debugging the bridge (flprobe)

`flprobe` is a CLI debugger for the file bridge — run it against a live FL session without the app:

```powershell
dotnet run --project tools/FruityLink.Probe -- doctor          # full diagnosis
dotnet run --project tools/FruityLink.Probe -- ping             # one ping + latency
dotnet run --project tools/FruityLink.Probe -- call project.info
dotnet run --project tools/FruityLink.Probe -- watch 30         # poll status + ping each second
```

`doctor` checks the bridge folder, the script's `status.json`, the installed script, and does a live
ping round-trip — then prints exactly what's wrong if it fails. The controller script also logs
`[FruityLink] …` lines to FL's **View ▸ Script output** (load result, a file-write self-test, and each
request it handles), which together pinpoint any issue.

## Data & settings

Everything is stored under `%APPDATA%\FruityLink`: `settings.json`, `chats\`, `versions\`,
`knowledge.db`, and DPAPI-encrypted `secrets.json` (API keys, encrypted per Windows user).

## Plugin knowledge base (sound design + FX chains)

The agent reads a per-plugin **manual** at runtime (`get_plugin_manual`; discover what's available with
`list_plugin_manuals`) to learn a plugin's parameters and recipes, then drives the knobs via the
plugin-param tools (`native_*_channel_plugin_params` for generators, `native_*_mixer_plugin_params` for
effects). Manuals are markdown embedded in `FruityLink.Agent.dll` (`src/FruityLink.Agent/Manuals/`) —
**add coverage by dropping a `Manuals/<plugin>.md` and rebuilding**, no code changes.

**Generators (sound design) — documented (7):**

| Plugin | Notes |
|---|---|
| 3xOSC | osc-shape control (sine/tri/square/saw/rounded-saw/noise) — live-verified |
| Sytrus | FM / RM + subtractive |
| FLEX | preset-first ROMpler |
| Harmor | additive resynthesis |
| Slicex | beat slicer / loop chopper |
| FPC | drum pads (kit builder) |
| Serum | 3rd-party wavetable VST (param path proven) |

**Effects (FX chains) — documented (7):**

| Plugin | Role |
|---|---|
| Fruity Parametric EQ 2 | 7-band parametric EQ |
| Fruity Compressor | single-band compressor |
| Fruity Limiter | compressor + limiter + gate (master/bus) |
| Fruity Delay 3 | delay / echo |
| Fruity Reverb 2 | reverb |
| Maximus | multiband maximizer / mastering |
| Gross Beat | time + volume FX (stutter, sidechain-gate, tape-stop) |

**Next up (not yet documented — pick from here to extend coverage):**
- *Generators:* Sawer, Harmless, PoiZone, Toxic Biohazard, GMS (Groove Machine Synth), DirectWave, FL Keys, BooBass, Fruit Kick, Slayer; 3rd-party VSTs: Vital, Massive, Kontakt.
- *Effects:* Fruity Reverb (v1), Fruity Chorus, Fruity Flanger, Fruity Phaser, Fruity Filter / Fast LP, Fruity Balance, Fruity Stereo Enhancer, Fruity Soft Clipper, Fruity Multiband Compressor, Fruity Blood Overdrive, Fruity Delay Bank, Fruity Love Philter, Transient Processor, Fruity Fast Dist.

## Roadmap

- [x] Core music-theory engine (12 scales/modes, diatonic progressions) + SysEx protocol codec
- [x] FL control bridge (SysEx RPC) + note-spec hand-off + FL Python scripts
- [x] Semantic Kernel agent with FL tool-plugins + RAG search tool
- [x] Provider-agnostic LLM + configurable embeddings (default = selected chat backend)
- [x] RAG knowledge engine (ingest/chunk/embed/retrieve)
- [x] Persistence: chats, version tree (branch/checkpoint), DPAPI secrets — *backend done*
- [x] WPF chat UI + light/dark black-orange-white theme
- [ ] UI panels for Settings (backend/model/keys), Knowledge sources, and session history/branching
- [ ] First-run setup wizard (install scripts, verify the file bridge with a probe self-test)
- [ ] Live-MIDI "play it in" preview stream

## Tests

```powershell
dotnet test FruityLink.slnx --blame-hang-timeout 60s
```

**115 tests across 5 suites, all passing:** music-theory correctness (e.g. I–vi–IV–V in C major →
correct MIDI notes), SysEx codec round-trips (base64/chunk/reassemble, the 7-bit-safety guarantee),
bridge RPC correlation/timeout, persistence round-trips, RAG ingest/retrieve, and the LLM embedding
client.

> Known tooling quirk: the Knowledge suite's *tests* pass, but its VSTest **testhost lingers on
> shutdown** once the native SQLite library (`e_sqlite3`) is loaded (a VSTest/native-lib teardown
> issue — the hang stack is entirely in `Microsoft.VisualStudio.TestPlatform.TestHost`, not our
> code). The `--blame-hang-timeout` flag makes the run terminate cleanly after the results are
> reported. Running a single suite (e.g. `dotnet test tests/FruityLink.Core.Tests`) is unaffected.
