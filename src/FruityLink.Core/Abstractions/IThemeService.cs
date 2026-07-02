using FruityLink.Core.Configuration;

namespace FruityLink.Core.Abstractions;

/// <summary>Applies and tracks the active UI theme (light/dark).</summary>
public interface IThemeService
{
    /// <summary>The currently applied theme.</summary>
    AppTheme Current { get; }

    /// <summary>Applies a theme by swapping the active resource dictionary.</summary>
    void Apply(AppTheme theme);

    /// <summary>Raised after the theme changes.</summary>
    event EventHandler? ThemeChanged;
}
