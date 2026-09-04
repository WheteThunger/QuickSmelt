using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Quick Smelt UI", "Lucien Murray-Pitts", "0.1.0")]
    [Description("Admin UI for editing Quick Smelt's per-furnace configuration")]
    internal class QuickSmeltUI : RustPlugin
    {
        [PluginReference] private Plugin ServerMenuSuite;

        #region Constants

        private const string PermissionAdmin = "quicksmelt.admin";
        private const string TargetPluginName = "QuickSmelt";
        private const string TargetConfigName = "QuickSmelt.json";

        private const string UiRoot = "QSUI.Root";

        // Quick Smelt's own placeholder keys. They ship in the default config, do nothing,
        // and only exist as documentation of the expected shape.
        private const string PlaceholderFurnace = "furnace.shortname";
        private const string PlaceholderItem = "item.shortname";
        private const string GlobalScope = "global";

        // Config section keys, spelled exactly as Quick Smelt writes them.
        private const string KeyUsePermission = "Use Permission";
        private const string KeySpeed = "Speed Multipliers";
        private const string KeyFuelSpeed = "Fuel Usage Speed Multipliers";
        private const string KeyFuelUsage = "Fuel Usage Multipliers";
        private const string KeyOutput = "Output Multipliers";
        private const string KeyWhitelist = "Whitelist";
        private const string KeyBlacklist = "Blacklist";
        private const string KeyFrequency = "Smelting Frequencies (Smelt items every N smelting ticks)";
        private const string KeyDebug = "Debug";

        #endregion

        #region Palette and layout

        private const string ColWindow = "0.115 0.125 0.135 0.985";
        private const string ColBar = "0.165 0.180 0.195 1";
        private const string ColPanel = "0.145 0.155 0.168 1";
        private const string ColCard = "0.205 0.220 0.235 1";
        private const string ColCardAlt = "0.245 0.262 0.280 1";
        private const string ColField = "0.085 0.092 0.100 1";
        private const string ColAccent = "0.278 0.529 0.757 1";
        private const string ColGreen = "0.353 0.545 0.278 1";
        private const string ColRed = "0.686 0.278 0.220 1";
        private const string ColAmber = "0.760 0.560 0.200 1";
        private const string ColNeutral = "0.310 0.330 0.350 1";
        private const string ColText = "0.870 0.880 0.890 1";
        private const string ColTextDim = "0.560 0.585 0.610 1";
        private const string ColShade = "0 0 0 0.72";

        private const float WinW = 1100f;
        private const float WinH = 640f;
        private const float TitleH = 40f;
        private const float TabH = 34f;
        private const float FootH = 44f;
        private const float Pad = 12f;
        private const float RowH = 46f;

        private static float ContentW => WinW - Pad * 2f;
        private static float ContentY => FootH + 8f;
        private static float ContentH => WinH - TitleH - TabH - FootH - 16f;

        private const int FurnaceCols = 8;
        private const int FurnaceRows = 3;
        private const float FCardW = 128f;
        private const float FCardH = 112f;
        private static int FurnacePageSize => FurnaceCols * FurnaceRows;

        private const int ItemGridCols = 10;
        private const int ItemGridRows = 4;
        private const float ICardW = 84f;
        private const float ICardH = 84f;
        private static int ItemPageSize => ItemGridCols * ItemGridRows;

        #endregion

        #region State

        private enum Tab { General, Furnaces, Rates, Output, Lists }

        private enum PickTarget { NewFurnace, OutputItem, WhitelistItem, BlacklistItem }

        private enum RateField { Speed, FuelSpeed, FuelUsage, Frequency }

        private class PickerState
        {
            public PickTarget Target;
            public string Scope = GlobalScope;
            public string Search = "";
            public int Page;
            public bool RelevantOnly = true;
        }

        private class UiState
        {
            public Tab Tab = Tab.General;
            public JObject Config;
            public bool Dirty;
            public DateTime DiskStamp;
            public bool ConflictArmed;
            public string Status = "";
            public string StatusColor = ColTextDim;

            public string SelectedScope = GlobalScope;
            public string EditScope;
            public bool DeleteArmed;
            public PickerState Picker;

            public int FurnacePage;
            public int RatesPage;
            public int OutputPage;
            public int ListPage;
        }

        private class FurnaceInfo
        {
            public string ShortName = "";
            public ItemDefinition Item;
            public string Label = "";
            public bool Deployable;
        }

        private readonly Dictionary<ulong, UiState> _states = new Dictionary<ulong, UiState>();

        private List<ItemDefinition> _catalog = new List<ItemDefinition>();
        private readonly Dictionary<string, ItemDefinition> _byShortName =
            new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);

        private List<FurnaceInfo> _furnaces = new List<FurnaceInfo>();
        private readonly Dictionary<string, FurnaceInfo> _furnaceByShortName =
            new Dictionary<string, FurnaceInfo>(StringComparer.OrdinalIgnoreCase);

        // Items a furnace can consume, and items a furnace can produce. Used to keep the
        // picker showing only what is meaningful for the list being edited.
        private readonly HashSet<string> _smeltableInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _smeltOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Lifecycle

        protected override void LoadDefaultConfig() { }

        private void Init()
        {
            // Quick Smelt registers only quicksmelt.use, so this UI owns its own admin gate.
            if (!permission.PermissionExists(PermissionAdmin))
            {
                permission.RegisterPermission(PermissionAdmin, this);
            }
        }

        private void OnServerInitialized()
        {
            RegisterWithServerMenu();
            BuildItemCatalog();
            BuildFurnaceCatalog();
        }

        // ServerMenu suite integration: a button on its Admin page opens this editor.
        private void OnServerMenuReady(Plugin suite) => RegisterWithServerMenu();

        private void RegisterWithServerMenu() =>
            ServerMenuSuite?.Call("RegisterAdminTool", this, Name, "Quick Smelt", PermissionAdmin, "quicksmeltui open");

        private void Unload()
        {
            ServerMenuSuite?.Call("UnregisterAdminTool", Name);
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UiRoot);
            }

            _states.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            _states.Remove(player.userID);
        }

        private static string DisplayNameOf(ItemDefinition definition)
        {
            if (definition == null)
                return "";

            var translated = definition.displayName?.english;
            return string.IsNullOrEmpty(translated) ? definition.shortname : translated;
        }

        private void BuildItemCatalog()
        {
            _catalog = ItemManager.GetItemDefinitions()
                .Where(d => d != null && !string.IsNullOrEmpty(d.shortname))
                .OrderBy(DisplayNameOf, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _byShortName.Clear();
            _smeltableInputs.Clear();
            _smeltOutputs.Clear();

            foreach (var definition in _catalog)
            {
                _byShortName[definition.shortname] = definition;

                var cookable = definition.GetComponent<ItemModCookable>();
                if (cookable != null)
                {
                    _smeltableInputs.Add(definition.shortname);

                    if (cookable.becomeOnCooked != null)
                    {
                        _smeltOutputs.Add(cookable.becomeOnCooked.shortname);
                    }
                }

                // Charcoal and similar fuel byproducts are also scaled by Output Multipliers.
                var burnable = definition.GetComponent<ItemModBurnable>();
                if (burnable?.byproductItem != null)
                {
                    _smeltOutputs.Add(burnable.byproductItem.shortname);
                }
            }

            Puts($"Item catalog: {_catalog.Count} items, {_smeltableInputs.Count} smeltable, {_smeltOutputs.Count} smelt outputs");
        }

        // Mirrors Quick Smelt's own validation sources: every BaseOven prefab in the game
        // manifest, plus the deployable item that places it so the UI has an icon.
        private void BuildFurnaceCatalog()
        {
            var stopwatch = Stopwatch.StartNew();
            var itemByFurnace = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var definition in ItemManager.itemList)
            {
                var deployable = definition.GetComponent<ItemModDeployable>();
                if (deployable == null)
                    continue;

                var oven = deployable.entityPrefab.GetEntity() as BaseOven;
                if (oven == null)
                    continue;

                itemByFurnace[oven.ShortPrefabName] = definition;
            }

            var shortNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prefabPath in GameManifest.Current.entities)
            {
                var oven = GameManager.server.FindPrefab(prefabPath)?.GetComponent<BaseOven>();
                if (oven == null)
                    continue;

                shortNames.Add(oven.ShortPrefabName);
            }

            _furnaces = shortNames
                .Select(shortName =>
                {
                    var item = itemByFurnace.TryGetValue(shortName, out var definition) ? definition : null;
                    return new FurnaceInfo
                    {
                        ShortName = shortName,
                        Item = item,
                        Label = item != null ? DisplayNameOf(item) : shortName,
                        Deployable = item != null,
                    };
                })
                .OrderByDescending(f => f.Deployable)
                .ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _furnaceByShortName.Clear();
            foreach (var furnace in _furnaces)
            {
                _furnaceByShortName[furnace.ShortName] = furnace;
            }

            stopwatch.Stop();
            Puts($"Furnace catalog: {_furnaces.Count} ovens ({_furnaces.Count(f => f.Deployable)} deployable) in {stopwatch.ElapsedMilliseconds}ms");
        }

        private ItemDefinition FindDefinition(string shortName)
        {
            if (string.IsNullOrEmpty(shortName))
                return null;

            return _byShortName.TryGetValue(shortName, out var definition) ? definition : null;
        }

        private FurnaceInfo FindFurnace(string shortName)
        {
            if (string.IsNullOrEmpty(shortName))
                return null;

            return _furnaceByShortName.TryGetValue(shortName, out var furnace) ? furnace : null;
        }

        private string ScopeLabel(string scope)
        {
            if (scope == GlobalScope)
                return "All furnaces";

            if (scope == PlaceholderFurnace)
                return "placeholder";

            var furnace = FindFurnace(scope);
            return furnace != null ? furnace.Label : scope;
        }

        #endregion

        #region Config file access

        private string TargetConfigPath => Path.Combine(Interface.Oxide.ConfigDirectory, TargetConfigName);

        private DateTime DiskStamp()
        {
            try
            {
                return File.Exists(TargetConfigPath)
                    ? File.GetLastWriteTimeUtc(TargetConfigPath)
                    : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private JObject ReadTargetConfig(out string error)
        {
            error = null;

            try
            {
                if (!File.Exists(TargetConfigPath))
                {
                    error = $"{TargetConfigName} does not exist. Load Quick Smelt once so it writes its config.";
                    return null;
                }

                var parsed = JObject.Parse(File.ReadAllText(TargetConfigPath));
                NormalizeConfig(parsed);
                return parsed;
            }
            catch (Exception e)
            {
                error = $"Could not read {TargetConfigName}: {e.Message}";
                return null;
            }
        }

        private static void NormalizeConfig(JObject config)
        {
            if (config[KeyUsePermission] == null)
            {
                config[KeyUsePermission] = true;
            }

            if (config[KeyDebug] == null)
            {
                config[KeyDebug] = false;
            }

            foreach (var key in new[] { KeySpeed, KeyFuelSpeed, KeyFuelUsage, KeyFrequency, KeyOutput, KeyWhitelist, KeyBlacklist })
            {
                if (!(config[key] is JObject))
                {
                    config[key] = new JObject();
                }
            }
        }

        private bool WriteTargetConfig(JObject config, out string error)
        {
            error = null;

            try
            {
                File.WriteAllText(TargetConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
                return true;
            }
            catch (Exception e)
            {
                error = $"Could not write {TargetConfigName}: {e.Message}";
                return false;
            }
        }

        private void ReloadTargetPlugin()
        {
            rust.RunServerCommand("oxide.reload", TargetPluginName);
        }

        #endregion

        #region Model

        private static string FmtNum(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static bool TryNum(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string S(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static JObject Map(JObject config, string key)
        {
            return (JObject)config[key];
        }

        private static string RateKey(RateField field)
        {
            switch (field)
            {
                case RateField.FuelSpeed: return KeyFuelSpeed;
                case RateField.FuelUsage: return KeyFuelUsage;
                case RateField.Frequency: return KeyFrequency;
                default: return KeySpeed;
            }
        }

        // Fuel usage and smelting frequency deserialize into Dictionary<string, int> on the
        // Quick Smelt side, so a fractional value there would throw and silently drop the
        // admin's whole config back to defaults.
        private static bool RateIsInteger(RateField field)
        {
            return field == RateField.FuelUsage || field == RateField.Frequency;
        }

        private static float RateDefault(RateField field)
        {
            return 1f;
        }

        private static string RateLabel(RateField field)
        {
            switch (field)
            {
                case RateField.FuelSpeed: return "Fuel burn speed";
                case RateField.FuelUsage: return "Fuel consumed";
                case RateField.Frequency: return "Smelt frequency";
                default: return "Smelt speed";
            }
        }

        private static float GetRate(JObject config, string scope, RateField field)
        {
            var token = Map(config, RateKey(field))[scope];
            if (token == null)
                return RateDefault(field);

            return token.Type == JTokenType.Float || token.Type == JTokenType.Integer
                ? token.Value<float>()
                : RateDefault(field);
        }

        private static bool HasRate(JObject config, string scope, RateField field)
        {
            return Map(config, RateKey(field))[scope] != null;
        }

        private static void SetRate(JObject config, string scope, RateField field, float value)
        {
            var map = Map(config, RateKey(field));

            if (RateIsInteger(field))
            {
                map[scope] = Mathf.Clamp(Mathf.RoundToInt(value), 0, 1000);
            }
            else
            {
                map[scope] = Mathf.Clamp(value, 0f, 1000f);
            }
        }

        // A scope exists if it appears as a key in any of the six per-furnace maps.
        private List<string> Scopes(JObject config)
        {
            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GlobalScope };

            foreach (var key in new[] { KeySpeed, KeyFuelSpeed, KeyFuelUsage, KeyFrequency, KeyOutput, KeyWhitelist, KeyBlacklist })
            {
                foreach (var property in Map(config, key).Properties())
                {
                    scopes.Add(property.Name);
                }
            }

            return scopes
                .OrderByDescending(s => s == GlobalScope)
                .ThenBy(ScopeLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static JObject OutputMap(JObject config, string scope, bool create)
        {
            var outputs = Map(config, KeyOutput);
            if (outputs[scope] is JObject existing)
                return existing;

            if (!create)
                return null;

            var fresh = new JObject();
            outputs[scope] = fresh;
            return fresh;
        }

        private static JArray ListFor(JObject config, string key, string scope, bool create)
        {
            var lists = Map(config, key);
            if (lists[scope] is JArray existing)
                return existing;

            if (!create)
                return null;

            var fresh = new JArray();
            lists[scope] = fresh;
            return fresh;
        }

        private static List<string> ListValues(JObject config, string key, string scope)
        {
            var list = ListFor(config, key, scope, false);
            return list == null
                ? new List<string>()
                : list.Select(t => t.ToString()).ToList();
        }

        private static void ListAdd(JObject config, string key, string scope, string shortName)
        {
            var list = ListFor(config, key, scope, true);
            if (list.Any(t => string.Equals(t.ToString(), shortName, StringComparison.OrdinalIgnoreCase)))
                return;

            list.Add(new JValue(shortName));
        }

        private static void ListRemove(JObject config, string key, string scope, string shortName)
        {
            var list = ListFor(config, key, scope, false);
            if (list == null)
                return;

            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (string.Equals(list[i].ToString(), shortName, StringComparison.OrdinalIgnoreCase))
                {
                    list.RemoveAt(i);
                }
            }
        }

        private static void RemoveScope(JObject config, string scope)
        {
            foreach (var key in new[] { KeySpeed, KeyFuelSpeed, KeyFuelUsage, KeyFrequency, KeyOutput, KeyWhitelist, KeyBlacklist })
            {
                Map(config, key).Remove(scope);
            }
        }

        // Counts the entries Quick Smelt ships as documentation, which produce no behaviour
        // and are easy to mistake for real configuration.
        private static int CountPlaceholders(JObject config)
        {
            var count = 0;

            foreach (var key in new[] { KeySpeed, KeyFuelSpeed, KeyFuelUsage, KeyFrequency, KeyOutput, KeyWhitelist, KeyBlacklist })
            {
                if (Map(config, key)[PlaceholderFurnace] != null)
                {
                    count++;
                }
            }

            foreach (var property in Map(config, KeyOutput).Properties())
            {
                if (property.Value is JObject inner && inner[PlaceholderItem] != null)
                {
                    count++;
                }
            }

            foreach (var key in new[] { KeyWhitelist, KeyBlacklist })
            {
                foreach (var property in Map(config, key).Properties())
                {
                    if (property.Value is JArray list
                        && list.Any(t => string.Equals(t.ToString(), PlaceholderItem, StringComparison.OrdinalIgnoreCase)))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void StripPlaceholders(JObject config)
        {
            RemoveScope(config, PlaceholderFurnace);

            foreach (var property in Map(config, KeyOutput).Properties().ToList())
            {
                if (property.Value is JObject inner)
                {
                    inner.Remove(PlaceholderItem);
                }
            }

            foreach (var key in new[] { KeyWhitelist, KeyBlacklist })
            {
                foreach (var property in Map(config, key).Properties().ToList())
                {
                    if (property.Value is JArray list)
                    {
                        for (var i = list.Count - 1; i >= 0; i--)
                        {
                            if (string.Equals(list[i].ToString(), PlaceholderItem, StringComparison.OrdinalIgnoreCase))
                            {
                                list.RemoveAt(i);
                            }
                        }
                    }
                }
            }
        }

        private List<ItemDefinition> SearchCatalog(string query, bool relevantOnly, PickTarget target)
        {
            IEnumerable<ItemDefinition> source = _catalog;

            if (relevantOnly)
            {
                var allowed = target == PickTarget.OutputItem ? _smeltOutputs : _smeltableInputs;
                source = source.Where(d => allowed.Contains(d.shortname));
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.Trim();
                source = source.Where(d =>
                    d.shortname.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || DisplayNameOf(d).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return source.ToList();
        }

        private List<FurnaceInfo> SearchFurnaces(string query)
        {
            IEnumerable<FurnaceInfo> source = _furnaces;

            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.Trim();
                source = source.Where(f =>
                    f.ShortName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || f.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return source.ToList();
        }

        #endregion

        #region Commands

        private bool HasAccess(BasePlayer player)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, PermissionAdmin);
        }

        private static string[] GetArgs(ConsoleSystem.Arg arg)
        {
            var count = arg.Args != null ? arg.Args.Length : 0;
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = arg.GetString(i);
            }

            return result;
        }

        private static string ArgAt(string[] args, int index)
        {
            return args != null && index >= 0 && index < args.Length ? args[index] : null;
        }

        private static string JoinFrom(string[] args, int index)
        {
            if (args == null || index >= args.Length)
                return "";

            return string.Join(" ", args.Skip(index)).Trim();
        }

        [ChatCommand("quicksmeltui")]
        private void ChatUi(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player))
            {
                SendReply(player, "You need the <color=#e0a020>quicksmelt.admin</color> permission.");
                return;
            }

            OpenUi(player);
        }

        [ConsoleCommand("quicksmeltui")]
        private void ConsoleUi(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasAccess(player))
                return;

            var args = GetArgs(arg);
            var sub = (ArgAt(args, 0) ?? "open").ToLowerInvariant();

            if (sub == "open")
            {
                OpenUi(player);
                return;
            }

            if (sub == "close")
            {
                CloseUi(player);
                return;
            }

            var state = GetState(player);
            if (state == null)
                return;

            HandleCommand(player, state, sub, args);
        }

        private UiState GetState(BasePlayer player)
        {
            return _states.TryGetValue(player.userID, out var state) ? state : null;
        }

        private void OpenUi(BasePlayer player)
        {
            var config = ReadTargetConfig(out var error);
            if (config == null)
            {
                SendReply(player, $"<color=#d04030>{error}</color>");
                return;
            }

            var state = new UiState
            {
                Config = config,
                DiskStamp = DiskStamp(),
            };

            if (plugins.Find(TargetPluginName) == null)
            {
                state.Status = "Quick Smelt is not loaded. Edits will apply when it loads.";
                state.StatusColor = ColAmber;
            }

            _states[player.userID] = state;
            Draw(player, state);
        }

        private void CloseUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiRoot);
            _states.Remove(player.userID);
        }

        private void HandleCommand(BasePlayer player, UiState state, string sub, string[] args)
        {
            switch (sub)
            {
                case "tab":
                {
                    if (Enum.TryParse(ArgAt(args, 1) ?? "", true, out Tab tab))
                    {
                        state.Tab = tab;
                        state.EditScope = null;
                        state.Picker = null;
                        state.DeleteArmed = false;
                    }

                    break;
                }

                case "toggle":
                {
                    var id = (ArgAt(args, 1) ?? "").ToLowerInvariant();
                    var key = id == "debug" ? KeyDebug : id == "useperm" ? KeyUsePermission : null;
                    if (key != null)
                    {
                        var current = state.Config[key] != null
                                      && state.Config[key].Type == JTokenType.Boolean
                                      && state.Config.Value<bool>(key);

                        state.Config[key] = !current;
                        state.Dirty = true;
                    }

                    break;
                }

                case "goto":
                {
                    var scope = ArgAt(args, 2);
                    if (Enum.TryParse(ArgAt(args, 1) ?? "", true, out Tab target) && !string.IsNullOrEmpty(scope))
                    {
                        state.Tab = target;
                        state.SelectedScope = scope;
                        state.EditScope = null;
                        state.DeleteArmed = false;
                        state.OutputPage = 0;
                        state.ListPage = 0;
                    }

                    break;
                }

                case "scope":
                {
                    var scope = ArgAt(args, 1);
                    if (!string.IsNullOrEmpty(scope))
                    {
                        state.SelectedScope = scope;
                        state.OutputPage = 0;
                        state.ListPage = 0;
                    }

                    break;
                }

                case "fpage":
                {
                    if (int.TryParse(ArgAt(args, 1), out var page))
                    {
                        state.FurnacePage = Mathf.Max(0, page);
                    }

                    break;
                }

                case "rpage":
                {
                    if (int.TryParse(ArgAt(args, 1), out var page))
                    {
                        state.RatesPage = Mathf.Max(0, page);
                    }

                    break;
                }

                case "opage":
                {
                    if (int.TryParse(ArgAt(args, 1), out var page))
                    {
                        state.OutputPage = Mathf.Max(0, page);
                    }

                    break;
                }

                case "lpage":
                {
                    if (int.TryParse(ArgAt(args, 1), out var page))
                    {
                        state.ListPage = Mathf.Max(0, page);
                    }

                    break;
                }

                case "fopen":
                {
                    var scope = ArgAt(args, 1);
                    if (!string.IsNullOrEmpty(scope))
                    {
                        state.EditScope = scope;
                        state.DeleteArmed = false;
                    }

                    break;
                }

                case "fclose":
                {
                    state.EditScope = null;
                    state.DeleteArmed = false;
                    break;
                }

                case "fadd":
                {
                    state.Tab = Tab.Furnaces;
                    state.Picker = new PickerState { Target = PickTarget.NewFurnace };
                    break;
                }

                case "fset":
                {
                    var scope = ArgAt(args, 1);
                    if (!string.IsNullOrEmpty(scope)
                        && Enum.TryParse(ArgAt(args, 2) ?? "", true, out RateField field)
                        && TryNum(JoinFrom(args, 3), out var value))
                    {
                        SetRate(state.Config, scope, field, value);
                        state.Dirty = true;
                    }

                    break;
                }

                case "fclear":
                {
                    var scope = ArgAt(args, 1);
                    if (!string.IsNullOrEmpty(scope) && Enum.TryParse(ArgAt(args, 2) ?? "", true, out RateField field))
                    {
                        Map(state.Config, RateKey(field)).Remove(scope);
                        state.Dirty = true;
                    }

                    break;
                }

                case "fdelarm":
                {
                    state.DeleteArmed = true;
                    break;
                }

                case "fdel":
                {
                    var scope = ArgAt(args, 1);
                    if (!string.IsNullOrEmpty(scope) && scope != GlobalScope && state.DeleteArmed)
                    {
                        RemoveScope(state.Config, scope);
                        state.Dirty = true;
                        state.EditScope = null;
                        state.DeleteArmed = false;

                        if (string.Equals(state.SelectedScope, scope, StringComparison.OrdinalIgnoreCase))
                        {
                            state.SelectedScope = GlobalScope;
                        }

                        state.Status = $"Removed {ScopeLabel(scope)} from every list. Press Save and apply to write it.";
                        state.StatusColor = ColTextDim;
                    }

                    break;
                }

                case "omadd":
                {
                    state.Picker = new PickerState
                    {
                        Target = PickTarget.OutputItem,
                        Scope = ArgAt(args, 1) ?? state.SelectedScope,
                    };

                    break;
                }

                case "omset":
                {
                    var scope = ArgAt(args, 1);
                    var shortName = ArgAt(args, 2);
                    if (!string.IsNullOrEmpty(scope) && !string.IsNullOrEmpty(shortName)
                        && TryNum(JoinFrom(args, 3), out var value))
                    {
                        OutputMap(state.Config, scope, true)[shortName] = Mathf.Clamp(value, 0f, 1000f);
                        state.Dirty = true;
                    }

                    break;
                }

                case "omdel":
                {
                    var scope = ArgAt(args, 1);
                    var shortName = ArgAt(args, 2);
                    if (!string.IsNullOrEmpty(scope) && !string.IsNullOrEmpty(shortName))
                    {
                        OutputMap(state.Config, scope, false)?.Remove(shortName);
                        state.Dirty = true;
                    }

                    break;
                }

                case "wladd":
                case "bladd":
                {
                    state.Picker = new PickerState
                    {
                        Target = sub == "wladd" ? PickTarget.WhitelistItem : PickTarget.BlacklistItem,
                        Scope = ArgAt(args, 1) ?? state.SelectedScope,
                    };

                    break;
                }

                case "wldel":
                case "bldel":
                {
                    var scope = ArgAt(args, 1);
                    var shortName = ArgAt(args, 2);
                    if (!string.IsNullOrEmpty(scope) && !string.IsNullOrEmpty(shortName))
                    {
                        ListRemove(state.Config, sub == "wldel" ? KeyWhitelist : KeyBlacklist, scope, shortName);
                        state.Dirty = true;
                    }

                    break;
                }

                case "pksearch":
                {
                    if (state.Picker != null)
                    {
                        state.Picker.Search = JoinFrom(args, 1);
                        state.Picker.Page = 0;
                    }

                    break;
                }

                case "pkpage":
                {
                    if (state.Picker != null && int.TryParse(ArgAt(args, 1), out var page))
                    {
                        state.Picker.Page = Mathf.Max(0, page);
                    }

                    break;
                }

                case "pkrel":
                {
                    if (state.Picker != null)
                    {
                        state.Picker.RelevantOnly = !state.Picker.RelevantOnly;
                        state.Picker.Page = 0;
                    }

                    break;
                }

                case "pkpick":
                {
                    HandlePick(state, ArgAt(args, 1));
                    break;
                }

                case "pkcancel":
                {
                    state.Picker = null;
                    break;
                }

                case "cleanup":
                {
                    var removed = CountPlaceholders(state.Config);
                    StripPlaceholders(state.Config);
                    state.Dirty = true;
                    state.Status = removed == 0
                        ? "No placeholder entries found."
                        : $"Removed {removed} placeholder entr{(removed == 1 ? "y" : "ies")}. Press Save and apply to write it.";
                    state.StatusColor = ColTextDim;
                    break;
                }

                case "save":
                {
                    SaveAndApply(player, state, force: false);
                    break;
                }

                case "saveforce":
                {
                    SaveAndApply(player, state, force: true);
                    break;
                }

                case "revert":
                {
                    var config = ReadTargetConfig(out var error);
                    if (config == null)
                    {
                        state.Status = error;
                        state.StatusColor = ColRed;
                    }
                    else
                    {
                        state.Config = config;
                        state.DiskStamp = DiskStamp();
                        state.Dirty = false;
                        state.ConflictArmed = false;
                        state.EditScope = null;
                        state.Picker = null;
                        state.Status = "Reloaded from disk. Staged edits discarded.";
                        state.StatusColor = ColTextDim;
                    }

                    break;
                }
            }

            Draw(player, state);
        }

        private void HandlePick(UiState state, string pickedName)
        {
            var picker = state.Picker;
            if (picker == null || string.IsNullOrEmpty(pickedName))
                return;

            switch (picker.Target)
            {
                case PickTarget.NewFurnace:
                {
                    var furnace = FindFurnace(pickedName);
                    if (furnace != null)
                    {
                        // Seeding the speed map is what makes the scope exist. Every other
                        // map stays absent so Quick Smelt keeps falling back to global.
                        if (!HasRate(state.Config, furnace.ShortName, RateField.Speed))
                        {
                            SetRate(state.Config, furnace.ShortName, RateField.Speed,
                                GetRate(state.Config, GlobalScope, RateField.Speed));
                            state.Dirty = true;
                        }

                        state.SelectedScope = furnace.ShortName;
                        state.EditScope = furnace.ShortName;
                    }

                    break;
                }

                case PickTarget.OutputItem:
                {
                    if (FindDefinition(pickedName) != null && OutputMap(state.Config, picker.Scope, true)[pickedName] == null)
                    {
                        OutputMap(state.Config, picker.Scope, true)[pickedName] = 1f;
                        state.Dirty = true;
                    }

                    break;
                }

                case PickTarget.WhitelistItem:
                {
                    if (FindDefinition(pickedName) != null)
                    {
                        ListAdd(state.Config, KeyWhitelist, picker.Scope, pickedName);
                        state.Dirty = true;
                    }

                    break;
                }

                case PickTarget.BlacklistItem:
                {
                    if (FindDefinition(pickedName) != null)
                    {
                        ListAdd(state.Config, KeyBlacklist, picker.Scope, pickedName);
                        state.Dirty = true;
                    }

                    break;
                }
            }

            state.Picker = null;
        }

        private void SaveAndApply(BasePlayer player, UiState state, bool force)
        {
            var stamp = DiskStamp();
            if (!force && state.DiskStamp != DateTime.MinValue && stamp != state.DiskStamp)
            {
                state.ConflictArmed = true;
                state.Status = "The config changed on disk since you opened this. Press again to overwrite, or Revert.";
                state.StatusColor = ColAmber;
                return;
            }

            if (!WriteTargetConfig(state.Config, out var error))
            {
                state.Status = error;
                state.StatusColor = ColRed;
                return;
            }

            state.Dirty = false;
            state.ConflictArmed = false;
            state.DiskStamp = DiskStamp();
            state.Status = "Saved. Reloading Quick Smelt, active furnaces restart...";
            state.StatusColor = ColGreen;

            ReloadTargetPlugin();

            timer.Once(4f, () =>
            {
                if (player == null || !player.IsConnected)
                    return;

                var live = GetState(player);
                if (live == null || live != state)
                    return;

                if (plugins.Find(TargetPluginName) == null)
                {
                    live.Status = "Quick Smelt did not come back. Check the Oxide log for a compile or config error.";
                    live.StatusColor = ColRed;
                }
                else
                {
                    live.Status = "Saved and applied.";
                    live.StatusColor = ColGreen;
                    live.DiskStamp = DiskStamp();
                }

                Draw(player, live);
            });
        }

        #endregion

        #region CUI helpers

        private int _uid;

        private string NextName()
        {
            return "QSUI.e" + (++_uid);
        }

        private static CuiRectTransformComponent Rect(float x, float y, float w, float h)
        {
            return new CuiRectTransformComponent
            {
                AnchorMin = "0 0",
                AnchorMax = "0 0",
                OffsetMin = $"{S(x)} {S(y)}",
                OffsetMax = $"{S(x + w)} {S(y + h)}",
            };
        }

        private string AddPanel(CuiElementContainer c, string parent, float x, float y, float w, float h,
            string color, string name = null)
        {
            var id = name ?? NextName();
            c.Add(new CuiElement
            {
                Parent = parent,
                Name = id,
                Components = { new CuiImageComponent { Color = color }, Rect(x, y, w, h) },
            });

            return id;
        }

        private string AddCard(CuiElementContainer c, string parent, string command,
            float x, float y, float w, float h, string color)
        {
            var id = NextName();
            c.Add(new CuiElement
            {
                Parent = parent,
                Name = id,
                Components =
                {
                    new CuiButtonComponent { Color = color, Command = command },
                    Rect(x, y, w, h),
                },
            });

            return id;
        }

        private void AddText(CuiElementContainer c, string parent, string text, float x, float y, float w, float h,
            int size, string color, TextAnchor align = TextAnchor.MiddleLeft)
        {
            c.Add(new CuiElement
            {
                Parent = parent,
                Name = NextName(),
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = text ?? "",
                        FontSize = size,
                        Color = color,
                        Align = align,
                        VerticalOverflow = VerticalWrapMode.Overflow,
                        BlocksRaycast = false,
                    },
                    Rect(x, y, w, h),
                },
            });
        }

        private void AddButton(CuiElementContainer c, string parent, string text, string command,
            float x, float y, float w, float h, string color, string textColor = ColText, int size = 12)
        {
            var id = AddCard(c, parent, command, x, y, w, h, color);
            AddText(c, id, text, 0, 0, w, h, size, textColor, TextAnchor.MiddleCenter);
        }

        private void AddInput(CuiElementContainer c, string parent, string text, string command,
            float x, float y, float w, float h, int size = 12, int charsLimit = 96,
            TextAnchor align = TextAnchor.MiddleLeft, string textColor = ColText)
        {
            var id = AddPanel(c, parent, x, y, w, h, ColField);
            c.Add(new CuiElement
            {
                Parent = id,
                Name = NextName(),
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = text ?? "",
                        FontSize = size,
                        Color = textColor,
                        Align = align,
                        CharsLimit = charsLimit,
                        Command = command,
                        NeedsKeyboard = true,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = "6 0",
                        OffsetMax = "-6 0",
                    },
                },
            });
        }

        private void AddItemIcon(CuiElementContainer c, string parent, ItemDefinition definition,
            float x, float y, float w, float h)
        {
            if (definition == null)
            {
                var id = AddPanel(c, parent, x, y, w, h, ColField);
                AddText(c, id, "?", 0, 0, w, h, Mathf.RoundToInt(h * 0.5f), ColTextDim, TextAnchor.MiddleCenter);
                return;
            }

            c.Add(new CuiElement
            {
                Parent = parent,
                Name = NextName(),
                Components =
                {
                    new CuiImageComponent { ItemId = definition.itemid, SkinId = 0, BlocksRaycast = false },
                    Rect(x, y, w, h),
                },
            });
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text ?? "";

            return text.Substring(0, max - 1) + "…";
        }

        #endregion

        #region Frame

        private void Draw(BasePlayer player, UiState state)
        {
            var c = new CuiElementContainer();

            c.Add(new CuiElement
            {
                Parent = "Overlay",
                Name = UiRoot,
                DestroyUi = UiRoot,
                Components =
                {
                    new CuiImageComponent { Color = ColShade },
                    new CuiNeedsCursorComponent(),
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                },
            });

            var win = NextName();
            c.Add(new CuiElement
            {
                Parent = UiRoot,
                Name = win,
                Components =
                {
                    new CuiImageComponent { Color = ColWindow },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = $"{S(-WinW / 2f)} {S(-WinH / 2f)}",
                        OffsetMax = $"{S(WinW / 2f)} {S(WinH / 2f)}",
                    },
                },
            });

            DrawTitleBar(c, win, state);
            DrawTabs(c, win, state);
            DrawFooter(c, win, state);

            var content = AddPanel(c, win, Pad, ContentY, ContentW, ContentH, ColPanel);

            // The whole window ships as one console command and Rust drops those above
            // 100000 bytes, so anything a modal hides is not drawn at all.
            var modalOpen = state.Picker != null || state.EditScope != null;

            if (!modalOpen)
            {
                switch (state.Tab)
                {
                    case Tab.Furnaces:
                        DrawFurnacesTab(c, content, state);
                        break;
                    case Tab.Rates:
                        DrawRatesTab(c, content, state);
                        break;
                    case Tab.Output:
                        DrawOutputTab(c, content, state);
                        break;
                    case Tab.Lists:
                        DrawListsTab(c, content, state);
                        break;
                    default:
                        DrawGeneralTab(c, content, state);
                        break;
                }
            }

            if (state.Picker != null)
            {
                DrawPicker(c, win, state);
            }
            else if (state.EditScope != null)
            {
                DrawFurnaceEditor(c, win, state);
            }

            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.AddUi(player, c);
        }

        private void DrawTitleBar(CuiElementContainer c, string win, UiState state)
        {
            var bar = AddPanel(c, win, 0, WinH - TitleH, WinW, TitleH, ColBar);

            AddText(c, bar, "Quick Smelt", 16, 0, 300, TitleH, 17, ColText);

            var loaded = plugins.Find(TargetPluginName) != null;
            AddText(c, bar, loaded ? "plugin loaded" : "plugin NOT loaded", 150, 0, 200, TitleH, 11,
                loaded ? ColTextDim : ColRed);

            if (state.Dirty)
            {
                var badge = AddPanel(c, bar, WinW - 220, 10, 110, 20, ColAmber);
                AddText(c, badge, "UNSAVED", 0, 0, 110, 20, 11, "0.1 0.1 0.1 1", TextAnchor.MiddleCenter);
            }

            AddButton(c, bar, "X", "quicksmeltui close", WinW - 44, 8, 32, 24, ColRed, ColText, 13);
        }

        private void DrawTabs(CuiElementContainer c, string win, UiState state)
        {
            var bar = AddPanel(c, win, 0, WinH - TitleH - TabH, WinW, TabH, ColWindow);

            var labels = new[]
            {
                new KeyValuePair<Tab, string>(Tab.General, "General"),
                new KeyValuePair<Tab, string>(Tab.Furnaces, "Furnaces"),
                new KeyValuePair<Tab, string>(Tab.Rates, "Rates"),
                new KeyValuePair<Tab, string>(Tab.Output, "Output"),
                new KeyValuePair<Tab, string>(Tab.Lists, "Allow and block"),
            };

            var x = Pad;
            foreach (var pair in labels)
            {
                var active = state.Tab == pair.Key;
                AddButton(c, bar, pair.Value, $"quicksmeltui tab {pair.Key}", x, 4, 150, TabH - 8,
                    active ? ColAccent : ColCard, active ? ColText : ColTextDim, 13);
                x += 156;
            }
        }

        private void DrawFooter(CuiElementContainer c, string win, UiState state)
        {
            var bar = AddPanel(c, win, 0, 0, WinW, FootH, ColBar);

            AddText(c, bar, state.Status, 16, 0, WinW - 400, FootH, 12, state.StatusColor);
            AddButton(c, bar, "Revert", "quicksmeltui revert", WinW - 330, 9, 100, 26, ColNeutral, ColText, 12);

            var saveLabel = state.ConflictArmed ? "Overwrite and apply" : "Save and apply";
            var saveCommand = state.ConflictArmed ? "quicksmeltui saveforce" : "quicksmeltui save";
            AddButton(c, bar, saveLabel, saveCommand, WinW - 220, 9, 204, 26,
                state.ConflictArmed ? ColAmber : (state.Dirty ? ColGreen : ColNeutral),
                state.ConflictArmed ? "0.1 0.1 0.1 1" : ColText, 13);
        }

        #endregion

        #region General tab

        private void DrawGeneralTab(CuiElementContainer c, string content, UiState state)
        {
            var top = ContentH - 14;

            AddText(c, content, "General", 16, top - 22, 400, 22, 15, ColText);
            top -= 34;

            var usePermission = state.Config.Value<bool>(KeyUsePermission);
            var row = AddPanel(c, content, 16, top - (RowH - 6), ContentW - 32, RowH - 6, ColCard);
            AddText(c, row, "Require permission", 12, 20, 300, 20, 13, ColText);
            AddText(c, row, "When on, only furnaces owned by a player with quicksmelt.use are boosted",
                12, 4, 600, 16, 10, ColTextDim);
            AddButton(c, row, usePermission ? "ON" : "OFF", "quicksmeltui toggle useperm", ContentW - 124, 8, 80, 24,
                usePermission ? ColGreen : ColNeutral, ColText, 12);
            top -= RowH;

            var debug = state.Config.Value<bool>(KeyDebug);
            var row2 = AddPanel(c, content, 16, top - (RowH - 6), ContentW - 32, RowH - 6, ColCard);
            AddText(c, row2, "Debug logging", 12, 20, 300, 20, 13, ColText);
            AddText(c, row2, "Writes a line to the server console on every smelting tick. Noisy, leave off",
                12, 4, 600, 16, 10, ColTextDim);
            AddButton(c, row2, debug ? "ON" : "OFF", "quicksmeltui toggle debug", ContentW - 124, 8, 80, 24,
                debug ? ColGreen : ColNeutral, ColText, 12);
            top -= RowH + 8;

            AddText(c, content, "How this UI applies changes", 16, top - 24, 400, 22, 15, ColText);
            top -= 32;

            var info = AddPanel(c, content, 16, top - 92, ContentW - 32, 88, ColCard);
            AddText(c, info, $"Config file: oxide/config/{TargetConfigName}", 12, 62, 900, 20, 12, ColTextDim);
            AddText(c, info, "Edits stage in memory. Save and apply writes the file, then reloads Quick Smelt.",
                12, 40, 1000, 20, 12, ColTextDim);
            AddText(c, info, "Reloading rebuilds every furnace controller, so lit furnaces stop and restart once.",
                12, 18, 1000, 20, 12, ColAmber);
            top -= 102;

            var scopes = Scopes(state.Config);
            var placeholders = CountPlaceholders(state.Config);

            AddText(c, content, "Current contents", 16, top - 24, 400, 22, 15, ColText);
            top -= 32;

            var stats = AddPanel(c, content, 16, top - 74, ContentW - 32, 70, ColCard);
            var outputCount = Map(state.Config, KeyOutput).Properties()
                .Sum(p => p.Value is JObject inner ? inner.Count : 0);
            var whitelistCount = Map(state.Config, KeyWhitelist).Properties()
                .Sum(p => p.Value is JArray list ? list.Count : 0);
            var blacklistCount = Map(state.Config, KeyBlacklist).Properties()
                .Sum(p => p.Value is JArray list ? list.Count : 0);

            AddText(c, stats, $"Furnace scopes configured: {scopes.Count}", 12, 44, 460, 20, 12, ColTextDim);
            AddText(c, stats, $"Ovens known to this server: {_furnaces.Count}", 492, 44, 460, 20, 12, ColTextDim);
            AddText(c, stats, $"Output multiplier entries: {outputCount}", 12, 24, 460, 20, 12, ColTextDim);
            AddText(c, stats, $"Allow list entries: {whitelistCount}", 492, 24, 460, 20, 12, ColTextDim);
            AddText(c, stats, $"Block list entries: {blacklistCount}", 12, 4, 460, 20, 12, ColTextDim);
            AddText(c, stats, $"Smeltable items in game: {_smeltableInputs.Count}", 492, 4, 460, 20, 12, ColTextDim);
            top -= 84;

            if (placeholders > 0)
            {
                var warn = AddPanel(c, content, 16, top - 40, ContentW - 32, 36, ColCard);
                AddText(c, warn, $"Quick Smelt's shipped placeholder entries are still present ({placeholders}). They do nothing.",
                    12, 0, 760, 36, 12, ColAmber);
                AddButton(c, warn, "Remove placeholders", "quicksmeltui cleanup", ContentW - 216, 6, 184, 24,
                    ColAmber, "0.1 0.1 0.1 1", 12);
            }
        }

        #endregion

        #region Furnaces tab

        private string RateSummary(JObject config, string scope)
        {
            var speed = GetRate(config, scope, RateField.Speed);
            var fuel = GetRate(config, scope, RateField.FuelUsage);
            return $"speed x{FmtNum(speed)} · fuel x{FmtNum(fuel)}";
        }

        private void DrawFurnacesTab(CuiElementContainer c, string content, UiState state)
        {
            var toolbarY = ContentH - 40;

            AddButton(c, content, "+ Add furnace", "quicksmeltui fadd", 12, toolbarY, 140, 28, ColGreen, ColText, 12);
            AddText(c, content, "A furnace with no entry of its own inherits every value from All furnaces.",
                162, toolbarY, 640, 28, 11, ColTextDim);

            var scopes = Scopes(state.Config);
            var pages = Mathf.Max(1, Mathf.CeilToInt(scopes.Count / (float)FurnacePageSize));
            state.FurnacePage = Mathf.Clamp(state.FurnacePage, 0, pages - 1);

            AddText(c, content, $"{scopes.Count} scope{(scopes.Count == 1 ? "" : "s")}",
                ContentW - 260, toolbarY, 240, 28, 11, ColTextDim, TextAnchor.MiddleRight);

            var gridTop = ContentH - 76;

            for (var i = 0; i < FurnacePageSize; i++)
            {
                var index = state.FurnacePage * FurnacePageSize + i;
                if (index >= scopes.Count)
                    break;

                var scope = scopes[index];
                var col = i % FurnaceCols;
                var line = i / FurnaceCols;
                var cx = 6 + col * (FCardW - 2 + 8);
                var cy = gridTop - (line + 1) * FCardH - line * 8;

                var isGlobal = scope == GlobalScope;
                var isPlaceholder = scope == PlaceholderFurnace;
                var known = isGlobal || FindFurnace(scope) != null;

                var color = isGlobal ? "0.160 0.215 0.285 1" : isPlaceholder || !known ? "0.270 0.160 0.150 1" : ColCard;
                var card = AddCard(c, content, $"quicksmeltui fopen {scope}", cx, cy, FCardW - 2, FCardH, color);

                var furnace = FindFurnace(scope);
                AddItemIcon(c, card, furnace?.Item, (FCardW - 2 - 50) / 2f, FCardH - 56, 50, 50);

                AddText(c, card, Trim(ScopeLabel(scope), 20), 3, 32, FCardW - 8, 20, 9, ColText,
                    TextAnchor.MiddleCenter);

                var detail = isPlaceholder ? "placeholder, delete me"
                    : !known ? "unknown furnace"
                    : RateSummary(state.Config, scope);

                AddText(c, card, detail, 3, 10, FCardW - 8, 18, 8,
                    known && !isPlaceholder ? ColTextDim : ColRed, TextAnchor.MiddleCenter);
            }

            if (pages > 1)
            {
                AddButton(c, content, "<", $"quicksmeltui fpage {state.FurnacePage - 1}", 12, 40, 30, 24, ColNeutral, ColText, 11);
                AddText(c, content, $"page {state.FurnacePage + 1} / {pages}", 48, 40, 120, 24, 11, ColTextDim);
                AddButton(c, content, ">", $"quicksmeltui fpage {state.FurnacePage + 1}", 176, 40, 30, 24, ColNeutral, ColText, 11);
            }
        }

        #endregion

        #region Rates tab

        private void DrawRatesTab(CuiElementContainer c, string content, UiState state)
        {
            var top = ContentH - 14;

            AddText(c, content, "Rates by furnace", 16, top - 22, 300, 22, 15, ColText);
            AddText(c, content, "A dim value is inherited from All furnaces. Typing into it creates an entry for that furnace.",
                180, top - 22, 800, 22, 11, ColTextDim);
            top -= 30;

            var columns = new[] { 330f, 470f, 610f, 750f };
            var header = AddPanel(c, content, 8, top - 24, ContentW - 16, 24, ColCardAlt);
            AddText(c, header, "Furnace", 8, 0, 300, 24, 11, ColText);

            var fields = new[] { RateField.Speed, RateField.FuelSpeed, RateField.FuelUsage, RateField.Frequency };
            for (var i = 0; i < fields.Length; i++)
            {
                AddText(c, header, RateLabel(fields[i]), columns[i] - 8, 0, 130, 24, 11, ColText,
                    TextAnchor.MiddleCenter);
            }

            AddText(c, header, "click a row to open it", ContentW - 200, 0, 180, 24, 10, ColTextDim,
                TextAnchor.MiddleRight);

            top -= 30;

            var scopes = Scopes(state.Config);
            const int rowsPerPage = 10;
            var pages = Mathf.Max(1, Mathf.CeilToInt(scopes.Count / (float)rowsPerPage));
            state.RatesPage = Mathf.Clamp(state.RatesPage, 0, pages - 1);

            for (var i = 0; i < rowsPerPage; i++)
            {
                var index = state.RatesPage * rowsPerPage + i;
                if (index >= scopes.Count)
                    break;

                var scope = scopes[index];
                var rowY = top - (i + 1) * 33 + 5;
                var row = AddCard(c, content, $"quicksmeltui fopen {scope}", 8, rowY, ContentW - 16, 28,
                    scope == GlobalScope ? "0.160 0.215 0.285 1" : ColCard);

                var furnace = FindFurnace(scope);
                AddItemIcon(c, row, furnace?.Item, 4, 3, 22, 22);
                AddText(c, row, Trim(ScopeLabel(scope), 34), 32, 0, 280, 28, 11, ColText);

                for (var f = 0; f < fields.Length; f++)
                {
                    var field = fields[f];
                    var explicitly = HasRate(state.Config, scope, field);
                    var value = explicitly
                        ? GetRate(state.Config, scope, field)
                        : GetRate(state.Config, GlobalScope, field);

                    AddInput(c, row, FmtNum(value), $"quicksmeltui fset {scope} {field}", columns[f] - 8, 3, 114, 22,
                        11, 10, TextAnchor.MiddleCenter, explicitly ? ColText : ColTextDim);
                }
            }

            if (pages > 1)
            {
                AddButton(c, content, "<", $"quicksmeltui rpage {state.RatesPage - 1}", 12, 12, 30, 24, ColNeutral, ColText, 11);
                AddText(c, content, $"page {state.RatesPage + 1} / {pages}", 48, 12, 120, 24, 11, ColTextDim);
                AddButton(c, content, ">", $"quicksmeltui rpage {state.RatesPage + 1}", 176, 12, 30, 24, ColNeutral, ColText, 11);
            }
        }

        #endregion

        #region Scope selector

        private float DrawScopeSelector(CuiElementContainer c, string content, UiState state)
        {
            var scopes = Scopes(state.Config);
            var top = ContentH - 10;

            AddText(c, content, "Furnace", 12, top - 22, 70, 22, 12, ColTextDim);

            const int perRow = 6;
            const float buttonW = 152f;
            var shown = Mathf.Min(scopes.Count, perRow * 2);

            for (var i = 0; i < shown; i++)
            {
                var scope = scopes[i];
                var col = i % perRow;
                var line = i / perRow;
                var bx = 86 + col * (buttonW + 6);
                var by = top - 22 - line * 32;
                var active = string.Equals(state.SelectedScope, scope, StringComparison.OrdinalIgnoreCase);

                AddButton(c, content, Trim(ScopeLabel(scope), 20), $"quicksmeltui scope {scope}", bx, by, buttonW, 26,
                    active ? ColAccent : ColCard, active ? ColText : ColTextDim, 11);
            }

            if (scopes.Count > shown)
            {
                AddText(c, content, $"{scopes.Count - shown} more, open them from the Furnaces tab",
                    86, top - 86, 600, 20, 10, ColAmber);
            }

            return top - (shown > perRow ? 60 : 28);
        }

        #endregion

        #region Output tab

        private void DrawOutputTab(CuiElementContainer c, string content, UiState state)
        {
            var top = DrawScopeSelector(c, content, state) - 12;
            var scope = state.SelectedScope;

            AddText(c, content, $"Output multipliers for {ScopeLabel(scope)}", 12, top - 24, 520, 24, 14, ColText);
            AddButton(c, content, "+ Add item", $"quicksmeltui omadd {scope}", ContentW - 152, top - 26, 140, 26,
                ColGreen, ColText, 12);
            top -= 32;

            AddText(c, content, "Scales the stack size a smelt produces. The item named global covers everything not listed.",
                12, top - 20, 900, 20, 11, ColTextDim);
            top -= 26;

            var map = OutputMap(state.Config, scope, false);
            var entries = map == null
                ? new List<JProperty>()
                : map.Properties().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

            if (entries.Count == 0)
            {
                AddText(c, content, scope == GlobalScope
                        ? "Nothing here. Every smelt produces its vanilla amount."
                        : $"Nothing here. {ScopeLabel(scope)} falls back to the All furnaces list.",
                    12, top - 30, 800, 24, 12, ColTextDim);
                return;
            }

            const int rowsPerCol = 8;
            const float colW = 520f;
            var pageSize = rowsPerCol * 2;
            var pages = Mathf.Max(1, Mathf.CeilToInt(entries.Count / (float)pageSize));
            state.OutputPage = Mathf.Clamp(state.OutputPage, 0, pages - 1);

            for (var i = 0; i < pageSize; i++)
            {
                var index = state.OutputPage * pageSize + i;
                if (index >= entries.Count)
                    break;

                var property = entries[index];
                var col = i / rowsPerCol;
                var line = i % rowsPerCol;
                var rx = 12 + col * (colW + 12);
                var ry = top - (line + 1) * 32 + 4;

                var isGlobalItem = string.Equals(property.Name, GlobalScope, StringComparison.OrdinalIgnoreCase);
                var isPlaceholder = string.Equals(property.Name, PlaceholderItem, StringComparison.OrdinalIgnoreCase);
                var definition = FindDefinition(property.Name);
                var valid = isGlobalItem || definition != null;

                var row = AddPanel(c, content, rx, ry, colW, 28, valid ? ColCard : "0.270 0.160 0.150 1");

                if (isGlobalItem)
                {
                    AddText(c, row, "ALL", 4, 0, 26, 28, 9, ColAccent, TextAnchor.MiddleCenter);
                }
                else
                {
                    AddItemIcon(c, row, definition, 4, 3, 22, 22);
                }

                var label = isGlobalItem ? "Every other item"
                    : isPlaceholder ? property.Name + "  (placeholder)"
                    : definition != null ? DisplayNameOf(definition)
                    : property.Name + "  (unknown item)";

                AddText(c, row, Trim(label, 44), 32, 0, colW - 160, 28, 11, valid ? ColText : ColRed);

                var value = property.Value.Type == JTokenType.Float || property.Value.Type == JTokenType.Integer
                    ? property.Value.Value<float>()
                    : 1f;

                AddInput(c, row, FmtNum(value), $"quicksmeltui omset {scope} {property.Name}", colW - 120, 3, 70, 22,
                    11, 10, TextAnchor.MiddleCenter);
                AddText(c, row, "x", colW - 46, 0, 14, 28, 11, ColTextDim, TextAnchor.MiddleCenter);
                AddButton(c, row, "X", $"quicksmeltui omdel {scope} {property.Name}", colW - 30, 3, 24, 22, ColRed, ColText, 10);
            }

            if (pages > 1)
            {
                AddButton(c, content, "<", $"quicksmeltui opage {state.OutputPage - 1}", 12, 12, 30, 24, ColNeutral, ColText, 11);
                AddText(c, content, $"page {state.OutputPage + 1} / {pages}   ({entries.Count} entries)",
                    48, 12, 260, 24, 11, ColTextDim);
                AddButton(c, content, ">", $"quicksmeltui opage {state.OutputPage + 1}", 316, 12, 30, 24, ColNeutral, ColText, 11);
            }
        }

        #endregion

        #region Allow and block tab

        private void DrawListsTab(CuiElementContainer c, string content, UiState state)
        {
            var top = DrawScopeSelector(c, content, state) - 12;
            var scope = state.SelectedScope;

            AddText(c, content, "The block list wins over the allow list. An allowed item smelts even below its normal temperature.",
                12, top - 20, 1000, 20, 11, ColTextDim);
            top -= 28;

            const float colW = 520f;
            DrawItemList(c, content, state, scope, KeyWhitelist, "Allow list", "wl", 12, top, colW);
            DrawItemList(c, content, state, scope, KeyBlacklist, "Block list", "bl", 12 + colW + 12, top, colW);
        }

        private void DrawItemList(CuiElementContainer c, string content, UiState state, string scope,
            string configKey, string title, string prefix, float x, float top, float w)
        {
            AddText(c, content, title, x, top - 26, 200, 26, 14, ColText);
            AddButton(c, content, "+ Add item", $"quicksmeltui {prefix}add {scope}", x + w - 130, top - 26, 130, 26,
                ColGreen, ColText, 11);

            var values = ListValues(state.Config, configKey, scope);
            var rowTop = top - 34;
            const int maxRows = 10;

            if (values.Count == 0)
            {
                AddText(c, content, scope == GlobalScope ? "Empty." : "Empty, so All furnaces applies.",
                    x, rowTop - 26, w, 24, 11, ColTextDim);
                return;
            }

            for (var i = 0; i < maxRows && i < values.Count; i++)
            {
                var shortName = values[i];
                var definition = FindDefinition(shortName);
                var isPlaceholder = string.Equals(shortName, PlaceholderItem, StringComparison.OrdinalIgnoreCase);
                var ry = rowTop - (i + 1) * 30 + 4;

                var row = AddPanel(c, content, x, ry, w, 26, definition != null ? ColCard : "0.270 0.160 0.150 1");
                AddItemIcon(c, row, definition, 4, 2, 22, 22);

                var label = definition != null
                    ? DisplayNameOf(definition)
                    : shortName + (isPlaceholder ? "  (placeholder)" : "  (unknown item)");

                AddText(c, row, Trim(label, 46), 32, 0, w - 70, 26, 11, definition != null ? ColText : ColRed);
                AddButton(c, row, "X", $"quicksmeltui {prefix}del {scope} {shortName}", w - 30, 2, 24, 22, ColRed, ColText, 10);
            }

            if (values.Count > maxRows)
            {
                AddText(c, content, $"{values.Count - maxRows} more not shown.", x, rowTop - maxRows * 30 - 24,
                    w, 20, 10, ColAmber);
            }
        }

        #endregion

        #region Furnace editor

        private void DrawFurnaceEditor(CuiElementContainer c, string win, UiState state)
        {
            var scope = state.EditScope;
            var isGlobal = scope == GlobalScope;
            var furnace = FindFurnace(scope);
            var known = isGlobal || furnace != null;

            var shade = AddPanel(c, win, 0, 0, WinW, WinH, ColShade);
            AddButton(c, shade, "", "quicksmeltui fclose", 0, 0, WinW, WinH, "0 0 0 0");

            const float dw = 720f;
            const float dh = 440f;
            var dialog = AddPanel(c, shade, (WinW - dw) / 2f, (WinH - dh) / 2f, dw, dh, ColWindow);

            var header = AddPanel(c, dialog, 0, dh - 48, dw, 48, ColBar);
            AddItemIcon(c, header, furnace?.Item, 10, 4, 40, 40);
            AddText(c, header, ScopeLabel(scope), 58, 24, 400, 22, 14, ColText);
            AddText(c, header, isGlobal ? "applies to every furnace with no entry of its own" : scope,
                58, 4, 500, 20, 11, known ? ColTextDim : ColRed);
            AddButton(c, header, "X", "quicksmeltui fclose", dw - 40, 12, 28, 24, ColRed, ColText, 12);

            if (!known)
            {
                AddText(c, dialog, "This short name is not a furnace on this server. Quick Smelt will warn about it at load.",
                    16, dh - 74, dw - 32, 22, 11, ColAmber);
            }

            var fields = new[] { RateField.Speed, RateField.FuelSpeed, RateField.FuelUsage, RateField.Frequency };
            var hints = new[]
            {
                "Items smelted per tick, and fuel consumed per tick",
                "How fast fuel burns down. Higher empties the fuel slot sooner",
                "Whole units of fuel taken each time a unit burns out",
                "Read from the config but never used by Quick Smelt 5.1.15",
            };

            var y = dh - 100;
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var explicitly = HasRate(state.Config, scope, field);
                var value = explicitly ? GetRate(state.Config, scope, field) : GetRate(state.Config, GlobalScope, field);

                var row = AddPanel(c, dialog, 16, y - 40, dw - 32, 38, ColCard);
                AddText(c, row, RateLabel(field), 10, 18, 200, 20, 12, ColText);
                AddText(c, row, hints[i], 10, 2, 420, 16, 9,
                    field == RateField.Frequency ? ColAmber : ColTextDim);

                AddInput(c, row, FmtNum(value), $"quicksmeltui fset {scope} {field}", dw - 250, 7, 100, 24, 12, 10,
                    TextAnchor.MiddleCenter, explicitly ? ColText : ColTextDim);

                if (RateIsInteger(field))
                {
                    AddText(c, row, "whole", dw - 144, 7, 46, 24, 9, ColTextDim, TextAnchor.MiddleCenter);
                }

                if (explicitly && !isGlobal)
                {
                    AddButton(c, row, "Inherit", $"quicksmeltui fclear {scope} {field}", dw - 92, 7, 74, 24,
                        ColNeutral, ColText, 10);
                }
                else if (!explicitly)
                {
                    AddText(c, row, "inherited", dw - 92, 7, 74, 24, 9, ColTextDim, TextAnchor.MiddleCenter);
                }

                y -= 44;
            }

            var outputCount = OutputMap(state.Config, scope, false)?.Count ?? 0;
            var whitelistCount = ListValues(state.Config, KeyWhitelist, scope).Count;
            var blacklistCount = ListValues(state.Config, KeyBlacklist, scope).Count;

            AddButton(c, dialog, $"Output multipliers ({outputCount})", $"quicksmeltui goto Output {scope}",
                16, y - 32, 220, 30, ColAccent, ColText, 12);
            AddButton(c, dialog, $"Allow list ({whitelistCount})", $"quicksmeltui goto Lists {scope}",
                244, y - 32, 220, 30, ColAccent, ColText, 12);
            AddButton(c, dialog, $"Block list ({blacklistCount})", $"quicksmeltui goto Lists {scope}",
                472, y - 32, 220, 30, ColAccent, ColText, 12);

            if (!isGlobal)
            {
                if (state.DeleteArmed)
                {
                    AddButton(c, dialog, "Confirm removal", $"quicksmeltui fdel {scope}", 16, 16, 200, 30,
                        ColRed, ColText, 12);
                    AddText(c, dialog, "Removes this furnace from all seven lists. It falls back to All furnaces.",
                        226, 16, 480, 30, 11, ColAmber);
                }
                else
                {
                    AddButton(c, dialog, "Remove this furnace", "quicksmeltui fdelarm", 16, 16, 200, 30,
                        ColNeutral, ColText, 12);
                }
            }

            AddButton(c, dialog, "Close", "quicksmeltui fclose", dw - 122, 16, 106, 30, ColNeutral, ColText, 12);
        }

        #endregion

        #region Picker

        private void DrawPicker(CuiElementContainer c, string win, UiState state)
        {
            var picker = state.Picker;

            var shade = AddPanel(c, win, 0, 0, WinW, WinH, ColShade);
            AddButton(c, shade, "", "quicksmeltui pkcancel", 0, 0, WinW, WinH, "0 0 0 0");

            const float dw = 920f;
            const float dh = 520f;
            var dialog = AddPanel(c, shade, (WinW - dw) / 2f, (WinH - dh) / 2f, dw, dh, ColWindow);

            string title;
            switch (picker.Target)
            {
                case PickTarget.NewFurnace:
                    title = "Choose a furnace to configure";
                    break;
                case PickTarget.OutputItem:
                    title = $"Add an output multiplier for {ScopeLabel(picker.Scope)}";
                    break;
                case PickTarget.WhitelistItem:
                    title = $"Add to the allow list for {ScopeLabel(picker.Scope)}";
                    break;
                default:
                    title = $"Add to the block list for {ScopeLabel(picker.Scope)}";
                    break;
            }

            var header = AddPanel(c, dialog, 0, dh - 48, dw, 48, ColBar);
            AddText(c, header, title, 14, 0, 700, 48, 14, ColText);
            AddButton(c, header, "X", "quicksmeltui pkcancel", dw - 40, 12, 28, 24, ColRed, ColText, 12);

            var searchY = dh - 86;
            AddText(c, dialog, "Search", 12, searchY, 54, 28, 11, ColTextDim);
            AddInput(c, dialog, picker.Search, "quicksmeltui pksearch", 68, searchY, 400, 28, 12, 48);
            AddButton(c, dialog, "Clear", "quicksmeltui pksearch", 474, searchY, 60, 28, ColNeutral, ColText, 11);

            if (picker.Target == PickTarget.NewFurnace)
            {
                DrawFurnaceResults(c, dialog, state, dw, dh);
                return;
            }

            var filterLabel = picker.Target == PickTarget.OutputItem
                ? (picker.RelevantOnly ? "Smelt outputs only: ON" : "Smelt outputs only: OFF")
                : (picker.RelevantOnly ? "Smeltable only: ON" : "Smeltable only: OFF");

            AddButton(c, dialog, filterLabel, "quicksmeltui pkrel", 542, searchY, 178, 28,
                picker.RelevantOnly ? ColAccent : ColCard, ColText, 11);

            var results = SearchCatalog(picker.Search, picker.RelevantOnly, picker.Target);
            var pages = Mathf.Max(1, Mathf.CeilToInt(results.Count / (float)ItemPageSize));
            picker.Page = Mathf.Clamp(picker.Page, 0, pages - 1);

            AddText(c, dialog, $"{results.Count} match{(results.Count == 1 ? "" : "es")}",
                728, searchY, 180, 28, 11, ColTextDim, TextAnchor.MiddleRight);

            var gridTop = dh - 92;

            for (var i = 0; i < ItemPageSize; i++)
            {
                var index = picker.Page * ItemPageSize + i;
                if (index >= results.Count)
                    break;

                var definition = results[index];
                var col = i % ItemGridCols;
                var line = i / ItemGridCols;
                var cx = 13 + col * (ICardW + 6f);
                var cy = gridTop - (line + 1) * ICardH - line * 6f;

                var card = AddCard(c, dialog, $"quicksmeltui pkpick {definition.shortname}", cx, cy, ICardW, ICardH, ColCard);
                AddItemIcon(c, card, definition, (ICardW - 44) / 2f, ICardH - 50, 44, 44);
                AddText(c, card, Trim(DisplayNameOf(definition), 30), 2, 4, ICardW - 4, 30, 8, ColText,
                    TextAnchor.MiddleCenter);
            }

            AddButton(c, dialog, "<", $"quicksmeltui pkpage {picker.Page - 1}", 12, 10, 34, 24, ColNeutral, ColText, 12);
            AddText(c, dialog, $"page {picker.Page + 1} / {pages}", 52, 10, 140, 24, 11, ColTextDim);
            AddButton(c, dialog, ">", $"quicksmeltui pkpage {picker.Page + 1}", dw - 46, 10, 34, 24, ColNeutral, ColText, 12);
        }

        private void DrawFurnaceResults(CuiElementContainer c, string dialog, UiState state, float dw, float dh)
        {
            var picker = state.Picker;
            var results = SearchFurnaces(picker.Search);
            var configured = new HashSet<string>(Scopes(state.Config), StringComparer.OrdinalIgnoreCase);

            const int cols = 6;
            const int rows = 3;
            const float cardW = 142f;
            const float cardH = 116f;
            var pageSize = cols * rows;
            var pages = Mathf.Max(1, Mathf.CeilToInt(results.Count / (float)pageSize));
            picker.Page = Mathf.Clamp(picker.Page, 0, pages - 1);

            AddText(c, dialog, $"{results.Count} oven{(results.Count == 1 ? "" : "s")}",
                728, dh - 86, 180, 28, 11, ColTextDim, TextAnchor.MiddleRight);

            var gridTop = dh - 96;
            var startX = (dw - (cols * cardW + (cols - 1) * 8f)) / 2f;

            for (var i = 0; i < pageSize; i++)
            {
                var index = picker.Page * pageSize + i;
                if (index >= results.Count)
                    break;

                var furnace = results[index];
                var col = i % cols;
                var line = i / cols;
                var cx = startX + col * (cardW + 8f);
                var cy = gridTop - (line + 1) * cardH - line * 8f;

                var already = configured.Contains(furnace.ShortName);
                var card = AddCard(c, dialog, $"quicksmeltui pkpick {furnace.ShortName}", cx, cy, cardW, cardH,
                    already ? "0.160 0.215 0.285 1" : ColCard);

                AddItemIcon(c, card, furnace.Item, (cardW - 50) / 2f, cardH - 58, 50, 50);
                AddText(c, card, Trim(furnace.Label, 22), 3, 34, cardW - 6, 20, 9, ColText, TextAnchor.MiddleCenter);
                AddText(c, card, Trim(furnace.ShortName, 24), 3, 18, cardW - 6, 16, 8, ColTextDim,
                    TextAnchor.MiddleCenter);
                AddText(c, card, already ? "already configured" : "", 3, 2, cardW - 6, 16, 8, ColAccent,
                    TextAnchor.MiddleCenter);
            }

            AddButton(c, dialog, "<", $"quicksmeltui pkpage {picker.Page - 1}", 12, 10, 34, 24, ColNeutral, ColText, 12);
            AddText(c, dialog, $"page {picker.Page + 1} / {pages}", 52, 10, 140, 24, 11, ColTextDim);
            AddText(c, dialog, "Ovens with no deployable item show a placeholder icon.", 200, 10, 560, 24, 10, ColTextDim);
            AddButton(c, dialog, ">", $"quicksmeltui pkpage {picker.Page + 1}", dw - 46, 10, 34, 24, ColNeutral, ColText, 12);
        }

        #endregion
    }
}
