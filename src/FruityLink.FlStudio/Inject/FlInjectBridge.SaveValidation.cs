namespace FruityLink.FlStudio.Inject;

public sealed partial class FlInjectBridge
{
    private const int SavePatternSlots = 1000;
    private const int SavePatternStride = 0xC0;
    private const int SaveNoteBatch = 170; // 4080 bytes, below the native 4096-byte read limit.

    // FL's serializer validator visits slots 0..999, including the normally reserved slot 0.
    // Verified FL 25 E609E0 / FL 26 F3D9E0: note channel u16 at +6 must be < channel count.
    // Do not use IsPatternEmpty: named/hidden patterns and unused playlist patterns still serialize.
    private async Task ValidateProjectNotesBeforeSaveAsync(CancellationToken ct)
    {
        int channels = await ReadSaveChannelCountAsync(ct);
        ulong table = await ResolveSymbolAddressAsync("NoteRecorderArrayBase", ct);
        int bytes = (SavePatternSlots - 1) * SavePatternStride + sizeof(ulong);
        byte[] recorders = await PeekAbsAsync(table, bytes, ct);
        for (int pattern = 0; pattern < SavePatternSlots; pattern++)
        {
            ct.ThrowIfCancellationRequested();
            ulong recorder = BitConverter.ToUInt64(recorders, pattern * SavePatternStride);
            if (recorder != 0) await ValidateSaveRecorderAsync(pattern, recorder, channels, ct);
        }
        if (await ReadSaveChannelCountAsync(ct) != channels)
            throw new InvalidOperationException("Cannot save: the channel list changed during note validation. Wait for editing to finish, then save again. No save was started.");
    }

    private async Task<int> ReadSaveChannelCountAsync(CancellationToken ct)
    {
        ulong list = await ChannelListAsync(ct);
        if (list == 0) throw new InvalidOperationException("Cannot save: the channel list is unavailable; note references could not be validated. No save was started.");
        return CheckedSaveChannelCount(await AI32Async(list + 0x10, ct));
    }

    private async Task ValidateSaveRecorderAsync(int pattern, ulong recorder, int channels, CancellationToken ct)
    {
        byte[] header = await PeekAbsAsync(recorder, 0x18, ct);
        int count = BitConverter.ToInt32(header, 0x14);
        ulong data = BitConverter.ToUInt64(header, 8);
        if (count is < 0 or > 1_000_000 || (count > 0 && data == 0))
            throw new InvalidOperationException($"Cannot save: pattern {pattern} has an unreadable note store (count {count}). No save was started; inspect the pattern before retrying.");
        for (int offset = 0; offset < count; offset += SaveNoteBatch)
        {
            int take = Math.Min(SaveNoteBatch, count - offset);
            byte[] notes = await PeekAbsAsync(checked(data + (ulong)offset * NoteStride), take * NoteStride, ct);
            ValidateSavedNoteChannels(notes, pattern, offset, channels);
        }
        byte[] after = await PeekAbsAsync(recorder, 0x18, ct);
        if (BitConverter.ToInt32(after, 0x14) != count || BitConverter.ToUInt64(after, 8) != data)
            throw new InvalidOperationException($"Cannot save: pattern {pattern}'s note store changed during validation. Wait for editing to finish, then save again. No save was started.");
    }

    private static void ValidateSavedNoteChannels(byte[] notes, int pattern, int offset, int channels)
    {
        for (int i = 0; i < notes.Length / NoteStride; i++)
        {
            int channel = BitConverter.ToUInt16(notes, i * NoteStride + 6);
            if (channel >= channels)
                throw new InvalidOperationException($"Cannot save: pattern {pattern}, note {offset + i} (zero-based) references missing channel {channel} (zero-based); the project has {channels} channels. Inspect and explicitly repair or remove that orphan note before saving. No notes or project identity were changed, and no save was started.");
        }
    }

    private static int CheckedSaveChannelCount(int count)
    {
        if (count is < 0 or > 65536)
            throw new InvalidOperationException($"Cannot save: invalid channel count {count}; note references could not be validated. No save was started.");
        return count;
    }
}
