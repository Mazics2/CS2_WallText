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
/// - Rotating Text Announcer (cycles multiple messages on a wall every 20-30s).
/// - Interactable Text Buttons (+USE / Shoot triggers with sound and commands).
/// - Instant Crosshair positioning and quick orientation tools (90°, 180°, Face Admin).
/// </summary>
public class WallTextModule
{
    private const string MenuTag = "CS2WallText.Menu";
    private const float CaptureDebounceSeconds = 0.15f;
    private const float WallOffset = 1.0f;

    private readonly ISwiftlyCore _core;
    private readonly MapContext _mapContext;

    private WallTextConfig _config = new();
    private string _configPath = "";
    private Guid _commandGuid;
    private Guid _chatHookGuid;
    private Guid _roundStartHookGuid;

    private readonly Dictionary<string, WallTextData> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CPointWorldText> _liveEntities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dynamicTextRenderCache = new(StringComparer.OrdinalIgnoreCase);

    // Pending chat inputs per player SteamID
    private readonly Dictionary<ulong, string> _pendingRename = new();
    private readonly Dictionary<ulong, string> _pendingTextChange = new();
    private readonly Dictionary<ulong, PendingCreation> _pendingNewText = new();
    private readonly Dictionary<ulong, string> _pendingColorChange = new();
    private readonly Dictionary<ulong, string> _pendingAddRotatingMsg = new();
    private readonly Dictionary<ulong, string> _pendingInteractMsg = new();
    private readonly Dictionary<ulong, string> _pendingInteractCmd = new();

    // Player menu preferences
    private readonly Dictionary<ulong, float> _playerStepSizes = new();
    private static readonly float[] AvailableSteps = [1f, 5f, 15f, 45f, 90f];

    // Rotating announcer runtime state: name -> (index, lastSwitchTime)
    private readonly Dictionary<string, (int Index, float LastSwitchTime)> _rotatingState = new(StringComparer.OrdinalIgnoreCase);

    // Player buttons and interaction cooldowns
    private readonly Dictionary<int, GameButtonFlags> _lastPlayerButtons = new();
    private readonly Dictionary<ulong, float> _playerInteractionCooldowns = new();

    private float _lastDynamicUpdateTime;
    private float _lastRainbowUpdateTime;

    private sealed class PendingCreation
    {
        public string Name = "";
        public Vector Position;
        public QAngle Angles;
    }

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

        _mapContext.MapChanged += ReloadForCurrentMap;
        ReloadForCurrentMap();
    }

    public void Unload()
    {
        if (!_config.Enabled)
        {
            return;
        }

        _core.Command.UnregisterCommand(_commandGuid);
        _core.Command.UnhookClientChat(_chatHookGuid);
        _core.GameEvent.Unhook(_roundStartHookGuid);

        _core.Event.OnTick -= OnTick;
        _core.Event.OnClientDisconnected -= OnClientDisconnected;
        _mapContext.MapChanged -= ReloadForCurrentMap;

        ClearAllEntities();
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
    // Math & Angle Helpers (Fixing tilted/horizontal text)
    // ------------------------------------------------------------------

    /// <summary>
    /// Calculates a perfectly upright facing angle for a wall text.
    /// If placed on a vertical or near-vertical wall, locks Pitch and Roll to 0
    /// so the text is guaranteed to stand upright and never lie horizontal.
    /// </summary>
    private static QAngle CalculateWallAngles(Vector normal, QAngle playerEyeAngles)
    {
        // If vertical or slanted wall surface (majority of cases)
        if (MathF.Abs(normal.Z) < 0.7f)
        {
            // Yaw pointing outward perpendicular from the wall surface
            float yaw = MathF.Atan2(normal.Y, normal.X) * (180f / MathF.PI);
            return new QAngle(0f, yaw, 0f);
        }

        // Flat floor (Z > 0) or ceiling (Z < 0)
        float pitch = normal.Z > 0 ? -90f : 90f;
        return new QAngle(pitch, playerEyeAngles.Yaw, 0f);
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
        return hex.StartsWith('#') ? hex : "#" + hex;
    }

    private static bool IsValidHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var clean = hex.Trim().TrimStart('#');
        return clean.Length == 6 && Regex.IsMatch(clean, "^[0-9A-Fa-f]{6}$");
    }

    private static Color ColorFromHsv(float hue, float saturation, float value)
    {
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

            string initialText = data.Text;
            if (data.IsRotating && data.RotatingMessages.Count > 0)
            {
                initialText = data.RotatingMessages[0];
                _rotatingState[data.Name] = (0, _core.Engine.GlobalVars.CurrentTime);
            }

            var formattedText = WallTextPlaceholders.Format(initialText, _core, _mapContext.CurrentMap);
            entity.MessageText = formattedText;
            entity.Color = Color.FromHex(NormalizeHex(data.ColorHex));
            entity.FontSize = data.FontSize;
            entity.WorldUnitsPerPx = data.Scale;
            entity.Fullbright = data.Fullbright;
            entity.Enabled = true;
            entity.DrawBackground = data.Background;
            entity.JustifyHorizontal = (PointWorldTextJustifyHorizontal_t)Math.Clamp(data.JustifyHorizontal, 0, 2);
            entity.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
            entity.ReorientMode = data.FacePlayer
                ? PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_AROUND_UP
                : PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            entity.DepthOffset = data.DepthOffset;

            entity.DispatchSpawn();

            var pos = new Vector(data.Position[0], data.Position[1], data.Position[2]);
            var ang = new QAngle(data.Angles[0], data.Angles[1], data.Angles[2]);
            entity.Teleport(pos, ang, new Vector(0f, 0f, 0f));

            _liveEntities[data.Name] = entity;
            _dynamicTextRenderCache[data.Name] = formattedText;
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Entity olusturulurken hata: {ex.Message}");
        }
    }

    private void DespawnEntity(string name)
    {
        _dynamicTextRenderCache.Remove(name);
        _rotatingState.Remove(name);
        if (_liveEntities.Remove(name, out var entity))
        {
            try
            {
                if (((CEntityInstance)entity).IsValid)
                {
                    entity.Despawn();
                }
            }
            catch
            {
                // Entity already cleaned up
            }
        }
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
        if (!_liveEntities.TryGetValue(data.Name, out var entity) || entity == null || !((CEntityInstance)entity).IsValid)
        {
            if (data.Enabled)
            {
                SpawnEntity(data);
            }
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

            string textToShow = data.Text;
            if (data.IsRotating && data.RotatingMessages.Count > 0)
            {
                if (!_rotatingState.TryGetValue(data.Name, out var rState) || rState.Index >= data.RotatingMessages.Count)
                {
                    rState = (0, _core.Engine.GlobalVars.CurrentTime);
                    _rotatingState[data.Name] = rState;
                }
                textToShow = data.RotatingMessages[rState.Index];
            }

            var formattedText = WallTextPlaceholders.Format(textToShow, _core, _mapContext.CurrentMap);
            entity.MessageText = formattedText;
            entity.MessageTextUpdated();
            _dynamicTextRenderCache[data.Name] = formattedText;

            entity.FontSize = data.FontSize;
            entity.FontSizeUpdated();
            entity.WorldUnitsPerPx = data.Scale;
            entity.WorldUnitsPerPxUpdated();
            if (!data.Rainbow)
            {
                entity.Color = Color.FromHex(NormalizeHex(data.ColorHex));
                entity.ColorUpdated();
            }
            entity.DrawBackground = data.Background;
            entity.DrawBackgroundUpdated();
            entity.Fullbright = data.Fullbright;
            entity.FullbrightUpdated();
            entity.JustifyHorizontal = (PointWorldTextJustifyHorizontal_t)Math.Clamp(data.JustifyHorizontal, 0, 2);
            entity.JustifyHorizontalUpdated();
            entity.ReorientMode = data.FacePlayer
                ? PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_AROUND_UP
                : PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            entity.ReorientModeUpdated();
            entity.DepthOffset = data.DepthOffset;
            entity.DepthOffsetUpdated();
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[CS2_WallText] Entity guncellenirken hata: {ex.Message}");
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event)
    {
        _core.Scheduler.NextTick(RecheckLiveEntities);
        return HookResult.Continue;
    }

    private void RecheckLiveEntities()
    {
        foreach (var data in _store.Values)
        {
            if (!data.Enabled) continue;

            if (!_liveEntities.TryGetValue(data.Name, out var entity) || entity == null || !((CEntityInstance)entity).IsValid)
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
        var now = _core.Engine.GlobalVars.CurrentTime;

        UpdateRainbowColors(now);
        UpdateRotatingAnnouncers(now);
        UpdateDynamicPlaceholders(now);
        ProcessAimCaptures(now);
        CheckPlayerInteractions(now);
    }

    private void UpdateRainbowColors(float now)
    {
        // 10 Hz update rate for silky smooth RGB cycle without flooding network
        if (now - _lastRainbowUpdateTime < 0.1f) return;
        _lastRainbowUpdateTime = now;

        foreach (var data in _store.Values)
        {
            if (!data.Enabled || !data.Rainbow) continue;
            if (!_liveEntities.TryGetValue(data.Name, out var entity) || entity == null || !((CEntityInstance)entity).IsValid) continue;

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
            if (!_liveEntities.TryGetValue(data.Name, out var entity) || entity == null || !((CEntityInstance)entity).IsValid) continue;

            if (!_rotatingState.TryGetValue(data.Name, out var state))
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
        if (now - _lastDynamicUpdateTime < _config.DynamicUpdateIntervalSeconds) return;
        _lastDynamicUpdateTime = now;

        foreach (var data in _store.Values)
        {
            if (!data.Enabled || data.IsRotating) continue;
            if (!WallTextPlaceholders.HasPlaceholders(data.Text)) continue;
            if (!_liveEntities.TryGetValue(data.Name, out var entity) || entity == null || !((CEntityInstance)entity).IsValid) continue;

            var rendered = WallTextPlaceholders.Format(data.Text, _core, _mapContext.CurrentMap);
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
            if (pawn == null || pawn.Health <= 0) continue;

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

            foreach (var data in _store.Values)
            {
                if (!data.Enabled || !data.IsInteractable) continue;

                // Match trigger filter
                if (data.TriggerType.Equals("use", StringComparison.OrdinalIgnoreCase) && !useRising) continue;
                if (data.TriggerType.Equals("shoot", StringComparison.OrdinalIgnoreCase) && !shootRising) continue;

                var textPos = new Vector(data.Position[0], data.Position[1], data.Position[2]);

                // Check player distance to text
                float distToPlayer = VectorDistance(playerPos, textPos);
                if (distToPlayer > data.InteractionDistance) continue;

                // Check hit radius on the wall text surface
                float hitRadius = MathF.Max(60f, data.FontSize * data.Scale * 8f);
                float distToHit = VectorDistance(hitPoint, textPos);
                if (distToHit > hitRadius) continue;

                // Check anti-spam cooldown
                if (_playerInteractionCooldowns.TryGetValue(player.SteamID, out var lastTime) && (now - lastTime < data.CooldownSeconds))
                {
                    continue;
                }
                _playerInteractionCooldowns[player.SteamID] = now;

                // Fire tactile feedback sound to player
                player.ExecuteCommand("play sounds/ui/beepclear.vsnd");

                // Send chat message to player if configured
                if (!string.IsNullOrWhiteSpace(data.MessageToPlayer))
                {
                    var msg = WallTextPlaceholders.Format(data.MessageToPlayer, _core, _mapContext.CurrentMap, player);
                    player.SendChat($"{Prefix()} {msg}");
                }

                // Run configured command
                if (!string.IsNullOrWhiteSpace(data.CommandToRun))
                {
                    var cmd = WallTextPlaceholders.Format(data.CommandToRun, _core, _mapContext.CurrentMap, player);
                    if (cmd.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
                    {
                        _core.Engine.ExecuteCommand(cmd[7..].Trim());
                    }
                    else
                    {
                        player.ExecuteCommand(cmd);
                    }
                }

                break; // Trigger closest text
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
            if (pawn == null || pawn.Health <= 0) continue;

            var pressed = (player.PressedButtons & GameButtonFlags.Mouse1) != 0;
            var risingEdge = pressed && !session.LastMouse1Down;
            session.LastMouse1Down = pressed;

            if (!risingEdge) continue;
            if (now - session.LastCaptureTime < CaptureDebounceSeconds) continue;

            if (!TryTraceAimPoint(pawn, out var point, out var normal)) continue;

            session.LastCaptureTime = now;
            _captureSessions.Remove(slot);
            session.OnCaptured?.Invoke(player, point, normal);
        }
    }

    private void MoveToCrosshair(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
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

        player.SendChat($"{Prefix()} {T("chat.moved_to_crosshair", data.Name)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, data.Name));
    }

    private bool TryTraceAimPoint(CCSPlayerPawn pawn, out Vector point, out Vector normal)
    {
        point = default;
        normal = new Vector(0f, 0f, 1f);

        Vector start;
        if (pawn.EyePosition.HasValue)
        {
            start = pawn.EyePosition.Value;
        }
        else if (pawn.AbsOrigin.HasValue)
        {
            start = new Vector(pawn.AbsOrigin.Value.X, pawn.AbsOrigin.Value.Y, pawn.AbsOrigin.Value.Z + 64f);
        }
        else
        {
            return false;
        }

        var angles = pawn.EyeAngles;

        var trace = _core.Trace.TraceShapeAngle(start, angles, 8192f, new TraceParams
        {
            EntitiesToIgnore = [pawn]
        });

        if (!trace.DidHit)
        {
            return false;
        }

        point = trace.HitPoint;
        normal = trace.HitNormal;
        return true;
    }

    private void BeginPointCapture(IPlayer player, Action<IPlayer, Vector, Vector> onCaptured)
    {
        _captureSessions[player.Slot] = new CaptureSession
        {
            OnCaptured = onCaptured,
            LastMouse1Down = (player.PressedButtons & GameButtonFlags.Mouse1) != 0,
        };
    }

    private bool IsCapturing(int slot) => _captureSessions.ContainsKey(slot);

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var slot = @event.PlayerId;
        _captureSessions.Remove(slot);
        _lastPlayerButtons.Remove(slot);

        var player = _core.PlayerManager.GetPlayer(slot);
        if (player != null && player.IsValid)
        {
            _pendingNewText.Remove(player.SteamID);
            _pendingTextChange.Remove(player.SteamID);
            _pendingRename.Remove(player.SteamID);
            _pendingColorChange.Remove(player.SteamID);
            _pendingAddRotatingMsg.Remove(player.SteamID);
            _pendingInteractMsg.Remove(player.SteamID);
            _pendingInteractCmd.Remove(player.SteamID);
            _playerStepSizes.Remove(player.SteamID);
            _playerInteractionCooldowns.Remove(player.SteamID);
        }
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
            player.SendChat($"{Prefix()} {T("chat.no_permission")}");
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
                    player.SendChat($"{Prefix()} {T("chat.usage_add")}");
                    return;
                }
                BeginCreate(player, string.Join(" ", args, 1, args.Length - 1));
                break;
            }
            case "edit":
            {
                if (args.Length < 2)
                {
                    player.SendChat($"{Prefix()} {T("chat.usage_edit_cmd")}");
                    return;
                }
                BeginRename(player, string.Join(" ", args, 1, args.Length - 1));
                break;
            }
            case "delete":
            {
                if (args.Length < 2)
                {
                    player.SendChat($"{Prefix()} {T("chat.usage_delete")}");
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
                    player.SendChat($"{Prefix()} {T("chat.usage_tp")}");
                    return;
                }
                TeleportToText(player, string.Join(" ", args, 1, args.Length - 1));
                break;
            }
            case "reload":
            {
                LoadConfig();
                ReloadForCurrentMap();
                player.SendChat($"{Prefix()} {T("chat.reloaded", _mapContext.CurrentMap, _store.Count)}");
                break;
            }
            case "help":
                ShowHelp(player);
                break;
            default:
                player.SendChat($"{Prefix()} {T("chat.usage_general")}");
                break;
        }
    }

    private void ShowHelp(IPlayer player)
    {
        player.SendChat(T("chat.help_header"));
        player.SendChat(T("chat.help_menu"));
        player.SendChat(T("chat.help_add"));
        player.SendChat(T("chat.help_edit"));
        player.SendChat(T("chat.help_delete"));
        player.SendChat(T("chat.help_list"));
        player.SendChat(T("chat.help_tp"));
        player.SendChat(T("chat.help_reload"));
    }

    private void BeginCreate(IPlayer player, string name)
    {
        if (FindKey(name) != null)
        {
            player.SendChat($"{Prefix()} {T("chat.already_exists", name)}");
            return;
        }

        if (IsCapturing(player.Slot))
        {
            player.SendChat($"{Prefix()} {T("chat.already_capturing")}");
            return;
        }

        CloseMenu(player);
        player.SendChat($"{Prefix()} {T("chat.capturing_point", name)}");

        BeginPointCapture(player, (p, point, normal) =>
        {
            var placedPos = point + normal * WallOffset;
            var angle = CalculateWallAngles(normal, p.PlayerPawn?.EyeAngles ?? default);

            _pendingNewText[p.SteamID] = new PendingCreation
            {
                Name = name,
                Position = placedPos,
                Angles = angle,
            };

            p.SendChat($"{Prefix()} {T("chat.enter_text")}");
        });
    }

    private void BeginReaim(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        if (IsCapturing(player.Slot))
        {
            player.SendChat($"{Prefix()} {T("chat.already_capturing")}");
            return;
        }

        CloseMenu(player);
        player.SendChat($"{Prefix()} {T("chat.reaim_prompt", data.Name)}");

        BeginPointCapture(player, (p, point, normal) =>
        {
            var placedPos = point + normal * WallOffset;
            var angle = CalculateWallAngles(normal, p.PlayerPawn?.EyeAngles ?? default);

            data.Position = [placedPos.X, placedPos.Y, placedPos.Z];
            data.Angles = [angle.Pitch, angle.Yaw, angle.Roll];
            Save();
            UpdateLiveEntity(data);

            p.SendChat($"{Prefix()} {T("chat.reaim_success", data.Name)}");
            _core.Scheduler.NextTick(() => OpenDetailMenu(p, data.Name));
        });
    }

    private void BeginDuplicate(IPlayer player, string sourceName)
    {
        var key = FindKey(sourceName);
        if (key == null || !_store.TryGetValue(key, out var source))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", sourceName)}");
            return;
        }

        string cloneName = $"{source.Name}_copy";
        int copyIndex = 2;
        while (FindKey(cloneName) != null)
        {
            cloneName = $"{source.Name}_copy{copyIndex++}";
        }

        CloseMenu(player);
        player.SendChat($"{Prefix()} {T("chat.duplicated", cloneName, source.Name)}");

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
                FontSize = source.FontSize,
                Scale = source.Scale,
                Background = source.Background,
                Fullbright = source.Fullbright,
                FacePlayer = source.FacePlayer,
                JustifyHorizontal = source.JustifyHorizontal,
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

            p.SendChat($"{Prefix()} {T("chat.created", cloneName, clone.Text)}");
            _core.Scheduler.NextTick(() => OpenDetailMenu(p, cloneName));
        });
    }

    private void TeleportToText(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || pawn.Health <= 0) return;

        float yawRad = data.Angles[1] * (MathF.PI / 180f);
        float forwardX = MathF.Cos(yawRad);
        float forwardY = MathF.Sin(yawRad);

        var targetPos = new Vector(
            data.Position[0] + forwardX * 75f,
            data.Position[1] + forwardY * 75f,
            data.Position[2] - 40f
        );

        float lookYaw = (data.Angles[1] + 180f) % 360f;
        var targetAng = new QAngle(0f, lookYaw, 0f);

        pawn.Teleport(targetPos, targetAng, new Vector(0f, 0f, 0f));
        player.SendChat($"{Prefix()} {T("chat.teleported", data.Name)}");
    }

    private void BeginRename(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null)
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        _pendingRename[player.SteamID] = key;
        player.SendChat($"{Prefix()} {T("chat.enter_new_name")}");
    }

    private HookResult OnClientChat(int playerId, string text, bool teamonly)
    {
        var player = _core.PlayerManager.GetPlayer(playerId);
        if (player == null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        text = text.Trim();

        if (_pendingNewText.TryGetValue(player.SteamID, out var pending))
        {
            _pendingNewText.Remove(player.SteamID);
            return HandleNewTextInput(player, pending, text);
        }

        if (_pendingTextChange.TryGetValue(player.SteamID, out var changeName))
        {
            _pendingTextChange.Remove(player.SteamID);
            return HandleTextChangeInput(player, changeName, text);
        }

        if (_pendingRename.TryGetValue(player.SteamID, out var oldName))
        {
            _pendingRename.Remove(player.SteamID);
            return HandleRenameInput(player, oldName, text);
        }

        if (_pendingColorChange.TryGetValue(player.SteamID, out var colorTargetName))
        {
            _pendingColorChange.Remove(player.SteamID);
            return HandleColorChangeInput(player, colorTargetName, text);
        }

        if (_pendingAddRotatingMsg.TryGetValue(player.SteamID, out var rotTargetName))
        {
            _pendingAddRotatingMsg.Remove(player.SteamID);
            return HandleAddRotatingMsgInput(player, rotTargetName, text);
        }

        if (_pendingInteractMsg.TryGetValue(player.SteamID, out var interactMsgName))
        {
            _pendingInteractMsg.Remove(player.SteamID);
            return HandleInteractMsgInput(player, interactMsgName, text);
        }

        if (_pendingInteractCmd.TryGetValue(player.SteamID, out var interactCmdName))
        {
            _pendingInteractCmd.Remove(player.SteamID);
            return HandleInteractCmdInput(player, interactCmdName, text);
        }

        return HookResult.Continue;
    }

    private static bool IsCancelWord(string text) =>
        text.Equals("cancel", StringComparison.OrdinalIgnoreCase) || text.Equals("iptal", StringComparison.OrdinalIgnoreCase);

    private HookResult HandleNewTextInput(IPlayer player, PendingCreation pending, string text)
    {
        if (IsCancelWord(text))
        {
            player.SendChat($"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            player.SendChat($"{Prefix()} {T("chat.enter_text")}");
            return HookResult.Stop;
        }

        if (FindKey(pending.Name) != null)
        {
            player.SendChat($"{Prefix()} {T("chat.already_exists", pending.Name)}");
            return HookResult.Stop;
        }

        text = text.Replace("\\n", "\n");

        var data = new WallTextData
        {
            Name = pending.Name,
            Text = text,
            Position = [pending.Position.X, pending.Position.Y, pending.Position.Z],
            Angles = [pending.Angles.Pitch, pending.Angles.Yaw, pending.Angles.Roll],
            ColorHex = _config.DefaultColorHex,
            FontSize = _config.DefaultFontSize,
            Scale = _config.DefaultScale,
            Background = _config.DefaultBackground,
            Fullbright = _config.DefaultFullbright,
            FacePlayer = _config.DefaultFacePlayer,
            JustifyHorizontal = _config.DefaultJustifyHorizontal,
            DepthOffset = _config.DefaultDepthOffset,
        };

        _store[pending.Name] = data;
        Save();
        SpawnEntity(data);

        player.SendChat($"{Prefix()} {T("chat.created", pending.Name, text)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, pending.Name));
        return HookResult.Stop;
    }

    private HookResult HandleTextChangeInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            player.SendChat($"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (string.IsNullOrWhiteSpace(text) || !_store.TryGetValue(name, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        text = text.Replace("\\n", "\n");
        data.Text = text;
        Save();

        UpdateLiveEntity(data);

        player.SendChat($"{Prefix()} {T("chat.text_changed", text)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, name));
        return HookResult.Stop;
    }

    private HookResult HandleRenameInput(IPlayer player, string oldName, string newName)
    {
        if (IsCancelWord(newName))
        {
            player.SendChat($"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (string.IsNullOrWhiteSpace(newName) || !_store.TryGetValue(oldName, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", oldName)}");
            return HookResult.Stop;
        }

        if (FindKey(newName) != null && !newName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
        {
            player.SendChat($"{Prefix()} {T("chat.already_exists", newName)}");
            return HookResult.Stop;
        }

        _store.Remove(oldName);
        data.Name = newName;
        _store[newName] = data;
        Save();

        if (_liveEntities.Remove(oldName, out var entity))
        {
            _liveEntities[newName] = entity;
        }

        player.SendChat($"{Prefix()} {T("chat.renamed", newName)}");
        _core.Scheduler.NextTick(() => OpenListMenu(player));
        return HookResult.Stop;
    }

    private HookResult HandleColorChangeInput(IPlayer player, string name, string hexInput)
    {
        if (IsCancelWord(hexInput))
        {
            player.SendChat($"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (!_store.TryGetValue(name, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        if (!IsValidHex(hexInput))
        {
            player.SendChat($"{Prefix()} {T("chat.invalid_color_hex")}");
            _pendingColorChange[player.SteamID] = name;
            return HookResult.Stop;
        }

        data.ColorHex = NormalizeHex(hexInput).ToUpperInvariant();
        data.Rainbow = false; // Turn off rainbow when picking static color
        Save();
        UpdateLiveEntity(data);

        player.SendChat($"{Prefix()} {T("chat.color_changed", data.ColorHex)}");
        _core.Scheduler.NextTick(() => OpenDetailMenu(player, name));
        return HookResult.Stop;
    }

    private HookResult HandleAddRotatingMsgInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            player.SendChat($"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (!_store.TryGetValue(name, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        text = text.Replace("\\n", "\n");
        data.RotatingMessages.Add(text);
        data.IsRotating = true; // Auto-enable rotation
        Save();
        UpdateLiveEntity(data);

        player.SendChat($"{Prefix()} {T("chat.rotating_message_added", text)}");
        _core.Scheduler.NextTick(() => OpenRotatingMenu(player, name));
        return HookResult.Stop;
    }

    private HookResult HandleInteractMsgInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            player.SendChat($"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (!_store.TryGetValue(name, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        data.MessageToPlayer = text.Replace("\\n", "\n");
        data.IsInteractable = true;
        Save();

        player.SendChat($"{Prefix()} {T("chat.interact_msg_set", data.MessageToPlayer)}");
        _core.Scheduler.NextTick(() => OpenInteractMenu(player, name));
        return HookResult.Stop;
    }

    private HookResult HandleInteractCmdInput(IPlayer player, string name, string text)
    {
        if (IsCancelWord(text))
        {
            player.SendChat($"{Prefix()} {T("chat.rename_cancelled")}");
            return HookResult.Stop;
        }

        if (!_store.TryGetValue(name, out var data))
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return HookResult.Stop;
        }

        data.CommandToRun = text.Trim();
        data.IsInteractable = true;
        Save();

        player.SendChat($"{Prefix()} {T("chat.interact_cmd_set", data.CommandToRun)}");
        _core.Scheduler.NextTick(() => OpenInteractMenu(player, name));
        return HookResult.Stop;
    }

    private void DeleteWallText(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null)
        {
            player.SendChat($"{Prefix()} {T("chat.not_found", name)}");
            return;
        }

        DespawnEntity(key);
        _store.Remove(key);
        Save();
        player.SendChat($"{Prefix()} {T("chat.deleted", key)}");
    }

    private void ListWallTexts(IPlayer player)
    {
        if (_store.Count == 0)
        {
            player.SendChat($"{Prefix()} {T("chat.list_empty")}");
            return;
        }

        player.SendChat($"{Prefix()} {T("chat.list_header", _mapContext.CurrentMap)}");
        foreach (var data in _store.Values)
        {
            player.SendChat($" - {data.Name}: \"{data.Text}\" [{(data.Enabled ? "ON" : "OFF")}] (Color: {(data.Rainbow ? "RAINBOW" : data.ColorHex)})");
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

    private void CloseMenu(IPlayer player)
    {
        var current = _core.MenusAPI.GetCurrentMenu(player);
        if (current != null)
        {
            _core.MenusAPI.CloseMenuForPlayer(player, current);
        }
    }

    private void OpenMainMenu(IPlayer player)
    {
        var builder = _core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(T("menu.title.main"));

        var createBtn = new ButtonMenuOption(T("menu.option.create"), 1000);
        createBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginCreate(player, $"Text_{_store.Count + 1}"));
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
        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
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
                var label = $"{data.Name}: \"{data.Text}\" [{(data.Enabled ? "ON" : "OFF")}]";
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
        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
    }

    private void OpenDetailMenu(IPlayer player, string name)
    {
        if (!_store.TryGetValue(name, out var data))
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

        // 2. Teleport To Text
        var tpBtn = new ButtonMenuOption(T("menu.option.teleport"), 1000);
        tpBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => TeleportToText(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(tpBtn);

        // 3. Move To Crosshair (Instant)
        var crosshairBtn = new ButtonMenuOption(T("menu.option.move_to_crosshair"), 1000);
        crosshairBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => MoveToCrosshair(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(crosshairBtn);

        // 4. Dikey & Yukarı Rotate / Açı Düzeltme Butonları
        var pitchUpBtn = new ButtonMenuOption(T("menu.option.pitch_up"), 1000);
        pitchUpBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = (data.Angles[0] + 90f) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchUpBtn);

        var pitchDownBtn = new ButtonMenuOption(T("menu.option.pitch_down"), 1000);
        pitchDownBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = (data.Angles[0] - 90f) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchDownBtn);

        var presetVertBtn = new ButtonMenuOption(T("menu.option.preset_vertical"), 1000);
        presetVertBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = 90f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
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
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(presetVertNegBtn);

        var presetFlatBtn = new ButtonMenuOption(T("menu.option.preset_flat"), 1000);
        presetFlatBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = 0f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(presetFlatBtn);

        var rot90Btn = new ButtonMenuOption(T("menu.option.rotate_90"), 1000);
        rot90Btn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = (data.Angles[1] + 90f) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rot90Btn);

        var rollBtn = new ButtonMenuOption(T("menu.option.roll_rotate"), 1000);
        rollBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[2] = (data.Angles[2] + 90f) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rollBtn);

        var flip180Btn = new ButtonMenuOption(T("menu.option.flip_180"), 1000);
        flip180Btn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = (data.Angles[1] + 180f) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(flip180Btn);

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
                    data.Angles[1] = MathF.Atan2(dy, dx) * (180f / MathF.PI);
                    Save();
                    UpdateLiveEntity(data);
                }
                OpenDetailMenu(player, data.Name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(faceAdminBtn);

        // 5. Re-aim & Reposition via shoot
        var reaimBtn = new ButtonMenuOption(T("menu.option.reaim"), 1000);
        reaimBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginReaim(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(reaimBtn);

        // 6. Duplicate / Clone
        var dupBtn = new ButtonMenuOption(T("menu.option.duplicate"), 1000);
        dupBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginDuplicate(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(dupBtn);

        // 7. Change Text
        var changeTextBtn = new ButtonMenuOption(T("menu.option.change_text"), 1000);
        changeTextBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                _pendingTextChange[player.SteamID] = data.Name;
                player.SendChat($"{Prefix()} {T("chat.enter_text")}");
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(changeTextBtn);

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

        // 10. Rainbow (RGB) Toggle & Speed
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

        // 11. Color Sub-Menu
        var colorBtn = new ButtonMenuOption(T("menu.option.color_menu", data.ColorHex), 1000);
        colorBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenColorMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(colorBtn);

        // 12. Face Player (Billboard) Toggle
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

        // 13. Fullbright Toggle
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

        // 14. Horizontal Justification Button
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

        // 15. Fine-Tune Position & Angles Sub-Menu
        var fineTuneBtn = new ButtonMenuOption(T("menu.option.fine_tune"), 1000);
        fineTuneBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenFineTuneMenu(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(fineTuneBtn);

        // 16. Sliders: Font Size, Scale, Background
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

        // 17. Rename Id
        var renameBtn = new ButtonMenuOption(T("menu.option.rename"), 1000);
        renameBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => BeginRename(player, data.Name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(renameBtn);

        // 18. Delete Button
        var deleteBtn = new ButtonMenuOption(T("menu.option.delete"), 1000);
        deleteBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                DespawnEntity(data.Name);
                _store.Remove(data.Name);
                Save();
                player.SendChat($"{Prefix()} {T("chat.deleted", data.Name)}");
                OpenListMenu(player);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(deleteBtn);

        // 19. Back Button
        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenListMenu(player));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Rotating Announcer Board
    // ------------------------------------------------------------------

    private void OpenRotatingMenu(IPlayer player, string name)
    {
        if (!_store.TryGetValue(name, out var data))
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
                OpenRotatingMenu(player, name);
            });
        };
        builder.AddOption(toggle);

        // Interval button: 10s -> 20s -> 30s -> 60s -> 5s
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
                OpenRotatingMenu(player, name);
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
                _pendingAddRotatingMsg[player.SteamID] = data.Name;
                player.SendChat($"{Prefix()} {T("chat.enter_rotating_message")}");
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
                    player.SendChat($"{Prefix()} {T("chat.rotating_messages_cleared", data.Name)}");
                    OpenRotatingMenu(player, name);
                });
                return ValueTask.CompletedTask;
            };
            builder.AddOption(clearBtn);

            // List of messages
            for (int i = 0; i < data.RotatingMessages.Count; i++)
            {
                int index = i;
                string preview = data.RotatingMessages[index];
                if (preview.Length > 28) preview = preview[..25] + "...";
                var itemBtn = new ButtonMenuOption($"#{index + 1}: {preview} [X]", 1000);
                itemBtn.Click += (sender, args) =>
                {
                    _core.Scheduler.NextTick(() =>
                    {
                        data.RotatingMessages.RemoveAt(index);
                        if (data.RotatingMessages.Count == 0) data.IsRotating = false;
                        Save();
                        UpdateLiveEntity(data);
                        OpenRotatingMenu(player, name);
                    });
                    return ValueTask.CompletedTask;
                };
                builder.AddOption(itemBtn);
            }
        }

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Interactive Trigger
    // ------------------------------------------------------------------

    private void OpenInteractMenu(IPlayer player, string name)
    {
        if (!_store.TryGetValue(name, out var data))
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
                OpenInteractMenu(player, name);
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
                OpenInteractMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(typeBtn);

        // Set message to player
        string curMsg = string.IsNullOrWhiteSpace(data.MessageToPlayer) ? "Yok" : data.MessageToPlayer;
        if (curMsg.Length > 20) curMsg = curMsg[..17] + "...";
        var setMsgBtn = new ButtonMenuOption($"{T("menu.option.set_interact_msg")} [{curMsg}]", 1000);
        setMsgBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                _pendingInteractMsg[player.SteamID] = data.Name;
                player.SendChat($"{Prefix()} {T("chat.enter_interact_msg")}");
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
                _pendingInteractCmd[player.SteamID] = data.Name;
                player.SendChat($"{Prefix()} {T("chat.enter_interact_cmd")}");
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
                OpenInteractMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(distBtn);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Color Selection
    // ------------------------------------------------------------------

    private void OpenColorMenu(IPlayer player, string name)
    {
        if (!_store.TryGetValue(name, out var data))
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

        foreach (var (key, hex) in presets)
        {
            var btn = new ButtonMenuOption(T(key), 1000);
            var capturedHex = hex;
            btn.Click += (sender, args) =>
            {
                _core.Scheduler.NextTick(() =>
                {
                    data.ColorHex = capturedHex;
                    data.Rainbow = false; // Disable rainbow when selecting static color
                    Save();
                    UpdateLiveEntity(data);
                    player.SendChat($"{Prefix()} {T("chat.color_changed", capturedHex)}");
                    OpenColorMenu(player, name);
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
                _pendingColorChange[player.SteamID] = data.Name;
                player.SendChat($"{Prefix()} {T("chat.enter_color_hex")}");
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(customHexBtn);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
    }

    // ------------------------------------------------------------------
    // Sub-Menu: Fine-Tune Position & Angles
    // ------------------------------------------------------------------

    private void OpenFineTuneMenu(IPlayer player, string name)
    {
        if (!_store.TryGetValue(name, out var data))
        {
            OpenListMenu(player);
            return;
        }

        if (!_playerStepSizes.TryGetValue(player.SteamID, out var step))
        {
            step = 5f;
            _playerStepSizes[player.SteamID] = step;
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
                _playerStepSizes[player.SteamID] = AvailableSteps[nextIdx];
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(stepBtn);

        var xPos = new ButtonMenuOption(T("menu.option.nudge_x_pos", step), 1000);
        xPos.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[0] += step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
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
                OpenFineTuneMenu(player, name);
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
                OpenFineTuneMenu(player, name);
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
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(yNeg);

        var zUp = new ButtonMenuOption(T("menu.option.nudge_z_up", step), 1000);
        zUp.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Position[2] += step;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
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
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(zDown);

        // Pitch (Yukarı / Aşağı Eğme)
        var pitchUp = new ButtonMenuOption(T("menu.option.nudge_pitch_up", step), 1000);
        pitchUp.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = (data.Angles[0] + step) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchUp);

        var pitchDown = new ButtonMenuOption(T("menu.option.nudge_pitch_down", step), 1000);
        pitchDown.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = (data.Angles[0] - step) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(pitchDown);

        // Yaw (Yatay Döndürme)
        var yawLeft = new ButtonMenuOption(T("menu.option.nudge_yaw_left", step), 1000);
        yawLeft.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = (data.Angles[1] - step) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(yawLeft);

        var yawRight = new ButtonMenuOption(T("menu.option.nudge_yaw_right", step), 1000);
        yawRight.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[1] = (data.Angles[1] + step) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(yawRight);

        // Roll (Yan Yatırma)
        var rollRight = new ButtonMenuOption(T("menu.option.nudge_roll_pos", step), 1000);
        rollRight.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[2] = (data.Angles[2] + step) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rollRight);

        var rollLeft = new ButtonMenuOption(T("menu.option.nudge_roll_neg", step), 1000);
        rollLeft.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[2] = (data.Angles[2] - step) % 360f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(rollLeft);

        var straighten = new ButtonMenuOption(T("menu.option.straighten"), 1000);
        straighten.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() =>
            {
                data.Angles[0] = 0f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                OpenFineTuneMenu(player, name);
            });
            return ValueTask.CompletedTask;
        };
        builder.AddOption(straighten);

        var backBtn = new ButtonMenuOption(T("menu.option.back"), 1000);
        backBtn.Click += (sender, args) =>
        {
            _core.Scheduler.NextTick(() => OpenDetailMenu(player, name));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        var menu = builder.Build();
        menu.Tag = MenuTag;
        _core.MenusAPI.OpenMenuForPlayer(player, menu);
    }
}
