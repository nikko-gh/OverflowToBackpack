/*
 * Copyright (C) 2026 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Overflow To Backpack", "VisEntities", "1.4.0")]
    [Description("Automatically moves items to your backpack when your inventory runs out of space.")]
    public class OverflowToBackpack : RustPlugin
    {
        #region Fields

        private static OverflowToBackpack _plugin;
        private static Configuration _config;
        private StoredData _storedData;

        [PluginReference]
        private Plugin Backpacks;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Enable Overflow For Gathered Resources (ores, wood, etc.)")]
            public bool EnableOverflowForGatheredResources { get; set; }

            [JsonProperty("Enable Overflow For Collectible Pickups (hemp, mushrooms, etc.)")]
            public bool EnableOverflowForCollectiblePickups { get; set; }

            [JsonProperty("Enable Overflow For Dropped Item Pickups")]
            public bool EnableOverflowForDroppedItemPickups { get; set; }

            [JsonProperty("Enable Overflow For Looted Items (from containers)")]
            public bool EnableOverflowForLootedItems { get; set; }

            [JsonProperty("Show Game Tip When Items Overflow To Backpack")]
            public bool ShowGameTipWhenItemsOverflowToBackpack { get; set; }

            [JsonProperty("Chat Command To Toggle Overflow (without slash)")]
            public string ChatCommandToToggleOverflow { get; set; }

            [JsonProperty("Backpack Provider (Vanilla, BackpacksPlugin, Both)")]
            public string BackpackProvider { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            if (string.Compare(_config.Version, "1.4.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                EnableOverflowForGatheredResources = true,
                EnableOverflowForCollectiblePickups = true,
                EnableOverflowForDroppedItemPickups = true,
                EnableOverflowForLootedItems = true,
                ShowGameTipWhenItemsOverflowToBackpack = true,
                ChatCommandToToggleOverflow = "overflow",
                BackpackProvider = "Vanilla"
            };
        }

        #endregion Configuration

        #region Data

        private class StoredData
        {
            [JsonProperty("Preferences")]
            public Dictionary<ulong, bool> Preferences = new Dictionary<ulong, bool>();
        }

        private StoredData LoadData()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile(Name))
            {
                StoredData loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (loaded != null)
                    return loaded;
            }

            return new StoredData();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);
        }

        #endregion Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
            _storedData = LoadData();
            cmd.AddChatCommand(_config.ChatCommandToToggleOverflow, this, nameof(cmdToggleOverflow));

            string provider = _config.BackpackProvider;
            if (provider != "Vanilla" && provider != "BackpacksPlugin" && provider != "Both")
            {
                PrintWarning($"Invalid BackpackProvider value '{provider}'. Defaulting to 'Vanilla'.");
                _config.BackpackProvider = "Vanilla";
            }

            if (!_config.EnableOverflowForGatheredResources)
            {
                Unsubscribe(nameof(OnDispenserGather));
                Unsubscribe(nameof(OnDispenserBonus));
            }

            if (!_config.EnableOverflowForCollectiblePickups)
                Unsubscribe(nameof(OnCollectiblePickup));

            if (!_config.EnableOverflowForDroppedItemPickups)
                Unsubscribe(nameof(OnItemPickup));

            if (!_config.EnableOverflowForLootedItems)
                Unsubscribe(nameof(CanMoveItem));
        }

        private void OnServerInitialized()
        {
            string provider = _config.BackpackProvider;
            if ((provider == "BackpacksPlugin" || provider == "Both") && !IsBackpacksPluginAvailable())
            {
                PrintWarning("Backpack Provider is set to '" + provider + "' but the Backpacks plugin is not loaded. Overflow to Backpacks plugin will be skipped until it loads.");
            }
        }

        private void Unload()
        {
            _config = null;
            _plugin = null;
        }

        private object OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !PreferencesFor(player))
                return null;

            if (!HasAnyBackpack(player))
                return null;

            if (!PlayerInventoryFull(player, item))
                return null;

            int originalAmount = item.amount;
            Item newItem = ItemManager.Create(item.info, originalAmount, item.skin);
            if (newItem != null)
            {
                if (TryMoveItemToBackpack(player, newItem, originalAmount))
                    return true;

                newItem.Remove();
            }

            return null;
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !PreferencesFor(player))
                return;

            if (!HasAnyBackpack(player))
                return;

            if (!PlayerInventoryFull(player, item))
                return;

            int originalAmount = item.amount;
            NextTick(() =>
            {
                if (player == null || item == null || item.info == null || item.amount <= 0 || !HasAnyBackpack(player))
                    return;

                TryMoveItemToBackpack(player, item, originalAmount);
            });
        }

        private object OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (player == null || collectible == null || collectible.itemList == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !PreferencesFor(player))
                return null;

            string provider = _config.BackpackProvider;
            bool useVanilla = provider == "Vanilla" || provider == "Both";
            bool usePlugin = provider == "BackpacksPlugin" || provider == "Both";

            ItemContainer vanillaContainer = null;
            ItemContainer pluginContainer = null;

            if (useVanilla)
            {
                Item backpackItem = player.inventory.GetBackpackWithInventory();
                if (backpackItem != null && backpackItem.contents != null)
                    vanillaContainer = backpackItem.contents;
            }

            if (usePlugin && IsBackpacksPluginAvailable())
                pluginContainer = GetBackpacksPluginContainer(player);

            if (vanillaContainer == null && pluginContainer == null)
                return null;

            bool needOverride = false;

            bool vanillaFits = vanillaContainer != null && CollectibleFitsInContainer(vanillaContainer, collectible, player);
            bool pluginFits = !vanillaFits && pluginContainer != null && CollectibleFitsInContainer(pluginContainer, collectible, player);

            if (!vanillaFits && !pluginFits)
            {
                foreach (var ia in collectible.itemList)
                {
                    int amount = (int)ia.amount;
                    Item probe = ItemManager.Create(ia.itemDef, amount, 0UL, true);
                    bool invFull = PlayerInventoryFull(player, probe);
                    probe.Remove();

                    if (invFull)
                        return null;
                }

                return null;
            }

            foreach (var ia in collectible.itemList)
            {
                int amount = (int)ia.amount;
                Item probe = ItemManager.Create(ia.itemDef, amount, 0UL, true);
                bool invFull = PlayerInventoryFull(player, probe);
                probe.Remove();

                if (invFull)
                {
                    needOverride = true;
                    break;
                }
            }

            if (!needOverride)
                return null;

            foreach (var ia in collectible.itemList)
            {
                int amount = (int)ia.amount;
                Item newItem = ItemManager.Create(ia.itemDef, amount, 0UL, true);
                if (newItem == null) continue;

                if (!TryMoveItemToBackpack(player, newItem, amount))
                    newItem.Remove();
            }

            collectible.Kill();
            return true;
        }

        private object OnItemPickup(Item item, BasePlayer player)
        {
            if (player == null || item == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !PreferencesFor(player))
                return null;

            if (!HasAnyBackpack(player))
                return null;

            if (!PlayerInventoryFull(player, item))
                return null;

            if (TryMoveItemToBackpack(player, item, item.amount))
                return true;

            return null;
        }

        private object CanMoveItem(Item item, PlayerInventory playerInventory, ItemContainerId itemContainerId)
        {
            if (item == null || playerInventory == null)
                return null;

            BasePlayer player = playerInventory.baseEntity;
            if (player == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !PreferencesFor(player))
                return null;

            if (!HasAnyBackpack(player))
                return null;

            ItemContainer sourceContainer = item.parent;
            if (sourceContainer == player.inventory.containerMain ||
                sourceContainer == player.inventory.containerBelt)
                return null;

            Item vanillaBackpackItem = player.inventory.GetBackpackWithInventory();
            if (vanillaBackpackItem != null && sourceContainer == vanillaBackpackItem.contents)
                return null;

            if (IsBackpacksPluginAvailable())
            {
                ItemContainer pluginContainer = GetBackpacksPluginContainer(player);
                if (pluginContainer != null && sourceContainer == pluginContainer)
                    return null;
            }

            if (!PlayerInventoryFull(player, item, playerInventory.FindContainer(itemContainerId)))
                return null;

            if (TryMoveItemToBackpack(player, item, item.amount))
                return true;

            return null;
        }

        #endregion Oxide Hooks

        #region Helpers

        private bool IsBackpacksPluginAvailable()
        {
            return Backpacks != null && Backpacks.IsLoaded;
        }

        private ItemContainer GetBackpacksPluginContainer(BasePlayer player)
        {
            if (!IsBackpacksPluginAvailable())
                return null;

            return Backpacks.Call("API_GetBackpackContainer", player.userID) as ItemContainer;
        }

        private bool HasAnyBackpack(BasePlayer player)
        {
            string provider = _config.BackpackProvider;

            if (provider == "Vanilla")
                return player.inventory.GetBackpackWithInventory() != null;

            if (provider == "BackpacksPlugin")
                return IsBackpacksPluginAvailable() && GetBackpacksPluginContainer(player) != null;

            if (player.inventory.GetBackpackWithInventory() != null)
                return true;

            return IsBackpacksPluginAvailable() && GetBackpacksPluginContainer(player) != null;
        }

        private bool CollectibleFitsInContainer(ItemContainer container, CollectibleEntity collectible, BasePlayer player)
        {
            int freeSlots = container.capacity - container.itemList.Count;

            Dictionary<ItemDefinition, int> stackSpace = new Dictionary<ItemDefinition, int>();
            foreach (Item existing in container.itemList)
            {
                if (existing.amount >= existing.info.stackable) continue;

                int spare = existing.info.stackable - existing.amount;
                if (stackSpace.TryGetValue(existing.info, out int current))
                    stackSpace[existing.info] = current + spare;
                else
                    stackSpace.Add(existing.info, spare);
            }

            foreach (var ia in collectible.itemList)
            {
                int amount = (int)ia.amount;
                var itemDef = ia.itemDef;

                Item probe = ItemManager.Create(itemDef, amount, 0UL, true);
                bool invFull = PlayerInventoryFull(player, probe);
                probe.Remove();

                if (!invFull)
                    continue;

                if (stackSpace.TryGetValue(itemDef, out int spareInStacks) && spareInStacks > 0)
                {
                    int used = Mathf.Min(spareInStacks, amount);
                    amount -= used;
                    stackSpace[itemDef] = spareInStacks - used;
                }

                if (amount > 0)
                {
                    int stackSize = itemDef.stackable;
                    int slotsNeeded = Mathf.CeilToInt(amount / (float)stackSize);

                    freeSlots -= slotsNeeded;
                    if (freeSlots < 0)
                        return false;
                }
            }

            return true;
        }

        private bool PlayerInventoryFull(BasePlayer player, Item item, ItemContainer? targetContainer = null)
        {
            if (ContainerHasSpaceForItem(player.inventory.containerMain, item, targetContainer))
                return false;

            if (ContainerHasSpaceForItem(player.inventory.containerBelt, item, targetContainer))
                return false;

            return true;
        }

        private bool ContainerHasSpaceForItem(ItemContainer container, Item item, ItemContainer? targetContainer = null)
        {
            foreach (Item existing in container.itemList)
            {
                if (existing.info == item.info && existing.amount < existing.info.stackable)
                    return true;

                if (targetContainer != null && HasAvailableSlot(targetContainer, item))
                    return true;
            }

            return container.itemList.Count < container.capacity;
        }

        private bool HasAvailableSlot(ItemContainer contents, Item item)
        {
            for (int i = 0; i < contents.capacity; i++)
            {
                if (contents.CanAcceptItem(item, i) == ItemContainer.CanAcceptResult.CanAccept)
                    return true;
            }

            return false;
        }

        private bool TryMoveItemToBackpack(BasePlayer player, Item item, int amount)
        {
            string provider = _config.BackpackProvider;

            if (provider == "Vanilla")
                return TryMoveToVanillaBackpack(player, item, amount);

            if (provider == "BackpacksPlugin")
                return TryMoveToBackpacksPlugin(player, item, amount);

            if (TryMoveToVanillaBackpack(player, item, amount))
                return true;

            return TryMoveToBackpacksPlugin(player, item, amount);
        }

        private bool TryMoveToVanillaBackpack(BasePlayer player, Item item, int amount)
        {
            Item backpack = player.inventory.GetBackpackWithInventory();
            if (backpack == null || backpack.contents == null)
                return false;

            if (!ContainerHasSpaceForItem(backpack.contents, item))
                return false;

            bool moved = item.MoveToContainer(backpack.contents, allowStack: true);
            if (moved && _config.ShowGameTipWhenItemsOverflowToBackpack)
                SendToastLocalized(player, Lang.Notification_ItemOverflowed, GameTip.Styles.Blue_Normal, amount, item.info.displayName.translated);

            return moved;
        }

        private bool TryMoveToBackpacksPlugin(BasePlayer player, Item item, int amount)
        {
            if (!IsBackpacksPluginAvailable())
                return false;

            ItemContainer container = GetBackpacksPluginContainer(player);
            if (container == null)
                return false;

            if (!ContainerHasSpaceForItem(container, item))
                return false;

            PauseBackpacksPluginGatherMode(player);

            bool moved = item.MoveToContainer(container, allowStack: true);
            if (moved && _config.ShowGameTipWhenItemsOverflowToBackpack)
                SendToastLocalized(player, Lang.Notification_ItemOverflowed, GameTip.Styles.Blue_Normal, amount, item.info.displayName.translated);

            return moved;
        }

        private void PauseBackpacksPluginGatherMode(BasePlayer player)
        {
            if (!IsBackpacksPluginAvailable())
                return;

            Backpacks.Call("API_PauseBackpackGatherMode", player.userID, 0f);
        }

        private bool PreferencesFor(BasePlayer player)
        {
            if (!_storedData.Preferences.TryGetValue(player.userID, out bool isEnabled))
            {
                isEnabled = true;
                _storedData.Preferences[player.userID] = true;
                SaveData();
            }

            return isEnabled;
        }

        #endregion Helpers

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "overflowtobackpack.use";
            private static readonly List<string> _permissions = new List<string>
            {
                USE,
            };

            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Commands

        private void cmdToggleOverflow(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
            {
                SendReplyLocalized(player, Lang.Error_NoPermission);
                return;
            }

            bool newValue = !PreferencesFor(player);
            _storedData.Preferences[player.userID] = newValue;
            SaveData();

            if (newValue)
                SendReplyLocalized(player, Lang.Toggle_Enabled);
            else
                SendReplyLocalized(player, Lang.Toggle_Disabled);
        }

        #endregion Commands

        #region Localization

        private class Lang
        {
            public const string Error_NoPermission = "Error.NoPermission";
            public const string Notification_ItemOverflowed = "Notification.ItemOverflowed";
            public const string Toggle_Enabled = "Toggle.Enabled";
            public const string Toggle_Disabled = "Toggle.Disabled";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.Error_NoPermission] = "You do not have permission to use this.",
                [Lang.Notification_ItemOverflowed] = "Inventory full. {0}x {1} moved to backpack.",
                [Lang.Toggle_Enabled] = "Backpack overflow is now enabled.",
                [Lang.Toggle_Disabled] = "Backpack overflow is now disabled."
            }, this, "en");
        }

        private static string GetLangText(BasePlayer player, string langKey, params object[] args)
        {
            string userId;
            if (player != null)
                userId = player.UserIDString;
            else
                userId = null;

            string message = _plugin.lang.GetMessage(langKey, _plugin, userId);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void SendReplyLocalized(BasePlayer player, string langKey, params object[] args)
        {
            string message = GetLangText(player, langKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                _plugin.SendReply(player, message);
        }

        public static void SendToastLocalized(BasePlayer player, string langKey, GameTip.Styles style = GameTip.Styles.Blue_Normal, params object[] args)
        {
            string message = GetLangText(player, langKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                player.SendConsoleCommand("gametip.showtoast", (int)style, message);
        }

        #endregion Localization
    }
}