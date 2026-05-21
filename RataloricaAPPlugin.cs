using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    [BepInPlugin("com.ratalorica.archipelago", "Ratalorica Archipelago Client", "1.0.0")]
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

        // Set in CheckAndResetForNewRun, consumed after Connected packet
        private bool needsSceneReloadAfterConnect = false;
        // Same session: save already has the items; don't re-apply the initial dump
        private bool skipInitialItemDump = false;
        // Stable identifier for the AP room — captured from RoomInfo
        private string roomSeedName = null;

        private HashSet<long> checkedLocations = new HashSet<long>();
        // All location IDs in the current room (checked + missing)
        private HashSet<long> roomLocationIds = new HashSet<long>();
        // Track how many times each upgrade has been purchased (for multi-level checks)
        internal Dictionary<string, int> upgradePurchaseCounts = new Dictionary<string, int>();

        // Base IDs matching the apworld
        private const long ITEM_BASE     = 0xCA7A0000;
        private const long LOCATION_BASE = 0xCA7A1000;
        private const int MAX_UPGRADE_LEVELS = 10;

        // ─── Item names (order must match apworld ALL_ITEM_NAMES) ───────────

        private static readonly string[] ItemNames = {
            "CmbtUp_AugmentRarityPoprate","CmbtUp_AugmentWalkingSpeed","CmbtUp_Plus1BigDamage",
            "CmbtUp_Plus1MaxEnemies","CmbtUp_Plus1SmallDamage","CmbtUp_Plus1Stam",
            "CmbtUp_ReduceSpawnCooldown","ExploUp_AdoptChick","ExploUp_AdoptRabbit",
            "ExploUp_AugmentDoubleSouls","ExploUp_AugmentSpawnChance","ExploUp_AugmentStability",
            "ShopUp_AttractMoreClients","ShopUp_AugmentRarityPrice","ShopUp_Charisma",
            "ShopUp_Plus1NpcAtATime","ShopUp_Plus1Shout","ShopUp_Plus1Stam",
            "ShopUp_Plus1Talk","Up_AutoInput",
            "World1 Unlock","World2 Unlock","World3 Unlock",
            "Gold Pouch","Soul Bundle",
        };

        // ─── Upgrade names (same order as apworld UPGRADE_ITEMS) ────────────

        private static readonly string[] UpgradeNames = {
            "CmbtUp_AugmentRarityPoprate","CmbtUp_AugmentWalkingSpeed","CmbtUp_Plus1BigDamage",
            "CmbtUp_Plus1MaxEnemies","CmbtUp_Plus1SmallDamage","CmbtUp_Plus1Stam",
            "CmbtUp_ReduceSpawnCooldown","ExploUp_AdoptChick","ExploUp_AdoptRabbit",
            "ExploUp_AugmentDoubleSouls","ExploUp_AugmentSpawnChance","ExploUp_AugmentStability",
            "ShopUp_AttractMoreClients","ShopUp_AugmentRarityPrice","ShopUp_Charisma",
            "ShopUp_Plus1NpcAtATime","ShopUp_Plus1Shout","ShopUp_Plus1Stam",
            "ShopUp_Plus1Talk","Up_AutoInput",
        };

        // ─── Location IDs (matching apworld formula) ────────────────────────
        // Event locations: LOCATION_BASE + index (0..18)
        // Upgrade locations: LOCATION_BASE + 100 + (upgradeIndex * 10) + (level - 1)

        internal static readonly Dictionary<string, long> LocationIDs = BuildLocationIDs();

        private static string UpgradeLocName(string upgradeName, int level)
        {
            return level == 1 ? $"Buy {upgradeName}" : $"Buy {upgradeName} {level}";
        }

        private static Dictionary<string, long> BuildLocationIDs()
        {
            var dict = new Dictionary<string, long>();
            // Event locations
            string[] events = {
                "Reach Shop Sign", "Reach Upgrades Sign",
                "SkillTree Floor2 Unlocked", "SkillTree Floor3 Unlocked",
                "Achievement - First Soul", "Achievement - Uncommon Soul",
                "Achievement - Rare Soul", "Achievement - Legendary Soul",
                "World1 - Kill first enemy", "World1 - Kill UNCOMMON enemy",
                "World1 - Kill RARE enemy", "World1 - Collect 10 souls",
                "World2 - Kill first enemy", "World2 - Kill RARE enemy",
                "World2 - Kill LEGENDARY enemy", "World2 - Collect 50 souls",
                "World3 - Kill first enemy", "World3 - Kill LEGENDARY enemy",
                "World3 - Collect 100 souls",
            };
            for (int i = 0; i < events.Length; i++)
                dict[events[i]] = LOCATION_BASE + i;
            // Upgrade locations (all possible levels)
            for (int ui = 0; ui < UpgradeNames.Length; ui++)
            {
                for (int lv = 0; lv < MAX_UPGRADE_LEVELS; lv++)
                {
                    string locName = UpgradeLocName(UpgradeNames[ui], lv + 1);
                    dict[locName] = LOCATION_BASE + 100 + (ui * MAX_UPGRADE_LEVELS) + lv;
                }
            }
            return dict;
        }

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

            Log.LogInfo("Ratalorica AP Plugin loaded. Waiting for connection...");
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

        // Used by the TransparentGame.Update patch to know whether to suppress click-through.
        public bool IsConnectionUIVisible => showConnectionUI;
        public Rect ConnectionWindowRect => windowRect;

        private void OnGUI()
        {
            if (showConnectionUI)
            {
                // Consume ALL input so game doesn't receive it
                if (Event.current.type == EventType.KeyDown ||
                    Event.current.type == EventType.KeyUp ||
                    Event.current.type == EventType.MouseDown ||
                    Event.current.type == EventType.MouseUp ||
                    Event.current.type == EventType.ScrollWheel)
                {
                    // Let IMGUI handle it, don't propagate
                }

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

            // Drag only from the title bar so clicks inside the window don't trigger drags
            // and don't leak through to the game underneath.
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void DoDisconnect()
        {
            // Save before tearing down so the stash on the next Connect reflects this run.
            try
            {
                if (DataPersistenceManager.instance != null)
                {
                    DataPersistenceManager.instance.SaveGame();
                    Log.LogInfo("[AP] Forced SaveGame() on disconnect");
                }
            }
            catch (Exception e) { Log.LogWarning("[AP] SaveGame on disconnect failed: " + e.Message); }

            // Persist currentIndex so we don't re-grant items on next connect to this session.
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

        private void DoConnect()
        {
            apServer   = uiServer.Trim();
            apSlotName = uiSlotName.Trim();
            apPassword = uiPassword;

            cfgServer.Value   = apServer;
            cfgSlotName.Value = apSlotName;
            cfgPassword.Value = apPassword;

            // Save reset is deferred to after we have seed_name (RoomInfo) and a confirmed
            // Connected response. Otherwise port changes would wipe the save spuriously.
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
                // If the plugin restarted since last play, currentIndex is back to 0 in memory
                // but the save already has all those items applied. Restore the persisted
                // index so HandleReceivedItems skips items <= currentIndex naturally.
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

            // Force a save first so what we're about to stash is the live game state.
            // The game only auto-saves every 60s, so without this we'd stash a stale file.
            try
            {
                if (DataPersistenceManager.instance != null)
                {
                    DataPersistenceManager.instance.SaveGame();
                    Log.LogInfo("[AP] Forced SaveGame() before stash");
                }
            }
            catch (Exception e) { Log.LogWarning("[AP] SaveGame before swap failed: " + e.Message); }

            // Persist the OLD session's currentIndex so we don't re-grant its items on return.
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

            // Backup the current save under the OLD session id, so we can restore it
            // if the user comes back to that run later.
            if (File.Exists(savePath) && !string.IsNullOrEmpty(lastSession))
            {
                string oldSessionSavePath = Path.Combine(saveDir, $"ap_save_{SessionId(lastSession)}.bak");
                File.Copy(savePath, oldSessionSavePath, true);
                Log.LogInfo($"[AP] Stashed old session save -> ap_save_{SessionId(lastSession)}.bak");
            }

            // Restore the NEW session's save if we have one, otherwise start fresh.
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

            // Reset runtime state so items re-apply cleanly to the swapped/fresh save
            checkedLocations.Clear();
            roomLocationIds.Clear();
            upgradePurchaseCounts.Clear();
            currentIndex = 0;
            APStateManager.Instance?.ResetRuntimeState();

            // Load the NEW session's persisted index if it has one (so we don't re-grant
            // its already-applied items on return).
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

            // After reload, DataPersistenceManager.Start() runs OnSceneLoaded() which
            // captures IDataPersistence objects available AT THAT MOMENT. Some Upgrades
            // are instantiated later (e.g. when their menu becomes active), so we re-run
            // OnSceneLoaded() after a short delay so their LoadData() is invoked and their
            // currentLevel is properly restored from the swapped save.
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
            // Force modern TLS — on some Windows / .NET 4.7.2 setups the default protocol
            // is too old for archipelago.gg's certificate, causing a silent handshake failure.
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 |
                    (SecurityProtocolType)3072 /* Tls13 — value used so it compiles on older .NET targets */ |
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
                    connectionStatus = "Disconnected";
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
                            if (packet.ContainsKey("seed_name"))
                            {
                                roomSeedName = packet["seed_name"].ToString();
                                Log.LogInfo($"[AP] Room seed: {roomSeedName}");
                            }
                            SendConnectPacket();
                            break;
                        case "Connected":
                            authenticated = true;
                            connectionStatus = $"Connected as {apSlotName}";
                            showConnectionUI = false;
                            Log.LogInfo("Authenticated with AP server!");

                            // Now that we have a confirmed identity (seed_name + slot), decide
                            // whether to reset the save. Done here (not in DoConnect) so a
                            // failed auth never wipes the save.
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
                                Log.LogInfo($"[AP] Already checked {locs.Count} locations:");
                                foreach (var l in locs)
                                    Log.LogInfo($"[AP]   - {GetLocationName(l) ?? "unknown"} (id={l})");
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

        private void HandleReceivedItems(Dictionary<string, object> packet)
        {
            long index = Convert.ToInt64(packet["index"]);
            var items = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(packet["items"].ToString());
            Log.LogInfo($"[AP] ReceivedItems: index={index}, count={items.Count}, currentIndex={currentIndex}");

            // Same session reconnect — save already has these items; skip the initial dump
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

                // World unlocks live in memory only (APStateManager flags), not in the
                // game's save. Re-apply them every reconnect so access state and goal
                // status are correct, even when their original index was already skipped.
                bool isWorldUnlock = itemName == "World1 Unlock"
                                   || itemName == "World2 Unlock"
                                   || itemName == "World3 Unlock";
                bool alreadyApplied = index + i < currentIndex;

                if (alreadyApplied && !isWorldUnlock) continue;
                if (!alreadyApplied) currentIndex = index + i + 1;

                Log.LogInfo($"[AP] Item #{index + i}: {itemName ?? "unknown"} (id={itemId})");
                if (itemName != null)
                    GrantItem(itemName);
            }
            Log.LogInfo($"[AP] Done processing items. currentIndex={currentIndex}");
            PersistCurrentIndex();
        }

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

        // ─── Grant item from AP ─────────────────────────────────────────────

        public void GrantItem(string itemName)
        {
            Log.LogInfo($"[AP] Granting item: {itemName}");

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
            // It's an upgrade — grant it via BuyUpgrade
            APStateManager.Instance?.GrantUpgrade(itemName);
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
        /// Get the next unchecked upgrade location for this upgrade type.
        /// Returns null if no more checks available.
        /// </summary>
        public string GetNextUpgradeCheck(string upgradeClassName)
        {
            if (!upgradePurchaseCounts.ContainsKey(upgradeClassName))
                upgradePurchaseCounts[upgradeClassName] = 0;

            int nextLevel = upgradePurchaseCounts[upgradeClassName] + 1;
            string locName = UpgradeLocName(upgradeClassName, nextLevel);

            // Check if this location exists in the room and hasn't been checked yet
            if (IsLocationInRoom(locName) && !IsLocationChecked(locName))
                return locName;

            return null;
        }

        public void SendGoal()
        {
            if (!authenticated) return;
            Log.LogInfo("[AP] Sending goal completion!");
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
        // Money received from AP that couldn't be applied yet (PlayerManager missing or
        // about to be destroyed by a scene reload). Drained once PlayerManager is alive.
        public float PendingMoney = 0f;

        // Flag: true when AP is granting an upgrade (allow BuyUpgrade to go through)
        internal bool isGrantingUpgrade = false;

        private HashSet<long> pendingCheckedLocations = null;
        private bool objectivesRestored = false;

        private int totalSoulsTracked = 0;
        private float retryTimer = 0f;

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
        }

        public void MarkObjectivesNotYetRestored()
        {
            objectivesRestored = false;
        }

        private void Update()
        {
            retryTimer += Time.deltaTime;
            if (retryTimer < 2f) return;
            retryTimer = 0f;

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

            // Number of classic objectives already completed in this AP run.
            // Game order: Shop Sign first, then Upgrades Sign.
            // We MUST call NextObjective() (not just set fields) so each objective's
            // attachedObjective UnityEvent fires — those events enable gameplay systems
            // like NPC customers asking for souls.
            int completedCount = 0;
            if (HasCheck("Reach Shop Sign")) completedCount++;
            if (HasCheck("Reach Upgrades Sign")) completedCount++;

            if (completedCount > 0)
            {
                // Read the live currentObjectiveId — if the save already advanced past
                // these objectives, calling NextObjective again throws IndexOutOfRange
                // because guidanceObjectivesList[currentObjectiveId] is out of bounds.
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
                            // Don't break — a failure on one objective shouldn't prevent
                            // later ones from being activated.
                            RataloricaAPPlugin.Log.LogWarning($"[AP] NextObjective call #{i + 1} failed (continuing): " + (e.InnerException?.Message ?? e.Message));
                            try
                            {
                                int cur = (int)idField.GetValue(objMgr);
                                idField.SetValue(objMgr, cur + 1);
                            }
                            catch { }
                        }
                    }
                    RataloricaAPPlugin.Log.LogInfo($"[AP] Called NextObjective {callsNeeded}x (currentObjectiveId was {currentId}, target {completedCount})");
                }
                else
                {
                    RataloricaAPPlugin.Log.LogInfo($"[AP] No NextObjective calls needed (currentObjectiveId={currentId} already >= {completedCount})");
                }

                // Fallback: directly activate the sign GameObjects. Idempotent.
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
            if (worldName == "World1 Unlock") World1Unlocked = true;
            if (worldName == "World2 Unlock") World2Unlocked = true;
            if (worldName == "World3 Unlock") World3Unlocked = true;
            RataloricaAPPlugin.Log.LogInfo($"[AP] {worldName} unlocked!");

            // Goal condition (matches apworld set_rules): receiving World3 Unlock = victory.
            if (worldName == "World3 Unlock")
                RataloricaAPPlugin.Instance?.SendGoal();
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
            // Always queue. The Update loop drains it once PlayerManager is alive AND
            // any in-flight scene reload has settled, so money survives reloads.
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
            string locName = RataloricaAPPlugin.Instance.GetNextUpgradeCheck(typeName);

            if (locName != null)
            {
                // Increment purchase count and send check
                if (!RataloricaAPPlugin.Instance.upgradePurchaseCounts.ContainsKey(typeName))
                    RataloricaAPPlugin.Instance.upgradePurchaseCounts[typeName] = 0;
                RataloricaAPPlugin.Instance.upgradePurchaseCounts[typeName]++;

                RataloricaAPPlugin.Instance.SendCheck(locName);
                RataloricaAPPlugin.Log.LogInfo($"[AP] Upgrade purchase check sent: {locName}");
            }
        }

        // ─── UI messages ────────────────────────────────────────────────────

        public void ShowLockedMessage(string worldName)
        {
            RataloricaAPPlugin.Instance?.StartCoroutine(
                ShowMessageCoroutine($"[Archipelago] {worldName} is locked!"));
        }

        private IEnumerator ShowMessageCoroutine(string message)
        {
            var go = new GameObject("AP_Msg");
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

        // ─── Event handlers (called by patches) ────────────────────────────

        public void OnEnemyKilled(Enemy enemy, WorldManager.World currentWorld)
        {
            string worldPrefix = currentWorld.ToString();

            string firstKillLoc = worldPrefix switch
            {
                "WORLD1" => "World1 - Kill first enemy",
                "WORLD2" => "World2 - Kill first enemy",
                "WORLD3" => "World3 - Kill first enemy",
                _ => null
            };
            if (firstKillLoc != null)
                RataloricaAPPlugin.Instance?.SendCheck(firstKillLoc);

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

    // CORE PATCH: Intercept upgrade purchases
    // - If AP is granting → allow (effect applies)
    // - If check available → block effect, send check, player still pays gold
    // - If check already sent → allow normal purchase
    // Ratalorica's TransparentGame.Update sets the window as click-through whenever the
    // mouse isn't over a collider on uiLayer. Our IMGUI Archipelago window isn't a collider,
    // so by default clicks on it leak through to whatever's behind (browser, explorer, etc.)
    // and steal focus. This postfix forces the window to capture clicks while the cursor is
    // over our IMGUI rect.
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
                float my = Screen.height - Input.mousePosition.y; // IMGUI Y goes top-down
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

                // Check if there's a next upgrade check available
                string nextCheck = RataloricaAPPlugin.Instance.GetNextUpgradeCheck(typeName);
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
                    RataloricaAPPlugin.Log.LogInfo("[AP] SkillTree Floor2 unlocked!");
                    RataloricaAPPlugin.Instance?.SendCheck("SkillTree Floor2 Unlocked");
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
                    RataloricaAPPlugin.Log.LogInfo("[AP] SkillTree Floor3 unlocked!");
                    RataloricaAPPlugin.Instance?.SendCheck("SkillTree Floor3 Unlocked");
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
