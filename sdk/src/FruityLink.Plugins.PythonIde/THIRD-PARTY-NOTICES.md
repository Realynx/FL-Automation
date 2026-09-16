# FL Python IDE dependencies

The IDE source is MIT licensed; see `LICENSE`. The Windows package includes the following libraries, also used by the framework's shared UI host. Original license texts and notices are in `licenses/` and must accompany redistribution.

| Component | Version | License / included text | Upstream |
| --- | --- | --- | --- |
| Avalonia, Desktop, Fluent theme, platform libraries | 11.3.18 | MIT, `Avalonia-11.3.18-LICENSE.txt` | [Avalonia](https://github.com/AvaloniaUI/Avalonia/tree/11.3.18) |
| Avalonia.AvaloniaEdit | 11.3.0 | MIT, `AvaloniaEdit-11.3.0-LICENSE.txt` | [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit/tree/11.3.0) |
| SkiaSharp and Windows native assets | 2.88.9 | MIT plus bundled third-party notices, `SkiaSharp-2.88.9-*` | [SkiaSharp](https://github.com/mono/SkiaSharp/tree/v2.88.9) |
| HarfBuzzSharp and Windows native assets | 8.3.1.1 | MIT plus bundled third-party notices, `HarfBuzzSharp-8.3.1.1-*` | [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) |
| MicroCom.Runtime | 0.11.0 | MIT, `MicroCom-LICENSE.txt` | [MicroCom](https://github.com/kekekeks/MicroCom) |
| Inter font, via Avalonia.Fonts.Inter | 11.3.18 package | SIL Open Font License 1.1, `Inter-OFL.txt` | [Inter](https://github.com/rsms/inter) |
| Tmds.DBus.Protocol (Avalonia desktop dependency) | 0.21.3 | MIT, `Tmds.DBus-0.21.3-LICENSE.txt` | [Tmds.DBus](https://github.com/tmds/Tmds.DBus/tree/8cb04f66c330b64244e996ff68f685f27f7381a0) |

SkiaSharp and HarfBuzzSharp notice files are copied verbatim from their pinned Windows native NuGet packages. Avalonia/AvaloniaEdit license texts come from the matching source tags; the Inter license comes from the official v4.0 tag and MicroCom's license from commit `4b8a38f773c109bad558ee3713d9f16d80776e42`.

CPython and the FruityLink Python library are provided separately by the framework installer. This plugin does not bundle another interpreter. Their license notices accompany the framework Python runtime.
