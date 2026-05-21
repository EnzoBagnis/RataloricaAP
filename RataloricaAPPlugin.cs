using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json;

namespace RataloricaAP
{
    [BepInPlugin("com.ratalorica.archipelago", "Ratalorica Archipelago Client", "2.0.0")]
    public class RataloricaAPPlugin : BaseUnityPlugin
    {
        public static RataloricaAPPlugin Instance;
        internal static ManualLogSource Log;

        // AP Connection settings
        private string apServer = "localhost:38281";
        private string apSlotName = "Player1";
        private string apPassword = "";
        private string apGame = "Ratalorica";

        // Config entries
        private ConfigEntry<string> cfgServer;
        private ConfigEntry<string> cfgSlotName;
        private ConfigEntry<string> cfgPassword;

        // UI state
        private bool showConnectionUI = true;
        private string uiServer = "";
        private string uiSlotName = "";
        private string uiPassword = "";
        internal string connectionStatus = "Disconnected";
        private bool isConnecting = false;

        // AP state
        private ClientWebSocket ws;
        internal bool connected = false;
        internal bool authenticated = false;
        private long currentIndex = 0;

        private bool needsSceneReloadAfterConnect = false;
        private bool skipInitialItemDump = false;
        private string roomSeedName = null;

        private HashSet<long> checkedLocations = new HashSet<long>();
        private HashSet<long> roomLocationIds = new HashSet<long>();
        internal Dictionary<string, int> upgradePurchaseCounts = new Dictionary<string, int>();

        // Player info from Connected packet
        internal int mySlot = 0;
        internal Dictionary<int, string> playerNames = new Dictionary<int, string>();

        // Scout cache: location_id → (item_name, player_slot, flags)
        internal Dictionary<long, ScoutInfo> scoutCache = new Dictionary<long, ScoutInfo>();

        // AP logo texture (loaded from ap_logo.png in plugin folder)
        internal Texture2D apLogoTexture;

        // Base IDs matching the apworld
        private const long ITEM_BASE     = 0xCA7A0000;
        private const long LOCATION_BASE = 0xCA7A1000;
        private const int MAX_UPGRADE_LEVELS = 10;

        internal struct ScoutInfo
        {
            public string itemName;
            public int player;
            public int flags;
        }

        // ─── Item names (order must match apworld ALL_ITEM_NAMES) ───────────

        private static readonly string[] ItemNames = {
            "Rarity Pop Rate Up","Walking Speed Up","Big Damage +1",
            "Max Enemies +1","Small Damage +1","Combat Stamina +1",
            "Spawn Cooldown Down","Adopt Chick","Adopt Rabbit",
            "Double Souls Up","Spawn Chance Up","Stability Up",
            "Attract More Clients","Rarity Price Up","Charisma Up",
            "NPC Capacity +1","Shout +1","Shop Stamina +1",
            "Talk +1","Auto Input",
            "World 1 Unlock","World 2 Unlock","World 3 Unlock",
            "Gold Pouch","Soul Bundle",
        };

        // ─── Display name ↔ Game class name mapping ─────────────────────────

        internal static readonly Dictionary<string, string> DisplayToClassName = new Dictionary<string, string> {
            {"Rarity Pop Rate Up",   "CmbtUp_AugmentRarityPoprate"},
            {"Walking Speed Up",     "CmbtUp_AugmentWalkingSpeed"},
            {"Big Damage +1",        "CmbtUp_Plus1BigDamage"},
            {"Max Enemies +1",       "CmbtUp_Plus1MaxEnemies"},
            {"Small Damage +1",      "CmbtUp_Plus1SmallDamage"},
            {"Combat Stamina +1",    "CmbtUp_Plus1Stam"},
            {"Spawn Cooldown Down",  "CmbtUp_ReduceSpawnCooldown"},
            {"Adopt Chick",          "ExploUp_AdoptChick"},
            {"Adopt Rabbit",         "ExploUp_AdoptRabbit"},
            {"Double Souls Up",      "ExploUp_AugmentDoubleSouls"},
            {"Spawn Chance Up",      "ExploUp_AugmentSpawnChance"},
            {"Stability Up",         "ExploUp_AugmentStability"},
            {"Attract More Clients", "ShopUp_AttractMoreClients"},
            {"Rarity Price Up",      "ShopUp_AugmentRarityPrice"},
            {"Charisma Up",          "ShopUp_Charisma"},
            {"NPC Capacity +1",      "ShopUp_Plus1NpcAtATime"},
            {"Shout +1",            "ShopUp_Plus1Shout"},
            {"Shop Stamina +1",      "ShopUp_Plus1Stam"},
            {"Talk +1",              "ShopUp_Plus1Talk"},
            {"Auto Input",           "Up_AutoInput"},
        };

        internal static readonly Dictionary<string, string> ClassToDisplayName = BuildClassToDisplayName();

        private static Dictionary<string, string> BuildClassToDisplayName()
        {
            var dict = new Dictionary<string, string>();
            foreach (var kv in DisplayToClassName)
                dict[kv.Value] = kv.Key;
            return dict;
        }

        // Upgrade display names list (same order as apworld UPGRADE_ITEMS)
        private static readonly string[] UpgradeDisplayNames = {
            "Rarity Pop Rate Up","Walking Speed Up","Big Damage +1",
            "Max Enemies +1","Small Damage +1","Combat Stamina +1",
            "Spawn Cooldown Down","Adopt Chick","Adopt Rabbit",
            "Double Souls Up","Spawn Chance Up","Stability Up",
            "Attract More Clients","Rarity Price Up","Charisma Up",
            "NPC Capacity +1","Shout +1","Shop Stamina +1",
            "Talk +1","Auto Input",
        };

        // ─── Location IDs (matching apworld formula) ────────────────────────

        internal static readonly Dictionary<string, long> LocationIDs = BuildLocationIDs();

        private static string UpgradeLocName(string displayName, int level)
        {
            return level == 1 ? $"Buy {displayName}" : $"Buy {displayName} Lv{level}";
        }

        private static Dictionary<string, long> BuildLocationIDs()
        {
            var dict = new Dictionary<string, long>();
            // Event locations (order must match apworld EVENT_LOCATIONS)
            string[] events = {
                // Classic events
                "Reach Shop Sign", "Reach Upgrades Sign",
                "SkillTree Floor 2 Unlocked", "SkillTree Floor 3 Unlocked",
                "First Soul Collected", "Uncommon Soul Collected",
                "Rare Soul Collected", "Legendary Soul Collected",
                // World locations
                "World 1 - First Kill", "World 1 - Kill Uncommon Enemy",
                "World 1 - Kill Rare Enemy", "World 1 - Collect 10 Souls",
                "World 2 - First Kill", "World 2 - Kill Rare Enemy",
                "World 2 - Kill Legendary Enemy", "World 2 - Collect 50 Souls",
                "World 3 - First Kill", "World 3 - Kill Legendary Enemy",
                "World 3 - Collect 100 Souls",
                // Kill milestones
                "Kill 10 Enemies", "Kill 25 Enemies", "Kill 50 Enemies",
                "Kill 100 Enemies", "Kill 200 Enemies",
            };
            for (int i = 0; i < events.Length; i++)
                dict[events[i]] = LOCATION_BASE + i;

            // Upgrade locations (all possible levels)
            for (int ui = 0; ui < UpgradeDisplayNames.Length; ui++)
            {
                for (int lv = 0; lv < MAX_UPGRADE_LEVELS; lv++)
                {
                    string locName = UpgradeLocName(UpgradeDisplayNames[ui], lv + 1);
                    dict[locName] = LOCATION_BASE + 100 + (ui * MAX_UPGRADE_LEVELS) + lv;
                }
            }
            return dict;
        }

        // ─── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            cfgServer   = Config.Bind("Connection", "Server",   "localhost:38281", "Archipelago server address");
            cfgSlotName = Config.Bind("Connection", "SlotName", "Player1",         "Your slot name");
            cfgPassword = Config.Bind("Connection", "Password", "",                "Server password (leave blank if none)");

            uiServer   = cfgServer.Value;
            uiSlotName = cfgSlotName.Value;
            uiPassword = cfgPassword.Value;

            var harmony = new Harmony("com.ratalorica.archipelago");
            harmony.PatchAll();

            var stateGo = new GameObject("APStateManager");
            DontDestroyOnLoad(stateGo);
            stateGo.AddComponent<APStateManager>();

            LoadAPLogo();

            Log.LogInfo("Ratalorica AP Plugin v2.0 loaded. Waiting for connection...");
        }

        private void LoadAPLogo()
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(typeof(RataloricaAPPlugin).Assembly.Location);
                string logoPath = Path.Combine(pluginDir, "ap_logo.png");
                if (File.Exists(logoPath))
                {
                    byte[] data = File.ReadAllBytes(logoPath);
                    apLogoTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    apLogoTexture.LoadImage(data);
                    apLogoTexture.filterMode = FilterMode.Point;
                    Log.LogInfo("[AP] Loaded AP logo from " + logoPath);
                }
                else
                {
                    Log.LogWarning("[AP] ap_logo.png not found at " + logoPath + " — place a pixel art AP logo there for upgrade previews.");
                }
            }
            catch (Exception e) { Log.LogWarning("[AP] Failed to load AP logo: " + e.Message); }
        }

        // ─── Input management ───────────────────────────────────────────────

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
                showConnectionUI = !showConnectionUI;

            if (showConnectionUI)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        // ─── Connection UI (IMGUI) ─────────────────────────────────────────

        private Rect windowRect;
        private bool windowRectInitialized = false;

        public bool IsConnectionUIVisible => showConnectionUI;
        public Rect ConnectionWindowRect => windowRect;

        private void OnGUI()
        {
            if (showConnectionUI)
            {
                if (!windowRectInitialized)
                {
                    float w = 380, h = 260;
                    windowRect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                    windowRectInitialized = true;
                }

                GUI.depth = -1000;
                windowRect = GUI.Window(98765, windowRect, DrawConnectionWindow, "Archipelago");

                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                DrawStatusBar();
            }

            // Always draw notifications (top-right)
            if (APStateManager.Instance != null)
                DrawNotifications();
        }

        private void DrawConnectionWindow(int windowID)
        {
            GUILayout.Space(5);
            GUILayout.Label("Server:");
            uiServer = GUILayout.TextField(uiServer);
            GUILayout.Label("Slot Name:");
            uiSlotName = GUILayout.TextField(uiSlotName);
            GUILayout.Label("Password:");
            uiPassword = GUILayout.PasswordField(uiPassword, '*');

            GUILayout.Space(10);
            GUI.enabled = !isConnecting && !authenticated;
            if (GUILayout.Button(isConnecting ? "Connecting..." : "Connect"))
                DoConnect();
            GUI.enabled = true;

            GUI.enabled = authenticated || connected;
            if (GUILayout.Button("Disconnect"))
                DoDisconnect();
            GUI.enabled = true;

            Color statusColor = authenticated ? Color.green : (isConnecting ? Color.yellow : Color.red);
            GUILayout.Label(connectionStatus, new GUIStyle(GUI.skin.label) { normal = { textColor = statusColor } });

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void DoDisconnect()
        {
            try
            {
                if (DataPersistenceManager.instance != null)
                {
                    DataPersistenceManager.instance.SaveGame();
                    Log.LogInfo("[AP] Forced SaveGame() on disconnect");
                }
            }
            catch (Exception e) { Log.LogWarning("[AP] SaveGame on disconnect failed: " + e.Message); }

            PersistCurrentIndex();

            try
            {
                if (ws != null && ws.State == WebSocketState.Open)
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "user disconnect", CancellationToken.None);
            }
            catch (Exception e)
            {
                Log.LogWarning("[AP] Error closing WebSocket: " + e.Message);
            }
            connected = false;
            authenticated = false;
            connectionStatus = "Disconnected";
            isConnecting = false;
            Log.LogInfo("[AP] Disconnected by user.");
        }

        private void DrawStatusBar()
        {
            Color c = authenticated ? Color.green : (connected ? Color.yellow : Color.red);
            GUI.Label(new Rect(10, 10, 400, 25), "[AP] " + connectionStatus,
                new GUIStyle(GUI.skin.label) { normal = { textColor = c }, fontSize = 14 });
        }

        // ─── Notification rendering ─────────────────────────────────────────

        private void DrawNotifications()
        {
            var notifications = APStateManager.Instance.notifications;
            float y = 40f;
            for (int i = 0; i < notifications.Count; i++)
            {
                var n = notifications[i];
                float alpha = Mathf.Clamp01(n.timer / 1.5f);
                Color c = n.color;
                c.a = alpha;
                var style = new GUIStyle(GUI.skin.label)
                {
                    normal = { textColor = c },
                    fontSize = 16,
                    alignment = TextAnchor.MiddleRight,
                    fontStyle = FontStyle.Bold,
                };

                // Draw shadow
                Color shadow = Color.black;
                shadow.a = alpha * 0.7f;
                var shadowStyle = new GUIStyle(style) { normal = { textColor = shadow } };
                GUI.Label(new Rect(Screen.width - 509, y + 1, 500, 28), n.text, shadowStyle);

                // Draw text
                GUI.Label(new Rect(Screen.width - 510, y, 500, 28), n.text, style);
                y += 28;
            }
        }

        // (Upgrade labels are handled via Unity UI in APStateManager.UpdateUpgradeLabels)

        // ─── Connect ────────────────────────────────────────────────────────

        private void DoConnect()
        {
            apServer   = uiServer.Trim();
            apSlotName = uiSlotName.Trim();
            apPassword = uiPassword;

            cfgServer.Value   = apServer;
            cfgSlotName.Value = apSlotName;
            cfgPassword.Value = apPassword;

            isConnecting = true;
            connectionStatus = "Connecting...";
            StartCoroutine(WebSocketConnect());
        }

        // ─── Save management ────────────────────────────────────────────────

        private static string SessionId(string session)
        {
            var sb = new StringBuilder();
            foreach (var c in session)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        private void CheckAndResetForNewRun()
        {
            string saveDir = Application.persistentDataPath;
            string sessionPath = Path.Combine(saveDir, "ap_session.txt");
            string savePath = Path.Combine(saveDir, "savedata");
            string bakPath = Path.Combine(saveDir, "savedata.bak");

            string currentSession = $"{roomSeedName}|{apSlotName}";
            string lastSession = File.Exists(sessionPath) ? File.ReadAllText(sessionPath).Trim() : null;

            Log.LogInfo($"[AP] Current session: {currentSession}");
            Log.LogInfo($"[AP] Last session: {lastSession ?? "(none)"}");

            string currentIndexPath = Path.Combine(saveDir, $"ap_index_{SessionId(currentSession)}.txt");

            if (lastSession == currentSession)
            {
                Log.LogInfo("[AP] Same AP run — keeping save.");
                if (currentIndex == 0 && File.Exists(currentIndexPath)
                    && long.TryParse(File.ReadAllText(currentIndexPath).Trim(), out long restoredIdx))
                {
                    currentIndex = restoredIdx;
                    Log.LogInfo($"[AP] Restored currentIndex={currentIndex} from {currentIndexPath}");
                }
                skipInitialItemDump = false;
                needsSceneReloadAfterConnect = false;
                return;
            }

            Log.LogInfo("[AP] Switching AP session — swapping per-session save files...");

            try
            {
                if (DataPersistenceManager.instance != null)
                {
                    DataPersistenceManager.instance.SaveGame();
                    Log.LogInfo("[AP] Forced SaveGame() before stash");
                }
            }
            catch (Exception e) { Log.LogWarning("[AP] SaveGame before swap failed: " + e.Message); }

            if (!string.IsNullOrEmpty(lastSession))
            {
                string oldIndexPath = Path.Combine(saveDir, $"ap_index_{SessionId(lastSession)}.txt");
                try
                {
                    File.WriteAllText(oldIndexPath, currentIndex.ToString());
                    Log.LogInfo($"[AP] Saved old session currentIndex={currentIndex} -> {Path.GetFileName(oldIndexPath)}");
                }
                catch (Exception e) { Log.LogWarning("[AP] Persist index failed: " + e.Message); }
            }

            if (File.Exists(savePath) && !string.IsNullOrEmpty(lastSession))
            {
                string oldSessionSavePath = Path.Combine(saveDir, $"ap_save_{SessionId(lastSession)}.bak");
                File.Copy(savePath, oldSessionSavePath, true);
                Log.LogInfo($"[AP] Stashed old session save -> ap_save_{SessionId(lastSession)}.bak");
            }

            string newSessionSavePath = Path.Combine(saveDir, $"ap_save_{SessionId(currentSession)}.bak");
            if (File.Exists(newSessionSavePath))
            {
                File.Copy(newSessionSavePath, savePath, true);
                Log.LogInfo($"[AP] Restored save from ap_save_{SessionId(currentSession)}.bak");
            }
            else
            {
                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                    Log.LogInfo("[AP] No prior save for this session — starting fresh.");
                }
            }
            if (File.Exists(bakPath)) File.Delete(bakPath);

            File.WriteAllText(sessionPath, currentSession);

            checkedLocations.Clear();
            roomLocationIds.Clear();
            upgradePurchaseCounts.Clear();
            scoutCache.Clear();
            currentIndex = 0;
            APStateManager.Instance?.ResetRuntimeState();

            if (File.Exists(currentIndexPath)
                && long.TryParse(File.ReadAllText(currentIndexPath).Trim(), out long newIdx))
            {
                currentIndex = newIdx;
                Log.LogInfo($"[AP] Loaded currentIndex={currentIndex} for new session");
            }

            skipInitialItemDump = false;
            needsSceneReloadAfterConnect = true;
        }

        private void PersistCurrentIndex()
        {
            if (string.IsNullOrEmpty(roomSeedName) || string.IsNullOrEmpty(apSlotName)) return;
            try
            {
                string saveDir = Application.persistentDataPath;
                string sid = SessionId($"{roomSeedName}|{apSlotName}");
                File.WriteAllText(Path.Combine(saveDir, $"ap_index_{sid}.txt"), currentIndex.ToString());
            }
            catch (Exception e) { Log.LogWarning("[AP] Persist index failed: " + e.Message); }
        }

        private IEnumerator ReloadSceneCoroutine()
        {
            Log.LogInfo("[AP] Reloading active scene to apply fresh save...");
            APStateManager.Instance?.MarkObjectivesNotYetRestored();
            yield return new WaitForSeconds(0.3f);
            var scene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(scene.buildIndex);

            yield return new WaitForSeconds(2f);
            try
            {
                if (DataPersistenceManager.instance != null)
                {
                    DataPersistenceManager.instance.OnSceneLoaded();
                    Log.LogInfo("[AP] Re-ran DataPersistenceManager.OnSceneLoaded() to catch late-instantiated upgrades");
                }
            }
            catch (Exception e) { Log.LogWarning("[AP] OnSceneLoaded retry failed: " + e.Message); }
        }

        // ─── WebSocket connection ───────────────────────────────────────────

        private IEnumerator WebSocketConnect()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 |
                    (SecurityProtocolType)3072 |
                    SecurityProtocolType.Tls11 |
                    SecurityProtocolType.Tls;
            }
            catch (Exception e) { Log.LogWarning("[AP] SecurityProtocol setup failed: " + e.Message); }

            ws = new ClientWebSocket();
            string protocol = apServer.StartsWith("localhost") || apServer.StartsWith("127.0.0.1") ? "ws" : "wss";
            Uri uri;
            try { uri = new Uri(protocol + "://" + apServer); }
            catch (Exception e)
            {
                Log.LogError($"[AP] Bad server URL '{apServer}': {e.Message}");
                connectionStatus = "Bad server URL";
                isConnecting = false;
                yield break;
            }
            Log.LogInfo($"[AP] Connecting to {uri}");

            Task connectTask;
            try { connectTask = ws.ConnectAsync(uri, CancellationToken.None); }
            catch (Exception e)
            {
                Log.LogError($"[AP] ConnectAsync threw {e.GetType().Name}: {e.Message}");
                connectionStatus = "Connection failed: " + e.Message;
                isConnecting = false;
                yield break;
            }
            yield return new WaitUntil(() => connectTask.IsCompleted);

            if (connectTask.IsFaulted)
            {
                var ex = connectTask.Exception?.GetBaseException();
                Log.LogError($"[AP] WebSocket connect faulted: {ex?.GetType().FullName}: {ex?.Message}");
                connectionStatus = "Connection failed: " + (ex?.Message ?? "unknown");
                isConnecting = false;
                yield break;
            }

            if (ws.State != WebSocketState.Open)
            {
                Log.LogWarning($"[AP] WebSocket not open after connect. State={ws.State}");
                connectionStatus = $"Connection failed (state={ws.State})";
                isConnecting = false;
                yield break;
            }

            connected = true;
            isConnecting = false;
            connectionStatus = "Connected, authenticating...";
            Log.LogInfo("Connected to AP server!");
            StartCoroutine(ReceiveLoop());
        }

        private IEnumerator ReceiveLoop()
        {
            var buffer = new byte[16384];
            var msgBuffer = new StringBuilder();
            while (ws != null && ws.State == WebSocketState.Open)
            {
                var segment = new ArraySegment<byte>(buffer);
                var receiveTask = ws.ReceiveAsync(segment, CancellationToken.None);
                yield return new WaitUntil(() => receiveTask.IsCompleted);

                if (receiveTask.Result.MessageType == WebSocketMessageType.Close)
                {
                    connected = false;
                    authenticated = false;
                    connectionStatus = "Disconnected";
                    Log.LogWarning("AP server disconnected.");
                    yield break;
                }

                msgBuffer.Append(Encoding.UTF8.GetString(buffer, 0, receiveTask.Result.Count));
                if (receiveTask.Result.EndOfMessage)
                {
                    HandleMessage(msgBuffer.ToString());
                    msgBuffer.Clear();
                }
            }
        }

        // ─── Message handling ───────────────────────────────────────────────

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
                            if (packet.ContainsKey("seed_name"))
                            {
                                roomSeedName = packet["seed_name"].ToString();
                                Log.LogInfo($"[AP] Room seed: {roomSeedName}");
                            }
                            SendConnectPacket();
                            break;

                        case "Connected":
                            HandleConnected(packet);
                            break;

                        case "ReceivedItems":
                            HandleReceivedItems(packet);
                            break;

                        case "LocationInfo":
                            HandleLocationInfo(packet);
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

        private void HandleConnected(Dictionary<string, object> packet)
        {
            authenticated = true;
            connectionStatus = $"Connected as {apSlotName}";
            showConnectionUI = false;
            Log.LogInfo("Authenticated with AP server!");

            // Parse slot info
            if (packet.ContainsKey("slot"))
                mySlot = Convert.ToInt32(packet["slot"]);

            // Parse player names
            playerNames.Clear();
            if (packet.ContainsKey("players"))
            {
                try
                {
                    var players = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(packet["players"].ToString());
                    foreach (var p in players)
                    {
                        int slot = Convert.ToInt32(p["slot"]);
                        string name = p.ContainsKey("alias") && p["alias"] != null && p["alias"].ToString() != ""
                            ? p["alias"].ToString()
                            : p["name"].ToString();
                        playerNames[slot] = name;
                    }
                }
                catch (Exception e) { Log.LogWarning("[AP] Failed to parse players: " + e.Message); }
            }

            CheckAndResetForNewRun();

            // Build the set of all location IDs in this room
            roomLocationIds.Clear();
            if (packet.ContainsKey("checked_locations"))
            {
                var locs = JsonConvert.DeserializeObject<List<long>>(packet["checked_locations"].ToString());
                foreach (var l in locs)
                {
                    checkedLocations.Add(l);
                    roomLocationIds.Add(l);
                }
                Log.LogInfo($"[AP] Already checked {locs.Count} locations");
            }
            if (packet.ContainsKey("missing_locations"))
            {
                var missing = JsonConvert.DeserializeObject<List<long>>(packet["missing_locations"].ToString());
                foreach (var l in missing) roomLocationIds.Add(l);
                Log.LogInfo($"[AP] Missing {missing.Count} locations. Total room locations: {roomLocationIds.Count}");
            }
            APStateManager.Instance?.RestoreFromCheckedLocations(checkedLocations);

            if (needsSceneReloadAfterConnect)
            {
                needsSceneReloadAfterConnect = false;
                StartCoroutine(ReloadSceneCoroutine());
            }

            // Scout all unchecked upgrade locations for the hint system
            SendScoutRequest();
        }

        private void HandleReceivedItems(Dictionary<string, object> packet)
        {
            long index = Convert.ToInt64(packet["index"]);
            var items = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(packet["items"].ToString());
            Log.LogInfo($"[AP] ReceivedItems: index={index}, count={items.Count}, currentIndex={currentIndex}");

            if (skipInitialItemDump && index == 0)
            {
                skipInitialItemDump = false;
                currentIndex = items.Count;
                Log.LogInfo($"[AP] Same session — skipping {items.Count} initial items already applied in save. currentIndex={currentIndex}");
                return;
            }
            skipInitialItemDump = false;

            for (int i = 0; i < items.Count; i++)
            {
                long itemId = Convert.ToInt64(items[i]["item"]);
                string itemName = GetItemName(itemId);

                bool isWorldUnlock = itemName == "World 1 Unlock"
                                   || itemName == "World 2 Unlock"
                                   || itemName == "World 3 Unlock";
                bool alreadyApplied = index + i < currentIndex;

                if (alreadyApplied && !isWorldUnlock) continue;
                if (!alreadyApplied) currentIndex = index + i + 1;

                Log.LogInfo($"[AP] Item #{index + i}: {itemName ?? "unknown"} (id={itemId})");
                if (itemName != null)
                {
                    GrantItem(itemName);

                    // Show notification for genuinely new items
                    if (!alreadyApplied && APStateManager.Instance != null)
                    {
                        Color notifColor = Color.white;
                        if (itemName.Contains("Unlock"))
                            notifColor = new Color(0.6f, 0.4f, 1f); // purple for progression
                        else if (itemName == "Gold Pouch" || itemName == "Soul Bundle")
                            notifColor = new Color(1f, 0.85f, 0.2f); // gold for filler
                        else
                            notifColor = new Color(0.3f, 0.85f, 1f); // blue for upgrades

                        APStateManager.Instance.AddNotification($"Received: {itemName}", notifColor);
                    }
                }
            }
            Log.LogInfo($"[AP] Done processing items. currentIndex={currentIndex}");
            PersistCurrentIndex();
        }

        // ─── Scout system ───────────────────────────────────────────────────

        private void SendScoutRequest()
        {
            var uncheckedUpgradeIds = new List<long>();
            foreach (var kv in LocationIDs)
            {
                if (kv.Key.StartsWith("Buy ") && !checkedLocations.Contains(kv.Value) && roomLocationIds.Contains(kv.Value))
                    uncheckedUpgradeIds.Add(kv.Value);
            }
            if (uncheckedUpgradeIds.Count == 0)
            {
                Log.LogInfo("[AP] No unchecked upgrade locations to scout.");
                return;
            }

            Log.LogInfo($"[AP] Scouting {uncheckedUpgradeIds.Count} unchecked upgrade locations...");
            SendPacket(new[] {
                new Dictionary<string, object> {
                    { "cmd", "LocationScouts" },
                    { "locations", uncheckedUpgradeIds },
                    { "create_as_hint", 2 }
                }
            });
        }

        private void HandleLocationInfo(Dictionary<string, object> packet)
        {
            if (!packet.ContainsKey("locations")) return;
            try
            {
                var locations = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(packet["locations"].ToString());
                foreach (var loc in locations)
                {
                    long locId = Convert.ToInt64(loc["location"]);
                    long itemId = Convert.ToInt64(loc["item"]);
                    int player = Convert.ToInt32(loc["player"]);
                    int flags = Convert.ToInt32(loc["flags"]);

                    // Try to resolve item name (only works for our game's items)
                    string itemName = GetItemName(itemId) ?? "AP Item";

                    scoutCache[locId] = new ScoutInfo { itemName = itemName, player = player, flags = flags };
                }
                Log.LogInfo($"[AP] Scouted {locations.Count} locations — press F2 to view");
            }
            catch (Exception e) { Log.LogWarning("[AP] Failed to parse LocationInfo: " + e.Message); }
        }

        // ─── Helpers ────────────────────────────────────────────────────────

        private string GetItemName(long id)
        {
            int idx = (int)(id - ITEM_BASE);
            if (idx >= 0 && idx < ItemNames.Length) return ItemNames[idx];
            return null;
        }

        internal string GetLocationName(long id)
        {
            foreach (var kv in LocationIDs)
            {
                if (kv.Value == id) return kv.Key;
            }
            return null;
        }

        private void SendConnectPacket()
        {
            SendPacket(new[] {
                new Dictionary<string, object> {
                    { "cmd", "Connect" },
                    { "game", apGame },
                    { "name", apSlotName },
                    { "password", apPassword },
                    { "version", new Dictionary<string, object> { {"major",0},{"minor",5},{"build",0},{"class","Version"} } },
                    { "items_handling", 7 },
                    { "tags", new string[0] },
                    { "uuid", Guid.NewGuid().ToString() },
                }
            });
        }

        // ─── Grant item from AP ─────────────────────────────────────────────

        public void GrantItem(string itemName)
        {
            Log.LogInfo($"[AP] Granting item: {itemName}");

            if (itemName == "World 1 Unlock" || itemName == "World 2 Unlock" || itemName == "World 3 Unlock")
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
            // It's an upgrade — map display name to class name
            if (DisplayToClassName.ContainsKey(itemName))
                APStateManager.Instance?.GrantUpgrade(DisplayToClassName[itemName]);
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

            // Show notification for checks sent
            if (APStateManager.Instance != null)
                APStateManager.Instance.AddNotification($"Check: {locationName}", new Color(0.2f, 1f, 0.2f));

            SendPacket(new[] {
                new Dictionary<string, object> {
                    { "cmd", "LocationChecks" },
                    { "locations", new long[] { id } }
                }
            });
        }

        public bool IsLocationChecked(string locationName)
        {
            if (!LocationIDs.ContainsKey(locationName)) return true;
            return checkedLocations.Contains(LocationIDs[locationName]);
        }

        public bool IsLocationInRoom(string locationName)
        {
            if (!LocationIDs.ContainsKey(locationName)) return false;
            return roomLocationIds.Contains(LocationIDs[locationName]);
        }

        /// <summary>
        /// Get the next unchecked upgrade location for this upgrade (by display name).
        /// </summary>
        public string GetNextUpgradeCheck(string upgradeDisplayName)
        {
            if (!upgradePurchaseCounts.ContainsKey(upgradeDisplayName))
                upgradePurchaseCounts[upgradeDisplayName] = 0;

            int nextLevel = upgradePurchaseCounts[upgradeDisplayName] + 1;
            string locName = UpgradeLocName(upgradeDisplayName, nextLevel);

            if (IsLocationInRoom(locName) && !IsLocationChecked(locName))
                return locName;

            return null;
        }

        public void SendGoal()
        {
            if (!authenticated) return;
            Log.LogInfo("[AP] Sending goal completion!");
            if (APStateManager.Instance != null)
                APStateManager.Instance.AddNotification("GOAL COMPLETE!", new Color(1f, 0.84f, 0f));
            SendPacket(new[] {
                new Dictionary<string, object> {
                    { "cmd", "StatusUpdate" },
                    { "status", 30 }
                }
            });
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

        public bool World1Unlocked = false;
        public bool World2Unlocked = false;
        public bool World3Unlocked = false;

        public List<string> PendingUpgrades = new List<string>();
        public float PendingMoney = 0f;

        internal bool isGrantingUpgrade = false;

        private HashSet<long> pendingCheckedLocations = null;
        private bool objectivesRestored = false;

        private int totalSoulsTracked = 0;
        private int totalKillsTracked = 0;
        private float retryTimer = 0f;

        // Track which upgrades are currently known to be active (for unlock detection)
        private HashSet<string> knownActiveUpgrades = new HashSet<string>();

        // Cached Upgrade references for hover tooltip (refreshed every 2s)
        internal Upgrade[] cachedUpgrades;

        // Hover tooltip: override the game's description text when hovering an AP upgrade
        private TMPro.TextMeshProUGUI _descriptionText;
        private string _originalDescription;
        private bool _isShowingAPDesc = false;
        private Upgrade _lastHoveredUpgrade;
        private bool _descriptionSearchDone = false;

        // ─── Notifications ──────────────────────────────────────────────────

        internal struct ItemNotification
        {
            public string text;
            public Color color;
            public float timer;
        }
        internal List<ItemNotification> notifications = new List<ItemNotification>();

        public void AddNotification(string text, Color color)
        {
            notifications.Insert(0, new ItemNotification { text = text, color = color, timer = 5f });
            if (notifications.Count > 12) notifications.RemoveAt(notifications.Count - 1);
        }

        // ─── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void ResetRuntimeState()
        {
            World1Unlocked = false;
            World2Unlocked = false;
            World3Unlocked = false;
            PendingUpgrades.Clear();
            PendingMoney = 0f;
            pendingCheckedLocations = null;
            objectivesRestored = false;
            totalSoulsTracked = 0;
            totalKillsTracked = 0;
            knownActiveUpgrades.Clear();
            cachedUpgrades = null;
            _descriptionText = null;
            _originalDescription = null;
            _isShowingAPDesc = false;
            _lastHoveredUpgrade = null;
            _descriptionSearchDone = false;
            notifications.Clear();
        }

        public void MarkObjectivesNotYetRestored()
        {
            objectivesRestored = false;
        }

        private void Update()
        {
            // Update notification timers
            for (int i = notifications.Count - 1; i >= 0; i--)
            {
                var n = notifications[i];
                n.timer -= Time.deltaTime;
                notifications[i] = n;
                if (n.timer <= 0) notifications.RemoveAt(i);
            }

            // Hover tooltip runs every frame for responsiveness
            UpdateHoverTooltip();

            retryTimer += Time.deltaTime;
            if (retryTimer < 2f) return;
            retryTimer = 0f;

            // Refresh cached upgrade list for hover tooltip
            cachedUpgrades = FindObjectsOfType<Upgrade>();

            if (PendingUpgrades.Count > 0)
                ApplyPendingUpgrades();

            if (PendingMoney != 0f && PlayerManager.Instance != null)
            {
                float toApply = PendingMoney;
                PendingMoney = 0f;
                PlayerManager.Instance.ModifyMoney(toApply);
                RataloricaAPPlugin.Log.LogInfo($"[AP] Applied {toApply} pending gold");
            }

            if (pendingCheckedLocations != null && !objectivesRestored)
                TryRestoreObjectives();

            // Scan for newly available upgrades (upgrade unlock checks)
            ScanForNewUpgrades();

            // Update AP item labels on upgrade icons
            UpdateUpgradeLabels();
        }

        // ─── Hover tooltip: show AP item in the game's description zone ────

        private void FindDescriptionText()
        {
            if (_descriptionSearchDone) return;
            _descriptionSearchDone = true;

            // Search all TMPro texts for one that looks like the upgrade description zone
            var allTmp = FindObjectsOfType<TMPro.TextMeshProUGUI>(true);
            TMPro.TextMeshProUGUI bestCandidate = null;
            float bestWidth = 0;

            foreach (var tmp in allTmp)
            {
                if (tmp == null) continue;
                // Skip our own labels
                if (tmp.gameObject.name.StartsWith("AP_")) continue;

                // Build hierarchy path
                string path = "";
                var t = tmp.transform;
                while (t != null) { path = t.gameObject.name + "/" + path; t = t.parent; }

                // Log all TMPro for debugging (first time only)
                RataloricaAPPlugin.Log.LogInfo($"[AP][Desc] TMPro: path={path} text='{tmp.text?.Substring(0, Math.Min(tmp.text?.Length ?? 0, 60))}' w={tmp.rectTransform.rect.width:F0}");

                // Look for description text: should be in the upgrade screen area, not inside a Floor/upgrade icon
                bool inUpgradeScreen = path.Contains("Upgrade") || path.Contains("Skill");
                bool inFloor = path.Contains("Floor") && path.Contains("Up_") || path.Contains("CmbtUp") || path.Contains("ShopUp") || path.Contains("ExploUp");
                bool isLargeEnough = tmp.rectTransform.rect.width > 80;

                // Prefer a text named "Description" or similar
                string goName = tmp.gameObject.name.ToLower();
                if (goName.Contains("desc") || goName.Contains("info") || goName.Contains("tooltip"))
                {
                    _descriptionText = tmp;
                    RataloricaAPPlugin.Log.LogInfo($"[AP][Desc] Found description by name: {tmp.gameObject.name}");
                    return;
                }

                // Otherwise pick the widest TMPro that's in the upgrade screen but NOT inside a floor/upgrade
                if (inUpgradeScreen && !inFloor && isLargeEnough)
                {
                    float w = tmp.rectTransform.rect.width;
                    if (w > bestWidth)
                    {
                        bestWidth = w;
                        bestCandidate = tmp;
                    }
                }
            }

            if (bestCandidate != null)
            {
                _descriptionText = bestCandidate;
                RataloricaAPPlugin.Log.LogInfo($"[AP][Desc] Best candidate: '{bestCandidate.gameObject.name}' width={bestWidth:F0} text='{bestCandidate.text?.Substring(0, Math.Min(bestCandidate.text?.Length ?? 0, 60))}'");
            }
            else
            {
                RataloricaAPPlugin.Log.LogWarning("[AP][Desc] Could not find description text component.");
            }
        }

        private void UpdateHoverTooltip()
        {
            if (cachedUpgrades == null || cachedUpgrades.Length == 0) return;
            if (RataloricaAPPlugin.Instance == null || !RataloricaAPPlugin.Instance.authenticated) return;
            if (RataloricaAPPlugin.Instance.scoutCache.Count == 0) return;

            // Find description text if not yet found
            if (_descriptionText == null)
            {
                FindDescriptionText();
                if (_descriptionText == null) return;
            }

            // If the description text got destroyed (scene change), re-search next time
            if (_descriptionText == null || _descriptionText.gameObject == null)
            {
                _descriptionText = null;
                _descriptionSearchDone = false;
                _isShowingAPDesc = false;
                return;
            }

            // Find the camera for the WorldSpace canvas
            Camera cam = null;
            var canvas = _descriptionText.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
                cam = canvas.worldCamera ?? Camera.main;

            Vector2 mousePos = Input.mousePosition;
            Upgrade hoveredUpgrade = null;

            foreach (var up in cachedUpgrades)
            {
                if (up == null) continue;
                Transform iconTransform = up.transform.parent;
                if (iconTransform == null) continue;
                var rt = iconTransform.GetComponent<RectTransform>();
                if (rt == null) continue;

                if (RectTransformUtility.RectangleContainsScreenPoint(rt, mousePos, cam))
                {
                    hoveredUpgrade = up;
                    break;
                }
            }

            if (hoveredUpgrade != null)
            {
                string className = hoveredUpgrade.GetType().Name;
                if (RataloricaAPPlugin.ClassToDisplayName.ContainsKey(className))
                {
                    string displayName = RataloricaAPPlugin.ClassToDisplayName[className];
                    string nextCheck = RataloricaAPPlugin.Instance.GetNextUpgradeCheck(displayName);
                    if (nextCheck != null && RataloricaAPPlugin.LocationIDs.ContainsKey(nextCheck))
                    {
                        long locId = RataloricaAPPlugin.LocationIDs[nextCheck];
                        if (RataloricaAPPlugin.Instance.scoutCache.ContainsKey(locId))
                        {
                            var info = RataloricaAPPlugin.Instance.scoutCache[locId];

                            // Build hint-style text:
                            // [Hint]: PlayerName's ItemName is at Buy UpgradeName in MyName's World.
                            string itemPlayerName = RataloricaAPPlugin.Instance.playerNames.ContainsKey(info.player)
                                ? RataloricaAPPlugin.Instance.playerNames[info.player]
                                : $"P{info.player}";
                            string myName = RataloricaAPPlugin.Instance.playerNames.ContainsKey(RataloricaAPPlugin.Instance.mySlot)
                                ? RataloricaAPPlugin.Instance.playerNames[RataloricaAPPlugin.Instance.mySlot]
                                : "Player";

                            string flagLabel = "";
                            if ((info.flags & 1) != 0) flagLabel = " (progression)";
                            else if ((info.flags & 2) != 0) flagLabel = " (useful)";
                            else if ((info.flags & 4) != 0) flagLabel = " (trap)";

                            string apText = $"[Hint]: {itemPlayerName}'s {info.itemName} is at {nextCheck} in {myName}'s World.{flagLabel}";

                            // Save original and override
                            if (!_isShowingAPDesc || _lastHoveredUpgrade != hoveredUpgrade)
                            {
                                _originalDescription = _descriptionText.text;
                                _isShowingAPDesc = true;
                                _lastHoveredUpgrade = hoveredUpgrade;
                            }
                            _descriptionText.text = apText;
                            return;
                        }
                    }
                }
            }

            // Not hovering an AP upgrade — restore original description
            if (_isShowingAPDesc)
            {
                _descriptionText.text = _originalDescription;
                _isShowingAPDesc = false;
                _lastHoveredUpgrade = null;
            }
        }

        // ─── Upgrade unlock detection ───────────────────────────────────────

        private void ScanForNewUpgrades()
        {
            if (RataloricaAPPlugin.Instance == null || !RataloricaAPPlugin.Instance.authenticated) return;

            try
            {
                var upgrades = FindObjectsOfType<Upgrade>();
                foreach (var up in upgrades)
                {
                    if (up == null || !up.gameObject.activeInHierarchy) continue;
                    string className = up.GetType().Name;
                    if (knownActiveUpgrades.Contains(className)) continue;

                    knownActiveUpgrades.Add(className);

                    // Look up the display name and send a check if the corresponding
                    // upgrade purchase check hasn't been sent yet. This fires when
                    // buying one upgrade reveals/unlocks another in the skill tree.
                    if (RataloricaAPPlugin.ClassToDisplayName.ContainsKey(className))
                    {
                        string displayName = RataloricaAPPlugin.ClassToDisplayName[className];
                        RataloricaAPPlugin.Log.LogInfo($"[AP] Detected newly available upgrade: {displayName} ({className})");
                    }
                }
            }
            catch { }
        }

        // ─── AP item labels on upgrade icons (Unity UI) ─────────────────────

        private Font _labelFont;
        private bool _labelsDebugLogged = false;

        private void UpdateUpgradeLabels()
        {
            if (cachedUpgrades == null || cachedUpgrades.Length == 0) return;
            if (RataloricaAPPlugin.Instance == null || !RataloricaAPPlugin.Instance.authenticated) return;
            if (RataloricaAPPlugin.Instance.scoutCache.Count == 0) return;

            if (_labelFont == null)
            {
                try { _labelFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch
                {
                    try { _labelFont = Font.CreateDynamicFontFromOSFont("Arial", 12); }
                    catch { return; }
                }
            }

            foreach (var up in cachedUpgrades)
            {
                if (up == null) continue;
                string className = up.GetType().Name;
                if (!RataloricaAPPlugin.ClassToDisplayName.ContainsKey(className)) continue;
                string displayName = RataloricaAPPlugin.ClassToDisplayName[className];

                string nextCheck = RataloricaAPPlugin.Instance.GetNextUpgradeCheck(displayName);
                if (nextCheck == null) continue;
                if (!RataloricaAPPlugin.LocationIDs.ContainsKey(nextCheck)) continue;
                long locId = RataloricaAPPlugin.LocationIDs[nextCheck];
                if (!RataloricaAPPlugin.Instance.scoutCache.ContainsKey(locId)) continue;

                var info = RataloricaAPPlugin.Instance.scoutCache[locId];

                // Debug: log the hierarchy and Canvas info once
                if (!_labelsDebugLogged)
                {
                    var canvas = up.GetComponentInParent<Canvas>();
                    bool hasRT = up.GetComponent<RectTransform>() != null;
                    string hierarchy = up.gameObject.name;
                    var t = up.transform.parent;
                    while (t != null) { hierarchy = t.gameObject.name + "/" + hierarchy; t = t.parent; }
                    RataloricaAPPlugin.Log.LogInfo($"[AP][Labels] DEBUG {displayName}: hierarchy={hierarchy}, hasRectTransform={hasRT}, hasCanvas={canvas != null}, canvasMode={canvas?.renderMode}");

                    // Also log children
                    string children = "";
                    for (int c = 0; c < up.transform.childCount; c++)
                        children += up.transform.GetChild(c).gameObject.name + ", ";
                    RataloricaAPPlugin.Log.LogInfo($"[AP][Labels] DEBUG {displayName}: children=[{children}]");

                    // Log all TMPro text in children
                    var tmps = up.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                    foreach (var tmp in tmps)
                        RataloricaAPPlugin.Log.LogInfo($"[AP][Labels] DEBUG {displayName}: TMPro child '{tmp.gameObject.name}' text='{tmp.text}'");
                }

                // Build label text and color
                bool isMyItem = (info.player == RataloricaAPPlugin.Instance.mySlot);
                string labelText = info.itemName;
                if (!isMyItem)
                {
                    string pName = RataloricaAPPlugin.Instance.playerNames.ContainsKey(info.player)
                        ? RataloricaAPPlugin.Instance.playerNames[info.player]
                        : $"P{info.player}";
                    labelText += $" ({pName})";
                }

                // Color based on item flags: 1=progression(purple), 2=useful(blue), 4=trap(red), else white
                Color labelColor;
                if ((info.flags & 1) != 0) labelColor = new Color(0.7f, 0.3f, 1f);       // progression
                else if ((info.flags & 2) != 0) labelColor = new Color(0.3f, 0.85f, 1f);  // useful
                else if ((info.flags & 4) != 0) labelColor = new Color(1f, 0.3f, 0.3f);   // trap
                else labelColor = new Color(1f, 0.95f, 0.7f);                              // filler

                // The Upgrade script is on #UpgradeScripts (empty child).
                // The actual visible icon is the PARENT (e.g. CmbtUp_Plus1BigDmg).
                // We place our label on the PARENT so it renders on the icon.
                Transform iconTransform = up.transform.parent;
                if (iconTransform == null) continue;

                // Find a TMPro template from the icon or its siblings for font
                var templateTmp = iconTransform.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                // If not found on icon, search the parent (floor container)
                if (templateTmp == null && iconTransform.parent != null)
                    templateTmp = iconTransform.parent.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);

                Transform existingLabel = iconTransform.Find("AP_TMPLabel");
                Transform existingUILabel = iconTransform.Find("AP_ItemLabel");

                if (templateTmp != null)
                {
                    // Use TMPro (matches game's font)
                    if (existingUILabel != null) Destroy(existingUILabel.gameObject); // remove old UI.Text

                    if (existingLabel == null)
                    {
                        var go = new GameObject("AP_TMPLabel");
                        go.transform.SetParent(iconTransform, false);

                        var label = go.AddComponent<TMPro.TextMeshProUGUI>();
                        label.font = templateTmp.font;
                        label.fontMaterial = templateTmp.fontMaterial;
                        label.text = labelText;
                        label.fontSize = 6;
                        label.color = labelColor;
                        label.alignment = TMPro.TextAlignmentOptions.Center;
                        label.overflowMode = TMPro.TextOverflowModes.Overflow;
                        label.raycastTarget = false;
                        label.enableWordWrapping = true;

                        var rt = go.GetComponent<RectTransform>();
                        rt.anchorMin = new Vector2(0f, 0f);
                        rt.anchorMax = new Vector2(1f, 0.35f);
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;

                        if (!_labelsDebugLogged)
                            RataloricaAPPlugin.Log.LogInfo($"[AP][Labels] Created TMPro label on {iconTransform.gameObject.name}: '{labelText}'");
                    }
                    else
                    {
                        var label = existingLabel.GetComponent<TMPro.TextMeshProUGUI>();
                        if (label != null) { label.text = labelText; label.color = labelColor; }
                    }
                }
                else
                {
                    // Fallback: UI.Text
                    if (existingLabel != null) Destroy(existingLabel.gameObject);

                    if (existingUILabel == null)
                    {
                        var go = new GameObject("AP_ItemLabel");
                        go.transform.SetParent(iconTransform, false);

                        var text = go.AddComponent<UnityEngine.UI.Text>();
                        text.text = labelText;
                        text.font = _labelFont;
                        text.fontSize = 8;
                        text.color = labelColor;
                        text.alignment = TextAnchor.LowerCenter;
                        text.horizontalOverflow = HorizontalWrapMode.Overflow;
                        text.verticalOverflow = VerticalWrapMode.Overflow;
                        text.raycastTarget = false;

                        var outline = go.AddComponent<UnityEngine.UI.Outline>();
                        outline.effectColor = Color.black;
                        outline.effectDistance = new Vector2(1, -1);

                        var rt = go.GetComponent<RectTransform>();
                        rt.anchorMin = new Vector2(0f, 0f);
                        rt.anchorMax = new Vector2(1f, 0.35f);
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;

                        if (!_labelsDebugLogged)
                            RataloricaAPPlugin.Log.LogInfo($"[AP][Labels] Created UI.Text label on {iconTransform.gameObject.name}: '{labelText}'");
                    }
                    else
                    {
                        var text = existingUILabel.GetComponent<UnityEngine.UI.Text>();
                        if (text != null) { text.text = labelText; text.color = labelColor; }
                    }
                }

                // AP logo overlay on ALL scouted upgrades (top-left corner)
                Transform existingLogo = iconTransform.Find("AP_Logo");
                if (RataloricaAPPlugin.Instance.apLogoTexture != null)
                {
                    if (existingLogo == null)
                    {
                        var logoGo = new GameObject("AP_Logo");
                        logoGo.transform.SetParent(iconTransform, false);

                        var img = logoGo.AddComponent<UnityEngine.UI.RawImage>();
                        img.texture = RataloricaAPPlugin.Instance.apLogoTexture;
                        img.raycastTarget = false;

                        var rt = logoGo.GetComponent<RectTransform>();
                        rt.anchorMin = new Vector2(0f, 0.6f);
                        rt.anchorMax = new Vector2(0.4f, 1f);
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;
                    }
                }
            }
            _labelsDebugLogged = true;
        }

        // ─── Restore state from AP ─────────────────────────────────────────

        public void RestoreFromCheckedLocations(HashSet<long> checkedLocations)
        {
            pendingCheckedLocations = checkedLocations;
            objectivesRestored = false;
            RataloricaAPPlugin.Log.LogInfo("[AP] Will restore game state from checked locations when ready...");
            TryRestoreObjectives();
        }

        private bool HasCheck(string locationName)
        {
            if (pendingCheckedLocations == null) return false;
            if (!RataloricaAPPlugin.LocationIDs.ContainsKey(locationName)) return false;
            return pendingCheckedLocations.Contains(RataloricaAPPlugin.LocationIDs[locationName]);
        }

        private void TryRestoreObjectives()
        {
            var objMgr = FindObjectOfType<ObjectivesManager>();
            if (objMgr == null)
            {
                RataloricaAPPlugin.Log.LogInfo("[AP] ObjectivesManager not ready yet, will retry...");
                return;
            }

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var t = typeof(ObjectivesManager);

            int completedCount = 0;
            if (HasCheck("Reach Shop Sign")) completedCount++;
            if (HasCheck("Reach Upgrades Sign")) completedCount++;

            if (completedCount > 0)
            {
                int currentId = 0;
                var idField = t.GetField("currentObjectiveId", flags);
                if (idField != null)
                {
                    try { currentId = (int)idField.GetValue(objMgr); } catch { }
                }
                int callsNeeded = Math.Max(0, completedCount - currentId);

                var nextObj = t.GetMethod("NextObjective", flags);
                if (nextObj != null && callsNeeded > 0)
                {
                    for (int i = 0; i < callsNeeded; i++)
                    {
                        try { nextObj.Invoke(objMgr, null); }
                        catch (Exception e)
                        {
                            RataloricaAPPlugin.Log.LogWarning($"[AP] NextObjective call #{i + 1} failed: " + (e.InnerException?.Message ?? e.Message));
                            try
                            {
                                int cur = (int)idField.GetValue(objMgr);
                                idField.SetValue(objMgr, cur + 1);
                            }
                            catch { }
                        }
                    }
                }

                if (HasCheck("Reach Shop Sign"))
                {
                    t.GetField("isShopSignActivated", flags)?.SetValue(objMgr, true);
                    var shopSign = t.GetField("shopSign", flags)?.GetValue(objMgr) as GameObject;
                    shopSign?.SetActive(true);
                }
                if (HasCheck("Reach Upgrades Sign"))
                {
                    t.GetField("isUpgradesSignActivated", flags)?.SetValue(objMgr, true);
                    var upgradeSign = t.GetField("upgradeSign", flags)?.GetValue(objMgr) as GameObject;
                    upgradeSign?.SetActive(true);
                }
            }

            objectivesRestored = true;
            RataloricaAPPlugin.Log.LogInfo("[AP] Game state restored from AP checked locations!");
        }

        // ─── World unlocks ─────────────────────────────────────────────────

        public void UnlockWorld(string worldName)
        {
            if (worldName == "World 1 Unlock") World1Unlocked = true;
            if (worldName == "World 2 Unlock") World2Unlocked = true;
            if (worldName == "World 3 Unlock") World3Unlocked = true;
            RataloricaAPPlugin.Log.LogInfo($"[AP] {worldName} unlocked!");

            // Check if goal conditions are now met
            CheckGoalCompletion();
        }

        public void CheckGoalCompletion()
        {
            if (!World1Unlocked || !World2Unlocked || !World3Unlocked) return;

            // Goal: all worlds unlocked + legendary kill in World 3 + 100 souls collected
            bool killedLegendary = RataloricaAPPlugin.Instance?.IsLocationChecked("World 3 - Kill Legendary Enemy") ?? false;
            bool collected100Souls = RataloricaAPPlugin.Instance?.IsLocationChecked("World 3 - Collect 100 Souls") ?? false;

            if (killedLegendary && collected100Souls)
            {
                RataloricaAPPlugin.Log.LogInfo("[AP] All goal conditions met!");
                RataloricaAPPlugin.Instance?.SendGoal();
            }
        }

        public bool CanAccessWorld(WorldManager.World world)
        {
            switch (world)
            {
                case WorldManager.World.WORLD1: return World1Unlocked;
                case WorldManager.World.WORLD2: return World1Unlocked && World2Unlocked;
                case WorldManager.World.WORLD3: return World1Unlocked && World2Unlocked && World3Unlocked;
                default: return true;
            }
        }

        // ─── Money / Souls ──────────────────────────────────────────────────

        public void AddMoney(float amount)
        {
            PendingMoney += amount;
            RataloricaAPPlugin.Log.LogInfo($"[AP] Queued {amount} gold (total pending: {PendingMoney})");
        }

        public void AddSouls(int amount)
        {
            RataloricaAPPlugin.Log.LogInfo("[AP] Soul Bundle received — converting to 50 gold.");
            AddMoney(50f);
        }

        // ─── Upgrade granting (from AP) ─────────────────────────────────────

        public void GrantUpgrade(string upgradeClassName)
        {
            PendingUpgrades.Add(upgradeClassName);
            ApplyPendingUpgrades();
        }

        public void ApplyPendingUpgrades()
        {
            var upgrades = FindObjectsOfType<Upgrade>();
            var toRemove = new List<string>();

            isGrantingUpgrade = true;
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
            isGrantingUpgrade = false;

            foreach (var r in toRemove) PendingUpgrades.Remove(r);
        }

        // ─── Upgrade purchase (by player, effect blocked) ───────────────────

        public void OnUpgradePurchased(Upgrade upgrade)
        {
            string typeName = upgrade.GetType().Name;
            // Map class name to display name for AP
            string displayName = RataloricaAPPlugin.ClassToDisplayName.ContainsKey(typeName)
                ? RataloricaAPPlugin.ClassToDisplayName[typeName]
                : typeName;

            string locName = RataloricaAPPlugin.Instance.GetNextUpgradeCheck(displayName);

            if (locName != null)
            {
                if (!RataloricaAPPlugin.Instance.upgradePurchaseCounts.ContainsKey(displayName))
                    RataloricaAPPlugin.Instance.upgradePurchaseCounts[displayName] = 0;
                RataloricaAPPlugin.Instance.upgradePurchaseCounts[displayName]++;

                RataloricaAPPlugin.Instance.SendCheck(locName);
                RataloricaAPPlugin.Log.LogInfo($"[AP] Upgrade purchase check sent: {locName}");
            }
        }

        // ─── UI messages ────────────────────────────────────────────────────

        public void ShowLockedMessage(string worldName)
        {
            AddNotification($"[AP] {worldName} is locked!", new Color(1f, 0.3f, 0.3f));
        }

        // ─── Event handlers (called by patches) ────────────────────────────

        public void OnEnemyKilled(Enemy enemy, WorldManager.World currentWorld)
        {
            totalKillsTracked++;

            // Kill milestones
            if (totalKillsTracked == 10)  RataloricaAPPlugin.Instance?.SendCheck("Kill 10 Enemies");
            if (totalKillsTracked == 25)  RataloricaAPPlugin.Instance?.SendCheck("Kill 25 Enemies");
            if (totalKillsTracked == 50)  RataloricaAPPlugin.Instance?.SendCheck("Kill 50 Enemies");
            if (totalKillsTracked == 100) RataloricaAPPlugin.Instance?.SendCheck("Kill 100 Enemies");
            if (totalKillsTracked == 200) RataloricaAPPlugin.Instance?.SendCheck("Kill 200 Enemies");

            // World-specific kill checks
            string firstKillLoc = currentWorld switch
            {
                WorldManager.World.WORLD1 => "World 1 - First Kill",
                WorldManager.World.WORLD2 => "World 2 - First Kill",
                WorldManager.World.WORLD3 => "World 3 - First Kill",
                _ => null
            };
            if (firstKillLoc != null)
                RataloricaAPPlugin.Instance?.SendCheck(firstKillLoc);

            if (enemy.Rarity == Enemy.RarityTypes.UNCOMMON && currentWorld == WorldManager.World.WORLD1)
                RataloricaAPPlugin.Instance?.SendCheck("World 1 - Kill Uncommon Enemy");

            if (enemy.Rarity == Enemy.RarityTypes.RARE)
            {
                if (currentWorld == WorldManager.World.WORLD1)
                    RataloricaAPPlugin.Instance?.SendCheck("World 1 - Kill Rare Enemy");
                if (currentWorld == WorldManager.World.WORLD2)
                    RataloricaAPPlugin.Instance?.SendCheck("World 2 - Kill Rare Enemy");
            }

            if (enemy.Rarity == Enemy.RarityTypes.LEGENDARY)
            {
                if (currentWorld == WorldManager.World.WORLD2)
                    RataloricaAPPlugin.Instance?.SendCheck("World 2 - Kill Legendary Enemy");
                if (currentWorld == WorldManager.World.WORLD3)
                {
                    RataloricaAPPlugin.Instance?.SendCheck("World 3 - Kill Legendary Enemy");
                    CheckGoalCompletion();
                }
            }
        }

        public void OnSoulsUpdated(int total)
        {
            totalSoulsTracked = total;
            if (total >= 1)   RataloricaAPPlugin.Instance?.SendCheck("First Soul Collected");
            if (total >= 10)  RataloricaAPPlugin.Instance?.SendCheck("World 1 - Collect 10 Souls");
            if (total >= 50)  RataloricaAPPlugin.Instance?.SendCheck("World 2 - Collect 50 Souls");
            if (total >= 100)
            {
                RataloricaAPPlugin.Instance?.SendCheck("World 3 - Collect 100 Souls");
                CheckGoalCompletion();
            }
        }
    }

    // ─── Harmony Patches ────────────────────────────────────────────────────

    [HarmonyPatch(typeof(TransparentGame), "Update")]
    public class TransparentGameUpdatePatch
    {
        private static MethodInfo _setClickthrough;

        static void Postfix(TransparentGame __instance)
        {
            try
            {
                var plugin = RataloricaAPPlugin.Instance;
                if (plugin == null || !plugin.IsConnectionUIVisible) return;

                var rect = plugin.ConnectionWindowRect;
                float mx = Input.mousePosition.x;
                float my = Screen.height - Input.mousePosition.y;
                if (!rect.Contains(new Vector2(mx, my))) return;

                if (_setClickthrough == null)
                    _setClickthrough = typeof(TransparentGame).GetMethod("SetClickthrough",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                _setClickthrough?.Invoke(__instance, new object[] { false });
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("TransparentGameUpdatePatch error: " + e.Message);
            }
        }
    }

    // CORE PATCH: Intercept upgrade purchases
    [HarmonyPatch(typeof(Upgrade), "BuyUpgrade")]
    public class UpgradeBuyPatch
    {
        static bool Prefix(Upgrade __instance)
        {
            try
            {
                if (APStateManager.Instance == null) return true;
                if (APStateManager.Instance.isGrantingUpgrade) return true;
                if (RataloricaAPPlugin.Instance == null) return true;

                string typeName = __instance.GetType().Name;
                if (!RataloricaAPPlugin.ClassToDisplayName.ContainsKey(typeName)) return true;
                string displayName = RataloricaAPPlugin.ClassToDisplayName[typeName];

                string nextCheck = RataloricaAPPlugin.Instance.GetNextUpgradeCheck(displayName);
                if (nextCheck == null) return true; // No more checks → normal purchase

                // Block the effect, send check
                APStateManager.Instance.OnUpgradePurchased(__instance);
                return false;
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("UpgradeBuyPatch error: " + e.Message);
                return true;
            }
        }
    }

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

    [HarmonyPatch(typeof(WorldManager), "SwitchToWorld")]
    public class WorldSwitchPatch
    {
        static void Prefix(ref WorldManager.World _worldToSwitchTo)
        {
            try
            {
                if (APStateManager.Instance == null) return;
                if (!APStateManager.Instance.CanAccessWorld(_worldToSwitchTo))
                {
                    string worldName = _worldToSwitchTo.ToString();
                    RataloricaAPPlugin.Log.LogInfo($"[AP] {worldName} is locked! Redirecting to CLASSIC.");
                    _worldToSwitchTo = WorldManager.World.CLASSIC;
                    APStateManager.Instance.ShowLockedMessage(worldName);
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
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var shopField = typeof(ObjectivesManager).GetField("isShopSignActivated", flags);
                var upField = typeof(ObjectivesManager).GetField("isUpgradesSignActivated", flags);

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

    // ─── SkillTree Floor Unlock Patches ─────────────────────────────────────

    [HarmonyPatch(typeof(SkillTree), "AddPointsToUnlockFloor2")]
    public class SkillTreeFloor2Patch
    {
        static void Postfix(SkillTree __instance)
        {
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var field = typeof(SkillTree).GetField("floor2", flags);
                if (field == null) return;

                object floor2 = field.GetValue(__instance);
                int pointsIn = (int)floor2.GetType().GetField("pointsIn").GetValue(floor2);
                int needed = (int)floor2.GetType().GetField("pointsToUnlock").GetValue(floor2);

                if (pointsIn >= needed)
                {
                    RataloricaAPPlugin.Log.LogInfo("[AP] SkillTree Floor 2 unlocked!");
                    RataloricaAPPlugin.Instance?.SendCheck("SkillTree Floor 2 Unlocked");
                }
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("SkillTreeFloor2Patch error: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(SkillTree), "AddPointsToUnlockFloor3")]
    public class SkillTreeFloor3Patch
    {
        static void Postfix(SkillTree __instance)
        {
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var field = typeof(SkillTree).GetField("floor3", flags);
                if (field == null) return;

                object floor3 = field.GetValue(__instance);
                int pointsIn = (int)floor3.GetType().GetField("pointsIn").GetValue(floor3);
                int needed = (int)floor3.GetType().GetField("pointsToUnlock").GetValue(floor3);

                if (pointsIn >= needed)
                {
                    RataloricaAPPlugin.Log.LogInfo("[AP] SkillTree Floor 3 unlocked!");
                    RataloricaAPPlugin.Instance?.SendCheck("SkillTree Floor 3 Unlocked");
                }
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("SkillTreeFloor3Patch error: " + e.Message);
            }
        }
    }

    // ─── Soul Counter Patch ─────────────────────────────────────────────────

    [HarmonyPatch(typeof(PlayerManager), "IncrementSoulCounter")]
    public class SoulCounterPatch
    {
        static void Postfix(PlayerManager __instance)
        {
            try
            {
                int total = __instance.TotalSoulsCollected;
                APStateManager.Instance?.OnSoulsUpdated(total);

                var souls = __instance.SoulsCollected;
                if (souls.ContainsKey(Enemy.RarityTypes.UNCOMMON) && souls[Enemy.RarityTypes.UNCOMMON] >= 1)
                    RataloricaAPPlugin.Instance?.SendCheck("Uncommon Soul Collected");
                if (souls.ContainsKey(Enemy.RarityTypes.RARE) && souls[Enemy.RarityTypes.RARE] >= 1)
                    RataloricaAPPlugin.Instance?.SendCheck("Rare Soul Collected");
                if (souls.ContainsKey(Enemy.RarityTypes.LEGENDARY) && souls[Enemy.RarityTypes.LEGENDARY] >= 1)
                    RataloricaAPPlugin.Instance?.SendCheck("Legendary Soul Collected");
            }
            catch (Exception e)
            {
                RataloricaAPPlugin.Log.LogError("SoulCounterPatch error: " + e.Message);
            }
        }
    }
}
