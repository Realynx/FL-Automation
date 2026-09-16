# What you can do

FruityLink gives C# and Python access to the same project operations. Availability
depends on the loaded FL engine, bridge, and hosted plugin. Start with
[installation and compatibility](installation.md#fl-studio-compatibility) when
choosing a build.

| Area | Available workflows | Details |
| --- | --- | --- |
| Transport and project | Read/set tempo, play/stop, seek, loop ranges, open/save projects | [C# API](fl-control-api.md) |
| Composition | Create channels and patterns; add, query, edit, and remove notes | [C# recipes](csharp/recipes.md), [Python SDK](python/index.md) |
| Arrangement | Place/edit playlist clips, manage tracks, markers, and arrangements | [C# API](fl-control-api.md) |
| Mixing | Query tracks, insert tracks, route channels, change levels, sends, and effects | [C# recipes](csharp/recipes.md) |
| Plugin parameters | Enumerate and set supported hosted generator or effect parameters | [Parameter limits](fl-control-api.md#the-surface-by-capability) |
| Automation | Create a linked automation channel and clip, place it again, read/replace its envelope | [Automation clips](automation-clips.md) |
| Plugin integration | Discovery, enable/disable, shadow copies, hot reload, menus, and toolbar contributions | [C# SDK](csharp/index.md) |
| Windows | Host plugin windows in FL chrome with Avalonia/WPF helpers and external-window fallback | [Window hosting](window-embedding.md) |
| Python | Embedded scripts and an authenticated endpoint for external programs | [Python SDK](python/index.md) |
| Audio measurements | Analyze supplied WAV files or PCM for levels, loudness estimates, and spectral summaries | [Audio analysis](python-audio-analysis.md), [Spectral analysis](python-spectral-analysis.md) |

## Important boundaries

**Sampler controls and plugin parameters are different.** The built-in Sampler does
not expose its sample settings or envelopes through the hosted-plugin parameter
interface. You can still load a sample and use supported channel volume, pan, mute,
pitch, and mixer-routing controls. Hosted generators and VSTs with a parameter
interface support enumeration and normalized parameter writes.

**Audio analysis works on supplied audio.** The Python utilities can run offline on
WAV files or PCM; they do not capture an isolated live channel. Managed WAV render
orchestration is a [FLMCP workflow](https://github.com/Realynx/Fl-MCP/tree/master/docs).
The C# control surface also exposes FL's export dialog for the user to complete.

**Automation creation links one initial destination.** Adding another playlist clip
for that automation channel reuses its curve and link. Linking additional destinations
to an existing automation channel is not exposed. Envelope and placement changes are
separate steps and have no automatic rollback.

**Native windows have a validation boundary.** Window creation has binary/fixture
coverage and some recorded live testing, but title-bar/drag stability and broader
DPI/theme behavior still need confirmation. Preserve the external-window fallback.

**A recognized engine is not a blanket guarantee.** Required symbols and object
layouts must be available for each operation. FL 25.2.5.5319 has binary/fixture
evidence for this feature set; FL 26.1.3.5570 also has recorded live workflows.
Do not generalize those results to every operation or another patch.

## Find the API you need

- Use [C# SDK](csharp/index.md) for plugin development and typed control methods.
- Use [Python SDK](python/index.md) for scripts, collections, and generated operations.
- Use [FLMCP docs](https://github.com/Realynx/Fl-MCP/tree/master/docs) for MCP tool discovery and client setup.
- Read [API gaps](api-gaps.md) and [Roadmap](roadmap.md) for engineering context; planned work is not an available capability.
