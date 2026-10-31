using System;
using System.Linq;
using System.Text.RegularExpressions;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace CS2WallText;

/// <summary>
/// Replaces dynamic variables such as {MAP}, {PLAYERS}, {MAXPLAYERS}, {TIME}, {DATE}, {SERVERNAME},
/// {NAME}, {STEAMID64} on wall texts and trigger commands in real-time.
/// </summary>
public static class WallTextPlaceholders
{
    private static readonly Regex TokenRegex = new(@"\{(?<k>[A-Z0-9_]+)\}", RegexOptions.Compiled);

    public static bool HasPlaceholders(string? text)
    {
        return !string.IsNullOrEmpty(text) && text.Contains('{') && text.Contains('}');
    }

    public static string Format(string text, ISwiftlyCore core, string currentMap, IPlayer? player = null)
    {
        if (!HasPlaceholders(text))
        {
            return text;
        }

        int humanPlayers = 0;
        int botPlayers = 0;
        try
        {
            var players = core.PlayerManager.GetAllPlayers().Where(p => p != null && p.IsValid).ToList();
            humanPlayers = players.Count(p => !p.IsFakeClient);
            botPlayers = players.Count(p => p.IsFakeClient);
        }
        catch
        {
            // Fallback if player manager isn't ready
        }

        int maxPlayers = 32;
        try
        {
            var mc = core.Engine.GlobalVars.MaxClients;
            if (mc > 0) maxPlayers = mc;
        }
        catch
        {
            // Fallback
        }

        string serverName = "Counter-Strike 2";
        try
        {
            serverName = core.ConVar.FindAsString("hostname")?.ValueAsString ?? "Counter-Strike 2";
        }
        catch
        {
            // Ignore convar lookup failure
        }

        var now = DateTime.Now;

        string playerName = player != null && player.IsValid ? player.Name : "";
        string steamId64 = player != null && player.IsValid ? player.SteamID.ToString() : "";
        string slot = player != null && player.IsValid ? player.Slot.ToString() : "";

        return TokenRegex.Replace(text, m =>
        {
            var key = m.Groups["k"].Value;
            return key switch
            {
                "MAP" => currentMap,
                "PLAYERS" => humanPlayers.ToString(),
                "BOTS" => botPlayers.ToString(),
                "TOTALPLAYERS" => (humanPlayers + botPlayers).ToString(),
                "MAXPLAYERS" => maxPlayers.ToString(),
                "TIME" => now.ToString("HH:mm:ss"),
                "SHORTTIME" => now.ToString("HH:mm"),
                "DATE" => now.ToString("dd.MM.yyyy"),
                "DATETIME" => now.ToString("dd.MM.yyyy HH:mm"),
                "SERVERNAME" => serverName,
                "NAME" => playerName,
                "PLAYERNAME" => playerName,
                "STEAMID64" => steamId64,
                "STEAMID" => steamId64,
                "SLOT" => slot,
                _ => m.Value
            };
        });
    }
}
