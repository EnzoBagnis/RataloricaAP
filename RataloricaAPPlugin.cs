using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json;

namespace RataloricaAP
{
    [BepInPlugin("com.ratalorica.archipelago", "Ratalorica Archipelago Client", "1.0.0")]
    public class RataloricaAPPlugin : BaseUnityPlugin
    {
        public static RataloricaAPPlugin Instance;
        internal static ManualLogSource Log;

        // AP Connection settings (modifiable via config)
        private string apServer = "localhost:38281";
        private string apSlotName = "Player1";
        private string apPassword = "";
        private string apGame = "Ratalorica";

        // AP state
        private ClientWebSocket ws;
        private bool connected = false;
        private bool authenticated = false;
        private long currentIndex = 0;

        // Tracks which checks have been sent to avoid duplicates
        private HashSet<long> checkedLocations = new HashSet<long>();
        // Tracks which items were received from AP
        private HashSet<string> receivedUpgrades = new HashSet<string>();

        // Base IDs matching the apworld
        private const long LOCATION_BASE = 0xCA7A1000;
        private const long ITEM_BASE     = 0xCA7A0000;

        // Location name -> AP ID
        private static readonly Dictionary<string, long> LocationIDs = new Dictionary<string, long>
        {
            { "Buy first upgrade",            LOCATION_BASE + 0  },
            { "Reach Shop Sign",              LOCATION_BASE + 1  },
            { "Reach Upgrades Sign",          LOCATION_BASE + 2  },
            { "SkillTree Floor2 Unlocked",    LOCATION_BASE + 3  },
            { "SkillTree Floor3 Unlocked",    LOCATION_BASE + 4  },
            { "Achievement - First Soul",     LOCATION_BASE + 5  },
            { "Achievement - Uncommon Soul",  LOCATION_BASE + 6  },
            { "Achievement - Rare Soul",      LOCATION_BASE + 7  },
            { "Achievement - Legendary Soul", LOCATION_BASE + 8  },
            { "World1 - Kill first enemy",    LOCATION_BASE + 9  },
            { "World1 - Kill UNCOMMON enemy", LOCATION_BASE + 10 },
            { "World1 - Kill RARE enemy",     LOCATION_BASE + 11 },
            { "World1 - Collect 10 souls",    LOCATION_BASE + 12 },
            { "World1 - Buy CmbtUp Floor1",   LOCATION_BASE + 13 },
            { "World1 - Buy ExploUp Floor1",  LOCATION_BASE + 14 },
            { "World2 - Kill first enemy",    LOCATION_BASE + 15 },
            { "World2 - Kill RARE enemy",     LOCATION_BASE + 16 },
            { "World2 - Kill LEGENDARY enemy",LOCATION_BASE + 17 },
            { "World2 - Collect 50 souls",    LOCATION_BASE + 18 },
            { "World2 - Buy CmbtUp Floor2",   LOCATION_BASE + 19 },
            { "World2 - Buy ExploUp Floor2",  LOCATION_BASE + 20 },
            { "World3 - Kill first enemy",    LOCATION_BASE + 21 },
            { "World3 - Kill LEGENDARY enemy",LOCATION_BASE + 22 },
            { "World3 - Collect 100 souls",   LOCATION_BASE + 23 },
            { "World3 - Buy CmbtUp Floor3",   LOCATION_BASE + 24 },
            { "World3 - Buy ShopUp Floor3",   LOCATION_BASE + 25 },
        };

        // AP item name -> upgrade classname in game
        private static readonly Dictionary<string, string> ItemToUpgradeClass = new Dictionary<string, string>
        {
            { "CmbtUp_AugmentRarityPoprate",  "CmbtUp_AugmentRarityPoprate"  },
            { "CmbtUp_AugmentWalkingSpeed",   "CmbtUp_AugmentWalkingSpeed"   },
            { "CmbtUp_Plus1BigDamage",        "CmbtUp_Plus1BigDamage"        },
            { "CmbtUp_Plus1MaxEnemies",       "CmbtUp_Plus1MaxEnemies"       },
            { "CmbtUp_Plus1SmallDamage",      "CmbtUp_Plus1SmallDamage"      },
            { "CmbtUp_Plus1Stam",             "CmbtUp_Plus1Stam"             },
            { "CmbtUp_ReduceSpawnCooldown",   "CmbtUp_ReduceSpawnCooldown"   },
            { "ExploUp_AdoptChick",           "ExploUp_AdoptChick"           },
            { "ExploUp_AdoptRabbit",          "ExploUp_AdoptRabbit"          },
            { "ExploUp_AugmentDoubleSouls",   "ExploUp_AugmentDoubleSouls"   },
            { "ExploUp_AugmentSpawnChance",   "ExploUp_AugmentSpawnChance"   },
            { "ExploUp_AugmentStability",     "ExploUp_AugmentStability"     },
            { "ShopUp_AttractMoreClients",    "ShopUp_AttractMoreClients"    },
            { "ShopUp_AugmentRarityPrice",    "ShopUp_AugmentRarityPrice"    },
            { "ShopUp_Charisma",              "ShopUp_Charisma"              },
            { "ShopUp_Plus1NpcAtATime",       "ShopUp_Plus1NpcAtATime"       },
            { "ShopUp_Plus1Shout",            "ShopUp_Plus1Shout"            },
            { "ShopUp_Plus1Stam",             "ShopUp_Plus1Stam"             },
            { "ShopUp_Plus1Talk",             "ShopUp_Plus1Talk"             },
            { "Up_AutoInput",                 "Up_AutoInput"                 },
        };

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Load config
            apServer   = Config.Bind("Connection", "Server",   "localhost:38281", "Archipelago server address").Value;
            apSlotName = Config.Bind("Connection", "SlotName", "Player1",         "Your slot name").Value;
            apPassword = Config.Bind("Connection", "Password", "",                "Server password (leave blank if none)").Value;

            var harmony = new Harmony("com.ratalorica.archipelago");
            harmony.PatchAll();

            Log.LogInfo("Ratalorica AP Plugin loaded. Connecting to " + apServer);
            StartCoroutine(ConnectToAP());
        }

        // ─── WebSocket connection ───────────────────────────────────────────

        private IEnumerator ConnectToAP()
        {
            while (true)
            {
                yield return new WaitForSeconds(3f);
                if (!connected)
                {
                    Log.LogInfo("Trying to connect to AP server...");
                    StartCoroutine(WebSocketLoop());
                }
            }
        }

        private IEnumerator WebSocketLoop()
        {
            ws = new ClientWebSocket();
            // archipelago.gg requires wss://, local servers use ws://
            string protocol = apServer.StartsWith("localhost") || apServer.StartsWith("127.0.0.1") ? "ws" : "wss";
            var uri = new Uri(protocol + "://" + apServer);
            var connectTask = ws.ConnectAsync(uri, CancellationToken.None);
            yield return new WaitUntil(() => connectTask.IsCompleted);

            if (ws.State != WebSocketState.Open)
            {
                Log.LogWarning("Could not connect to AP server.");
                yield break;
            }

            connected = true;
            Log.LogInfo("Connected to AP server!");
            StartCoroutine(ReceiveLoop());
        }

        private IEnumerator ReceiveLoop()
        {
            var buffer = new byte[4096];
            while (ws != null && ws.State == WebSocketState.Open)
            {
                var segment = new ArraySegment<byte>(buffer);
                var receiveTask = ws.ReceiveAsync(segment, CancellationToken.None);
                yield return new WaitUntil(() => receiveTask.IsCompleted);

                if (receiveTask.Result.MessageType == WebSocketMessageType.Close)
                {
                    connected = false;
                    authenticated = false;
                    Log.LogWarning("AP server disconnected.");
                    yield break;
                }

                string msg = Encoding.UTF8.GetString(buffer, 0, receiveTask.Result.Count);
                HandleMessage(msg);
            }
        }

        private void HandleMessage(string raw)
        {
            try
            {
                var packets = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(raw);
                foreach (var packet in packets)
                {
                    string cmd = packet["cmd"].ToString();
                    switch (cmd)
                    {
                        case "RoomInfo":
                            SendConnect();
                            break;
                        case "Connected":
                            authenticated = true;
                            Log.LogInfo("Authenticated with AP server!");
                            // Sync already-received items
                            if (packet.ContainsKey("checked_locations"))
                            {
                                var locs = JsonConvert.DeserializeObject<List<long>>(packet["checked_locations"].ToString());
                                foreach (var l in locs) checkedLocations.Add(l);
                            }
                            break;
                        case "ReceivedItems":
                            HandleReceivedItems(packet);
                            break;
                        case "PrintJSON":
                            if (packet.ContainsKey("data"))
                                Log.LogInfo("[AP] " + packet["data"]);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogError("AP message parse error: " + e.Message);
            }
        }

        private void SendConnect()
        {
            var connectPacket = new[]
            {
                new Dictionary<string, object>
                {
                    { "cmd", "Connect" },
                    { "game", apGame },
                    { "name", apSlotName },
                    { "password", apPassword },
                    { "version", new Dictionary<string, object> { {"major",0},{"minor",5},{"build",0},{"class","Version"} } },
                    { "items_handling", 7 },
                    { "tags", new string[0] },
                    { "uuid", Guid.NewGuid().ToString() },
                }
            };
            SendPacket(connectPacket);
        }

        private void HandleReceivedItems(Dictionary<string, object> packet)
        {
            long index = Convert.ToInt64(packet["index"]);
            var items = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(packet["items"].ToString());

            for (int i = 0; i < items.Count; i++)
            {
                if (index + i <= currentIndex) continue;
                currentIndex = index + i + 1;

                long itemId = Convert.ToInt64(items[i]["item"]);
                string itemName = GetItemName(itemId);
                if (itemName != null)
                    GrantItem(itemName);
            }
        }

        private string GetItemName(long id)
        {
            int idx = (int)(id - ITEM_BASE);
            string[] names = {
                "CmbtUp_AugmentRarityPoprate","CmbtUp_AugmentWalkingSpeed","CmbtUp_Plus1BigDamage",
                "CmbtUp_Plus1MaxEnemies","CmbtUp_Plus1SmallDamage","CmbtUp_Plus1Stam",
                "CmbtUp_ReduceSpawnCooldown","ExploUp_AdoptChick","ExploUp_AdoptRabbit",
                "ExploUp_AugmentDoubleSouls","ExploUp_AugmentSpawnChance","ExploUp_AugmentStability",
                "ShopUp_AttractMoreClients","ShopUp_AugmentRarityPrice","ShopUp_Charisma",
                "ShopUp_Plus1NpcAtATime","ShopUp_Plus1Shout","ShopUp_Plus1Stam",
                "ShopUp_Plus1Talk","Up_AutoInput",
                "World1 Unlock","World2 Unlock","World3 Unlock",
                "Gold Pouch","Soul Bundle"
            };
            if (idx >= 0 && idx < names.Length) return names[idx];
            return null;
        }

        // ─── Grant item to player ───────────────────────────────────────────

        public void GrantItem(string itemName)
        {
            Log.LogInfo($"[AP] Received item: {itemName}");

            if (itemName == "World1 Unlock" || itemName == "World2 Unlock" || itemName == "World3 Unlock")
            {
                APStateManager.Instance?.UnlockWorld(itemName);
                return;
            }
            if (itemName == "Gold Pouch")
            {
                APStateManager.Instance?.AddMoney(100f);
                return;
            }
            if (itemName == "Soul Bundle")
            {
                APStateManager.Instance?.AddSouls(10);
                return;
            }
            if (ItemToUpgradeClass.ContainsKey(itemName))
            {
                APStateManager.Instance?.GrantUpgrade(ItemToUpgradeClass[itemName]);
            }
        }

        // ─── Send location check ────────────────────────────────────────────

        public void SendCheck(string locationName)
        {
            if (!authenticated) return;
            if (!LocationIDs.ContainsKey(locationName)) return;
            long id = LocationIDs[locationName];
            if (checkedLocations.Contains(id)) return;

            checkedLocations.Add(id);
            Log.LogInfo($"[AP] Sending check: {locationName} ({id})");

            var packet = new[]
            {
                new Dictionary<string, object>
                {
                    { "cmd", "LocationChecks" },
                    { "locations", new long[] { id } }
                }
            };
            SendPacket(packet);
        }

        public void SendGoal()
        {
            if (!authenticated) return;
            Log.LogInfo("[AP] Sending goal completion!");
            var packet = new[]
            {
                new Dictionary<string, object>
                {
                    { "cmd", "StatusUpdate" },
                    { "status", 30 } // 30 = CLIENT_GOAL
                }
            };
            SendPacket(packet);
        }

        private void SendPacket(object obj)
        {
            if (ws == null || ws.State != WebSocketState.Open) return;
            string json = JsonConvert.SerializeObject(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    // ─── State Manager (MonoBehaviour singleton) ────────────────────────────

    public class APStateManager : MonoBehaviour
    {
        public static APStateManager Instance;

        // World unlocks received from AP
        public bool World1Unlocked = false;
        public bool World2Unlocked = false;
        public bool World3Unlocked = false;

        // Pending upgrade grants (applied when save is loaded)
        public List<string> PendingUpgrades = new List<string>();

        // Soul tracking for checks
        private int totalSoulsTracked = 0;
        private Dictionary<Enemy.RarityTypes, bool> firstKillSentPerWorld = new Dictionary<Enemy.RarityTypes, bool>();

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void UnlockWorld(string worldName)
        {
            if (worldName == "World1 Unlock") World1Unlocked = true;
            if (worldName == "World2 Unlock") World2Unlocked = true;
            if (worldName == "World3 Unlock") World3Unlocked = true;
            RataloricaAPPlugin.Log.LogInfo($"[AP] {worldName} unlocked!");
        }

        public void AddMoney(float amount)
        {
            if (PlayerManager.Instance != null)
                PlayerManager.Instance.ModifyMoney(amount);
            else
                RataloricaAPPlugin.Log.LogWarning("[AP] PlayerManager not ready, cannot add money.");
        }

        public void AddSouls(int amount)
        {
            // SoulManager spawns souls via Unity prefabs — no direct add method.
            // Soul Bundle gives 50 gold instead as filler reward.
            RataloricaAPPlugin.Log.LogInfo("[AP] Soul Bundle received — converting to 50 gold.");
            AddMoney(50f);
        }

        public void GrantUpgrade(string upgradeClassName)
        {
            PendingUpgrades.Add(upgradeClassName);
            ApplyPendingUpgrades();
        }

        public void ShowLockedMessage()
        {
            // Simple on-screen message via Unity UI (no extra dependency needed)
            RataloricaAPPlugin.Instance?.StartCoroutine(ShowMessageCoroutine("[Archipelago] Ce monde est verrouillé !"));
        }

        private System.Collections.IEnumerator ShowMessageCoroutine(string message)
        {
            // Create a temporary canvas text overlay
            var go = new GameObject("AP_LockedMsg");
            DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999;
            go.AddComponent<UnityEngine.UI.CanvasScaler>();
            go.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = message;
            text.fontSize = 28;
            text.color = new Color(1f, 0.3f, 0.3f, 1f);
            text.alignment = TMPro.TextAlignmentOptions.Center;
            var rect = textGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0.85f);
            rect.anchorMax = new Vector2(1, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            yield return new WaitForSeconds(3f);
            Destroy(go);
        }

        public void ApplyPendingUpgrades()
        {
            var upgrades = FindObjectsOfType<Upgrade>();
            var toRemove = new List<string>();
            foreach (var upName in PendingUpgrades)
            {
                foreach (var up in upgrades)
                {
                    if (up.GetType().Name == upName)
                    {
                        up.BuyUpgrade();
                        toRemove.Add(upName);
                        RataloricaAPPlugin.Log.LogInfo($"[AP] Applied upgrade: {upName}");
                        break;
                    }
                }
            }
            foreach (var r in toRemove) PendingUpgrades.Remove(r);
        }

        // Called by patches when events happen
        public void OnEnemyKilled(Enemy enemy, WorldManager.World currentWorld)
        {
            string worldPrefix = currentWorld.ToString(); // "WORLD1", "WORLD2", "WORLD3"

            // First kill in world
            string firstKillLoc = worldPrefix switch
            {
                "WORLD1" => "World1 - Kill first enemy",
                "WORLD2" => "World2 - Kill first enemy",
                "WORLD3" => "World3 - Kill first enemy",
                _ => null
            };
            if (firstKillLoc != null)
                RataloricaAPPlugin.Instance?.SendCheck(firstKillLoc);

            // Rarity-based checks
            if (enemy.Rarity == Enemy.RarityTypes.UNCOMMON && worldPrefix == "WORLD1")
                RataloricaAPPlugin.Instance?.SendCheck("World1 - Kill UNCOMMON enemy");
            if (enemy.Rarity == Enemy.RarityTypes.RARE)
            {
                if (worldPrefix == "WORLD1") RataloricaAPPlugin.Instance?.SendCheck("World1 - Kill RARE enemy");
                if (worldPrefix == "WORLD2") RataloricaAPPlugin.Instance?.SendCheck("World2 - Kill RARE enemy");
            }
            if (enemy.Rarity == Enemy.RarityTypes.LEGENDARY)
            {
                if (worldPrefix == "WORLD2") RataloricaAPPlugin.Instance?.SendCheck("World2 - Kill LEGENDARY enemy");
                if (worldPrefix == "WORLD3") RataloricaAPPlugin.Instance?.SendCheck("World3 - Kill LEGENDARY enemy");
            }
        }

        public void OnUpgradeBought(Upgrade upgrade)
        {
            // Generic upgrade check triggers
            RataloricaAPPlugin.Instance?.SendCheck("Buy first upgrade");

            // SkillTree floor checks are sent by the SkillTree patches
        }

        public void OnSoulsUpdated(int total)
        {
            totalSoulsTracked = total;
            if (total >= 1)   RataloricaAPPlugin.Instance?.SendCheck("Achievement - First Soul");
            if (total >= 10)  RataloricaAPPlugin.Instance?.SendCheck("World1 - Collect 10 souls");
            if (total >= 50)  RataloricaAPPlugin.Instance?.SendCheck("World2 - Collect 50 souls");
            if (total >= 100) RataloricaAPPlugin.Instance?.SendCheck("World3 - Collect 100 souls");
        }
    }

    // ─── Harmony Patches ────────────────────────────────────────────────────

    [HarmonyPatch(typeof(Enemy), "Kill")]
    public class EnemyKillPatch
    {
        static void Postfix(Enemy __instance)
        {
            try
            {
                if (WorldManager.Instance != null && APStateManager.Instance != null)
                    APStateManager.Instance.OnEnemyKilled(__instance, WorldManager.Instance.CurrentWorld);
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("EnemyKillPatch error: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Upgrade), "BuyUpgrade")]
    public class UpgradeBuyPatch
    {
        static void Postfix(Upgrade __instance)
        {
            try
            {
                APStateManager.Instance?.OnUpgradeBought(__instance);
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("UpgradeBuyPatch error: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(WorldManager), "SwitchToWorld")]
    public class WorldSwitchPatch
    {
        // Prefix: if the world is AP-locked, redirect to CLASSIC before the switch happens
        static void Prefix(ref WorldManager.World _worldToSwitchTo)
        {
            try
            {
                if (APStateManager.Instance == null) return;

                bool blocked = false;
                if (_worldToSwitchTo == WorldManager.World.WORLD1 && !APStateManager.Instance.World1Unlocked) blocked = true;
                if (_worldToSwitchTo == WorldManager.World.WORLD2 && !APStateManager.Instance.World2Unlocked) blocked = true;
                if (_worldToSwitchTo == WorldManager.World.WORLD3 && !APStateManager.Instance.World3Unlocked) blocked = true;

                if (blocked)
                {
                    RataloricaAPPlugin.Log.LogInfo($"[AP] {_worldToSwitchTo} is locked! Redirecting to CLASSIC.");
                    // Redirect the argument — Harmony passes it by ref so this actually changes what the method receives
                    _worldToSwitchTo = WorldManager.World.CLASSIC;
                    APStateManager.Instance.ShowLockedMessage();
                }
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("WorldSwitchPatch error: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(ObjectivesManager), "NextObjective")]
    public class ObjectivePatch
    {
        static void Postfix(ObjectivesManager __instance)
        {
            try
            {
                // Check if shop/upgrades sign just got activated
                // We read the private fields via reflection
                var shopField = typeof(ObjectivesManager).GetField("isShopSignActivated",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var upField = typeof(ObjectivesManager).GetField("isUpgradesSignActivated",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (shopField != null && (bool)shopField.GetValue(__instance))
                    RataloricaAPPlugin.Instance?.SendCheck("Reach Shop Sign");
                if (upField != null && (bool)upField.GetValue(__instance))
                    RataloricaAPPlugin.Instance?.SendCheck("Reach Upgrades Sign");
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("ObjectivePatch error: " + e.Message);
            }
        }
    }
}

// ─── Soul Counter Patch ─────────────────────────────────────────────────────
// PlayerManager.IncrementSoulCounter is private, so we patch it via EventBus.OnEnemyKilled
// by hooking the same event. We read TotalSoulsCollected after kill.

namespace RataloricaAP
{
    [HarmonyPatch(typeof(PlayerManager), "IncrementSoulCounter")]
    public class SoulCounterPatch
    {
        static void Postfix(PlayerManager __instance)
        {
            try
            {
                int total = __instance.TotalSoulsCollected;
                APStateManager.Instance?.OnSoulsUpdated(total);

                // Rarity achievement checks based on soulsCollected dict
                var souls = __instance.SoulsCollected;
                if (souls.ContainsKey(Enemy.RarityTypes.UNCOMMON) && souls[Enemy.RarityTypes.UNCOMMON] >= 1)
                    RataloricaAPPlugin.Instance?.SendCheck("Achievement - Uncommon Soul");
                if (souls.ContainsKey(Enemy.RarityTypes.RARE) && souls[Enemy.RarityTypes.RARE] >= 1)
                    RataloricaAPPlugin.Instance?.SendCheck("Achievement - Rare Soul");
                if (souls.ContainsKey(Enemy.RarityTypes.LEGENDARY) && souls[Enemy.RarityTypes.LEGENDARY] >= 1)
                    RataloricaAPPlugin.Instance?.SendCheck("Achievement - Legendary Soul");
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("SoulCounterPatch error: " + e.Message);
            }
        }
    }
}