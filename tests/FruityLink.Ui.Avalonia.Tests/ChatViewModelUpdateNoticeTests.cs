using System.ComponentModel;
using FruityLink.Ui.Avalonia.ViewModels;
using Shouldly;
using Xunit;

namespace FruityLink.Ui.Avalonia.Tests;

/// <summary>
/// The dismissible "update available" banner is plain view-model state (no updater/gateway
/// reference — the host presenter drives it via <see cref="ChatViewModel.ShowUpdateNotice"/>),
/// so these run without an Avalonia dispatcher like the other view-model tests.
/// </summary>
public sealed class ChatViewModelUpdateNoticeTests
{
    [Fact]
    public void Show_sets_the_text_and_visibility_and_notifies()
    {
        var vm = new ChatViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.UpdateNoticeText.ShouldBeNull();       // hidden until the host reports an update
        vm.HasUpdateNotice.ShouldBeFalse();

        vm.ShowUpdateNotice("1.4.0", "https://fl-automate.test/download");

        vm.UpdateNoticeText.ShouldBe("Update 1.4.0 is available");
        vm.HasUpdateNotice.ShouldBeTrue();
        changed.ShouldContain(nameof(ChatViewModel.UpdateNoticeText));
        changed.ShouldContain(nameof(ChatViewModel.HasUpdateNotice));
    }

    [Fact]
    public void Dismiss_hides_the_banner_and_wins_for_the_rest_of_the_session()
    {
        var vm = new ChatViewModel();
        vm.ShowUpdateNotice("1.4.0", "https://fl-automate.test/download");

        vm.DismissUpdateCommand.Execute(null);

        vm.UpdateNoticeText.ShouldBeNull();
        vm.HasUpdateNotice.ShouldBeFalse();

        // A later notice (e.g. the presenter re-checks) must NOT resurrect the banner.
        vm.ShowUpdateNotice("1.4.1", "https://fl-automate.test/download");

        vm.UpdateNoticeText.ShouldBeNull();
        vm.HasUpdateNotice.ShouldBeFalse();
    }

    [Fact]
    public void Show_called_twice_before_dismissal_just_updates_the_text()
    {
        var vm = new ChatViewModel();

        vm.ShowUpdateNotice("1.4.0", "https://fl-automate.test/download/1.4.0");
        vm.ShowUpdateNotice("1.5.0", "https://fl-automate.test/download/1.5.0");

        vm.UpdateNoticeText.ShouldBe("Update 1.5.0 is available");
        vm.HasUpdateNotice.ShouldBeTrue();
    }
}
