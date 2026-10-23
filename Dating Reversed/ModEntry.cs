using GenericModConfigMenu;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Mods;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Dating_Reversed
{
    public sealed class ModEntry : Mod
    {
        private const string DailyNpcKey = "DatingReversed/DailyNpc";
        private const string BouquetEventsThisMonthKey = "DatingReversed/BouquetEventsThisMonth";
        private const string BouquetEventMonthKey = "DatingReversed/BouquetEventMonth";
        private const string CooldownDayKey = "DatingReversed/BouquetCooldownUntil";
        private const string EventIdPrefix = "DatingReversed/EventId/";
        private const int PointsPerHeart = 250;
        private const int EligibleMinHearts = 8;
        private const int EligibleMaxHeartsExclusive = 11;
        private const int EligibleMinPoints = EligibleMinHearts * PointsPerHeart;
        private const int EligibleMaxExclusivePoints = EligibleMaxHeartsExclusive * PointsPerHeart;

        // Keep base event IDs in a range where (eventId * 10 + 2) stays within Int32.
        private const int SafeBaseEventIdMin = 180_000_000;
        private const int SafeBaseEventIdSpan = 10_000_000;
        private const int SafeBaseEventIdMaxExclusive = SafeBaseEventIdMin + SafeBaseEventIdSpan;

        // Upper bound when counting a dialogue key's numbered variants (bouquet.ask.1, bouquet.ask.2, ...).
        private const int MaxDialogueVariants = 100;

        // SMAPI files i18n/default.json under this locale name.
        private const string EnglishLocale = "default";

        private ModConfig Config = null!;
        private readonly Dictionary<string, int> _npcToEventId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _appliedEventOutcomes = new(StringComparer.Ordinal);
        private readonly Queue<Action> _nextTickQueue = new();
        private readonly HashSet<string> _addedNpcToggles = new(StringComparer.Ordinal);

        // Game1.random is reseeded from the save every morning, so reloading a save replays the same lines.
        // Dialogue uses its own generator instead, and remembers the last pick per key to avoid back-to-back repeats.
        private readonly Random _dialogueRandom = new();
        private readonly Dictionary<string, int> _lastDialogueIndex = new(StringComparer.Ordinal);

        private bool _lateRefreshRan;
        private IGenericModConfigMenuApi? _gmcm;
        private bool _gmcmAdvancedAdded;
        private BouquetEventService? _eventService;

        private ModDataDictionary ModData => Game1.player.modData;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>() ?? new ModConfig();
            _eventService = new BouquetEventService(this);

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            helper.Events.Player.Warped += OnWarped;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
        }

        private string SeenThisMonthKey(string npcName, int monthKey)
            => $"DatingReversed/Seen/{npcName}/{monthKey}";

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RefreshNpcConfig();
            SetupGmcm();
            RegisterConsoleCommands();
        }

        private string BuildNpcTooltip(string rawName, string displayName)
        {
            string gender = GetNpcGenderLabel(rawName, displayName);
            return $"Allow {displayName} to initiate bouquet events.\n" +
                   $"Gender: {gender}";
        }

        private string GetNpcGenderLabel(string rawName, string displayName)
        {
            string canonicalName = ResolveCanonicalNpcName(rawName, displayName);

            if (TryGetGenderFromCharacterData(canonicalName, out Gender dataGender))
                return ToGenderLabel(dataGender);

            if (!string.Equals(canonicalName, rawName, StringComparison.Ordinal)
                && TryGetGenderFromCharacterData(rawName, out dataGender))
            {
                return ToGenderLabel(dataGender);
            }

            if (!string.Equals(canonicalName, displayName, StringComparison.Ordinal)
                && TryGetGenderFromCharacterData(displayName, out dataGender))
            {
                return ToGenderLabel(dataGender);
            }

            NPC? npc = ResolveNpcForDisplay(canonicalName, rawName, displayName);
            if (npc is not null)
            {
                if (TryGetGenderFromCharacterData(npc.Name, out dataGender))
                    return ToGenderLabel(dataGender);

                return ToGenderLabel(npc.Gender);
            }

            return "Unknown";
        }

        private string ResolveCanonicalNpcName(string rawName, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(rawName) && Game1.characterData.ContainsKey(rawName))
                return rawName;

            if (!string.IsNullOrWhiteSpace(displayName) && Game1.characterData.ContainsKey(displayName))
                return displayName;

            string cleanedRawName = GetNpcDisplayName(rawName);
            if (!string.IsNullOrWhiteSpace(cleanedRawName) && Game1.characterData.ContainsKey(cleanedRawName))
                return cleanedRawName;

            foreach (string key in Game1.characterData.Keys)
            {
                if (string.Equals(GetNpcDisplayName(key), displayName, StringComparison.OrdinalIgnoreCase))
                    return key;
            }

            foreach (string key in Config.NpcAllowed.Keys)
            {
                if (string.Equals(key, rawName, StringComparison.Ordinal))
                    return key;

                if (string.Equals(GetNpcDisplayName(key), displayName, StringComparison.OrdinalIgnoreCase))
                    return key;
            }

            return rawName;
        }

        private static bool TryGetGenderFromCharacterData(string npcName, out Gender gender)
        {
            gender = Gender.Undefined;

            if (string.IsNullOrWhiteSpace(npcName))
                return false;

            if (!Game1.characterData.TryGetValue(npcName, out var data) || data is null)
                return false;

            gender = data.Gender;
            return true;
        }

        private static string ToGenderLabel(Gender gender)
        {
            return gender switch
            {
                Gender.Male => "Male",
                Gender.Female => "Female",
                Gender.Undefined => "Other",
                _ => "Unknown"
            };
        }

        private NPC? ResolveNpcForDisplay(string canonicalName, string rawName, string displayName)
        {
            return Utility.getAllCharacters().FirstOrDefault(p =>
                       string.Equals(p.Name, canonicalName, StringComparison.Ordinal)
                       || string.Equals(p.Name, rawName, StringComparison.Ordinal)
                       || string.Equals(p.Name, displayName, StringComparison.Ordinal))
                   ?? Game1.getCharacterFromName(canonicalName, mustBeVillager: false)
                   ?? Game1.getCharacterFromName(rawName, mustBeVillager: false)
                   ?? Game1.getCharacterFromName(displayName, mustBeVillager: false);
        }

        private static string GetNpcDisplayName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return rawName;

            int dotIndex = rawName.LastIndexOf('.');
            string tail = dotIndex >= 0 ? rawName[(dotIndex + 1)..] : rawName;

            int underscoreIndex = tail.LastIndexOf('_');
            if (underscoreIndex >= 0 && underscoreIndex < tail.Length - 1)
                tail = tail[(underscoreIndex + 1)..];

            return string.IsNullOrWhiteSpace(tail) ? rawName : tail;
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            RefreshNpcConfig();
            AddNpcToggleOptions();
            AddAdvancedGmcmOptions();
            BuildEventIdMap();
            PickDailyBouquetNpc();
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (!Config.EnableMod)
                return;

            _appliedEventOutcomes.Clear();
            _lateRefreshRan = false;

            BuildEventIdMap();
            PickDailyBouquetNpc();

            Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked;
            Helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        }

        private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || _lateRefreshRan)
            {
                Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked;
                return;
            }

            _lateRefreshRan = true;
            Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked;

            RefreshNpcConfig();
            BuildEventIdMap();
            PickDailyBouquetNpc();
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            RunNextTickQueue();

            if (!Context.IsWorldReady || !Config.EnableMod || Game1.eventUp)
                return;

            if (!TryGetTodayNpc(out string todayNpc))
                return;

            if (!_npcToEventId.TryGetValue(todayNpc, out int eventId))
                return;

            string acceptTopic = $"DR_ACCEPT_{eventId}";
            string rejectTopic = $"DR_REJECT_{eventId}";
            string appliedKey = eventId.ToString();
            int monthKey = GetMonthKey();

            if (Game1.player.activeDialogueEvents.Remove(acceptTopic) && _appliedEventOutcomes.Add(appliedKey))
            {
                ApplyDating(todayNpc);
                ApplyCooldownAndCount();
                ModData[SeenThisMonthKey(todayNpc, monthKey)] = "1";
                ModData.Remove(DailyNpcKey);
                return;
            }

            if (Game1.player.activeDialogueEvents.Remove(rejectTopic) && _appliedEventOutcomes.Add(appliedKey))
            {
                ApplyRejectionPenalty(todayNpc, Config.RejectPenaltyPoints <= 0 ? 250 : Config.RejectPenaltyPoints);
                ApplyCooldownAndCount();
                ModData[SeenThisMonthKey(todayNpc, monthKey)] = "1";
                ModData.Remove(DailyNpcKey);
            }
        }

        private void OnWarped(object? sender, WarpedEventArgs e)
        {
            if (!e.IsLocalPlayer || !Context.IsWorldReady || !Context.IsMainPlayer || !Config.EnableMod)
                return;
            if (Game1.eventUp || Game1.activeClickableMenu != null)
                return;
            if (e.OldLocation.Name != "FarmHouse" || e.NewLocation is not Farm)
                return;
            if (Game1.timeOfDay < 600 || Game1.timeOfDay > 1000)
                return;
            if (_eventService is null)
                return;

            if (!TryGetTodayNpc(out string npcName))
                return;

            // Covers an NPC picked before the farmer got engaged, e.g. one waiting on the wedding day.
            if (IsPausedForFarmerRelationship())
            {
                ModData.Remove(DailyNpcKey);
                return;
            }

            if (!Game1.player.friendshipData.TryGetValue(npcName, out Friendship? friendship))
            {
                ModData.Remove(DailyNpcKey);
                return;
            }

            if (IsBouquetBlockedByRelationship(npcName, friendship))
            {
                ModData.Remove(DailyNpcKey);
                return;
            }

            if (ModData.ContainsKey(SeenThisMonthKey(npcName, GetMonthKey())))
                return;

            _eventService.TryStartOnCurrentLocation(npcName);
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || !Config.EnableMod || !Config.EnableDebugCommands)
                return;
            if (Game1.eventUp || Game1.activeClickableMenu != null)
                return;
            if (!Config.OpenDebugMenuKey.JustPressed())
                return;

            Helper.Input.SuppressActiveKeybinds(Config.OpenDebugMenuKey);
            OpenDebugMenu();
        }

        private void OpenDebugMenu()
        {
            Game1.activeClickableMenu = new DrDebugMenu(this);
        }

        internal void UiRepick()
        {
            ModData.Remove(DailyNpcKey);
            ModData.Remove(CooldownDayKey);
            PickDailyBouquetNpc();
            Monitor.Log($"[DR] UI Repick → DailyNpc = {(TryGetTodayNpc(out string npc) ? npc : "<none>")}", LogLevel.Alert);
        }

        internal void UiClearCooldown()
        {
            ModData.Remove(CooldownDayKey);
            Monitor.Log("[DR] UI cleared cooldown.", LogLevel.Alert);
        }

        internal string GetTodayNpcName()
            => TryGetTodayNpc(out string npcName) ? npcName : "<none>";

        internal IReadOnlyList<string> GetDateableNpcNames()
        {
            if (_npcToEventId.Count > 0)
                return _npcToEventId.Keys.OrderBy(static p => p).ToList();

            return Utility.getAllCharacters()
                .Where(static n => n.datable.Value)
                .Select(static n => n.Name)
                .OrderBy(static p => p)
                .ToList();
        }

        internal string GetTodayNpc()
            => GetTodayNpcName();

        internal void ClearTodayNpc()
        {
            ModData.Remove(DailyNpcKey);
        }

        internal void ForceTodayNpc(string npcName, bool clearCooldown = true, bool clearSeenThisMonth = true)
        {
            if (_npcToEventId.Count == 0)
                BuildEventIdMap();

            if (clearCooldown)
                ModData.Remove(CooldownDayKey);

            if (clearSeenThisMonth)
                ModData.Remove(SeenThisMonthKey(npcName, GetMonthKey()));

            ModData[DailyNpcKey] = npcName;

            if (_npcToEventId.TryGetValue(npcName, out int eventId))
                _appliedEventOutcomes.Remove(eventId.ToString());

            Monitor.Log($"[DR] Forced today NPC -> {npcName}", LogLevel.Alert);
        }

        internal bool TryPickRandomEligibleForToday(out string pickedNpc)
        {
            pickedNpc = string.Empty;

            if (!Context.IsWorldReady)
                return false;

            if (_npcToEventId.Count == 0)
                BuildEventIdMap();

            List<string> pool = GetEligibleNpcPool(ignoreSeenThisMonth: true, ignoreMonthlyCap: true, ignoreCooldown: true).ToList();
            if (pool.Count == 0)
            {
                Game1.addHUDMessage(new HUDMessage("[DR] No eligible NPCs found."));
                return false;
            }

            pickedNpc = pool[Game1.random.Next(pool.Count)];
            ForceTodayNpc(pickedNpc, clearCooldown: true, clearSeenThisMonth: true);
            Game1.addHUDMessage(new HUDMessage($"[DR] Picked random eligible NPC: {pickedNpc}"));
            return true;
        }

        internal bool TryPlayBouquetNow(string npcName)
        {
            if (!Context.IsWorldReady || string.IsNullOrWhiteSpace(npcName) || npcName == "<none>" || _eventService is null)
                return false;

            if (!_npcToEventId.ContainsKey(npcName))
                BuildEventIdMap();

            if (!_npcToEventId.ContainsKey(npcName))
            {
                Game1.addHUDMessage(new HUDMessage($"[DR] Unknown NPC: {npcName}"));
                return false;
            }

            ForceTodayNpc(npcName, clearCooldown: true, clearSeenThisMonth: true);

            if (Game1.currentLocation is not Farm)
            {
                Game1.warpFarmer("Farm", 64, 15, false);
                QueueNextTick(() => _eventService.TryStartOnCurrentLocation(npcName));
                return true;
            }

            return _eventService.TryStartOnCurrentLocation(npcName);
        }

        internal string BuildDebugSummary()
        {
            if (!Context.IsWorldReady)
                return "Load a save first.";

            string dailyNpc = GetTodayNpcName();
            string eventId = "<n/a>";
            if (dailyNpc != "<none>" && _npcToEventId.TryGetValue(dailyNpc, out int id))
                eventId = id.ToString();

            string monthCount = ModData.TryGetValue(BouquetEventsThisMonthKey, out string? count) ? count : "0";

            string cooldown = "none";
            if (ModData.TryGetValue(CooldownDayKey, out string? cooldownText)
                && int.TryParse(cooldownText, out int untilDay)
                && untilDay > Game1.Date.TotalDays)
            {
                int remaining = untilDay - Game1.Date.TotalDays;
                cooldown = $"{remaining} day(s) (until idx {untilDay})";
            }

            return
                $"Today NPC: {dailyNpc}\n" +
                $"Event ID: {eventId}\n" +
                $"Month count: {monthCount}/{Config.MaxBouquetsPerMonth}\n" +
                $"Cooldown: {cooldown}\n" +
                $"Month key: {GetMonthKey()}";
        }

        internal string UiBuildStatusText()
        {
            if (!Context.IsWorldReady)
                return "Load a save first.";

            string dailyNpc = TryGetTodayNpc(out string npcName) ? npcName : "<none>";
            string monthCount = ModData.TryGetValue(BouquetEventsThisMonthKey, out string? count) ? count : "0";

            string eventMonth = "<none>";
            if (ModData.TryGetValue(BouquetEventMonthKey, out string? eventMonthRaw))
                eventMonth = FormatMonthKey(eventMonthRaw);

            string cooldown = "<none>";
            if (ModData.TryGetValue(CooldownDayKey, out string? cooldownRaw) && int.TryParse(cooldownRaw, out int untilDay))
            {
                int remaining = Math.Max(0, untilDay - Game1.Date.TotalDays);
                cooldown = remaining > 0 ? $"{remaining} day(s) remaining" : "<none>";
            }

            return
                $"Today's NPC: {dailyNpc}\n" +
                $"Bouquets this month: {monthCount}/{Config.MaxBouquetsPerMonth}\n" +
                $"Event month: {eventMonth}\n" +
                $"Cooldown: {cooldown}";
        }

        internal string UiGetTodayNpc()
            => GetTodayNpcName();

        internal IEnumerable<string> UiGetEligiblePool(bool includeAllDateables = false)
        {
            if (_npcToEventId.Count == 0)
                BuildEventIdMap();

            if (_npcToEventId.Count == 0)
                return Enumerable.Empty<string>();

            IEnumerable<string> source = includeAllDateables
                ? _npcToEventId.Keys
                : GetEligibleNpcPool(ignoreSeenThisMonth: false, ignoreMonthlyCap: true, ignoreCooldown: true);

            // Reuse GMCM-style dedupe logic
            var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawName in source)
            {
                string displayName = GetNpcDisplayName(rawName);

                if (!chosen.TryGetValue(displayName, out string? existing))
                {
                    chosen[displayName] = rawName;
                    continue;
                }

                if (GetNpcToggleNamePriority(rawName, displayName) > GetNpcToggleNamePriority(existing, displayName))
                    chosen[displayName] = rawName;
            }

            return chosen
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Value)
                .ToList();
        }
        internal IEnumerable<string> UiGetEligiblePool()
            => UiGetEligiblePool(includeAllDateables: true);

        internal void UiSetTodayNpc(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "<none>")
            {
                UiClearTodayNpc();
                return;
            }

            ForceTodayNpc(name, clearCooldown: false, clearSeenThisMonth: true);
            Monitor.Log($"[DR] UI SetToday → {name}", LogLevel.Alert);
        }

        internal void UiClearTodayNpc()
        {
            ModData.Remove(DailyNpcKey);
            Monitor.Log("[DR] UI cleared today NPC.", LogLevel.Alert);
        }

        internal void UiStopCurrentEvent()
        {
            if (!Context.IsWorldReady || !Game1.eventUp)
                return;

            try
            {
                Game1.currentLocation?.currentEvent?.exitEvent();
            }
            catch
            {
            }

            Game1.eventUp = false;
            Game1.player.canMove = true;
            Game1.freezeControls = false;
            Monitor.Log("[DR] UI stopped current event.", LogLevel.Alert);
        }

        internal void UiPlayNow()
        {
            if (!Context.IsWorldReady)
                return;

            string npcName = UiGetTodayNpc();
            if (npcName == "<none>")
            {
                Monitor.Log("[DR] UI PlayNow: no today NPC set.", LogLevel.Warn);
                return;
            }

            if (Game1.activeClickableMenu is not null)
                Game1.exitActiveMenu();

            QueueNextTick(() => TryPlayBouquetNow(npcName));
        }

        internal void QueueNextTick(Action action)
        {
            _nextTickQueue.Enqueue(action);
        }

        private void RunNextTickQueue()
        {
            if (!Context.IsWorldReady)
                return;

            while (_nextTickQueue.Count > 0)
            {
                Action action = _nextTickQueue.Dequeue();
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[DR] queued action failed: {ex}", LogLevel.Warn);
                }
            }
        }

        private bool TryGetTodayNpc(out string npcName)
        {
            if (ModData.TryGetValue(DailyNpcKey, out string? raw) && !string.IsNullOrWhiteSpace(raw))
            {
                npcName = raw;
                return true;
            }

            npcName = string.Empty;
            return false;
        }

        private void RefreshNpcConfig()
        {
            foreach (NPC npc in Utility.getAllCharacters().Where(static n => n.datable.Value))
                Config.EnsureNpcKey(npc.Name);

            Helper.WriteConfig(Config);
        }

        private void BuildEventIdMap()
        {
            _npcToEventId.Clear();

            foreach (NPC npc in Utility.getAllCharacters().Where(static n => n.datable.Value))
            {
                string key = EventIdPrefix + npc.Name;
                if (!ModData.TryGetValue(key, out string? storedRaw) || !int.TryParse(storedRaw, out int storedId) || !IsSafeBaseEventId(storedId))
                {
                    storedId = MakeBaseId(npc.Name);
                    ModData[key] = storedId.ToString();
                }

                _npcToEventId[npc.Name] = storedId;
            }
        }

        private static bool IsSafeBaseEventId(int eventId)
            => eventId >= SafeBaseEventIdMin && eventId < SafeBaseEventIdMaxExclusive;

        private IEnumerable<string> GetEligibleNpcPool(bool ignoreSeenThisMonth, bool ignoreMonthlyCap, bool ignoreCooldown)
        {
            if (!Context.IsWorldReady)
                yield break;

            int monthKey = GetMonthKey();

            if (IsPausedForFarmerRelationship())
                yield break;

            if (!ignoreMonthlyCap)
            {
                int currentCount = ModData.TryGetValue(BouquetEventsThisMonthKey, out string? countRaw) && int.TryParse(countRaw, out int count)
                    ? count
                    : 0;
                if (currentCount >= Config.MaxBouquetsPerMonth)
                    yield break;
            }

            if (!ignoreCooldown
                && ModData.TryGetValue(CooldownDayKey, out string? cooldownRaw)
                && int.TryParse(cooldownRaw, out int untilDay)
                && untilDay > Game1.Date.TotalDays)
            {
                yield break;
            }

            int minEligibleHearts = Math.Min(Math.Max(0, Config.MinEligibleHearts), 8);
            int maxEligibleHeartsExclusive = Math.Max(minEligibleHearts + 1, Config.MaxEligibleHeartsExclusive);
            int minEligiblePoints = minEligibleHearts * PointsPerHeart;
            int maxEligiblePointsExclusive = maxEligibleHeartsExclusive * PointsPerHeart;

            foreach ((string npcName, _) in _npcToEventId)
            {
                NPC? npc = Game1.getCharacterFromName(npcName);
                if (npc is null)
                    continue;
                if (!GenderAllowed(npc))
                    continue;
                if (!Config.NpcAllowed.TryGetValue(npcName, out bool allowed) || !allowed)
                    continue;
                if (!Game1.player.friendshipData.TryGetValue(npcName, out Friendship? friendship))
                    continue;
                if (friendship.Points < minEligiblePoints || friendship.Points >= maxEligiblePointsExclusive)
                    continue;
                if (IsBouquetBlockedByRelationship(npcName, friendship))
                    continue;
                if (!ignoreSeenThisMonth && ModData.ContainsKey(SeenThisMonthKey(npcName, monthKey)))
                    continue;

                yield return npcName;
            }
        }

        private static int GetMonthKey()
            => Game1.year * 100 + Game1.seasonIndex;

        private bool GenderAllowed(NPC npc)
            => (npc.Gender == Gender.Male && Config.AllowMaleSuitors)
            || (npc.Gender == Gender.Female && Config.AllowFemaleSuitors)
            || npc.Gender == Gender.Undefined;

        // Checks every relationship rather than just Game1.player.spouse, so multi-spouse mods are covered too.
        // Roommates (e.g. Krobus) aren't a romance, so they don't count.
        private static bool IsFarmerEngagedOrMarried()
        {
            Farmer player = Game1.player;
            if (player.team.IsEngaged(player.UniqueMultiplayerID) || player.team.IsMarried(player.UniqueMultiplayerID))
                return true;

            return player.friendshipData.Values.Any(static f => (f.IsEngaged() || f.IsMarried()) && !f.IsRoommate());
        }

        private bool IsPausedForFarmerRelationship()
            => Config.PauseWhileFarmerEngagedOrMarried && IsFarmerEngagedOrMarried();

        private bool IsBouquetBlockedByRelationship(string npcName, Friendship friendship)
        {
            if (friendship.Status == FriendshipStatus.Dating
                || friendship.Status == FriendshipStatus.Engaged
                || friendship.Status == FriendshipStatus.Married)
            {
                return true;
            }

            if (friendship.RoommateMarriage)
                return true;

            if (!string.IsNullOrEmpty(Game1.player.spouse)
                && string.Equals(Game1.player.spouse, npcName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private void PickDailyBouquetNpc()
        {
            if (!Config.EnableMod || !Context.IsWorldReady)
                return;

            int today = Game1.Date.TotalDays;
            int monthKey = GetMonthKey();

            if (!ModData.TryGetValue(BouquetEventMonthKey, out string? monthRaw)
                || !int.TryParse(monthRaw, out int storedMonth)
                || storedMonth != monthKey)
            {
                ModData[BouquetEventMonthKey] = monthKey.ToString();
                ModData[BouquetEventsThisMonthKey] = "0";

                foreach (string key in ModData.Keys.Where(static key => key.StartsWith("DatingReversed/Seen/", StringComparison.Ordinal)).ToArray())
                    ModData.Remove(key);
            }

            if (ModData.TryGetValue(CooldownDayKey, out string? cooldownRaw)
                && int.TryParse(cooldownRaw, out int untilDay)
                && untilDay > today)
            {
                ModData.Remove(DailyNpcKey);
                return;
            }

            if (TryGetTodayNpc(out string existingNpc)
                && _npcToEventId.ContainsKey(existingNpc)
                && !ModData.ContainsKey(SeenThisMonthKey(existingNpc, monthKey)))
            {
                return;
            }

            ModData.Remove(DailyNpcKey);

            int dailyTriggerChancePercent = Math.Clamp(Config.DailyTriggerChancePercent, 0, 100);
            if (dailyTriggerChancePercent <= 0)
                return;

            if (dailyTriggerChancePercent < 100 && Game1.random.Next(100) >= dailyTriggerChancePercent)
                return;

            List<string> pool = GetEligibleNpcPool(ignoreSeenThisMonth: false, ignoreMonthlyCap: false, ignoreCooldown: true).ToList();
            if (pool.Count == 0)
                return;

            ModData[DailyNpcKey] = pool[Game1.random.Next(pool.Count)];
        }

        private void ApplyDating(string npcName)
        {
            if (!Game1.player.friendshipData.TryGetValue(npcName, out Friendship? friendship))
                return;
            if (IsBouquetBlockedByRelationship(npcName, friendship))
                return;

            int minEligibleHearts = Math.Min(Math.Max(0, Config.MinEligibleHearts), 8);
            int minEligiblePoints = minEligibleHearts * PointsPerHeart;
            if (friendship.Points < minEligiblePoints)
                friendship.Points = minEligiblePoints;

            friendship.Status = FriendshipStatus.Dating;
        }

        private void ApplyCooldownAndCount()
        {
            int currentCount = ModData.TryGetValue(BouquetEventsThisMonthKey, out string? countRaw) && int.TryParse(countRaw, out int count)
                ? count
                : 0;
            ModData[BouquetEventsThisMonthKey] = (currentCount + 1).ToString();

            int cooldownDays;
            if (Config.UseRandomCooldown)
            {
                int minCooldownDays = Math.Max(1, Config.RandomCooldownMinDays);
                int maxCooldownDays = Math.Max(minCooldownDays, Config.RandomCooldownMaxDays);
                cooldownDays = Game1.random.Next(minCooldownDays, maxCooldownDays + 1);
            }
            else
            {
                cooldownDays = Math.Max(1, Config.FixedCooldownDays);
            }

            ModData[CooldownDayKey] = (Game1.Date.TotalDays + cooldownDays).ToString();
        }

        private void ApplyRejectionPenalty(string npcName, int amount = 250)
        {
            if (!Game1.player.friendshipData.TryGetValue(npcName, out Friendship? friendship))
                return;

            friendship.Points = Math.Max(0, friendship.Points - amount);
        }

        private int MakeBaseId(string npcName)
        {
            unchecked
            {
                int hash = $"{ModManifest.UniqueID}:{npcName}".GetHashCode();
                if (hash < 0)
                    hash = -hash;

                return SafeBaseEventIdMin + (hash % SafeBaseEventIdSpan);
            }
        }

        private static string Fallback(string value, string defaultValue)
            => string.IsNullOrWhiteSpace(value) ? defaultValue : value;

        private int PickDialogueIndex(string baseKey, int variantCount)
        {
            int index;
            if (variantCount > 1 && _lastDialogueIndex.TryGetValue(baseKey, out int last) && last <= variantCount)
            {
                // Pick uniformly from the other variants.
                index = _dialogueRandom.Next(1, variantCount);
                if (index >= last)
                    index++;
            }
            else
            {
                index = _dialogueRandom.Next(1, variantCount + 1);
            }

            _lastDialogueIndex[baseKey] = index;
            return index;
        }

        // The locales that make up the player's own language, in SMAPI's fallback order but without English.
        private string[] GetOwnLocales()
        {
            string locale = string.IsNullOrEmpty(Helper.Translation.Locale) ? "en" : Helper.Translation.Locale;
            var locales = new List<string> { locale };
            for (int dash = locale.LastIndexOf('-'); dash > 0; dash = locale.LastIndexOf('-'))
            {
                locale = locale[..dash];
                locales.Add(locale);
            }

            return locales.ToArray();
        }

        private int CountVariantsIn(string baseKey, string[] locales)
        {
            int count = 0;
            while (count < MaxDialogueVariants)
            {
                IDictionary<string, Translation> byLocale = Helper.Translation.GetInAllLocales($"{baseKey}.{count + 1}");
                if (!byLocale.Keys.Any(l => locales.Contains(l, StringComparer.OrdinalIgnoreCase)))
                    break;
                count++;
            }

            return count;
        }

        // Translations can have fewer variants than English. Only count the ones the player's language defines itself,
        // otherwise SMAPI fills the gaps with English and the scene switches language partway through.
        private int CountDialogueVariants(string baseKey, bool englishOnly)
        {
            int count = englishOnly ? 0 : CountVariantsIn(baseKey, GetOwnLocales());
            return count > 0 ? count : CountVariantsIn(baseKey, new[] { EnglishLocale });
        }

        private string GetDialogueText(string key, bool englishOnly)
        {
            if (!englishOnly)
                return Helper.Translation.Get(key).ToString();

            return Helper.Translation.GetInAllLocales(key).TryGetValue(EnglishLocale, out Translation? english)
                ? english.ToString()
                : "";
        }

        private string GetRandomDialogue(string baseKey, bool englishOnly)
        {
            int count = CountDialogueVariants(baseKey, englishOnly);
            if (count == 0)
                return "";

            string text = GetDialogueText($"{baseKey}.{PickDialogueIndex(baseKey, count)}", englishOnly);
            return string.IsNullOrWhiteSpace(text) ? GetDialogueText($"{baseKey}.1", englishOnly) : text;
        }

        private static string SeasonName(int seasonIndex)
        {
            return seasonIndex switch
            {
                0 => "Spring",
                1 => "Summer",
                2 => "Fall",
                3 => "Winter",
                _ => $"season#{seasonIndex}"
            };
        }

        private static string FormatEventMonth(string monthStr)
        {
            if (!int.TryParse(monthStr, out int month))
                return "<none>";

            int year = Math.Max(1, month / 100);
            int season = month % 100;
            return $"{SeasonName(season)} (Year {year})";
        }

        private static string FormatMonthKey(string monthStr)
            => FormatEventMonth(monthStr);

        private void SetupGmcm()
        {
            _gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (_gmcm is null)
                return;

            _gmcm.Register(
                mod: ModManifest,
                reset: () =>
                {
                    Config = new ModConfig();
                    RefreshNpcConfig();
                },
                save: () => Helper.WriteConfig(Config)
            );

            _gmcm.AddBoolOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.enableMod.name"),
                tooltip: () => Helper.Translation.Get("gmcm.enableMod.tooltip"),
                getValue: () => Config.EnableMod,
                setValue: value => Config.EnableMod = value);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.maxPerMonth.name"),
                tooltip: () => Helper.Translation.Get("gmcm.maxPerMonth.tooltip"),
                getValue: () => Config.MaxBouquetsPerMonth,
                setValue: value => Config.MaxBouquetsPerMonth = Math.Clamp(value, 1, 99),
                min: 1, max: 20, interval: 1);

            _gmcm.AddBoolOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.randomCd.name"),
                tooltip: () => Helper.Translation.Get("gmcm.randomCd.tooltip"),
                getValue: () => Config.UseRandomCooldown,
                setValue: value => Config.UseRandomCooldown = value);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.randomCdMin.name"),
                tooltip: () => Helper.Translation.Get("gmcm.randomCdMin.tooltip"),
                getValue: () => Config.RandomCooldownMinDays,
                setValue: value => Config.RandomCooldownMinDays = Math.Max(1, value),
                min: 1, max: 112, interval: 1);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.randomCdMax.name"),
                tooltip: () => Helper.Translation.Get("gmcm.randomCdMax.tooltip"),
                getValue: () => Config.RandomCooldownMaxDays,
                setValue: value => Config.RandomCooldownMaxDays = Math.Max(1, value),
                min: 1, max: 112, interval: 1);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.fixedCd.name"),
                tooltip: () => Helper.Translation.Get("gmcm.fixedCd.tooltip"),
                getValue: () => Config.FixedCooldownDays,
                setValue: value => Config.FixedCooldownDays = Math.Clamp(value, 1, 9),
                min: 1, max: 9, interval: 1);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.rejectPenalty.name"),
                tooltip: () => Helper.Translation.Get("gmcm.rejectPenalty.tooltip"),
                getValue: () => Config.RejectPenaltyPoints,
                setValue: value => Config.RejectPenaltyPoints = Math.Clamp(value, 0, 1000),
                min: 0, max: 1000, interval: 50);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.minEligibleHearts.name"),
                tooltip: () => Helper.Translation.Get("gmcm.minEligibleHearts.tooltip"),
                getValue: () => Config.MinEligibleHearts,
                setValue: value => Config.MinEligibleHearts = Math.Clamp(value, 0, 14),
                min: 0, max: 14, interval: 1);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.maxEligibleHearts.name"),
                tooltip: () => Helper.Translation.Get("gmcm.maxEligibleHearts.tooltip"),
                getValue: () => Config.MaxEligibleHeartsExclusive,
                setValue: value => Config.MaxEligibleHeartsExclusive = Math.Clamp(value, 1, 15),
                min: 1, max: 15, interval: 1);

            _gmcm.AddNumberOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.dailyTriggerChance.name"),
                tooltip: () => Helper.Translation.Get("gmcm.dailyTriggerChance.tooltip"),
                getValue: () => Config.DailyTriggerChancePercent,
                setValue: value => Config.DailyTriggerChancePercent = Math.Clamp(value, 0, 100),
                min: 0, max: 100, interval: 1);

            _gmcm.AddBoolOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.allowMale.name"),
                tooltip: () => Helper.Translation.Get("gmcm.allowMale.tooltip"),
                getValue: () => Config.AllowMaleSuitors,
                setValue: value => Config.AllowMaleSuitors = value);

            _gmcm.AddBoolOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.allowFemale.name"),
                tooltip: () => Helper.Translation.Get("gmcm.allowFemale.tooltip"),
                getValue: () => Config.AllowFemaleSuitors,
                setValue: value => Config.AllowFemaleSuitors = value);

            _gmcm.AddBoolOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.pauseWhenTaken.name"),
                tooltip: () => Helper.Translation.Get("gmcm.pauseWhenTaken.tooltip"),
                getValue: () => Config.PauseWhileFarmerEngagedOrMarried,
                setValue: value => Config.PauseWhileFarmerEngagedOrMarried = value);

            _gmcm.AddSectionTitle(ModManifest, text: () => Helper.Translation.Get("gmcm.section.npcToggles"));
            AddNpcToggleOptions();

            if (Context.IsWorldReady)
                AddAdvancedGmcmOptions();
        }

        private void AddAdvancedGmcmOptions()
        {
            if (_gmcm is null || _gmcmAdvancedAdded)
                return;

            _gmcm.AddSectionTitle(ModManifest, text: () => Helper.Translation.Get("gmcm.section.advanced"));
            _gmcm.AddBoolOption(ModManifest,
                name: () => Helper.Translation.Get("gmcm.debugCmds.name"),
                tooltip: () => Helper.Translation.Get("gmcm.debugCmds.tooltip"),
                getValue: () => Config.EnableDebugCommands,
                setValue: value => Config.EnableDebugCommands = value);

            _gmcm.AddKeybindList(ModManifest,
                name: () => Helper.Translation.Get("gmcm.debugMenuKey.name"),
                tooltip: () => Helper.Translation.Get("gmcm.debugMenuKey.tooltip"),
                getValue: () => Config.OpenDebugMenuKey,
                setValue: value => Config.OpenDebugMenuKey = value);

            _gmcmAdvancedAdded = true;
        }

        private void AddNpcToggleOptions()
        {
            if (_gmcm is null)
                return;

            foreach (var pair in GetPreferredNpcToggleEntries())
            {
                string rawName = pair.RawName;
                string displayName = pair.DisplayName;

                if (_addedNpcToggles.Contains(rawName))
                    continue;

                _gmcm.AddBoolOption(
                    mod: ModManifest,
                    name: () => displayName,
                    getValue: () => Config.NpcAllowed[rawName],
                    setValue: value => Config.NpcAllowed[rawName] = value,
                    tooltip: () => BuildNpcTooltip(rawName, displayName),
                    fieldId: $"npc_{rawName}");

                _addedNpcToggles.Add(rawName);
            }
        }

        private IEnumerable<(string RawName, string DisplayName)> GetPreferredNpcToggleEntries()
        {
            var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawName in Config.NpcAllowed.Keys.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
            {
                string displayName = GetNpcDisplayName(rawName);

                if (!chosen.TryGetValue(displayName, out string? existingRawName))
                {
                    chosen[displayName] = rawName;
                    continue;
                }

                if (GetNpcToggleNamePriority(rawName, displayName) > GetNpcToggleNamePriority(existingRawName, displayName))
                    chosen[displayName] = rawName;
            }

            foreach (var pair in chosen.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
                yield return (pair.Value, pair.Key);
        }

        private static int GetNpcToggleNamePriority(string rawName, string displayName)
        {
            if (Game1.characterData.ContainsKey(rawName) && !Game1.characterData.ContainsKey(displayName))
                return 4;

            if (string.Equals(rawName, displayName, StringComparison.Ordinal))
                return 3;

            if (!rawName.Contains('.') && !rawName.Contains('_'))
                return 2;

            return 1;
        }

        private Action<string, string[]> Guarded(Action<string, string[]> inner)
        {
            return (command, args) =>
            {
                if (!Context.IsWorldReady)
                {
                    Monitor.Log("Load a save first.", LogLevel.Warn);
                    return;
                }

                if (!Config.EnableDebugCommands)
                {
                    Monitor.Log("Debug commands are disabled. Enable them in GMCM > Advanced > Enable Debug Commands.", LogLevel.Warn);
                    return;
                }

                inner(command, args);
            };
        }

        private void RegisterConsoleCommands()
        {
            Helper.ConsoleCommands.Add("dr.clearcooldown", "Clear bouquet cooldown (debug)",
                Guarded((_, __) =>
                {
                    ModData.Remove(CooldownDayKey);
                    ModData.Remove(DailyNpcKey);
                    Monitor.Log("Cooldown cleared", LogLevel.Alert);
                }));
        }

        private sealed class BouquetEventService
        {
            private readonly ModEntry _mod;

            public BouquetEventService(ModEntry mod)
            {
                _mod = mod;
            }

            public bool TryStartOnCurrentLocation(string npcName)
            {
                if (!StardewModdingAPI.Context.IsWorldReady || Game1.eventUp || Game1.activeClickableMenu != null)
                    return false;
                if (Game1.currentLocation is not Farm farm)
                    return false;
                if (!_mod._npcToEventId.TryGetValue(npcName, out int eventId))
                    return false;

                string? eventData = BuildMainEvent(npcName, eventId);
                if (eventData is null)
                    return false;

                farm.startEvent(new Event(eventData));
                return true;
            }

            // Community translations feed straight into the event script, so a stray character can break its
            // structure. Check the script with the game's own parser and fall back to English text if it's broken.
            private string? BuildMainEvent(string npcName, int eventId)
            {
                BouquetScript script = BuildScript(npcName, eventId, englishOnly: false);
                if (script.TryValidate(out string problem))
                    return script.Text;

                _mod.Monitor.Log($"[DR] The '{_mod.Helper.Translation.Locale}' translation broke the bouquet event ({problem}). Using English text instead; please report this to the translation's author.", LogLevel.Warn);

                script = BuildScript(npcName, eventId, englishOnly: true);
                if (script.TryValidate(out problem))
                    return script.Text;

                _mod.Monitor.Log($"[DR] The bouquet event for {npcName} is broken even in English ({problem}); skipping it.", LogLevel.Error);
                return null;
            }

            private string GetText(string key, bool englishOnly)
                => _mod.GetDialogueText(key, englishOnly);

            private string GetRandomLine(string baseKey, bool englishOnly)
                => _mod.GetRandomDialogue(baseKey, englishOnly);

            private BouquetScript BuildScript(string npcName, int eventId, bool englishOnly)
            {
                int farmerTileX = (int)(Game1.player.Position.X / Game1.tileSize);
                int farmerTileY = (int)(Game1.player.Position.Y / Game1.tileSize);
                string playerName = Game1.player.Name;

                string ask1 = Clean(GetRandomLine("bouquet.ask", englishOnly).Replace("@", playerName));
                string ask2 = Clean(GetRandomLine("bouquet.q", englishOnly).Replace("@", playerName));
                string prompt = CleanBranch(Fallback(GetText("bouquet.prompt", englishOnly), "Will you accept this bouquet?"));
                string yesLabel = CleanBranch(Fallback(GetText("bouquet.choiceYes", englishOnly), "Yes"));
                string noLabel = CleanBranch(Fallback(GetText("bouquet.choiceNo", englishOnly), "Not right now"));
                string acceptLine = CleanBranch(GetRandomLine("bouquet.accept", englishOnly).Replace("@", playerName));
                string rejectLine = CleanBranch(GetRandomLine("bouquet.reject", englishOnly).Replace("@", playerName));

                // Both answers are inlined via quickQuestion instead of 'fork', so the outcome never depends on
                // Data/Events/Farm, which other mods can reload or replace between the event starting and the answer.
                string[] acceptCommands =
                {
                    $"emote {npcName}? 20",
                    "pause 400",
                    $"speak {npcName}? \"{acceptLine}\"",
                    "addItem 458",
                    $"addConversationTopic DR_ACCEPT_{eventId} 1",
                    "end"
                };
                string[] rejectCommands =
                {
                    $"emote {npcName}? 28",
                    "pause 400",
                    $"speak {npcName}? \"{rejectLine}\"",
                    $"addConversationTopic DR_REJECT_{eventId} 1",
                    "end"
                };
                string acceptBranch = string.Join("\\", acceptCommands);
                string rejectBranch = string.Join("\\", rejectCommands);

                string[] parts =
                {
                    "none",
                    "-1000 -1000",
                    $"farmer {farmerTileX} {farmerTileY} 2 {npcName} {farmerTileX} {farmerTileY + 1} 0",
                    $"faceDirection {npcName}? 0",
                    "faceDirection farmer 2",
                    $"speak {npcName}? \"{ask1}\"",
                    "pause 80",
                    $"speak {npcName}? \"{ask2}\"",
                    "pause 80",
                    $"quickQuestion {prompt}#{yesLabel}#{noLabel}(break){acceptBranch}(break){rejectBranch}",
                    "end"
                };

                return new BouquetScript(string.Join("/", parts), parts.Length, acceptCommands.Length, rejectCommands.Length);
            }

            private sealed record BouquetScript(string Text, int CommandCount, int AcceptCount, int RejectCount)
            {
                // Mirrors how the game reads the script: Event.ParseCommands for the '/' split, the
                // quickQuestion handler for '(break)', '#' and '\', and SplitBySpaceQuoteAware for arguments.
                public bool TryValidate(out string problem)
                {
                    string[] commands = Event.ParseCommands(Text);
                    if (commands.Length != CommandCount)
                    {
                        problem = $"expected {CommandCount} commands but the game read {commands.Length}";
                        return false;
                    }

                    string? question = commands.FirstOrDefault(static c => c.StartsWith("quickQuestion ", StringComparison.Ordinal));
                    if (question is null)
                    {
                        problem = "the yes/no question is missing";
                        return false;
                    }

                    string[] sections = question.Substring(question.IndexOf(' ') + 1).Split("(break)");
                    if (sections.Length != 3)
                    {
                        problem = $"expected 2 answer branches but the game read {sections.Length - 1}";
                        return false;
                    }

                    string[] choices = sections[0].Split('#');
                    if (choices.Length != 3 || choices.Any(string.IsNullOrWhiteSpace))
                    {
                        problem = "the question or its Yes/No choices are blank or split apart";
                        return false;
                    }

                    string[] accept = sections[1].Split('\\');
                    string[] reject = sections[2].Split('\\');
                    if (accept.Length != AcceptCount || reject.Length != RejectCount)
                    {
                        problem = "an answer's reaction was split into extra commands";
                        return false;
                    }

                    foreach (string command in commands.Concat(accept).Concat(reject))
                    {
                        if (command.StartsWith("speak ", StringComparison.Ordinal) && ArgUtility.SplitBySpaceQuoteAware(command).Length != 3)
                        {
                            problem = $"a line of dialogue was split apart: {command}";
                            return false;
                        }
                    }

                    problem = "";
                    return true;
                }
            }

            // Latin languages draw dialogue with the game's bitmap font (LooseSprites/font_bold), which has no glyph for
            // full-width characters or these typographic quotes and leaves a blank gap instead. Other languages use
            // fonts that do have full-width characters, so they keep those as stand-ins.
            private static bool UsesBitmapFont => LocalizedContentManager.CurrentLanguageLatin;

            private static string ToDrawable(string text)
            {
                if (!UsesBitmapFont)
                    return text;

                return text
                    .Replace('‘', '\'').Replace('‚', '\'').Replace('´', '\'')
                    .Replace('“', '\'').Replace('”', '\'').Replace('„', '\'').Replace('"', '\'')
                    .Replace("…", "...");
            }

            private static string Clean(string text)
                => ToDrawable(text).Replace("#", UsesBitmapFont ? "№" : "＃").Replace("\"", "\\\"").Replace("\n", "\\n");

            // quickQuestion branches are split on '\' and "(break)", so backslash escapes can't be used inside them.
            private static string CleanBranch(string text)
            {
                bool bitmap = UsesBitmapFont;
                return ToDrawable(text)
                    .Replace("#", bitmap ? "№" : "＃").Replace("\\", bitmap ? "-" : "＼")
                    .Replace("\"", bitmap ? "'" : "＂").Replace("/", bitmap ? "-" : "／")
                    .Replace("(break)", "(brk)").Replace("\r", "").Replace("\n", " ");
            }
        }
    }
}
