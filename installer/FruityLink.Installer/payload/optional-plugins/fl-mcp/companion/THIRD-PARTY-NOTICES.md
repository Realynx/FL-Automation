# Third-party software

FL MCP is licensed under PolyForm Noncommercial 1.0.0. Dependencies keep their own licenses; this project's license does not replace them.

Full license and notice texts for bundled runtime dependencies are provided in `licenses/` and copied into the distribution.

- [FruityLink SDK](https://github.com/Realynx/FL-Automation), MIT. The plugin targets `FruityLink.Plugins.Abstractions` and `FruityLink.Scripting` 0.2.0. The distribution includes `FruityLink.Scripting.dll` and the SDK-owned `fruitylink-python` 0.2.0 wheel, with their MIT licenses. Shared Core and plugin contract assemblies come from the user's matching host installation.
- [Model Context Protocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), Apache-2.0. Version 2.2.0.
- [Microsoft .NET extensions](https://github.com/dotnet/runtime), MIT. Used by the stdio companion. See dependency metadata and `packages.lock.json` for the resolved graph.
- xUnit and Microsoft test SDK packages are development dependencies; they are not included in the distribution.

FL Studio, its factory content, third-party audio plugins, and Blender are not bundled. Their owners' terms continue to apply. FL MCP is not affiliated with or endorsed by Image-Line.

The Blender MCP research document links upstream source for architectural comparison. No Blender MCP source, branding assets, telemetry, or integrations are copied into this project.
