namespace FruityLink.Plugins.PythonIde.Ui;

/// <summary>Owns the editor window on the shared Avalonia thread.</summary>
public interface IPythonIdeWindowHost : IAsyncDisposable
{
    /// <summary>Gets visibility without a blocking native bridge request.</summary>
    bool IsVisible { get; }

    /// <summary>Creates or shows the editor.</summary>
    Task ShowAsync(CancellationToken ct = default);

    /// <summary>Shows or hides the editor.</summary>
    Task ToggleAsync(CancellationToken ct = default);
}
