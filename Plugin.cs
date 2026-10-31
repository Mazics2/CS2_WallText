using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace CS2WallText;

[PluginMetadata(
    Id = "CS2_WallText",
    Version = "1.0.0",
    Name = "CS2 Wall Text",
    Author = "CanDaysa",
    Description = "Per-map point_worldtext placement with precise trace-based aiming, a move/rotate menu, and EN/TR translations.")]
public class WallTextPlugin(ISwiftlyCore core) : BasePlugin(core)
{
    private MapContext? _mapContext;
    private WallTextModule? _wallTextModule;

    public override void Load(bool hotReload)
    {
        _mapContext = new MapContext(Core);
        _wallTextModule = new WallTextModule(Core, _mapContext);

        _mapContext.Load();
        _wallTextModule.Load();

        Core.Logger.LogInformation("CS2_WallText loaded.");
    }

    public override void Unload()
    {
        _wallTextModule?.Unload();
        _mapContext?.Unload();

        Core.Logger.LogInformation("CS2_WallText unloaded.");
    }
}
