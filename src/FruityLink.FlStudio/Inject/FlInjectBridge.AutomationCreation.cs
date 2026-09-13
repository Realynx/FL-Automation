using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

public sealed partial class FlInjectBridge
{
    /// <inheritdoc />
    public async Task<FlAutomationClipResult> CreateAutomationClipAsync(FlAutomationTarget target, int track,
        int startTick, int lengthTick, string? name = null, CancellationToken ct = default)
    {
        ValidateAutomationPlacement(track, startTick, lengthTick);
        ArgumentNullException.ThrowIfNull(target);
        if (name is not null && (name.Length > 1024 || name.Contains('\0')))
            throw new ArgumentException("Name must be at most 1024 characters without NUL.", nameof(name));
        await RequireAutomationAsync(ct);
        uint targetId = await ResolveAutomationTargetAsync(target, ct);
        int ppq = await GetPpqAsync(ct);
        if (ppq <= 0) throw new InvalidOperationException("Project PPQ is unavailable.");
        if (await ClipCollObjAsync(ct) == 0) throw new InvalidOperationException("Playlist clip collection is unavailable.");
        var created = await AutomationCommandAsync($"automation_create {targetId}", ct);
        try { return await CompleteAutomationCreationAsync(created, track, startTick, lengthTick, ppq, name, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Automation channel {created.Channel} was created, but setup did not complete. Inspect that channel before retrying. {error.Message}", error);
        }
    }

    private async Task<FlAutomationClipResult> CompleteAutomationCreationAsync(AutomationSnapshot created,
        int track, int start, int length, int ppq, string? name, CancellationToken ct)
    {
        if (created.Points.Count != 2) throw new InvalidOperationException("Unexpected initial automation envelope.");
        double value = created.Points[0].Value;
        await SetAutomationPointsAsync(created.Channel, new[] {
            new FlAutomationPointSpec(0, value), new FlAutomationPointSpec((double)length / ppq, value)
        }, ct);
        if (name is not null) await SetChannelNameAsync(created.Channel, name, ct);
        int clip = await AddAutomationClipAsync(created.Channel, track, start, length, ct);
        return new(created.Channel, clip);
    }

    /// <inheritdoc />
    public async Task<int> AddAutomationClipAsync(int channel, int track, int startTick, int lengthTick, CancellationToken ct = default)
    {
        ValidateAutomationPlacement(track, startTick, lengthTick);
        var state = await ReadAutomationAsync(channel, ct);
        // FL's own creator uses the source channel's persistent event ID, not its display-list index.
        uint source = checked(state.SourceEventId + 0x5000u);
        int index = await InsertClipRawAsync(startTick, source, lengthTick, track, -1, -1, ct);
        await ResizeClipAsync(index, lengthTick, ct);
        ulong address = await ClipAddrAsync(index, ct);
        byte[] actual = await PeekAbsAsync(address, 0x10, ct);
        if (BitConverter.ToInt32(actual, 0) != startTick || BitConverter.ToUInt32(actual, 4) != source ||
            BitConverter.ToInt32(actual, 8) != lengthTick || BitConverter.ToInt16(actual, 0xc) != 500 - track)
            throw new InvalidOperationException("Automation clip insertion changed the playlist but its readback did not match. Inspect the playlist before retrying.");
        return index;
    }

    private static void ValidateAutomationPlacement(int track, int start, int length)
    {
        if (track is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(track), "Playlist tracks are 1..500.");
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if ((long)start + length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), "Clip end exceeds the tick range.");
    }

    private async Task<uint> ResolveAutomationTargetAsync(FlAutomationTarget target, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(target.Index);
        if (target.Kind == "plugin_parameter") return await PluginAutomationTargetAsync(target, ct);
        if (target.Slot != -1 || target.Parameter != -1) throw new ArgumentException("Only plugin parameters accept Slot and Parameter.", nameof(target));
        return target.Kind switch {
            "channel_volume" => await ChannelAutomationTargetAsync(target.Index, ChanVol, ct),
            "channel_pan" => await ChannelAutomationTargetAsync(target.Index, ChanPan, ct),
            "channel_pitch" => await ChannelAutomationTargetAsync(target.Index, ChanPitch, ct),
            "mixer_volume" => await MixerAutomationTargetAsync(target.Index, MixerVolOffset, ct),
            "mixer_pan" => await MixerAutomationTargetAsync(target.Index, MixerPanOffset, ct),
            _ => throw new ArgumentException("Unknown automation target kind.", nameof(target))
        };
    }

    private async Task<uint> ChannelAutomationTargetAsync(int channel, uint offset, CancellationToken ct)
    {
        ulong address = await ChannelObjAsync(channel, ct);
        if (address == 0) throw new InvalidOperationException("Target channel is unavailable.");
        uint eventId = unchecked((uint)await AI32Async(address + 0x9c, ct));
        if (eventId >= 0x10000000 || (eventId & 0xffff) != 0)
            throw new InvalidOperationException("Target channel has an invalid persistent event ID.");
        return checked(eventId + offset);
    }

    private async Task<uint> MixerAutomationTargetAsync(int track, uint offset, CancellationToken ct)
    {
        await MixerLayoutAsync(ct);
        await ValidateAddressableMixerTrackAsync(track, ct);
        return MixerTrackParamId(track, offset);
    }

    private async Task<uint> PluginAutomationTargetAsync(FlAutomationTarget target, CancellationToken ct)
    {
        if (target.Slot is < -1 or > 9 || target.Parameter < 0) throw new ArgumentOutOfRangeException(nameof(target));
        var (instance, count, command) = await ResolvePluginAsync(target.Index, target.Slot, ct);
        if (instance == 0) throw new InvalidOperationException(PluginParametersUnavailableMessage(target.Index, target.Slot));
        if (target.Parameter >= count) throw new ArgumentOutOfRangeException(nameof(target), "Plugin parameter is out of range.");
        return checked(command + (uint)target.Parameter);
    }
}
