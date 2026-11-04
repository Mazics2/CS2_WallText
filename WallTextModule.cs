using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Trace;

namespace CS2WallText;

/// <summary>
/// Main module for CS2_WallText:
/// - Precise trace-based aiming with auto-upright wall snapping.
/// - Rainbow (RGB) cycle effects.
/// - Rotating Text Announcer (cycles multiple messages on a wall with real-time placeholder support).
/// - Interactable Text Buttons (+USE / Shoot triggers with sound, cooldown, and commands).
/// - Instant Crosshair positioning, wall-relative nudging, and quick orientation tools.
/// </summary>
public class WallTextModule
{
    private const string MenuTag = "CS2WallText.Menu";
    private const float CaptureDebounceSeconds = 0.25f;
    private const float WallOffset = 1.0f;

    private static readonly float[] AvailableSteps = [1f, 5f, 15f, 45f, 90f];
    private static readonly string[] AvailableFonts = ["", "Arial", "Impact", "Trebuchet MS", "Verdana", "Consolas", "Tahoma"];

    private readonly ISwiftlyCore _core;
    private readonly MapContext _mapContext;

    private WallTextConfig _config = new();
    private string _configPath = "";
    private bool _isRegistered;
    private Guid _commandGuid;
    private Guid _chatHookGuid;
    private Guid _roundStartHookGuid;

    private readonly Dictionary<string, WallTextData> _store = new(StringComparer.OrdinalIgnoreCase);
    // Stored as entity handles (index + serial) so a stale/reused entity index is never mistaken for our text
    private readonly Dictionary<string, CHandle<CPointWorldText>> _liveEntities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dynamicTextRenderCache = new(StringComparer.OrdinalIgnoreCase);

    private enum ChatPromptKind
    {
        NewText,
        ChangeText,
        Rename,
        ColorHex,
        AddRotatingMessage,
        InteractMessage,
        InteractCommand
    }

    private sealed class PendingChatPrompt
    {
        public ulong SteamId;
        public ChatPromptKind Kind;
        public string TargetName = "";
        public Vector Position;
        public QAngle Angles;
    }

    // Unified pending chat prompt per player slot (prevents multi-prompt collisions and cleans up on disconnect)
    private readonly Dictionary<int, PendingChatPrompt> _pendingChatBySlot = new();

    // Player menu preferences (keyed by slot for reliable disconnect cleanup)
    private readonly Dictionary<int, float> _playerStepSizes = new();
    private readonly Dictionary<int, Dictionary<string, int>> _menuCursorBySlot = new(); // remembers highlighted option per menu
    private readonly Dictionary<int, string> _activeMenuKeyBySlot = new();

    // Rotating announcer runtime state: name -> (index, lastSwitchTime)
    private readonly Dictionary<string, (int Index, float LastSwitchTime)> _rotatingState = new(StringComparer.OrdinalIgnoreCase);

    // Player buttons and interaction cooldowns (keyed by slot)
    private readonly Dictionary<int, GameButtonFlags> _lastPlayerButtons = new();
    private readonly Dictionary<int, float> _playerInteractionCooldowns = new();

    private float _lastDynamicUpdateTime;
    private float _lastRainbowUpdateTime;

    private sealed class CaptureSession
    {
        public bool LastMouse1Down;
        public float LastCaptureTime;
        public Action<IPlayer, Vector, Vector>? OnCaptured;
    }

    private readonly Dictionary<int, CaptureSession> _captureSessions = new();

    public WallTextModule(ISwiftlyCore core, MapContext mapContext)
    {
        _core = core;
        _mapContext = mapContext;
    }

    public void Load()
    {
        LoadConfig();
        if (!_config.Enabled)
        {
            return;
        }

        _commandGuid = _core.Command.RegisterCommand("text", OnCommand, registerRaw: true);
        _chatHookGuid = _core.Command.HookClientChat(OnClientChat);
        _roundStartHookGuid = _core.GameEvent.HookPost<EventRoundStart>(OnRoundStart);

        _core.Event.OnTick += OnTick;
        _core.Event.OnClientDisconnected += OnClientDisconnected;
        _mapContext.MapChanged += OnMapChanged;
        _isRegistered = true;

        _core.Scheduler.NextTick(() =>
        {
            _mapContext.RefreshFromEngine();
            if (!IsUnknownMap(_mapContext.CurrentMap))
            {
                ReloadForCurrentMap();
            }
        });
    }

    public void Unload()
    {
        if (_isRegistered)
        {
            _core.Command.UnregisterCommand(_commandGuid);
            _core.Command.UnhookClientChat(_chatHookGuid);
            _core.GameEvent.Unhook(_roundStartHookGuid);

            _core.Event.OnTick -= OnTick;
            _core.Event.OnClientDisconnected -= OnClientDisconnected;
            _mapContext.MapChanged -= OnMapChanged;
            _isRegistered = false;
        }

        CloseAllMenus();
        ClearAllEntities();
        _pendingChatBySlot.Clear();
        _captureSessions.Clear();
        _lastPlayerButtons.Clear();
        _playerInteractionCooldowns.Clear();
        _playerStepSizes.Clear();
        _menuCursorBySlot.Clear();
        _activeMenuKeyBySlot.Clear();
    }

    private void OnMapChanged()
    {
        // All entities from the previous map are already gone - just forget the handles
        // (never try to Despawn them, their indices may belong to new map entities).
        _liveEntities.Clear();
        _core.Scheduler.NextTick(ReloadForCurrentMap);

        // OnMapLoad fires very early; entities created before the map finishes spawning can be
        // wiped. Re-check shortly after so texts are guaranteed to exist once the map is ready.
        _core.Scheduler.DelayBySeconds(1.0f, RecheckLiveEntities);
        _core.Scheduler.DelayBySeconds(3.0f, RecheckLiveEntities);
    }

    private static bool IsUnknownMap(string? mapName) =>
        string.IsNullOrWhiteSpace(mapName) || string.Equals(mapName, "unknown", StringComparison.OrdinalIgnoreCase);

    private float SafeCurrentTime()
    {
        try
        {
            return _core.Engine.GlobalVars.CurrentTime;
        }
        catch
        {
            return 0f;
        }
    }

    // ------------------------------------------------------------------
    // Config
    // ------------------------------------------------------------------

    private void LoadConfig()
    {
        _configPath = Path.Combine(_core.Configuration.BasePath, "WallText.json");
        try
        {
            Directory.CreateDirectory(_core.Configuration.BasePath);
            if (File.Exists(_configPath))
            {
                var loaded = JsonSerializer.Deserialize<WallTextConfig>(File.ReadAllText(_configPath));
                _config = loaded ?? new WallTextConfig();
                if (_config.MergeMissingDefaultMessages())
                {
                    SaveConfig();
                }
            }
            else
            {
                _config = new WallTextConfig();
                SaveConfig();
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Config yuklenirken hata: {ex.Message}");
            _config = new WallTextConfig();
        }
    }

    private void SaveConfig()
    {
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Config kaydedilirken hata: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Per-map persistence
    // ------------------------------------------------------------------

    private string MapDirectory => Path.Combine(_core.Configuration.BasePath, "WorldTexts");
    private string MapFilePath => Path.Combine(MapDirectory, $"{_mapContext.CurrentMap}.json");

    private void ReloadForCurrentMap()
    {
        ClearAllEntities();
        _store.Clear();
        _dynamicTextRenderCache.Clear();
        _rotatingState.Clear();
        _captureSessions.Clear();
        _pendingChatBySlot.Clear();
        _lastPlayerButtons.Clear();
        _playerInteractionCooldowns.Clear();
        _lastDynamicUpdateTime = 0f;
        _lastRainbowUpdateTime = 0f;

        if (!_config.Enabled)
        {
            return;
        }

        if (IsUnknownMap(_mapContext.CurrentMap))
        {
            _mapContext.RefreshFromEngine();
            if (IsUnknownMap(_mapContext.CurrentMap))
            {
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(MapDirectory);
            var path = MapFilePath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, WallTextData>>(File.ReadAllText(path));
                if (loaded != null)
                {
                    foreach (var kv in loaded)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Value.Name))
                        {
                            kv.Value.Name = kv.Key;
                        }
                        _store[kv.Key] = kv.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Harita verisi yuklenirken hata: {ex.Message}");
        }

        foreach (var data in _store.Values)
        {
            SpawnEntity(data);
        }
    }

    private void Save()
    {
        if (IsUnknownMap(_mapContext.CurrentMap))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(MapDirectory);
            File.WriteAllText(MapFilePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Harita verisi kaydedilirken hata: {ex.Message}");
        }
    }

    private string? FindKey(string name)
    {
        foreach (var key in _store.Keys)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Math & Angle Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Normalizes an angle in degrees into the [-180, 180) range.
    /// </summary>
    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle >= 180f) angle -= 360f;
        else if (angle < -180f) angle += 360f;
        return MathF.Abs(angle) < 0.001f ? 0f : angle;
    }

    /// <summary>
    /// Calculates a readable facing angle for a wall text from a surface normal.
    /// <para>
    /// IMPORTANT: <c>point_worldtext</c> renders its text in the entity's local X/Y plane
    /// (reading direction = +X, glyph "up" = +Y, visible face = +Z). With angles (0, yaw, 0)
    /// the text therefore lies flat and - when yaw follows the wall normal - sticks out of the
    /// wall like a bullet. To stand it on a wall facing the viewer we must use
    /// yaw = normalYaw + 90 (reading direction runs along the wall to the viewer's right) and
    /// roll = 90 - elevation (rolls the face out of the wall; floor = 0, wall = 90, ceiling = 180).
    /// </para>
    /// </summary>
    private static QAngle CalculateWallAngles(Vector normal, QAngle playerEyeAngles)
    {
        float lenSq = normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z;
        if (lenSq > 0.0001f)
        {
            float invLen = 1f / MathF.Sqrt(lenSq);
            normal = new Vector(normal.X * invLen, normal.Y * invLen, normal.Z * invLen);
        }
        else
        {
            float eyeYawRad = playerEyeAngles.Yaw * (MathF.PI / 180f);
            normal = new Vector(-MathF.Cos(eyeYawRad), -MathF.Sin(eyeYawRad), 0f);
        }

        // Vertical or slanted wall: keep text perfectly upright on (near) vertical walls
        if (MathF.Abs(normal.Z) < 0.7f)
        {
            float normalYaw = MathF.Atan2(normal.Y, normal.X) * (180f / MathF.PI);
            float elevation = MathF.Abs(normal.Z) < 0.15f ? 0f : MathF.Asin(normal.Z) * (180f / MathF.PI);
            return new QAngle(0f, NormalizeAngle(normalYaw + 90f), NormalizeAngle(90f - elevation));
        }

        // Floor (face up, roll 0) or ceiling (face down, roll 180):
        // text top points away from the player so it reads naturally while looking at it.
        float yaw = NormalizeAngle(playerEyeAngles.Yaw - 90f);
        return new QAngle(0f, yaw, normal.Z > 0 ? 0f : 180f);
    }

    /// <summary>Horizontal direction (yaw, degrees) the visible face of the text points to.</summary>
    private static float FaceYaw(WallTextData data) => NormalizeAngle(data.Angles[1] - 90f);

    /// <summary>
    /// Builds point_worldtext angles whose visible face points along (dx, dy, dz).
    /// See <see cref="CalculateWallAngles"/> for the axis convention.
    /// </summary>
    private static (float Pitch, float Yaw, float Roll) AnglesFacingDirection(float dx, float dy, float dz, bool keepUpright)
    {
        float horiz = MathF.Sqrt(dx * dx + dy * dy);
        if (horiz < 0.001f)
        {
            return (0f, 0f, dz >= 0 ? 0f : 180f);
        }

        float dirYaw = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        float elevation = keepUpright ? 0f : MathF.Atan2(dz, horiz) * (180f / MathF.PI);
        return (0f, NormalizeAngle(dirYaw + 90f), NormalizeAngle(90f - elevation));
    }

    private static bool IsValidTransform(float[] position, float[] angles)
    {
        if (position == null || position.Length < 3 || angles == null || angles.Length < 3)
        {
            return false;
        }

        foreach (var v in position)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                return false;
            }
        }
        foreach (var v in angles)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                return false;
            }
        }
        return true;
    }

    private static string NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "#FFFFFF";
        hex = hex.Trim();
        return hex.StartsWith('#') ? hex.ToUpperInvariant() : "#" + hex.ToUpperInvariant();
    }

    private static bool IsValidHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var clean = hex.Trim().TrimStart('#');
        return clean.Length == 6 && Regex.IsMatch(clean, "^[0-9A-Fa-f]{6}$");
    }

    private static Color ColorFromHsv(float hue, float saturation, float value)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        int hi = (int)MathF.Floor(hue / 60f) % 6;
        float f = (hue / 60f) - MathF.Floor(hue / 60f);

        value *= 255f;
        byte v = (byte)Math.Clamp((int)value, 0, 255);
        byte p = (byte)Math.Clamp((int)(value * (1f - saturation)), 0, 255);
        byte q = (byte)Math.Clamp((int)(value * (1f - f * saturation)), 0, 255);
        byte t = (byte)Math.Clamp((int)(value * (1f - (1f - f) * saturation)), 0, 255);

        return hi switch
        {
            0 => new Color(v, t, p, (byte)255),
            1 => new Color(q, v, p, (byte)255),
            2 => new Color(p, v, t, (byte)255),
            3 => new Color(p, q, v, (byte)255),
            4 => new Color(t, p, v, (byte)255),
            _ => new Color(v, p, q, (byte)255)
        };
    }

    // ------------------------------------------------------------------
    // Entity lifecycle
    // ------------------------------------------------------------------

    private void SpawnEntity(WallTextData data)
    {
        DespawnEntity(data.Name);
        if (!data.Enabled)
        {
            return;
        }

        if (!IsValidTransform(data.Position, data.Angles))
        {
            _core.Logger.LogError($"[CS2_WallText] '{data.Name}' gecersiz konum/aci degeri icerdigi icin olusturulmadi.");
            return;
        }

        try
        {
            var entity = _core.EntitySystem.CreateEntityByDesignerName<CPointWorldText>("point_worldtext");
            if (entity == null)
            {
                return;
            }

            string initialText = GetActiveTemplateText(data, resetRotationTimer: true);
            var formattedText = WallTextPlaceholders.Format(initialText, _core, _mapContext.CurrentMap);

            ApplyEntityProperties(entity, data, formattedText, notifyNetwork: false);
            entity.DispatchSpawn();

            var pos = new Vector(data.Position[0], data.Position[1], data.Position[2]);
            var ang = new QAngle(data.Angles[0], data.Angles[1], data.Angles[2]);
            entity.Teleport(pos, ang, new Vector(0f, 0f, 0f));

            _liveEntities[data.Name] = _core.EntitySystem.GetRefEHandle(entity);
            _dynamicTextRenderCache[data.Name] = formattedText;
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Entity olusturulurken hata: {ex.Message}");
        }
    }

    private string GetActiveTemplateText(WallTextData data, bool resetRotationTimer = false)
    {
        if (data.IsRotating && data.RotatingMessages.Count > 0)
        {
            if (resetRotationTimer || !_rotatingState.TryGetValue(data.Name, out var rState) || rState.Index < 0 || rState.Index >= data.RotatingMessages.Count)
            {
                rState = (0, SafeCurrentTime());
                _rotatingState[data.Name] = rState;
            }
            return data.RotatingMessages[rState.Index];
        }
        return data.Text;
    }

    private static void ApplyEntityProperties(CPointWorldText entity, WallTextData data, string formattedText, bool notifyNetwork)
    {
        entity.MessageText = formattedText;
        if (!string.IsNullOrWhiteSpace(data.FontName))
        {
            entity.FontName = data.FontName;
        }
        else
        {
            entity.FontName = "";
        }

        entity.FontSize = data.FontSize;
        entity.WorldUnitsPerPx = data.Scale;
        if (!data.Rainbow)
        {
            entity.Color = Color.FromHex(NormalizeHex(data.ColorHex));
        }
        entity.Enabled = true;
        entity.Fullbright = data.Fullbright;
        entity.DrawBackground = data.Background;
        if (data.Background)
        {
            entity.BackgroundBorderWidth = 0.2f;
            entity.BackgroundBorderHeight = 0.2f;
            entity.BackgroundWorldToUV = 0.05f;
        }
        entity.JustifyHorizontal = (PointWorldTextJustifyHorizontal_t)Math.Clamp(data.JustifyHorizontal, 0, 2);
        entity.JustifyVertical = (PointWorldTextJustifyVertical_t)Math.Clamp(data.JustifyVertical, 0, 2);
        entity.ReorientMode = data.FacePlayer
            ? PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_AROUND_UP
            : PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
        entity.DepthOffset = data.DepthOffset;

        if (notifyNetwork)
        {
            entity.MessageTextUpdated();
            entity.FontNameUpdated();
            entity.FontSizeUpdated();
            entity.WorldUnitsPerPxUpdated();
            if (!data.Rainbow)
            {
                entity.ColorUpdated();
            }
            entity.EnabledUpdated();
            entity.FullbrightUpdated();
            entity.DrawBackgroundUpdated();
            if (data.Background)
            {
                entity.BackgroundBorderWidthUpdated();
                entity.BackgroundBorderHeightUpdated();
                entity.BackgroundWorldToUVUpdated();
            }
            entity.JustifyHorizontalUpdated();
            entity.JustifyVerticalUpdated();
            entity.ReorientModeUpdated();
            entity.DepthOffsetUpdated();
        }
    }

    private void DespawnEntity(string name)
    {
        _dynamicTextRenderCache.Remove(name);
        _rotatingState.Remove(name);
        if (_liveEntities.Remove(name, out var handle))
        {
            try
            {
                // Handle validity checks index + serial, so we never delete an unrelated entity
                // that reused the index after a round/map cleanup.
                if (handle.IsValid)
                {
                    handle.Value?.Despawn();
                }
            }
            catch
            {
                // Entity already cleaned up
            }
        }
    }

    private bool TryGetLiveEntity(string name, out CPointWorldText entity)
    {
        entity = null!;
        if (!_liveEntities.TryGetValue(name, out var handle))
        {
            return false;
        }

        try
        {
            if (handle.IsValid && handle.Value is { } value && value.IsValid)
            {
                entity = value;
                return true;
            }
        }
        catch
        {
            // Stale handle
        }

        return false;
    }

    private void ClearAllEntities()
    {
        foreach (var name in new List<string>(_liveEntities.Keys))
        {
            DespawnEntity(name);
        }
        _dynamicTextRenderCache.Clear();
        _rotatingState.Clear();
    }

    private void UpdateLiveEntity(WallTextData data)
    {
        if (!data.Enabled)
        {
            DespawnEntity(data.Name);
            return;
        }

        if (!TryGetLiveEntity(data.Name, out var entity))
        {
            SpawnEntity(data);
            return;
        }

        if (!IsValidTransform(data.Position, data.Angles))
        {
            _core.Logger.LogError($"[CS2_WallText] '{data.Name}' gecersiz konum/aci degeri icerdigi icin guncellenmedi.");
            return;
        }

        try
        {
            var pos = new Vector(data.Position[0], data.Position[1], data.Position[2]);
            var ang = new QAngle(data.Angles[0], data.Angles[1], data.Angles[2]);
            entity.Teleport(pos, ang, new Vector(0f, 0f, 0f));

            string textToShow = GetActiveTemplateText(data);
            var formattedText = WallTextPlaceholders.Format(textToShow, _core, _mapContext.CurrentMap);

            ApplyEntityProperties(entity, data, formattedText, notifyNetwork: true);
            _dynamicTextRenderCache[data.Name] = formattedText;
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Entity guncellenirken hata: {ex.Message}");
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event)
    {
        // Round restart (mp_restartgame / new round) runs a map cleanup that deletes
        // point_worldtext entities created by plugins. Respawn any missing texts.
        _core.Scheduler.NextTick(RecheckLiveEntities);
        _core.Scheduler.DelayBySeconds(0.5f, RecheckLiveEntities);
        return HookResult.Continue;
    }

    private void RecheckLiveEntities()
    {
        if (!_config.Enabled) return;

        foreach (var data in _store.Values)
        {
            if (!data.Enabled) continue;

            if (!TryGetLiveEntity(data.Name, out var entity))
            {
                SpawnEntity(data);
            }
        }
    }

    // ------------------------------------------------------------------
    // Tick Systems: Dynamic Placeholders, Rainbow & Rotating Announcers
    // ------------------------------------------------------------------

    private void OnTick()
    {
        if (!_config.Enabled) return;

        var now = SafeCurrentTime();

        UpdateRainbowColors(now);
        UpdateRotatingAnnouncers(now);
        UpdateDynamicPlaceholders(now);
        ProcessAimCaptures(now);
        CheckPlayerInteractions(now);

        // Watchdog: if any cleanup (round restart, mp_restartgame, other plugins) removed a text,
        // bring it back within ~2 seconds even if no round_start event fired.
        if (now < _lastWatchdogTime || now - _lastWatchdogTime >= 2f)
        {
            _lastWatchdogTime = now;
            RecheckLiveEntities();
        }
    }

    private float _lastWatchdogTime;

    private void UpdateRainbowColors(float now)
    {
        // Guard against GlobalVars.CurrentTime rollback on map transitions
        if (now >= _lastRainbowUpdateTime && now - _lastRainbowUpdateTime < 0.1f) return;
        _lastRainbowUpdateTime = now;

        foreach (var data in _store.Values)
        {
            if (!data.Enabled || !data.Rainbow) continue;
            if (!TryGetLiveEntity(data.Name, out var entity)) continue;

            float hue = (now * 60f * data.RainbowSpeed) % 360f;
            entity.Color = ColorFromHsv(hue, 1f, 1f);
            entity.ColorUpdated();
        }
    }

    private void UpdateRotatingAnnouncers(float now)
    {
        foreach (var data in _store.Values)
        {
            if (!data.Enabled || !data.IsRotating || data.RotatingMessages.Count == 0) continue;
            if (!TryGetLiveEntity(data.Name, out var entity)) continue;

            if (!_rotatingState.TryGetValue(data.Name, out var state) || now < state.LastSwitchTime)
            {
                state = (0, now);
                _rotatingState[data.Name] = state;
            }

            if (now - state.LastSwitchTime >= data.RotationIntervalSeconds)
            {
                int nextIndex = (state.Index + 1) % data.RotatingMessages.Count;
                _rotatingState[data.Name] = (nextIndex, now);

                string nextMsg = data.RotatingMessages[nextIndex];
                string rendered = WallTextPlaceholders.Format(nextMsg, _core, _mapContext.CurrentMap);

                entity.MessageText = rendered;
                entity.MessageTextUpdated();
                _dynamicTextRenderCache[data.Name] = rendered;
            }
        }
    }

    private void UpdateDynamicPlaceholders(float now)
    {
        if (_config.DynamicUpdateIntervalSeconds <= 0f) return;
        if (now >= _lastDynamicUpdateTime && now - _lastDynamicUpdateTime < _config.DynamicUpdateIntervalSeconds) return;
        _lastDynamicUpdateTime = now;

        foreach (var data in _store.Values)
        {
            if (!data.Enabled) continue;

            string activeTemplate = GetActiveTemplateText(data);
            if (!WallTextPlaceholders.HasPlaceholders(activeTemplate)) continue;
            if (!TryGetLiveEntity(data.Name, out var entity)) continue;

            var rendered = WallTextPlaceholders.Format(activeTemplate, _core, _mapContext.CurrentMap);
            if (_dynamicTextRenderCache.TryGetValue(data.Name, out var cached) && cached == rendered) continue;

            try
            {
                entity.MessageText = rendered;
                entity.MessageTextUpdated();
                _dynamicTextRenderCache[data.Name] = rendered;
            }
            catch
            {
                // Ignore transient network errors
            }
        }
    }

    // ------------------------------------------------------------------
    // Interactable Text Buttons (+USE & Shoot triggers)
    // ------------------------------------------------------------------

    private void CheckPlayerInteractions(float now)
    {
        // Optimization: Only trace if at least one interactable text is active on this map
        bool hasInteractable = false;
        foreach (var d in _store.Values)
        {
            if (d.Enabled && d.IsInteractable)
            {
                hasInteractable = true;
                break;
            }
        }
        if (!hasInteractable) return;

        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            if (player == null || !player.IsValid || player.IsFakeClient) continue;
            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid || pawn.Health <= 0) continue;

            var currentButtons = player.PressedButtons;
            _lastPlayerButtons.TryGetValue(player.Slot, out var lastButtons);
            _lastPlayerButtons[player.Slot] = currentButtons;

            bool useRising = (currentButtons & GameButtonFlags.E) != 0 && (lastButtons & GameButtonFlags.E) == 0;
            bool shootRising = (currentButtons & GameButtonFlags.Mouse1) != 0 && (lastButtons & GameButtonFlags.Mouse1) == 0;

            if (!useRising && !shootRising) continue;
            if (_captureSessions.ContainsKey(player.Slot)) continue; // Admin currently capturing aim point

            if (!TryTraceAimPoint(pawn, out var hitPoint, out _)) continue;
            if (!pawn.AbsOrigin.HasValue) continue;
            var playerPos = pawn.AbsOrigin.Value;

            // Find the closest matching interactable text to the aimed hitPoint
            WallTextData? bestMatch = null;
            float bestHitDist = float.MaxValue;

            foreach (var data in _store.Values)
            {
                if (!data.Enabled || !data.IsInteractable) continue;

                if (data.TriggerType.Equals("use", StringComparison.OrdinalIgnoreCase) && !useRising) continue;
                if (data.TriggerType.Equals("shoot", StringComparison.OrdinalIgnoreCase) && !shootRising) continue;

                var textPos = new Vector(data.Position[0], data.Position[1], data.Position[2]);

                float distToPlayer = VectorDistance(playerPos, textPos);
                if (distToPlayer > data.InteractionDistance) continue;

                float hitRadius = Math.Clamp(data.FontSize * data.Scale * 6f, 45f, 180f);
                float distToHit = VectorDistance(hitPoint, textPos);
                if (distToHit <= hitRadius && distToHit < bestHitDist)
                {
                    bestHitDist = distToHit;
                    bestMatch = data;
                }
            }

            if (bestMatch == null) continue;

            if (_playerInteractionCooldowns.TryGetValue(player.Slot, out var lastTime)
                && now >= lastTime
                && (now - lastTime < bestMatch.CooldownSeconds))
            {
                continue;
            }
            _playerInteractionCooldowns[player.Slot] = now;

            // Fire tactile feedback sound to player
            player.ExecuteCommand("play sounds/ui/beepclear.vsnd");

            // Send chat message to player if configured
            if (!string.IsNullOrWhiteSpace(bestMatch.MessageToPlayer))
            {
                var msg = WallTextPlaceholders.Format(bestMatch.MessageToPlayer, _core, _mapContext.CurrentMap, player);
                SendColoredChat(player, $"{Prefix()} {msg}");
            }

            // Run configured command(s)
            if (!string.IsNullOrWhiteSpace(bestMatch.CommandToRun))
            {
                var rawCmd = WallTextPlaceholders.Format(bestMatch.CommandToRun, _core, _mapContext.CurrentMap, player);
                ExecuteTriggerCommands(player, rawCmd);
            }
        }
    }

    private void ExecuteTriggerCommands(IPlayer player, string rawCmd)
    {
        bool isServerCmd = false;
        if (rawCmd.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
        {
            isServerCmd = true;
            rawCmd = rawCmd[7..].Trim();
        }

        foreach (var part in rawCmd.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;

            if (isServerCmd)
            {
                _core.Engine.ExecuteCommand(part);
            }
            else if (part.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                _core.Engine.ExecuteCommand(part[7..].Trim());
            }
            else if (part.StartsWith('!') || part.StartsWith('/'))
            {
                // Convert chat command shorthand (!vip) into client say command
                player.ExecuteCommand($"say \"{part}\"");
            }
            else
            {
                player.ExecuteCommand(part);
            }
        }
    }

    private static float VectorDistance(Vector a, Vector b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ------------------------------------------------------------------
    // Aim capture & instant crosshair positioning
    // ------------------------------------------------------------------

    private void ProcessAimCaptures(float now)
    {
        if (_captureSessions.Count == 0) return;

        foreach (var (slot, session) in new List<KeyValuePair<int, CaptureSession>>(_captureSessions))
        {
            var player = _core.PlayerManager.GetPlayer(slot);
            if (player == null || !player.IsValid)
            {
                session.LastMouse1Down = false;
                continue;
            }

            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid || pawn.Health <= 0) continue;

            var pressed = (player.PressedButtons & GameButtonFlags.Mouse1) != 0;
            var risingEdge = pressed && !session.LastMouse1Down;
            session.LastMouse1Down = pressed;

            if (!risingEdge) continue;
            if (now >= session.LastCaptureTime && now - session.LastCaptureTime < CaptureDebounceSeconds) continue;

            if (!TryTraceAimPoint(pawn, out var point, out var normal)) continue;

            session.LastCaptureTime = now;
            _captureSessions.Remove(slot);
            session.OnCaptured?.Invoke(player, point, normal);
        }
    }

    private void MoveToCrosshair(IPlayer player, string name, bool returnToOrientationMenu = false)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid) return;

        if (!TryTraceAimPoint(pawn, out var point, out var normal))
        {
            return;
        }

        var placedPos = point + normal * WallOffset;
        var angle = CalculateWallAngles(normal, pawn.EyeAngles);

        data.Position = [placedPos.X, placedPos.Y, placedPos.Z];
        data.Angles = [angle.Pitch, angle.Yaw, angle.Roll];
        Save();
        UpdateLiveEntity(data);

        SendColoredChat(player, $"{Prefix()} {T("chat.moved_to_crosshair", data.Name)}");
        _core.Scheduler.NextTick(() =>
        {
            if (returnToOrientationMenu) OpenOrientationMenu(player, data.Name);
            else OpenDetailMenu(player, data.Name);
        });
    }

    private bool TryTraceAimPoint(CCSPlayerPawn pawn, out Vector point, out Vector normal)
    {
        point = default;
        normal = new Vector(0f, 0f, 1f);

        if (pawn == null || !pawn.IsValid) return false;
        if (!TryGetEyePosition(pawn, out var start)) return false;

        var angles = pawn.EyeAngles;

        TraceParams? traceParams = TraceParams.Builder()
            .WithLineRay()
            .WithObjectQuery(RnQueryObjectSet.All)
            .WithInteraction(
                MaskTrace.WorldGeometry | MaskTrace.StaticLevel | MaskTrace.CsgoOpaque |
                MaskTrace.PhysicsProp | MaskTrace.PlayerClip | MaskTrace.Window | MaskTrace.Solid,
                MaskTrace.Empty,
                MaskTrace.Empty)
            .WithCollisionGroup(CollisionGroup.Player)
            .HitSolid(true)
            .HitTrigger(false)
            .IgnoreEntity(pawn)
            .IgnoreOwner(pawn)
            .Build();

        var trace = _core.Trace.TraceShapeAngle(in start, in angles, 8192f, in traceParams);
        if (!trace.DidHit)
        {
            TraceParams? fallbackParams = TraceParams.Builder()
                .WithLineRay()
                .WithObjectQuery(RnQueryObjectSet.All)
                .HitSolid(true)
                .HitTrigger(false)
                .IgnoreEntity(pawn)
                .IgnoreOwner(pawn)
                .Build();

            trace = _core.Trace.TraceShapeAngle(in start, in angles, 8192f, in fallbackParams);
            if (!trace.DidHit)
            {
                return false;
            }
        }

        // If trace started inside or immediately hit player's own bbox/equipment, nudge forward along view ray
        if (trace.StartInSolid || trace.Distance < 1.5f)
        {
            angles.ToDirectionVectors(out var forward, out _, out _);
            var nudgedStart = new Vector(
                start.X + forward.X * 20f,
                start.Y + forward.Y * 20f,
                start.Z + forward.Z * 20f);

            var retry = _core.Trace.TraceShapeAngle(in nudgedStart, in angles, 8192f, in traceParams);
            if (retry.DidHit && !retry.StartInSolid)
            {
                trace = retry;
            }
        }

        point = trace.HitPoint;
        normal = trace.HitNormal;

        float normLenSq = normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z;
        if (normLenSq < 0.001f)
        {
            float yawRad = angles.Yaw * (MathF.PI / 180f);
            normal = new Vector(-MathF.Cos(yawRad), -MathF.Sin(yawRad), 0f);
        }

        return true;
    }

    private static bool TryGetEyePosition(CCSPlayerPawn pawn, out Vector eyePos)
    {
        eyePos = default;
        try
        {
            if (pawn.AbsOrigin.HasValue)
            {
                var origin = pawn.AbsOrigin.Value;
                float viewZ = 64f;
                try
                {
                    var zOffset = pawn.ViewOffset.Z.Value;
                    if (zOffset >= 16f && zOffset <= 96f)
                    {
                        viewZ = zOffset;
                    }
                }
                catch
                {
                    // Fallback to 64f
                }

                // Only use pawn.EyePosition if it is in valid world-space proximity to AbsOrigin
                if (pawn.EyePosition.HasValue)
                {
                    var ep = pawn.EyePosition.Value;
                    float dx = MathF.Abs(ep.X - origin.X);
                    float dy = MathF.Abs(ep.Y - origin.Y);
                    float dz = ep.Z - origin.Z;
                    if (dx < 32f && dy < 32f && dz >= 16f && dz <= 96f)
                    {
                        eyePos = ep;
                        return true;
                    }
                }

                eyePos = new Vector(origin.X, origin.Y, origin.Z + viewZ);
                return true;
            }

            if (pawn.EyePosition.HasValue)
            {
                eyePos = pawn.EyePosition.Value;
                return true;
            }
        }
        catch
        {
            // Ignore schema access error
        }

        return false;
    }

    private void BeginPointCapture(IPlayer player, Action<IPlayer, Vector, Vector> onCaptured)
    {
        _captureSessions[player.Slot] = new CaptureSession
        {
            OnCaptured = onCaptured,
            LastMouse1Down = (player.PressedButtons & GameButtonFlags.Mouse1) != 0,
            LastCaptureTime = SafeCurrentTime(),
        };
    }

    private bool IsCapturing(int slot) => _captureSessions.ContainsKey(slot);

    private void SetPendingChat(IPlayer player, ChatPromptKind kind, string targetName, Vector position = default, QAngle angles = default)
    {
        CloseMenu(player);
        _pendingChatBySlot[player.Slot] = new PendingChatPrompt
        {
            SteamId = player.SteamID,
            Kind = kind,
            TargetName = targetName,
            Position = position,
            Angles = angles
        };
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var slot = @event.PlayerId;
        _captureSessions.Remove(slot);
        _lastPlayerButtons.Remove(slot);
        _pendingChatBySlot.Remove(slot);
        _playerStepSizes.Remove(slot);
        _menuCursorBySlot.Remove(slot);
        _activeMenuKeyBySlot.Remove(slot);
        _playerInteractionCooldowns.Remove(slot);
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    private void OnCommand(ICommandContext ctx)
    {
        var player = ctx.Sender;
        if (player == null || !player.IsValid)
        {
            ctx.Reply("This command can only be used in-game.");
            return;
        }

        if (!HasPermission(player))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.no_permission")}");
            return;
        }

        var args = ctx.Args;
        if (args.Length == 0 || args[0].Equals("menu", StringComparison.OrdinalIgnoreCase))
        {
            OpenMainMenu(player);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "add":
            {
                if (args.Length < 2)
                {
                    SendColoredChat(player, $"{Prefix()} {T("chat.usage_add")}");
                    return;
                }
                BeginCreate(player, string.Join(" ", args, 1, args.Length - 1));
                break;
            }
            case "edit":
            {
                if (args.Length < 2)
                {
                    SendColoredChat(player, $"{Prefix()} {T("chat.usage_edit_cmd")}");
                    return;
                }
                BeginRename(player, string.Join(" ", args, 1, args.Length - 1));
                break;
            }
            case "delete":
            {
                if (args.Length < 2)
                {
                    SendColoredChat(player, $"{Prefix()} {T("chat.usage_delete")}");
                    return;
                }
                DeleteWallText(player, string.Join(" ", args, 1, args.Length - 1));
                break;
            }
            case "list":
                ListWallTexts(player);
                break;
            case "tp":
            {
                if (args.Length < 2)
                {
                    SendColoredChat(player, $"{Prefix()} {T("chat.usage_tp")}");
                    return;
                }
                TeleportToText(player, string.Join(" ", args, 1, args.Length - 1));
                break;
            }
            case "reload":
            {
                LoadConfig();
                ReloadForCurrentMap();
                SendColoredChat(player, $"{Prefix()} {T("chat.reloaded", _mapContext.CurrentMap, _store.Count)}");
                break;
            }
            case "help":
                ShowHelp(player);
                break;
            default:
                SendColoredChat(player, $"{Prefix()} {T("chat.usage_general")}");
                break;
        }
    }

    private void ShowHelp(IPlayer player)
    {
        SendColoredChat(player, T("chat.help_header"));
        SendColoredChat(player, T("chat.help_menu"));
        SendColoredChat(player, T("chat.help_add"));
        SendColoredChat(player, T("chat.help_edit"));
        SendColoredChat(player, T("chat.help_delete"));
        SendColoredChat(player, T("chat.help_list"));
        SendColoredChat(player, T("chat.help_tp"));
        SendColoredChat(player, T("chat.help_reload"));
    }

    private void BeginCreate(IPlayer player, string name)
    {
        name = name.Trim();
        if (FindKey(name) != null)
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.already_exists", name)}");
            return;
        }

        if (IsCapturing(player.Slot))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.already_capturing")}");
            return;
        }

        CloseMenu(player);
        SendColoredChat(player, $"{Prefix()} {T("chat.capturing_point", name)}");

        BeginPointCapture(player, (p, point, normal) =>
        {
            var placedPos = point + normal * WallOffset;
            var angle = CalculateWallAngles(normal, p.PlayerPawn?.EyeAngles ?? default);

            SetPendingChat(p, ChatPromptKind.NewText, name, placedPos, angle);
            SendColoredChat(p, $"{Prefix()} {T("chat.enter_text")}");
        });
    }

    private void BeginReaim(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        if (IsCapturing(player.Slot))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.already_capturing")}");
            return;
        }

        CloseMenu(player);
        SendColoredChat(player, $"{Prefix()} {T("chat.reaim_prompt", data.Name)}");

        BeginPointCapture(player, (p, point, normal) =>
        {
            var placedPos = point + normal * WallOffset;
            var angle = CalculateWallAngles(normal, p.PlayerPawn?.EyeAngles ?? default);

            data.Position = [placedPos.X, placedPos.Y, placedPos.Z];
            data.Angles = [angle.Pitch, angle.Yaw, angle.Roll];
            Save();
            UpdateLiveEntity(data);

            SendColoredChat(p, $"{Prefix()} {T("chat.reaim_success", data.Name)}");
            _core.Scheduler.NextTick(() => OpenDetailMenu(p, data.Name));
        });
    }

    private void BeginDuplicate(IPlayer player, string sourceName)
    {
        var key = FindKey(sourceName);
        if (key == null || !_store.TryGetValue(key, out var source))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", sourceName)}");
            return;
        }

        if (IsCapturing(player.Slot))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.already_capturing")}");
            return;
        }

        string cloneName = $"{source.Name}_copy";
        int copyIndex = 2;
        while (FindKey(cloneName) != null)
        {
            cloneName = $"{source.Name}_copy{copyIndex++}";
        }

        CloseMenu(player);
        SendColoredChat(player, $"{Prefix()} {T("chat.duplicated", cloneName, source.Name)}");

        BeginPointCapture(player, (p, point, normal) =>
        {
            var placedPos = point + normal * WallOffset;
            var angle = CalculateWallAngles(normal, p.PlayerPawn?.EyeAngles ?? default);

            var clone = new WallTextData
            {
                Name = cloneName,
                Text = source.Text,
                Position = [placedPos.X, placedPos.Y, placedPos.Z],
                Angles = [angle.Pitch, angle.Yaw, angle.Roll],
                Enabled = true,
                ColorHex = source.ColorHex,
                FontName = source.FontName,
                FontSize = source.FontSize,
                Scale = source.Scale,
                Background = source.Background,
                Fullbright = source.Fullbright,
                FacePlayer = source.FacePlayer,
                JustifyHorizontal = source.JustifyHorizontal,
                JustifyVertical = source.JustifyVertical,
                DepthOffset = source.DepthOffset,
                Rainbow = source.Rainbow,
                RainbowSpeed = source.RainbowSpeed,
                IsRotating = source.IsRotating,
                RotationIntervalSeconds = source.RotationIntervalSeconds,
                RotatingMessages = new List<string>(source.RotatingMessages),
                IsInteractable = source.IsInteractable,
                TriggerType = source.TriggerType,
                CommandToRun = source.CommandToRun,
                MessageToPlayer = source.MessageToPlayer,
                InteractionDistance = source.InteractionDistance,
                CooldownSeconds = source.CooldownSeconds
            };

            _store[cloneName] = clone;
            Save();
            SpawnEntity(clone);

            SendColoredChat(p, $"{Prefix()} {T("chat.created", cloneName, clone.Text)}");
            _core.Scheduler.NextTick(() => OpenDetailMenu(p, cloneName));
        });
    }

    private void TeleportToText(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || pawn.Health <= 0) return;

        // Text face points along yaw - 90 (see CalculateWallAngles)
        float yawRad = FaceYaw(data) * (MathF.PI / 180f);
        float forwardX = MathF.Cos(yawRad);
        float forwardY = MathF.Sin(yawRad);

        // Place player in front of the text without sinking below floor level
        var targetPos = new Vector(
            data.Position[0] + forwardX * 75f,
            data.Position[1] + forwardY * 75f,
            data.Position[2]
        );

        float lookYaw = NormalizeAngle(FaceYaw(data) + 180f);
        var targetAng = new QAngle(0f, lookYaw, 0f);

        pawn.Teleport(targetPos, targetAng, new Vector(0f, 0f, 0f));
        SendColoredChat(player, $"{Prefix()} {T("chat.teleported", data.Name)}");
    }

    private void BeginRename(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null)
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        SetPendingChat(player, ChatPromptKind.Rename, key);
        SendColoredChat(player, $"{Prefix()} {T("chat.enter_new_name")}");
    }

    private HookResult OnClientChat(int playerId, string text, bool teamonly)
    {
        var player = _core.PlayerManager.GetPlayer(playerId);
        if (player == null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        if (!_pendingChatBySlot.TryGetValue(player.Slot, out var prompt) || prompt.SteamId != player.SteamID)
        {
            return HookResult.Continue;
        }

        text = text.Trim();

        // Allow !text or /text commands to cancel the prompt and execute the command
        if (text.StartsWith("!text", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/text", StringComparison.OrdinalIgnoreCase))
        {
            _pendingChatBySlot.Remove(player.Slot);
            return HookResult.Continue;
        }

        _pendingChatBySlot.Remove(player.Slot);

        return prompt.Kind switch
        {
            ChatPromptKind.NewText => HandleNewTextInput(player, prompt, text),
            ChatPromptKind.ChangeText => HandleTextChangeInput(player, prompt.TargetName, text),
            ChatPromptKind.Rename => HandleRenameInput(player, prompt.TargetName, text),
            ChatPromptKind.ColorHex => HandleColorChangeInput(player, prompt.TargetName, text),
            ChatPromptKind.AddRotatingMessage => HandleAddRotatingMsgInput(player, prompt.TargetName, text),
            ChatPromptKind.InteractMessage => HandleInteractMsgInput(player, prompt.TargetName, text),
            ChatPromptKind.InteractCommand => HandleInteractCmdInput(player, prompt.TargetName, text),
            _ => HookResult.Continue
        };
    }

    private static bool IsCancelWord(string text) =>
        text.Equals("cancel", StringComparison.OrdinalIgnoreCase) || text.Equals("iptal", StringComparison.OrdinalIgnoreCase);

    private HookResult HandleNewTextInput(IPlayer player, PendingChatPrompt pending, string text)
    {
        if (IsCancelWord(text))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _pendingChatBySlot[player.Slot] = pending;
            SendColoredChat(player, $"{Prefix()} {T("chat.enter_text")}");
            return HookResult.Stop;
        }

        if (FindKey(pending.TargetName) != null)
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.already_exists", pending.TargetName)}");
            return HookResult.Stop;
        }

        text = text.Replace("\\n", "\n");

        var data = new WallTextData
        {
            Name = pending.TargetName,
            Text = text,
            Position = [pending.Position.X, pending.Position.Y, pending.Position.Z],
            Angles = [pending.Angles.Pitch, pending.Angles.Yaw, pending.Angles.Roll],
            ColorHex = NormalizeHex(_config.DefaultColorHex),
            FontName = _config.DefaultFontName,
            FontSize = _config.DefaultFontSize,
            Scale = _config.DefaultScale,
            Background = _config.DefaultBackground,
            Fullbright = _config.DefaultFullbright,
            FacePlayer = _config.DefaultFacePlayer,
            JustifyHorizontal = _config.DefaultJustifyHorizontal,
            JustifyVertical = _config.DefaultJustifyVertical,
            DepthOffset = _config.DefaultDepthOffset,
        };

        _store[pending.TargetName] = data;
        Save();
        SpawnEntity(data);

        SendColoredChat(player, $"{Prefix()} {T("chat.created", pending.TargetName, text)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, pending.TargetName));
        return HookResult.Stop;
    }

    private HookResult HandleTextChangeInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.rename_cancelled")}");
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, name));
            return HookResult.Stop;
        }

        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SetPendingChat(player, ChatPromptKind.ChangeText, data.Name);
            SendColoredChat(player, $"{Prefix()} {T("chat.enter_text")}");
            return HookResult.Stop;
        }

        text = text.Replace("\\n", "\n");
        data.Text = text;
        Save();

        UpdateLiveEntity(data);

        SendColoredChat(player, $"{Prefix()} {T("chat.text_changed", text)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
        return HookResult.Stop;
    }

    private HookResult HandleRenameInput(IPlayer player, string oldName, string newName)
    {
        if (IsCancelWord(newName))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.rename_cancelled")}");
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, oldName));
            return HookResult.Stop;
        }

        var oldKey = FindKey(oldName);
        if (oldKey == null || !_store.TryGetValue(oldKey, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", oldName)}");
            return HookResult.Stop;
        }

        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            SetPendingChat(player, ChatPromptKind.Rename, oldKey);
            SendColoredChat(player, $"{Prefix()} {T("chat.enter_new_name")}");
            return HookResult.Stop;
        }

        if (FindKey(newName) != null && !newName.Equals(oldKey, StringComparison.OrdinalIgnoreCase))
        {
            SetPendingChat(player, ChatPromptKind.Rename, oldKey);
            SendColoredChat(player, $"{Prefix()} {T("chat.already_exists", newName)}");
            return HookResult.Stop;
        }

        _store.Remove(oldKey);
        data.Name = newName;
        _store[newName] = data;
        Save();

        if (_liveEntities.Remove(oldKey, out var entity))
        {
            _liveEntities[newName] = entity;
        }
        if (_dynamicTextRenderCache.Remove(oldKey, out var cachedText))
        {
            _dynamicTextRenderCache[newName] = cachedText;
        }
        if (_rotatingState.Remove(oldKey, out var rotState))
        {
            _rotatingState[newName] = rotState;
        }

        SendColoredChat(player, $"{Prefix()} {T("chat.renamed", newName)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, newName));
        return HookResult.Stop;
    }

    private HookResult HandleColorChangeInput(IPlayer player, string name, string hexInput)
    {
        if (IsCancelWord(hexInput))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.rename_cancelled")}");
            _core.Scheduler.NextTick(() => OpenColorMenu(player, name));
            return HookResult.Stop;
        }

        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        if (!IsValidHex(hexInput))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.invalid_color_hex")}");
            SetPendingChat(player, ChatPromptKind.ColorHex, data.Name);
            return HookResult.Stop;
        }

        data.ColorHex = NormalizeHex(hexInput);
        data.Rainbow = false; // Turn off rainbow when picking static color
        Save();
        UpdateLiveEntity(data);

        SendColoredChat(player, $"{Prefix()} {T("chat.color_changed", data.ColorHex)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
        return HookResult.Stop;
    }

    private HookResult HandleAddRotatingMsgInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.rename_cancelled")}");
            _core.Scheduler.NextTick(() => OpenRotatingMenu(player, name));
            return HookResult.Stop;
        }

        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        text = text.Replace("\\n", "\n");
        data.RotatingMessages.Add(text);
        data.IsRotating = true; // Auto-enable rotation
        Save();
        UpdateLiveEntity(data);

        SendColoredChat(player, $"{Prefix()} {T("chat.rotating_message_added", text)}");
        _core.Scheduler.NextTick(() => OpenRotatingMenu(player, data.Name));
        return HookResult.Stop;
    }

    private HookResult HandleInteractMsgInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.rename_cancelled")}");
            _core.Scheduler.NextTick(() => OpenInteractMenu(player, name));
            return HookResult.Stop;
        }

        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        data.MessageToPlayer = text.Replace("\\n", "\n");
        data.IsInteractable = true;
        Save();

        SendColoredChat(player, $"{Prefix()} {T("chat.interact_msg_set", data.MessageToPlayer)}");
        _core.Scheduler.NextTick(() => OpenInteractMenu(player, data.Name));
        return HookResult.Stop;
    }

    private HookResult HandleInteractCmdInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.rename_cancelled")}");
            _core.Scheduler.NextTick(() => OpenInteractMenu(player, name));
            return HookResult.Stop;
        }

        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        data.CommandToRun = text.Trim();
        data.IsInteractable = true;
        Save();

        SendColoredChat(player, $"{Prefix()} {T("chat.interact_cmd_set", data.CommandToRun)}");
        _core.Scheduler.NextTick(() => OpenInteractMenu(player, data.Name));
        return HookResult.Stop;
    }

    private void DeleteWallText(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null)
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        DespawnEntity(key);
        _store.Remove(key);
        Save();
        SendColoredChat(player, $"{Prefix()} {T("chat.deleted", key)}");
    }

    private void ListWallTexts(IPlayer player)
    {
        if (_store.Count == 0)
        {
            SendColoredChat(player, $"{Prefix()} {T("chat.list_empty")}");
            return;
        }

        SendColoredChat(player, $"{Prefix()} {T("chat.list_header", _mapContext.CurrentMap)}");
        foreach (var data in _store.Values)
        {
            SendColoredChat(player, $" - [lime]{data.Name}[default]: \"{data.Text}\" [{(data.Enabled ? "ON" : "OFF")}] (Color: {(data.Rainbow ? "RAINBOW" : data.ColorHex)})");
        }
    }

    private bool HasPermission(IPlayer player)
    {
        if (string.IsNullOrWhiteSpace(_config.Permission))
        {
            return true;
        }
        return _core.Permission.PlayerHasPermission(player.SteamID, _config.Permission);
    }

    private static void SendColoredChat(IPlayer player, string message)
    {
        player.SendChat(WallTextPlaceholders.NormalizeChatColors(message));
    }

    private string T(string key, params object[] args)
    {
        string? template = null;

        if (_config.Messages.TryGetValue(_config.Language, out var langDict) && langDict.TryGetValue(key, out var value))
        {
            template = value;
        }
        else if (_config.Messages.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var fallback))
        {
            template = fallback;
        }

        template ??= key;
        return args.Length > 0 ? string.Format(template, args) : template;
    }

    private string Prefix() => T("chat.prefix");

    // ------------------------------------------------------------------
    // Menus
    // ------------------------------------------------------------------

    private void ShowMenu(IPlayer player, IMenuAPI menu, string contextName, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        // Menus are rebuilt after every click (to refresh labels), which resets the cursor to the top.
        // Remember the highlighted index per (menu, text) and restore it so the admin stays on the same option.
        string key = $"{caller}:{contextName}";
        int slot = player.Slot;
        if (!_menuCursorBySlot.TryGetValue(slot, out var cursors))
        {
            cursors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _menuCursorBySlot[slot] = cursors;
        }

        try
        {
            var current = _core.MenusAPI.GetCurrentMenu(player);
            if (current != null
                && string.Equals(current.Tag as string, MenuTag, StringComparison.Ordinal)
                && _activeMenuKeyBySlot.TryGetValue(slot, out var currentKey))
            {
                cursors[currentKey] = current.GetCurrentOptionIndex(player);
            }
        }
        catch
        {
            // Menu may already be closing
        }

        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
        _activeMenuKeyBySlot[slot] = key;

        if (cursors.TryGetValue(key, out var index) && index > 0)
        {
            try
            {
                menu.MoveToOptionIndex(player, index);
            }
            catch
            {
                // Option count may have shrunk - keep default cursor
            }
        }
    }

    private void CloseMenu(IPlayer player)
    {
        var current = _core.MenusAPI.GetCurrentMenu(player);
        if (current != null)
        {
            _core.MenusAPI.CloseMenuForPlayer(player, current);
        }
    }

    private void CloseAllMenus()
    {
        try
        {
            foreach (var player in _core.PlayerManager.GetAllPlayers())
            {
                if (player != null && player.IsValid && !player.IsFakeClient)
                {
                    var current = _core.MenusAPI.GetCurrentMenu(player);
                    if (current != null && string.Equals(current.Tag as string, MenuTag, StringComparison.Ordinal))
                    {
                        _core.MenusAPI.CloseMenuForPlayer(player, current);
                    }
                }
            }
        }
        catch
        {
            // Ignore during shutdown
        }
    }

    private string GenerateUniqueDefaultName()
    {
        int idx = _store.Count + 1;
        string candidate = $"Text_{idx}";
        while (FindKey(candidate) != null)
        {
            idx++;
            candidate = $"Text_{idx}";
        }
        return candidate;
    }

    private void OpenMainMenu(IPlayer player)
    {
        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.main"));

        var createBtn = new ButtonMenuOption(T("menu.option.create"), 1000);
        createBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginCreate(player, GenerateUniqueDefaultName()));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(createBtn);

        var manageBtn = new ButtonMenuOption(T("menu.option.manage"), 1000);
        manageBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenListMenu(player));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(manageBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, "");
    }

    private void OpenListMenu(IPlayer player)
    {
        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.list"));

        if (_store.Count == 0)
        {
            builder.AddOption(new TextMenuOption(T("menu.option.no_items"), 1000));
        }
        else
        {
            foreach (var name in new List<string>(_store.Keys))
            {
                var capturedName = name;
                var data = _store[name];
                string preview = data.Text.Replace("\n", " ");
                if (preview.Length > 20) preview = preview[..17] + "...";
                var label = $"{data.Name}: \"{preview}\" [{(data.Enabled ? "ON" : "OFF")}]";
                var btn = new ButtonMenuOption(label, 1000);
                btn.Click += (sender, args) =>
                {
                    _core.Scheduler.NextTick(() => OpenDetailMenu(player, capturedName));
                    return ValueTask.CompletedTask;
                };
                builder.AddOption(btn);
            }
        }

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenMainMenu(player));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, "");
    }

    private void OpenDetailMenu(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.detail", $"{data.Name} [P:{data.Angles[0]:0}° Y:{data.Angles[1]:0}° R:{data.Angles[2]:0}°]"));

        // 1. Enabled Toggle
        var enabledToggle = new ToggleMenuOption(T("menu.option.enabled"), data.Enabled);
        enabledToggle.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Enabled = e.NewValue;
                Save();
                if (data.Enabled)
                {
                    SpawnEntity(data);
                }
                else
                {
                    DespawnEntity(data.Name);
                }
            });
        };
        builder.AddOption(enabledToggle);

        // 2. Move To Crosshair (Instant)
        var crosshairBtn = new ButtonMenuOption(T("menu.option.move_to_crosshair"), 1000);
        crosshairBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => MoveToCrosshair(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(crosshairBtn);

        // 3. Orientation & Quick Align Sub-Menu
        var orientMenuBtn = new ButtonMenuOption(T("menu.option.orientation_menu"), 1000);
        orientMenuBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenOrientationMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(orientMenuBtn);

        // 4. Fine-Tune Position & Angles Sub-Menu
        var fineTuneBtn = new ButtonMenuOption(T("menu.option.fine_tune"), 1000);
        fineTuneBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenFineTuneMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(fineTuneBtn);

        // 5. Change Text
        var changeTextBtn = new ButtonMenuOption(T("menu.option.change_text"), 1000);
        changeTextBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                SetPendingChat(player, ChatPromptKind.ChangeText, data.Name);
                SendColoredChat(player, $"{Prefix()} {T("chat.enter_text")}");
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(changeTextBtn);

        // 6. Color Sub-Menu
        var colorBtn = new ButtonMenuOption(T("menu.option.color_menu", data.ColorHex), 1000);
        colorBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenColorMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(colorBtn);

        // 7. Rainbow (RGB) Toggle & Speed
        var rainbowToggle = new ToggleMenuOption(T("menu.option.rainbow", data.Rainbow ? "ON" : "OFF"), data.Rainbow);
        rainbowToggle.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Rainbow = e.NewValue;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
        };
        builder.AddOption(rainbowToggle);

        if (data.Rainbow)
        {
            var speedBtn = new ButtonMenuOption(T("menu.option.rainbow_speed", data.RainbowSpeed), 1000);
            speedBtn.Click += (sender, args) =>
            {
                _core.Scheduler.NextTick(() =>
                {
                    data.RainbowSpeed = data.RainbowSpeed switch
                    {
                        <= 0.5f => 1.0f,
                        <= 1.0f => 2.0f,
                        <= 2.0f => 0.5f,
                        _ => 1.0f
                    };
                    Save();
                    OpenDetailMenu(player, data.Name);
                });
                return ValueTask.CompletedTask;
            };
            builder.AddOption(speedBtn);
        }

        // 8. Rotating Announcer Board Sub-Menu
        var rotMenuBtn = new ButtonMenuOption(T("menu.option.rotating_menu", data.RotatingMessages.Count), 1000);
        rotMenuBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenRotatingMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rotMenuBtn);

        // 9. Interactive Trigger Sub-Menu
        var interactMenuBtn = new ButtonMenuOption(T("menu.option.interact_menu"), 1000);
        interactMenuBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenInteractMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(interactMenuBtn);

        // 10. Sliders & Styling: Font, Font Size, Scale, Background, Fullbright, FacePlayer, Alignment
        string fontLabel = string.IsNullOrWhiteSpace(data.FontName) ? "Default" : data.FontName;
        var fontBtn = new ButtonMenuOption(T("menu.option.font", fontLabel), 1000);
        fontBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                int curIdx = Array.IndexOf(AvailableFonts, data.FontName ?? "");
                int nextIdx = (curIdx + 1) % AvailableFonts.Length;
                data.FontName = AvailableFonts[nextIdx];
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(fontBtn);

        var fontSizeSlider = new SliderMenuOption(T("menu.option.font_size"), 8f, 72f, data.FontSize, 1f, 10, 120, 1000);
        fontSizeSlider.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.FontSize = e.NewValue;
                Save();
                UpdateLiveEntity(data);
            });
        };
        builder.AddOption(fontSizeSlider);

        var scaleSlider = new SliderMenuOption(T("menu.option.scale"), 0.05f, 2f, data.Scale, 0.05f, 10, 120, 1000);
        scaleSlider.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Scale = e.NewValue;
                Save();
                UpdateLiveEntity(data);
            });
        };
        builder.AddOption(scaleSlider);

        string justifyLabel = data.JustifyHorizontal switch
        {
            0 => T("menu.option.justify_left"),
            2 => T("menu.option.justify_right"),
            _ => T("menu.option.justify_center")
        };
        var justifyBtn = new ButtonMenuOption(T("menu.option.justify", justifyLabel), 1000);
        justifyBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.JustifyHorizontal = (data.JustifyHorizontal + 1) % 3;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(justifyBtn);

        string justifyVertLabel = data.JustifyVertical switch
        {
            0 => T("menu.option.justify_bottom"),
            2 => T("menu.option.justify_top"),
            _ => T("menu.option.justify_center")
        };
        var justifyVertBtn = new ButtonMenuOption(T("menu.option.justify_vert", justifyVertLabel), 1000);
        justifyVertBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.JustifyVertical = (data.JustifyVertical + 1) % 3;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(justifyVertBtn);

        var facePlayerToggle = new ToggleMenuOption(T("menu.option.face_player"), data.FacePlayer);
        facePlayerToggle.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.FacePlayer = e.NewValue;
                Save();
                UpdateLiveEntity(data);
            });
        };
        builder.AddOption(facePlayerToggle);

        var fullbrightToggle = new ToggleMenuOption(T("menu.option.fullbright"), data.Fullbright);
        fullbrightToggle.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Fullbright = e.NewValue;
                Save();
                UpdateLiveEntity(data);
            });
        };
        builder.AddOption(fullbrightToggle);

        var backgroundToggle = new ToggleMenuOption(T("menu.option.background"), data.Background);
        backgroundToggle.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Background = e.NewValue;
                Save();
                UpdateLiveEntity(data);
            });
        };
        builder.AddOption(backgroundToggle);

        // 11. Teleport, Re-aim, Duplicate, Rename, Delete
        var tpBtn = new ButtonMenuOption(T("menu.option.teleport"), 1000);
        tpBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => TeleportToText(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(tpBtn);

        var reaimBtn = new ButtonMenuOption(T("menu.option.reaim"), 1000);
        reaimBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginReaim(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(reaimBtn);

        var dupBtn = new ButtonMenuOption(T("menu.option.duplicate"), 1000);
        dupBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginDuplicate(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(dupBtn);

        var renameBtn = new ButtonMenuOption(T("menu.option.rename"), 1000);
        renameBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginRename(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(renameBtn);

        var deleteBtn = new ButtonMenuOption(T("menu.option.delete"), 1000);
        deleteBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                DespawnEntity(data.Name);
                _store.Remove(data.Name);
                Save();
                SendColoredChat(player, $"{Prefix()} {T("chat.deleted", data.Name)}");
                OpenListMenu(player);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(deleteBtn);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenListMenu(player));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, name);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Orientation & Quick Alignment Tools
    // ------------------------------------------------------------------

    private void OpenOrientationMenu(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.orientation", $"{data.Name} [P:{data.Angles[0]:0}° Y:{data.Angles[1]:0}° R:{data.Angles[2]:0}°]"));

        // Move to crosshair
        var crosshairBtn = new ButtonMenuOption(T("menu.option.move_to_crosshair"), 1000);
        crosshairBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => MoveToCrosshair(player, data.Name, returnToOrientationMenu: true));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(crosshairBtn);

        // Face towards admin (Horizontal Yaw only)
        var faceAdminBtn = new ButtonMenuOption(T("menu.option.face_admin"), 1000);
        faceAdminBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                if (player.PlayerPawn != null && player.PlayerPawn.AbsOrigin.HasValue)
                {
                    var pPos = player.PlayerPawn.AbsOrigin.Value;
                    float dx = pPos.X - data.Position[0];
                    float dy = pPos.Y - data.Position[1];
                    var (fp, fy, fr) = AnglesFacingDirection(dx, dy, 0f, keepUpright: true);
                    data.Angles[0] = fp;
                    data.Angles[1] = fy;
                    data.Angles[2] = fr;
                    Save();
                    UpdateLiveEntity(data);
                }
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(faceAdminBtn);

        // Face towards admin's eyes (Full 3D Pitch + Yaw)
        var faceAdmin3dBtn = new ButtonMenuOption(T("menu.option.face_admin_3d"), 1000);
        faceAdmin3dBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                if (player.PlayerPawn != null && TryGetEyePosition(player.PlayerPawn, out var eyePos))
                {
                    float dx = eyePos.X - data.Position[0];
                    float dy = eyePos.Y - data.Position[1];
                    float dz = eyePos.Z - data.Position[2];
                    var (fp, fy, fr) = AnglesFacingDirection(dx, dy, dz, keepUpright: false);
                    data.Angles[0] = fp;
                    data.Angles[1] = fy;
                    data.Angles[2] = fr;
                    Save();
                    UpdateLiveEntity(data);
                }
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(faceAdmin3dBtn);

        // Flip 180 deg
        var flip180Btn = new ButtonMenuOption(T("menu.option.flip_180"), 1000);
        flip180Btn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = NormalizeAngle(data.Angles[1] + 180f);
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(flip180Btn);

        // Rotate Yaw +90 deg
        var rot90Btn = new ButtonMenuOption(T("menu.option.rotate_90"), 1000);
        rot90Btn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = NormalizeAngle(data.Angles[1] + 90f);
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rot90Btn);

        // Pitch +90 deg
        var pitchUpBtn = new ButtonMenuOption(T("menu.option.pitch_up"), 1000);
        pitchUpBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = NormalizeAngle(data.Angles[0] + 90f);
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchUpBtn);

        // Pitch -90 deg
        var pitchDownBtn = new ButtonMenuOption(T("menu.option.pitch_down"), 1000);
        pitchDownBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = NormalizeAngle(data.Angles[0] - 90f);
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchDownBtn);

        // Roll +90 deg
        var rollBtn = new ButtonMenuOption(T("menu.option.roll_rotate"), 1000);
        rollBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[2] = NormalizeAngle(data.Angles[2] + 90f);
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rollBtn);

        // Presets
        var presetVertBtn = new ButtonMenuOption(T("menu.option.preset_vertical"), 1000);
        presetVertBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = 90f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(presetVertBtn);

        var presetVertNegBtn = new ButtonMenuOption(T("menu.option.preset_vertical_neg"), 1000);
        presetVertNegBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = -90f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(presetVertNegBtn);

        var presetWallRoll90Btn = new ButtonMenuOption(T("menu.option.preset_wall_roll90"), 1000);
        presetWallRoll90Btn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = 0f;
                data.Angles[2] = 90f;
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(presetWallRoll90Btn);

        var presetFlatBtn = new ButtonMenuOption(T("menu.option.preset_flat"), 1000);
        presetFlatBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = 0f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                OpenOrientationMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(presetFlatBtn);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, name);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Rotating Announcer Board
    // ------------------------------------------------------------------

    private void OpenRotatingMenu(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.rotating", data.Name));

        // Toggle rotation
        var toggle = new ToggleMenuOption(T("menu.option.rotating_toggle"), data.IsRotating);
        toggle.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.IsRotating = e.NewValue;
                Save();
                UpdateLiveEntity(data);
                OpenRotatingMenu(player, data.Name);
            });
        };
        builder.AddOption(toggle);

        // Interval button: 5s -> 10s -> 20s -> 30s -> 60s
        var intervalBtn = new ButtonMenuOption(T("menu.option.rotating_interval", (int)data.RotationIntervalSeconds), 1000);
        intervalBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.RotationIntervalSeconds = data.RotationIntervalSeconds switch
                {
                    <= 5f => 10f,
                    <= 10f => 20f,
                    <= 20f => 30f,
                    <= 30f => 60f,
                    _ => 5f
                };
                Save();
                OpenRotatingMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(intervalBtn);

        // Add new message button
        var addBtn = new ButtonMenuOption(T("menu.option.add_rotating_msg"), 1000);
        addBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                SetPendingChat(player, ChatPromptKind.AddRotatingMessage, data.Name);
                SendColoredChat(player, $"{Prefix()} {T("chat.enter_rotating_message")}");
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(addBtn);

        // Clear all messages
        if (data.RotatingMessages.Count > 0)
        {
            var clearBtn = new ButtonMenuOption(T("menu.option.clear_rotating_msgs"), 1000);
            clearBtn.Click += (sender, args) =>
            {
                _core.Scheduler.NextTick(() =>
                {
                    data.RotatingMessages.Clear();
                    data.IsRotating = false;
                    Save();
                    UpdateLiveEntity(data);
                    SendColoredChat(player, $"{Prefix()} {T("chat.rotating_messages_cleared", data.Name)}");
                    OpenRotatingMenu(player, data.Name);
                });
                return ValueTask.CompletedTask;
            };
            builder.AddOption(clearBtn);

            // List of messages
            for (int i = 0; i < data.RotatingMessages.Count; i++)
            {
                int index = i;
                string preview = data.RotatingMessages[index].Replace("\n", " ");
                if (preview.Length > 28) preview = preview[..25] + "...";
                var itemBtn = new ButtonMenuOption($"#{index + 1}: {preview} [X]", 1000);
                itemBtn.Click += (sender, args) =>
                {
                    _core.Scheduler.NextTick(() =>
                    {
                        if (index >= 0 && index < data.RotatingMessages.Count)
                        {
                            data.RotatingMessages.RemoveAt(index);
                        }
                        if (data.RotatingMessages.Count == 0) data.IsRotating = false;
                        Save();
                        UpdateLiveEntity(data);
                        OpenRotatingMenu(player, data.Name);
                    });
                    return ValueTask.CompletedTask;
                };
                builder.AddOption(itemBtn);
            }
        }

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, name);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Interactive Trigger
    // ------------------------------------------------------------------

    private void OpenInteractMenu(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.interact", data.Name));

        // Toggle interactable
        var toggle = new ToggleMenuOption(T("menu.option.interact_toggle"), data.IsInteractable);
        toggle.ValueChanged += (sender, e) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.IsInteractable = e.NewValue;
                Save();
                OpenInteractMenu(player, data.Name);
            });
        };
        builder.AddOption(toggle);

        // Trigger type button (Both -> Use -> Shoot)
        string trigLabel = data.TriggerType.ToLowerInvariant() switch
        {
            "use" => T("trigger.type.use"),
            "shoot" => T("trigger.type.shoot"),
            _ => T("trigger.type.both")
        };
        var typeBtn = new ButtonMenuOption(T("menu.option.interact_trigger_type", trigLabel), 1000);
        typeBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.TriggerType = data.TriggerType.ToLowerInvariant() switch
                {
                    "both" => "use",
                    "use" => "shoot",
                    _ => "both"
                };
                Save();
                OpenInteractMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(typeBtn);

        // Set message to player
        string curMsg = string.IsNullOrWhiteSpace(data.MessageToPlayer) ? "Yok" : data.MessageToPlayer.Replace("\n", " ");
        if (curMsg.Length > 20) curMsg = curMsg[..17] + "...";
        var setMsgBtn = new ButtonMenuOption($"{T("menu.option.set_interact_msg")} [{curMsg}]", 1000);
        setMsgBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                SetPendingChat(player, ChatPromptKind.InteractMessage, data.Name);
                SendColoredChat(player, $"{Prefix()} {T("chat.enter_interact_msg")}");
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(setMsgBtn);

        // Set command to run
        string curCmd = string.IsNullOrWhiteSpace(data.CommandToRun) ? "Yok" : data.CommandToRun;
        if (curCmd.Length > 20) curCmd = curCmd[..17] + "...";
        var setCmdBtn = new ButtonMenuOption($"{T("menu.option.set_interact_cmd")} [{curCmd}]", 1000);
        setCmdBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                SetPendingChat(player, ChatPromptKind.InteractCommand, data.Name);
                SendColoredChat(player, $"{Prefix()} {T("chat.enter_interact_cmd")}");
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(setCmdBtn);

        // Max interaction distance: 150 -> 250 -> 400 -> 600
        var distBtn = new ButtonMenuOption(T("menu.option.interact_dist", (int)data.InteractionDistance), 1000);
        distBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.InteractionDistance = data.InteractionDistance switch
                {
                    <= 150f => 250f,
                    <= 250f => 400f,
                    <= 400f => 600f,
                    _ => 150f
                };
                Save();
                OpenInteractMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(distBtn);

        // Cooldown: 0.5s -> 1s -> 2s -> 5s -> 10s
        var cooldownBtn = new ButtonMenuOption(T("menu.option.interact_cooldown", data.CooldownSeconds), 1000);
        cooldownBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.CooldownSeconds = data.CooldownSeconds switch
                {
                    <= 0.5f => 1.0f,
                    <= 1.0f => 2.0f,
                    <= 2.0f => 5.0f,
                    <= 5.0f => 10.0f,
                    _ => 0.5f
                };
                Save();
                OpenInteractMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(cooldownBtn);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, name);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Color Selection
    // ------------------------------------------------------------------

    private void OpenColorMenu(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.color", data.ColorHex));

        var presets = new (string Key, string Hex)[]
        {
            ("menu.color.white", "#FFFFFF"),
            ("menu.color.red", "#FF3333"),
            ("menu.color.green", "#33FF33"),
            ("menu.color.blue", "#3388FF"),
            ("menu.color.yellow", "#FFFF33"),
            ("menu.color.cyan", "#00FFFF"),
            ("menu.color.orange", "#FFA500"),
            ("menu.color.magenta", "#DA70D6"),
            ("menu.color.gold", "#FFD700"),
            ("menu.color.pink", "#FF69B4"),
            ("menu.color.lime", "#32CD32"),
            ("menu.color.grey", "#888888"),
        };

        foreach (var (presetKey, hex) in presets)
        {
            var btn = new ButtonMenuOption(T(presetKey), 1000);
            var capturedHex = hex;
            btn.Click += (sender, args) =>
            {
                _core.Scheduler.NextTick(() =>
                {
                    data.ColorHex = capturedHex;
                    data.Rainbow = false; // Disable rainbow when selecting static color
                    Save();
                    UpdateLiveEntity(data);
                    SendColoredChat(player, $"{Prefix()} {T("chat.color_changed", capturedHex)}");
                    OpenColorMenu(player, data.Name);
                });
                return ValueTask.CompletedTask;
            };
            builder.AddOption(btn);
        }

        var customHexBtn = new ButtonMenuOption(T("menu.color.custom"), 1000);
        customHexBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                SetPendingChat(player, ChatPromptKind.ColorHex, data.Name);
                SendColoredChat(player, $"{Prefix()} {T("chat.enter_color_hex")}");
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(customHexBtn);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, name);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Fine-Tune Position & Angles
    // ------------------------------------------------------------------

    private void OpenFineTuneMenu(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        if (!_playerStepSizes.TryGetValue(player.Slot, out var step))
        {
            step = 5f;
            _playerStepSizes[player.Slot] = step;
        }

        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.fine_tune", data.Name));

        var stepBtn = new ButtonMenuOption(T("menu.option.step_size", step), 1000);
        stepBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                int curIdx = Array.IndexOf(AvailableSteps, step);
                int nextIdx = (curIdx + 1) % AvailableSteps.Length;
                _playerStepSizes[player.Slot] = AvailableSteps[nextIdx];
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(stepBtn);

        // Wall-relative sliding (Right / Left / Out / In based on current Yaw)
        var wallRight = new ButtonMenuOption(T("menu.option.nudge_wall_right", step), 1000);
        wallRight.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                // Viewer's right when facing the text is (-sin(yaw), cos(yaw))
                data.Position[0] += -MathF.Sin(yawRad) * step;
                data.Position[1] += MathF.Cos(yawRad) * step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(wallRight);

        var wallLeft = new ButtonMenuOption(T("menu.option.nudge_wall_left", step), 1000);
        wallLeft.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                data.Position[0] -= -MathF.Sin(yawRad) * step;
                data.Position[1] -= MathF.Cos(yawRad) * step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(wallLeft);

        var zUp = new ButtonMenuOption(T("menu.option.nudge_z_up", step), 1000);
        zUp.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[2] += step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(zUp);

        var zDown = new ButtonMenuOption(T("menu.option.nudge_z_down", step), 1000);
        zDown.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[2] -= step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(zDown);

        var wallOut = new ButtonMenuOption(T("menu.option.nudge_wall_out", step), 1000);
        wallOut.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                data.Position[0] += MathF.Cos(yawRad) * step;
                data.Position[1] += MathF.Sin(yawRad) * step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(wallOut);

        var wallIn = new ButtonMenuOption(T("menu.option.nudge_wall_in", step), 1000);
        wallIn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                data.Position[0] -= MathF.Cos(yawRad) * step;
                data.Position[1] -= MathF.Sin(yawRad) * step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(wallIn);

        // World-axis X / Y nudges
        var xPos = new ButtonMenuOption(T("menu.option.nudge_x_pos", step), 1000);
        xPos.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[0] += step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(xPos);

        var xNeg = new ButtonMenuOption(T("menu.option.nudge_x_neg", step), 1000);
        xNeg.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[0] -= step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(xNeg);

        var yPos = new ButtonMenuOption(T("menu.option.nudge_y_pos", step), 1000);
        yPos.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[1] += step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(yPos);

        var yNeg = new ButtonMenuOption(T("menu.option.nudge_y_neg", step), 1000);
        yNeg.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[1] -= step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(yNeg);

        // Pitch (Up / Down tilt)
        var pitchUp = new ButtonMenuOption(T("menu.option.nudge_pitch_up", step), 1000);
        pitchUp.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = NormalizeAngle(data.Angles[0] + step);
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchUp);

        var pitchDown = new ButtonMenuOption(T("menu.option.nudge_pitch_down", step), 1000);
        pitchDown.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = NormalizeAngle(data.Angles[0] - step);
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchDown);

        // Yaw (Horizontal rotation)
        var yawLeft = new ButtonMenuOption(T("menu.option.nudge_yaw_left", step), 1000);
        yawLeft.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = NormalizeAngle(data.Angles[1] - step);
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(yawLeft);

        var yawRight = new ButtonMenuOption(T("menu.option.nudge_yaw_right", step), 1000);
        yawRight.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = NormalizeAngle(data.Angles[1] + step);
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(yawRight);

        // Roll (Sideways banking)
        var rollRight = new ButtonMenuOption(T("menu.option.nudge_roll_pos", step), 1000);
        rollRight.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[2] = NormalizeAngle(data.Angles[2] + step);
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rollRight);

        var rollLeft = new ButtonMenuOption(T("menu.option.nudge_roll_neg", step), 1000);
        rollLeft.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[2] = NormalizeAngle(data.Angles[2] - step);
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rollLeft);

        var straighten = new ButtonMenuOption(T("menu.option.straighten"), 1000);
        straighten.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                // Snap to the nearest clean orientation: floor (roll 0), wall upright (roll 90) or ceiling (roll 180)
                float absRoll = MathF.Abs(data.Angles[2]);
                data.Angles[0] = 0f;
                data.Angles[2] = absRoll < 45f ? 0f : absRoll > 135f ? 180f : 90f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(straighten);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        ShowMenu(player, menu, name);
    }
}
