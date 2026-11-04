using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace CS2WallText;

/// <summary>
/// Replaces dynamic variables such as {MAP}, {PLAYERS}, {MAXPLAYERS}, {TIME}, {DATE}, {SERVERNAME},
/// {CT_PLAYERS}, {T_PLAYERS}, {SPEC_PLAYERS}, {ALIVE}, {NAME}, {STEAMID64} on wall texts and trigger commands in real-time.
/// Also normalizes SwiftlyS2 chat color tags and safely truncates UTF-8 strings for CPointWorldText (512-byte buffer).
/// </summary>
public static class WallTextPlaceholders
{
    private const int MaxWorldTextUtf8Bytes = 500;

    private static readonly Regex TokenRegex = new(@"\{(?<k>[A-Za-z0-9_]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BracketColorRegex = new(@"\[(?<c>[A-Za-z/]+)\]", RegexOptions.Compiled);
    private static readonly Regex BraceColorRegex = new(@"\{(?<c>[A-Za-z]+)\}", RegexOptions.Compiled);

    private static readonly HashSet<string> ValidChatColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "default", "/", "white", "darkred", "green", "lightyellow", "lightblue",
        "olive", "lime", "red", "lightpurple", "purple", "grey", "yellow", "gold",
        "silver", "blue", "darkblue", "bluegrey", "magenta", "lightred", "orange"
    };

    private static readonly Dictionary<string, string> ColorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gray"] = "grey",
        ["cyan"] = "lightblue",
        ["pink"] = "magenta",
        ["lightgreen"] = "lime",
        ["lightolive"] = "gold",
        ["reset"] = "default"
    };

    public static bool HasPlaceholders(string? text)
    {
        return !string.IsNullOrEmpty(text) && text.Contains('{') && text.Contains('}');
    }

    public static string Format(string text, ISwiftlyCore core, string currentMap, IPlayer? player = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!HasPlaceholders(text))
        {
            return TruncateUtf8(text, MaxWorldTextUtf8Bytes);
        }

        int humanPlayers = 0;
        int botPlayers = 0;
        int ctPlayers = 0;
        int tPlayers = 0;
        int specPlayers = 0;
        int alivePlayers = 0;

        try
        {
            var players = core.PlayerManager.GetAllPlayers().Where(p => p != null && p.IsValid).ToList();
            foreach (var p in players)
            {
                if (p.IsFakeClient)
                {
                    botPlayers++;
                }
                else
                {
                    humanPlayers++;
                    var teamNum = p.Controller?.TeamNum ?? 0;
                    if (teamNum == 3) ctPlayers++;
                    else if (teamNum == 2) tPlayers++;
                    else specPlayers++;
                }

                if (p.PlayerPawn is { IsValid: true, Health: > 0 })
                {
                    alivePlayers++;
                }
            }
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

        string playerName = player is { IsValid: true } ? player.Name : "";
        string steamId64 = player is { IsValid: true } ? player.SteamID.ToString() : "";
        string slot = player is { IsValid: true } ? player.Slot.ToString() : "";

        var formatted = TokenRegex.Replace(text, m =>
        {
            var key = m.Groups["k"].Value.ToUpperInvariant();
            return key switch
            {
                "MAP" => currentMap,
                "PLAYERS" => humanPlayers.ToString(),
                "BOTS" => botPlayers.ToString(),
                "TOTALPLAYERS" => (humanPlayers + botPlayers).ToString(),
                "MAXPLAYERS" => maxPlayers.ToString(),
                "CT_PLAYERS" or "CT" => ctPlayers.ToString(),
                "T_PLAYERS" or "T" => tPlayers.ToString(),
                "SPEC_PLAYERS" or "SPECS" => specPlayers.ToString(),
                "ALIVE" => alivePlayers.ToString(),
                "TIME" => now.ToString("HH:mm:ss"),
                "SHORTTIME" => now.ToString("HH:mm"),
                "DATE" => now.ToString("dd.MM.yyyy"),
                "DATETIME" => now.ToString("dd.MM.yyyy HH:mm"),
                "DAY" => now.ToString("dd"),
                "MONTH" => now.ToString("MM"),
                "YEAR" => now.ToString("yyyy"),
                "SERVERNAME" or "HOSTNAME" => serverName,
                "NAME" or "PLAYERNAME" => playerName,
                "STEAMID64" or "STEAMID" => steamId64,
                "SLOT" or "USERID" => slot,
                _ => m.Value
            };
        });

        return TruncateUtf8(formatted, MaxWorldTextUtf8Bytes);
    }

    /// <summary>
    /// Normalizes chat color tags like [Lime], [Default], {RED}, {DEFAULT} into lowercase
    /// [lime], [default], [red] tags expected by SwiftlyS2's SendChat.
    /// </summary>
    public static string NormalizeChatColors(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var step1 = BracketColorRegex.Replace(text, m =>
        {
            var tag = m.Groups["c"].Value;
            if (ColorAliases.TryGetValue(tag, out var mapped))
            {
                return $"[{mapped}]";
            }
            if (ValidChatColors.Contains(tag))
            {
                return $"[{tag.ToLowerInvariant()}]";
            }
            return m.Value;
        });

        return BraceColorRegex.Replace(step1, m =>
        {
            var tag = m.Groups["c"].Value;
            if (ColorAliases.TryGetValue(tag, out var mapped))
            {
                return $"[{mapped}]";
            }
            if (ValidChatColors.Contains(tag))
            {
                return $"[{tag.ToLowerInvariant()}]";
            }
            return m.Value;
        });
    }

    /// <summary>
    /// Truncates a string so its UTF-8 byte representation does not exceed <paramref name="maxBytes"/>,
    /// without splitting multi-byte UTF-8 characters (such as Turkish characters).
    /// </summary>
    public static string TruncateUtf8(string? text, int maxBytes = MaxWorldTextUtf8Bytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        int byteCount = 0;
        int charCount = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes)
            {
                break;
            }
            byteCount += runeBytes;
            charCount += rune.Utf16SequenceLength;
        }

        return text[..charCount];
    }
}
