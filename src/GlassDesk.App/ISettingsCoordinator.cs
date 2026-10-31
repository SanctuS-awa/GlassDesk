using GlassDesk.Core;
using System.Text.Json;

namespace GlassDesk.App;

public readonly record struct SettingsUpdateResult(bool Success, string? Message = null);

/// <summary>UI-thread contract between the resident coordinator and the settings window.</summary>
public interface ISettingsCoordinator
{
    bool IsUiPreview=>false;
    bool UsesSystemCompositorLens=>false;
    Windows.Graphics.IGeometrySource2D? CreatePreviewGeometry(System.Numerics.Vector2[] points)=>null;
    void PreviewCompanionAppearance(uint color,string appearanceId) { }
    WidgetPreferences Preferences { get; }
    MediaSnapshot Media { get; }
    string Status { get; }
    string? ConnectionIssue { get; }
    bool IsWidgetVisible { get; }
    IReadOnlyList<PluginStatus> Plugins { get; }
    IReadOnlyList<PluginContentPage> PluginPages { get; }
    string AudioStatus { get; }
    string LiquidGlassStatus { get; }
    Task<SettingsUpdateResult> ImportPluginAsync(string packagePath);
    Task<SettingsUpdateResult> RemovePluginAsync(string id);
    Task<SettingsUpdateResult> SetPluginEnabledAsync(string id,bool enabled);
    Task<SettingsUpdateResult> ExecutePluginCommandAsync(string pluginId,string commandId);
    JsonElement? GetPluginConfiguration(string id);
    Task<SettingsUpdateResult> SavePluginConfigurationAsync(string id,JsonElement value);
    IReadOnlyList<PluginSetting> GetPluginSettings(string id);
    event Action? Changed;
    Task<SettingsUpdateResult> UpdatePreferencesAsync(WidgetPreferences preferences);
    void ResetPosition();
    void OpenMusic();
    Task RetryMediaAsync();
    void ToggleVisibility();
    Task<string> ExportDiagnosticsAsync();
    void Quit();
}
