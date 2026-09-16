using System.Text;
using FruityLink.Core.Diagnostics;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class FlDialogInspectorTests
{
    [Theory]
    [InlineData("25.2.5.5319")]
    [InlineData("26.1.3.5570")]
    public void VerifiedProfilesReadUnicodeBodyAndSemanticButtonResults(string version)
    {
        var fixture = new Fixture(version);
        var result = fixture.Read();
        Assert.Equal("A real Unicode message: café 🎹", result.Body);
        Assert.Equal(new[] { 6, 7 }, result.Choices.Select(choice => choice.Result));
        Assert.Equal(new nint[] { 202, 203 }, result.Choices.Select(choice => choice.Window));
    }

    [Theory]
    [InlineData("26.1.0.5530")]
    [InlineData("26.1.4.5571")]
    [InlineData("24.2.0.0000")]
    [InlineData(null)]
    public void UnverifiedBuildDoesNotInheritDialogOffsets(string? version) => Assert.Null(FlDialogInspector.Layout(version));

    [Theory]
    [InlineData("form-hwnd")]
    [InlineData("body-owner")]
    [InlineData("body-hwnd")]
    [InlineData("wrapper-hwnd")]
    [InlineData("callback-code")]
    [InlineData("callback-self")]
    [InlineData("duplicate-result")]
    [InlineData("unexpected-result")]
    [InlineData("class-self")]
    [InlineData("class-kind")]
    [InlineData("text-length")]
    [InlineData("text-terminator")]
    public void ChangedOrMalformedControlsCannotBeVerified(string corruption)
    {
        var fixture = new Fixture("26.1.3.5570");
        fixture.Corrupt(corruption);
        Assert.ThrowsAny<Exception>(() => fixture.Read());
    }

    private sealed class Fixture : IFlDialogMemory
    {
        private const ulong Form = 0x100000, Body = 0x200000, Yes = 0x300000, No = 0x400000;
        private const ulong YesInner = 0x500000, NoInner = 0x600000, Text = 0x700000, Module = 0x10000000;
        private readonly FlDialogLayout layout;
        private readonly Dictionary<ulong, byte> memory = [];
        private readonly Dictionary<ulong, ulong> classes = [];
        private readonly string text = "A real Unicode message: café 🎹";

        public Fixture(string version)
        {
            layout = FlDialogInspector.Layout(version)!;
            Control(Form, 201, "TMsgForm");
            Control(Body, 204, "TQuickMemo");
            Control(Yes, 202, "TQuickFocusBtn");
            Control(No, 203, "TQuickFocusBtn");
            Control(YesInner, 0, "TQuickBtn");
            Control(NoInner, 0, "TQuickBtn");
            U64(Form + (uint)layout.BodyOffset, Body);
            U64(Body + (uint)layout.TextOffset, Text);
            Write(Text - 4, BitConverter.GetBytes(text.Length));
            Write(Text, Encoding.Unicode.GetBytes(text + "\0"));
            Button(Yes, YesInner, 6);
            Button(No, NoInner, 7);
        }

        public FlDialogInspection Read() => FlDialogDecoder.Read(this, layout, Module, 201, Form,
            (204, Body), [(202, Yes), (203, No)]);

        private void Control(ulong obj, ulong hwnd, string name)
        {
            var vmt = obj + 0x10000;
            var type = obj + 0x20000;
            classes[obj] = vmt;
            U64(obj, vmt);
            U64(vmt - 0xC8, vmt);
            U64(vmt - 0xA8, type);
            Write(type, [7, (byte)name.Length]);
            Write(type + 2, Encoding.ASCII.GetBytes(name));
            U64(obj + 0x45C, hwnd);
        }

        private void Button(ulong wrapper, ulong inner, ulong result)
        {
            U64(wrapper + (uint)layout.InnerButtonOffset, inner);
            U64(inner + (uint)layout.ClickOffset, Module + layout.ClickRva);
            U64(inner + (uint)layout.ClickSelfOffset, Form);
            U64(inner + 0x18, result);
        }

        public void Corrupt(string value)
        {
            switch (value)
            {
                case "form-hwnd": U64(Form + 0x45C, 999); break;
                case "body-owner": U64(Form + (uint)layout.BodyOffset, Yes); break;
                case "body-hwnd": U64(Body + 0x45C, 999); break;
                case "wrapper-hwnd": U64(Yes + 0x45C, 999); break;
                case "callback-code": U64(YesInner + (uint)layout.ClickOffset, Module + 123); break;
                case "callback-self": U64(YesInner + (uint)layout.ClickSelfOffset, Body); break;
                case "duplicate-result": U64(NoInner + 0x18, 6); break;
                case "unexpected-result": U64(YesInner + 0x18, 1); break;
                case "class-self": U64(classes[Form] - 0xC8, 0); break;
                case "class-kind": Write(Form + 0x20000, [0]); break;
                case "text-length": Write(Text - 4, BitConverter.GetBytes(int.MaxValue)); break;
                case "text-terminator": Write(Text + (ulong)text.Length * 2, [1, 1]); break;
            }
        }

        private void U64(ulong address, ulong value) => Write(address, BitConverter.GetBytes(value));
        private void Write(ulong address, byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++) memory[address + (ulong)i] = bytes[i];
        }

        public byte[] Read(ulong address, int length)
        {
            var result = new byte[length];
            for (var i = 0; i < length; i++) result[i] = memory.TryGetValue(address + (ulong)i, out var value)
                ? value : throw new InvalidDataException("Unmapped synthetic memory.");
            return result;
        }
    }
}
