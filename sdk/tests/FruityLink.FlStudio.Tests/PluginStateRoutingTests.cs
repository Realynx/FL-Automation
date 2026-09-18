using System.IO;
using System.Text;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

/// <summary>Route selection and the <c>.fst</c> identity guard for plugin-state loads (FL 26.1.3.5570,
/// 2026-09-17): FL's own generators ignore the wrapper dispatcher for <c>.fst</c> presets and store their name
/// as a single-byte string, wrapped plugins do neither.</summary>
public sealed class PluginStateRoutingTests
{
    // Header of a real native-generator preset (Sytrus/Arp/"3o3ish 2.fst"): "FLhd" chunk, then the "FLdt" chunk
    // whose version tag (event 0xC7 "5.3.1") is followed by event 0xC9 = the plugin name as a length-prefixed,
    // NUL-terminated SINGLE-BYTE string. Nothing in the file carries the name as UTF-16.
    private static byte[] NativeGeneratorPreset(string plugin)
    {
        var data = new List<byte>();
        data.AddRange("FLhd"u8.ToArray());
        data.AddRange(new byte[] { 6, 0, 0, 0, 0x30, 0, 5, 0, 0x60, 0 });
        data.AddRange("FLdt"u8.ToArray());
        data.AddRange(new byte[] { 0x6C, 6, 0, 0 });
        data.Add(0xC7);
        data.AddRange("5.3.1\0"u8.ToArray());
        data.Add(0xC9);
        data.Add((byte)(plugin.Length + 1));
        data.AddRange(Encoding.UTF8.GetBytes(plugin));
        data.Add(0);
        data.AddRange(new byte[] { 0xD4, 0x34, 0, 0, 0, 0, 0, 0 });
        return data.ToArray();
    }

    /// <summary>A wrapped VST/VST3 preset: FL's wrapper record names the plugin as UTF-16.</summary>
    private static byte[] WrappedPluginPreset(string plugin)
    {
        var data = new List<byte>();
        data.AddRange("FLhd"u8.ToArray());
        data.AddRange(new byte[] { 6, 0, 0, 0, 0x30, 0, 5, 0, 0x60, 0 });
        data.AddRange("FLdt"u8.ToArray());
        data.AddRange(Encoding.Unicode.GetBytes(plugin));
        data.AddRange(new byte[] { 0, 0, 1, 2, 3 });
        return data.ToArray();
    }

    private static string WritePreset(byte[] data, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fruitylink-preset-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Theory]
    [InlineData("Sytrus")]
    [InlineData("Harmor")]
    [InlineData("GMS")]
    public void NativeGeneratorFstIsRoutedThroughTheChannelLoader(string plugin)
    {
        Assert.True(FlInjectBridge.RequiresChannelLoader($@"C:\presets\Sync Lead.fst", plugin));
        Assert.True(FlInjectBridge.RequiresChannelLoader($@"C:\presets\Sync Lead.FST", plugin));
    }

    [Fact]
    public void WrappedPluginFstKeepsTheDispatcherRoute()
    {
        Assert.False(FlInjectBridge.RequiresChannelLoader(@"C:\presets\Lead.fst", "Fruity Wrapper"));
        Assert.False(FlInjectBridge.RequiresChannelLoader(@"C:\presets\Lead.fst", "fruity wrapper"));
    }

    /// <summary>An unreadable holder name must not flip the route: the dispatcher is the documented default and the
    /// channel loader mutes/renames the channel, so an unexpected layout can only keep behaviour, never change it.</summary>
    [Fact]
    public void UnknownHolderNameKeepsTheDispatcherRoute()
        => Assert.False(FlInjectBridge.RequiresChannelLoader(@"C:\presets\Lead.fst", ""));

    [Theory]
    [InlineData(@"C:\presets\Lead.vstpreset", "Fruity Wrapper")]
    [InlineData(@"C:\presets\Pad.gmsynth", "GMS")]
    [InlineData(@"C:\presets\Pad.SerumPreset", "Fruity Wrapper")]
    public void OnlyFstFilesEverUseTheChannelLoader(string path, string plugin)
        => Assert.False(FlInjectBridge.RequiresChannelLoader(path, plugin));

    /// <summary>The regression this guard had until 2026-09-17: every native FL generator preset was refused
    /// ("does not name the hosted plugin 'Sytrus'") because only the UTF-16 spelling was searched.</summary>
    [Fact]
    public void NativeGeneratorPresetNamingThePluginAsASingleByteStringIsAccepted()
    {
        string path = WritePreset(NativeGeneratorPreset("Sytrus"), ".fst");
        try
        {
            Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.Unicode.GetBytes("Sytrus")) < 0,
                "the fixture must carry the plugin name ONLY as a single-byte string, like FL's own presets");
            FlInjectBridge.EnsureStateFileTargetsPlugin(path, "Sytrus");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WrappedPluginPresetNamingThePluginAsUtf16IsAccepted()
    {
        string path = WritePreset(WrappedPluginPreset("Serum 2"), ".fst");
        try { FlInjectBridge.EnsureStateFileTargetsPlugin(path, "Serum 2"); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PresetForAnotherPluginIsStillRefused()
    {
        string path = WritePreset(NativeGeneratorPreset("Harmor"), ".fst");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => FlInjectBridge.EnsureStateFileTargetsPlugin(path, "Sytrus"));
            Assert.Contains("does not name the hosted plugin 'Sytrus'", error.Message);
            Assert.Contains(Path.GetFileName(path), error.Message);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Non-.fst formats are validated by the wrapper itself (class id / fxID) and an unknown plugin name
    /// disables the guard entirely, so neither may be refused here.</summary>
    [Fact]
    public void OtherFormatsAndUnknownPluginNamesBypassTheGuard()
    {
        string preset = WritePreset(new byte[] { 1, 2, 3, 4 }, ".vstpreset");
        string fst = WritePreset(NativeGeneratorPreset("Harmor"), ".fst");
        try
        {
            FlInjectBridge.EnsureStateFileTargetsPlugin(preset, "Serum 2");
            FlInjectBridge.EnsureStateFileTargetsPlugin(fst, "");
        }
        finally { File.Delete(preset); File.Delete(fst); }
    }

    /// <summary>What the channel loader did live to a Sytrus channel: muted it and renamed it to the preset's base
    /// name, mixer route untouched.</summary>
    [Fact]
    public void RestoreLineNamesTheMuteAndRenameTheChannelLoaderCaused()
    {
        string line = FlInjectBridge.DescribeRestoredChannelState("Sytrus", "Sync Lead", false, true, 3, 3);
        Assert.Equal("restored name 'Sync Lead' -> 'Sytrus', muted -> unmuted", line);
    }

    [Fact]
    public void RestoreLineReportsAMovedMixerRoute()
    {
        string line = FlInjectBridge.DescribeRestoredChannelState("Harmor", "Rhodes", true, false, 5, 0);
        Assert.Equal("restored name 'Rhodes' -> 'Harmor', unmuted -> muted, mixer route 0 -> 5", line);
    }

    /// <summary>A verification line may never imply a restore that did not happen.</summary>
    [Fact]
    public void RestoreLineSaysSoWhenTheLoaderLeftTheChannelAlone()
    {
        string line = FlInjectBridge.DescribeRestoredChannelState("Sytrus", "Sytrus", true, true, 3, 3);
        Assert.Equal("name, mute and mixer route survived the load, nothing to restore", line);
        Assert.DoesNotContain("restored", line);
    }
}
