using System;
using System.IO;
using SwiftlyS2.Shared;

namespace CS2WallText;

/// <summary>
/// Tracks the sanitized name of the currently loaded map so wall texts can be
/// stored/reloaded per map - leaving one map closes its texts, loading a map that
/// already has saved texts opens them.
/// </summary>
public class MapContext
{
    private readonly ISwiftlyCore _core;

    public string CurrentMap { get; private set; } = "unknown";

    public event Action? MapChanged;

    public MapContext(ISwiftlyCore core)
    {
        _core = core;
    }

    public void Load()
    {
        RefreshFromEngine();
        _core.Event.OnMapLoad += OnMapLoad;
    }

    public void Unload()
    {
        _core.Event.OnMapLoad -= OnMapLoad;
    }

    private void OnMapLoad(SwiftlyS2.Shared.Events.IOnMapLoadEvent @event)
    {
        var sanitized = Sanitize(@event.MapName);
        CurrentMap = sanitized;

        // Always invoke MapChanged on map load (even if reloading the same map),
        // because Source 2 destroys all entities across map transitions.
        MapChanged?.Invoke();
    }

    public void RefreshFromEngine()
    {
        try
        {
            var mapName = Sanitize(_core.Engine.GlobalVars.MapName);
            if (!string.Equals(mapName, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                CurrentMap = mapName;
            }
        }
        catch
        {
            // Engine globals may not be ready this early during plugin load.
        }
    }

    private static string Sanitize(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return "unknown";
        }

        var cleaned = mapName.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalidChar, '_');
        }

        cleaned = cleaned.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}
