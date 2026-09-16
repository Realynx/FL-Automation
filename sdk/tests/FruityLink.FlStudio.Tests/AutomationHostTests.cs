using FruityLink.Host;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class AutomationHostTests
{
    [Fact]
    public void DisabledModeLeavesInteractiveStartupUntouched()
    {
        var values = new Dictionary<string, string?>();

        Assert.False(AutomationHost.TryReadConfiguration(values.GetValueOrDefault,
            out var configuration, out var error));
        Assert.Null(configuration);
        Assert.Null(error);
    }

    [Fact]
    public void EnabledModeRequiresDiscoveryDirectory()
    {
        var values = new Dictionary<string, string?> { [AutomationHost.EnabledVariable] = "1" };

        Assert.False(AutomationHost.TryReadConfiguration(values.GetValueOrDefault,
            out var configuration, out var error));
        Assert.Null(configuration);
        Assert.Contains(AutomationHost.DiscoveryVariable, error);
    }

    [Fact]
    public void EnabledModeRejectsRelativeDiscoveryDirectory()
    {
        var values = new Dictionary<string, string?>
        {
            [AutomationHost.EnabledVariable] = "1",
            [AutomationHost.DiscoveryVariable] = "relative-job"
        };

        Assert.False(AutomationHost.TryReadConfiguration(values.GetValueOrDefault,
            out _, out var error));
        Assert.Contains("absolute", error);
    }

    [Fact]
    public void EnabledModeNormalizesAbsoluteDiscoveryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fruitylink-job", "..", "job");
        var values = new Dictionary<string, string?>
        {
            [AutomationHost.EnabledVariable] = "1",
            [AutomationHost.DiscoveryVariable] = directory
        };

        Assert.True(AutomationHost.TryReadConfiguration(values.GetValueOrDefault,
            out var configuration, out var error));
        Assert.Equal(Path.GetFullPath(directory), configuration!.DiscoveryDirectory);
        Assert.Null(error);
    }
}
