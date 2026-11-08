using MenuApi.API;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace CS2WallText;

[PluginMetadata(
    Id = "CS2_WallText",
    Version = "1.1.0",
    Name = "CS2 Wall Text",
    Author = "surfevo",
    Description = "Per-map point_worldtext placement with precise trace-based aiming, a move/rotate menu, and EN/TR translations.")]
public class WallTextPlugin(ISwiftlyCore core) : BasePlugin(core)
{
    private MapContext? _mapContext;
    private WallTextModule? _wallTextModule;
    private IMenuApi? _menuApi;

    public override void Load(bool hotReload)
    {
        _mapContext = new MapContext(Core);
        _wallTextModule = new WallTextModule(Core, _mapContext)
        {
            MenuApi = _menuApi
        };

        _mapContext.Load();
        _wallTextModule.Load();

        Core.Logger.LogInformation("CS2_WallText loaded.");
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        if (interfaceManager.HasSharedInterface("MenuApi"))
        {
            try
            {
                _menuApi = interfaceManager.GetSharedInterface<IMenuApi>("MenuApi");
                if (_wallTextModule != null)
                {
                    _wallTextModule.MenuApi = _menuApi;
                }
                Core.Logger.LogInformation("[CS2_WallText] SwiftlyS2-MenuApi arayüzü bağlandı.");
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "[CS2_WallText] SwiftlyS2-MenuApi bağlanırken hata oluştu.");
            }
        }
        else
        {
            _menuApi = null;
            if (_wallTextModule != null) _wallTextModule.MenuApi = null;
        }
    }

    public override void Unload()
    {
        _wallTextModule?.Unload();
        _mapContext?.Unload();

        Core.Logger.LogInformation("CS2_WallText unloaded.");
    }
}
