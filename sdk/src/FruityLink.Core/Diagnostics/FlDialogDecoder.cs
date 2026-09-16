using System.Buffers.Binary;
using System.Text;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FruityLink.FlStudio.Tests")]

namespace FruityLink.Core.Diagnostics;

internal sealed record FlDialogLayout(int BodyOffset, int TextOffset, int InnerButtonOffset, int ClickOffset, int ClickSelfOffset, ulong ClickRva);

internal interface IFlDialogMemory
{
    byte[] Read(ulong address, int length);
}

internal static class FlDialogDecoder
{
    internal static FlDialogInspection Read(IFlDialogMemory memory, FlDialogLayout layout, ulong module,
        nint window, ulong form, (nint Window, ulong Object) bodyControl, IReadOnlyList<(nint Window, ulong Object)> buttons)
    {
        RequireControl(memory, form, window, "TMsgForm");
        var body = Pointer(memory, form + (uint)layout.BodyOffset);
        if (body != bodyControl.Object) throw new InvalidDataException("FL message body is not a child of this form.");
        RequireControl(memory, body, bodyControl.Window, "TQuickMemo");
        var text = Unicode(memory, Pointer(memory, body + (uint)layout.TextOffset));
        var choices = new List<FlDialogChoice>();
        foreach (var button in buttons)
        {
            RequireControl(memory, button.Object, button.Window, "TQuickFocusBtn");
            var inner = Pointer(memory, button.Object + (uint)layout.InnerButtonOffset);
            RequireClass(memory, inner, "TQuickBtn");
            if (Pointer(memory, inner + (uint)layout.ClickOffset) != checked(module + layout.ClickRva) ||
                Pointer(memory, inner + (uint)layout.ClickSelfOffset) != form)
                throw new InvalidDataException("FL message button callback does not match the verified form.");
            var result = checked((int)Pointer(memory, inner + 0x18));
            if (result is not (6 or 7)) throw new InvalidDataException("Unsupported FL message choice.");
            choices.Add(new(button.Window, result));
        }
        if (choices.Count != 2 || choices.Select(choice => choice.Result).Distinct().Count() != 2)
            throw new InvalidDataException("FL message form does not contain the verified Yes/No choices.");
        return new(window, text, choices);
    }

    private static void RequireControl(IFlDialogMemory memory, ulong value, nint window, string className)
    {
        RequireClass(memory, value, className);
        if (Pointer(memory, value + 0x45C) != (ulong)window)
            throw new InvalidDataException("FL control HWND changed during inspection.");
    }

    private static void RequireClass(IFlDialogMemory memory, ulong value, string expected)
    {
        var vmt = Pointer(memory, value);
        if (vmt < 0x10000 || Pointer(memory, vmt - 0xC8) != vmt)
            throw new InvalidDataException("Invalid Delphi class identity.");
        var type = Pointer(memory, vmt - 0xA8);
        var header = memory.Read(type, 2);
        if (header[0] != 7 || header[1] != expected.Length ||
            Encoding.ASCII.GetString(memory.Read(type + 2, header[1])) != expected)
            throw new InvalidDataException("Unexpected FL message control class.");
    }

    private static string Unicode(IFlDialogMemory memory, ulong text)
    {
        if (text < 0x10000) throw new InvalidDataException("FL message text is unavailable.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(memory.Read(text - 4, 4));
        if (length is < 1 or > 8192) throw new InvalidDataException("FL message text length is outside the inspection bound.");
        var raw = memory.Read(text, checked((length + 1) * 2));
        if (raw[^2] != 0 || raw[^1] != 0) throw new InvalidDataException("FL message text is not terminated.");
        return new UnicodeEncoding(false, false, true).GetString(raw, 0, length * 2);
    }

    private static ulong Pointer(IFlDialogMemory memory, ulong address) =>
        BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(address, 8));
}
