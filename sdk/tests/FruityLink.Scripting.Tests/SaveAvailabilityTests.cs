using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class SaveAvailabilityTests
{
    [Theory]
    [InlineData("save_project", "{\"path\":\"safe.flp\"}", "SaveProjectAsync")]
    [InlineData("save_project_as", "{\"path\":\"safe.flp\"}", "SaveProjectAsAsync")]
    [InlineData("save_copy", "{\"path\":\"safe.flp\"}", "SaveCopyAsync")]
    [InlineData("save_new_version", "{}", "SaveNewVersionAsync")]
    public async Task SaveCannotBypassMissingNoteValidationSymbols(string operation, string arguments, string method)
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Status = new(0, 113, 0, new HashSet<string>()) { Supported = true, Complete = true };
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Contains("project_note_validation", Assert.Single(dispatcher.Catalog.Operations, item => item.Name == operation).Requires);
        foreach (string missing in new[] { "NoteRecorderArrayBase", "ChannelList" })
        {
            recorder.Status = recorder.Status with { Unresolved = new HashSet<string> { missing } };
            Assert.Contains(operation, (await dispatcher.GetCapabilitiesAsync()).UnavailableOperations.Keys);
            var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(operation, RecordingControl.Json(arguments)));
            Assert.Equal("unavailable", error.Code);
            Assert.DoesNotContain(recorder.Calls, call => call.Method == method);
        }
        recorder.Status = recorder.Status with { Unresolved = new HashSet<string>() };
        await dispatcher.InvokeAsync(operation, RecordingControl.Json(arguments));
        Assert.Contains(recorder.Calls, call => call.Method == method);
    }
}
