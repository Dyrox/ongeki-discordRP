using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using MU3.Data;
using MU3.Game;
using MU3.Sequence;

namespace OngekiDiscordRP
{
    [BepInPlugin(Guid, "Ongeki Discord Rich Presence", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.dyrox.ongeki.discordrp";

        // Discord caps details/state at 128 chars; stay under to leave headroom.
        private const int MaxFieldLen = 120;

        private static readonly string[] DiffNames =
            { "BASIC", "ADVANCED", "EXPERT", "MASTER", "LUNATIC" };

        private ConfigEntry<string> _clientId;
        private ConfigEntry<float> _interval;
        private ConfigEntry<bool> _showArtist;
        private ConfigEntry<bool> _showTimestamp;
        private ConfigEntry<string> _largeImageKey;
        private ConfigEntry<string> _largeImageText;

        private DiscordIpcClient _rpc;
        private long _sessionStart;
        private float _timer;
        private bool _firstTickLogged;
        private string _lastPresenceLogged;

        private void Awake()
        {
            _clientId = Config.Bind("Discord", "ClientId", "",
                "Your Discord Application (Client) ID from https://discord.com/developers/applications . Required.");
            _interval = Config.Bind("Discord", "UpdateIntervalSeconds", 3f,
                "How often (seconds) to refresh the presence. Minimum 1.");
            _showArtist = Config.Bind("Display", "ShowArtist", true,
                "Append the artist name after the difficulty while playing.");
            _showTimestamp = Config.Bind("Display", "ShowSessionTimer", true,
                "Show an elapsed timer counting from when the game started (total session time).");
            _largeImageKey = Config.Bind("Display", "LargeImageKey", "ongeki",
                "Art-asset key uploaded under your Discord app (Rich Presence > Art Assets). Leave blank to hide the image.");
            _largeImageText = Config.Bind("Display", "LargeImageText", "ONGEKI",
                "Tooltip text shown when hovering the large image.");

            _sessionStart = UnixNow();

            string id = (_clientId.Value ?? "").Trim();
            if (id.Length == 0)
            {
                Logger.LogWarning("No Discord ClientId set - presence is disabled. " +
                    "Add it to BepInEx/config/" + Guid + ".cfg and restart.");
                return;
            }

            _rpc = new DiscordIpcClient(id,
                m => Logger.LogInfo(m),
                w => Logger.LogWarning(w));
            _rpc.Start();
            Logger.LogInfo("Ongeki Discord Rich Presence started.");
        }

        private void Update()
        {
            if (_rpc == null) return;

            _timer += Time.unscaledDeltaTime;
            float step = _interval.Value < 1f ? 1f : _interval.Value;
            if (_timer < step) return;
            _timer = 0f;

            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                Logger.LogInfo("Update loop is running (presence will refresh every " + step + "s).");
            }

            try
            {
                string json = BuildActivityJson();
                if (json != _lastPresenceLogged)
                {
                    _lastPresenceLogged = json;
                    Logger.LogInfo("Presence -> " + json);
                }
                _rpc.SetActivity(json);
            }
            catch (Exception e) { Logger.LogError("Failed to build presence: " + e); }
        }

        private void OnDestroy()
        {
            if (_rpc != null) { _rpc.Stop(); _rpc = null; }
        }

        // ---- presence composition -------------------------------------------------

        private string BuildActivityJson()
        {
            string scene = SafeSceneName();
            string details, state;
            ResolvePresence(scene, out details, out state);

            var members = new List<string>(5);
            if (!string.IsNullOrEmpty(details))
                members.Add("\"details\":\"" + Esc(Clip(details)) + "\"");
            if (!string.IsNullOrEmpty(state))
                members.Add("\"state\":\"" + Esc(Clip(state)) + "\"");

            if (_showTimestamp.Value)
                members.Add("\"timestamps\":{\"start\":" + _sessionStart + "}");

            string img = (_largeImageKey.Value ?? "").Trim();
            if (img.Length > 0)
            {
                var assets = new StringBuilder();
                assets.Append("\"assets\":{\"large_image\":\"").Append(Esc(img)).Append('"');
                string imgText = (_largeImageText.Value ?? "").Trim();
                if (imgText.Length > 0)
                    assets.Append(",\"large_text\":\"").Append(Esc(Clip(imgText))).Append('"');
                assets.Append('}');
                members.Add(assets.ToString());
            }

            return "{" + string.Join(",", members.ToArray()) + "}";
        }

        private void ResolvePresence(string scene, out string details, out string state)
        {
            details = "ONGEKI";
            state = null;

            // Actively playing a song is detected by the GameEngine singleton, which
            // only exists during play. This is reliable regardless of which stage
            // scene (st_####) is currently active.
            if (IsPlaying())
            {
                var mdPlay = CurrentMusic();
                if (mdPlay != null)
                {
                    details = "♪ " + mdPlay.name;                 // ♪ Title
                    state = DifficultyLabel() + ArtistSuffix(mdPlay);
                }
                else details = "Playing a song";
                return;
            }

            switch (scene)
            {
                case "33_PlayMusic":
                {
                    var md = CurrentMusic();
                    if (md != null)
                    {
                        details = "♪ " + md.name;                 // ♪ Title
                        state = DifficultyLabel() + ArtistSuffix(md);
                    }
                    else details = "Playing a song";
                    break;
                }
                case "37_Result":
                {
                    var md = CurrentMusic();
                    if (md != null) { details = "Result: " + md.name; state = DifficultyLabel(); }
                    else details = "Viewing results";
                    break;
                }
                case "32_PrePlayMusic":
                case "98_DebugPlaySelect":
                    details = "Selecting a song";
                    break;
                case "20_Advertise":
                case "00_Launcher":
                    details = "At the title screen";
                    break;
                case "25_Login":
                    details = "Logging in";
                    break;
                case "30_NoticeReward":
                    details = "Checking rewards";
                    break;
                case "40_SettingRoom":
                    details = "In the setting room";
                    break;
                case "41_CardCustom":
                case "42_CardCustomDeck":
                    details = "Customizing cards";
                    break;
                case "31_Story":
                    details = "Watching a story";
                    break;
                case "38_End":
                case "39_Logout":
                    details = "Finishing up";
                    break;
                case "01_Initialize":
                case "10_Root":
                case "11_PowerOnTest":
                case "12_Initialize":
                    details = "Booting up";
                    break;
                default:
                    // st_#### are gameplay stage scenes; if we're not actually
                    // playing (GameEngine null), it's a transition/loading moment.
                    details = "In the menus";
                    break;
            }
        }

        // ---- game-state reads (all defensive; the game may not be in a state
        //      where these singletons/fields are populated) -----------------------

        private static bool IsPlaying()
        {
            // GameEngine (SingletonMonoBehaviour) exists only while a song is being played.
            try { return MU3.Battle.GameEngine.instance != null; }
            catch { return false; }
        }

        private static MusicData CurrentMusic()
        {
            try
            {
                var pi = PlayInfo.instance;
                if (pi == null) return null;
                return pi.sessionInfo.musicData;
            }
            catch { return null; }
        }

        private static SessionInfo? CurrentSession()
        {
            try
            {
                var pi = PlayInfo.instance;
                if (pi == null) return null;
                return pi.sessionInfo;
            }
            catch { return null; }
        }

        private static string DifficultyLabel()
        {
            var s = CurrentSession();
            if (!s.HasValue) return "";
            int d = (int)s.Value.musicLevel;
            string diff = (d >= 0 && d < DiffNames.Length) ? DiffNames[d] : s.Value.musicLevel.ToString();
            string lvl = LevelString(s.Value.musicData, d);
            return lvl.Length > 0 ? diff + " " + lvl : diff;
        }

        private static string LevelString(MusicData md, int diffIndex)
        {
            try
            {
                if (md == null) return "";
                var fs = md.fumenData;
                if (fs == null || diffIndex < 0 || diffIndex >= fs.Length) return "";
                var f = fs[diffIndex];
                if (f == null || !f.isExist) return "";
                int n = (int)f.fumenConst;
                return n.ToString() + (f.isFumenConstPlus ? "+" : "");
            }
            catch { return ""; }
        }

        private string ArtistSuffix(MusicData md)
        {
            if (!_showArtist.Value || md == null) return "";
            try
            {
                string a = md.artistName;
                if (!string.IsNullOrEmpty(a)) return "  ·  " + a;  // ·
            }
            catch { }
            return "";
        }

        // ---- helpers --------------------------------------------------------------

        private static string SafeSceneName()
        {
            try { return SceneManager.GetActiveScene().name; }
            catch { return null; }
        }

        private static long UnixNow()
        {
            // Discord's Activity timestamps are Unix time in MILLISECONDS.
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static string Clip(string s)
        {
            if (s == null) return "";
            return s.Length <= MaxFieldLen ? s : s.Substring(0, MaxFieldLen - 1) + "…";
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c); // UTF-8 handles Japanese/other chars directly
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
