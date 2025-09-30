using GenericModConfigMenu;
using GenericModConfigMenu.Framework;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Dating_Reversed
{
    public sealed class ModEntry : Mod
    {
        // ────────── mod-data keys ──────────
        private const string DailyNpcKey = "DatingReversed/DailyNpc";
        private const string BouquetEventsThisMonthKey = "DatingReversed/BouquetEventsThisMonth";
        private const string BouquetEventMonthKey = "DatingReversed/BouquetEventMonth";
        private const string CooldownDayKey = "DatingReversed/BouquetCooldownUntil";
        private string SeenThisMonthKey(string npc, int month) => $"DatingReversed/Seen/{npc}/{month}";
        private const int EligibleMin = 2000;          // inclusive
        private const int EligibleMaxExclusive = 2300; // exclusive

        // ────────── runtime state ──────────
        private ModConfig Config = null!;
        private readonly Dictionary<string, int> _npcToEventId = new();
        private readonly HashSet<string> _applied = new();
        private bool _lateRefreshed;

        // GMCM
        private IGenericModConfigMenuApi? _gmcm;
        private readonly HashSet<string> _addedNpcToggles = new();

        private ModDataDictionary ModData => Game1.player.modData;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>()!;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.Content.AssetRequested += OnAssetRequested;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Player.Warped += OnWarped;
        }

        private Action<string, string[]> Guarded(Action<string, string[]> inner)
        {
            return (cmd, args) =>
            {
                if (!Config.EnableDebugCommands)
                {
                    Monitor.Log("Debug commands are disabled. Enable them in GMCM > Advanced > Enable Debug Commands.", LogLevel.Warn);
                    return;
                }
                inner(cmd, args);
            };
        }

        private static string Fallback(string s, string def)
            => string.IsNullOrWhiteSpace(s) ? def : s;

        private string SafeI18n(string baseKey)
        {
            int idx = Game1.random.Next(1, 21);
            string s = Helper.Translation.Get($"{baseKey}.{idx}").ToString();
            if (string.IsNullOrWhiteSpace(s))
                s = Helper.Translation.Get($"{baseKey}.1").ToString();
            return s;
        }
        private static string SeasonName(int idx)
        {
            switch (idx)
            {
                case 0: return "Spring";
                case 1: return "Summer";
                case 2: return "Fall";
                case 3: return "Winter";
                default: return $"season#{idx}";
            }
        }

        private static string FormatEventMonth(string monthStr)
        {
            if (!int.TryParse(monthStr, out int m))
                return "<none>";
            int y = Math.Max(1, m / 100);
            int s = m % 100;
            return $"{SeasonName(s)} (Year {y})";
        }

        // alias to match your current call sites
        private static string FormatMonthKey(string monthStr) => FormatEventMonth(monthStr);

        // ─────────────────────────────────────────────────────────────────────────
        // Asset edit: ensure YES/NO subevents exist in Data/Events/Farm for today
        // ─────────────────────────────────────────────────────────────────────────
        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (!e.NameWithoutLocale.IsEquivalentTo("Data/Events/Farm"))
                return;

            e.Edit(asset =>
            {
                if (!Context.IsWorldReady) return;
                if (!Context.IsMainPlayer) return;
                if (!ModData.TryGetValue(DailyNpcKey, out var npc)) return;
                if (!_npcToEventId.TryGetValue(npc, out var eid)) return;

                string yesId = (eid * 10 + 1).ToString();
                string noId = (eid * 10 + 2).ToString();

                string Clean(string s) => s.Replace("#", "＃").Replace("\"", "\\\"").Replace("\n", "\\n");
                string accept = Clean(SafeI18n("bouquet.accept").Replace("@", Game1.player.Name));
                string reject = Clean(SafeI18n("bouquet.reject").Replace("@", Game1.player.Name));

                var dict = asset.AsDictionary<string, string>().Data;

                dict[yesId] =
                    $"emote {npc} 20/" +
                    $"pause 400/" +
                    $"speak {npc} \"{accept}\"/" +
                    $"addItem 458/" +
                    $"addConversationTopic DR_ACCEPT_{eid} 1/end";

                dict[noId] =
                    $"emote {npc} 28/" +
                    $"pause 400/" +
                    $"speak {npc} \"{reject}\"/" +
                    $"addConversationTopic DR_REJECT_{eid} 1/end";

                Monitor.Log($"[DR] Injected YES {yesId} and NO {noId} for {npc}", LogLevel.Trace);
            }, AssetEditPriority.Late);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RefreshNpcConfig();
            SetupGmcm();
            RegisterConsoleCommands();
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            RefreshNpcConfig();
            AddNpcToggleOptions();
            BuildEventIdMap();
            PickDailyBouquetNpc();
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (!Config.EnableMod) return;
            _applied.Clear();
            _lateRefreshed = false;

            // early pick (may be refined after late refresh)
            PickDailyBouquetNpc();

            // late refresh once, to catch CP NPCs that spawn after maps load
            Helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        }

        private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
        {
            if (_lateRefreshed) { Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked; return; }
            _lateRefreshed = true;
            Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked;

            BuildEventIdMap();
            RefreshNpcConfig();
            PickDailyBouquetNpc();
        }

        // Accept / Reject topic handler
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || !Config.EnableMod || Game1.eventUp)
                return;

            // only process the scheduled suitor for today
            if (!ModData.TryGetValue(DailyNpcKey, out var todayNpc))
                return;
            if (!_npcToEventId.TryGetValue(todayNpc, out var eid))
                return;

            string rid = $"DR_REJECT_{eid}";
            string aid = $"DR_ACCEPT_{eid}";
            string seid = eid.ToString();
            int month = GetMonthKey();

            // ACCEPTED → dating + cooldown + per-month flag
            if (Game1.player.activeDialogueEvents.Remove(aid) && _applied.Add(seid))
            {
                Monitor.Log($"[DR] Consumed {aid} (npc={todayNpc})", LogLevel.Trace);
                ApplyDating(todayNpc);
                ApplyCooldownAndCount();
                ModData[SeenThisMonthKey(todayNpc, month)] = "1";
                ModData.Remove(DailyNpcKey);
                return;
            }

            // REJECTED → penalty + cooldown + per-month flag
            if (Game1.player.activeDialogueEvents.Remove(rid) && _applied.Add(seid))
            {
                Monitor.Log($"[DR] Consumed {rid} (npc={todayNpc})", LogLevel.Trace);
                ApplyRejectionPenalty(todayNpc, Config.RejectPenaltyPoints <= 0 ? 250 : Config.RejectPenaltyPoints);
                ApplyCooldownAndCount();
                ModData[SeenThisMonthKey(todayNpc, month)] = "1";
                ModData.Remove(DailyNpcKey);
                return;
            }

            foreach (var key in Game1.player.activeDialogueEvents.Keys.ToList())
                if (key.StartsWith("DR_ACCEPT_") || key.StartsWith("DR_REJECT_"))
                    if (!key.EndsWith($"_{eid}"))
                        Monitor.Log($"[DR] Ignoring stray topic '{key}' (not today's NPC {todayNpc})", LogLevel.Debug);

        }


        // ─────────────────────────────────────────────────────────────────────────
        // Farmhouse → Farm one-shot scene. YES forks into the injected subevent.
        // ─────────────────────────────────────────────────────────────────────────
        private void OnWarped(object? sender, WarpedEventArgs e)
        {
            if (!e.IsLocalPlayer || !Context.IsWorldReady || Game1.eventUp || !Config.EnableMod)
                return;
            if (!Context.IsMainPlayer)
                return;
            if (e.OldLocation.Name != "FarmHouse" || e.NewLocation is not Farm)
                return;

            int time = Game1.timeOfDay;
            if (time < 600 || time > 1000) // widen while testing if desired
                return;

            if (!ModData.TryGetValue(DailyNpcKey, out string npc) ||
                !_npcToEventId.TryGetValue(npc, out int eid))
                return;

            // do not re-run if this NPC's per-month gate is already set
            if (ModData.ContainsKey(SeenThisMonthKey(npc, GetMonthKey())))
                return;

            int px = (int)(Game1.player.Position.X / Game1.tileSize);
            int py = (int)(Game1.player.Position.Y / Game1.tileSize);

            string Clean(string s) => s.Replace("#", "＃").Replace("\"", "\\\"").Replace("\n", "\\n");

            string ask1 = Clean(SafeI18n("bouquet.ask").Replace("@", Game1.player.Name));
            string ask2 = Clean(SafeI18n("bouquet.q").Replace("@", Game1.player.Name));
            string prompt = Clean(Fallback(Helper.Translation.Get("bouquet.prompt").ToString(), "Will you accept this bouquet?"));
            string yesLabel = Clean(Fallback(Helper.Translation.Get("bouquet.choiceYes").ToString(), "Yes"));
            string noLabel = Clean(Fallback(Helper.Translation.Get("bouquet.choiceNo").ToString(), "Not right now"));
            string reject = Clean(SafeI18n("bouquet.reject").Replace("@", Game1.player.Name));
            string yesId = (eid * 10 + 1).ToString();

            var parts = new[]
            {
                "none", "-1000 -1000",
                $"farmer {px} {py} 2 {npc} {px} {py + 1} 0",
                $"faceDirection {npc} 0",
                $"faceDirection farmer 2",
                $"speak {npc} \"{ask1}\"",
                "pause 80",
                $"speak {npc} \"{ask2}\"",
                "pause 80",
                $"question fork0 \"{prompt}#{yesLabel}#{noLabel}\"",
                $"fork {yesId}",                 // YES → injected sub-event
                $"emote {npc} 28",               // NO  → inline branch
                "pause 400",
                $"speak {npc} \"{reject}\"",
                $"addConversationTopic DR_REJECT_{eid} 1",
                "end"
            };

            string data = string.Join("/", parts);

            Helper.GameContent.InvalidateCache("Data/Events/Farm");
            Helper.GameContent.Load<Dictionary<string, string>>("Data/Events/Farm");

            e.NewLocation.startEvent(new Event(data));
            Monitor.Log($"\nRAW EVENT >>>\n{data}\n<<< RAW EVENT", LogLevel.Alert);
        }

        // ────── Helpers ──────
        private static int GetMonthKey() => Game1.year * 100 + Game1.seasonIndex;

        private void RefreshNpcConfig()
        {
            foreach (var n in Utility.getAllCharacters().Where(n => n.datable.Value))
                Config.NpcAllowed.TryAdd(n.Name, true);
            Helper.WriteConfig(Config);
        }

        private void BuildEventIdMap()
        {
            _npcToEventId.Clear();
            int next = 92000001;
            foreach (var n in Utility.getAllCharacters().Where(n => n.datable.Value))
            {
                var key = $"DatingReversed/EventId/{n.Name}";
                if (!ModData.TryGetValue(key, out var s) || !int.TryParse(s, out var stored))
                {
                    stored = next++;
                    ModData[key] = stored.ToString();
                }
                _npcToEventId[n.Name] = stored;
            }
        }

        private bool GenderAllowed(NPC n)
            => (n.Gender == Gender.Male && Config.AllowMaleSuitors)
            || (n.Gender == Gender.Female && Config.AllowFemaleSuitors)
            || n.Gender == Gender.Undefined;

        private void PickDailyBouquetNpc()
        {
            if (!Config.EnableMod) return;

            var d = ModData;
            int today = Game1.Date.TotalDays;
            int month = GetMonthKey();

            // 1) MONTH ROLL-OVER FIRST (even if we're on cooldown)
            if (!d.TryGetValue(BouquetEventMonthKey, out var monthStr)
                || !int.TryParse(monthStr, out var lastMonth)
                || lastMonth != month)
            {
                d[BouquetEventMonthKey] = month.ToString();
                d[BouquetEventsThisMonthKey] = "0";
                // optional: prune old "Seen" keys if you want
                foreach (var k in d.Keys.Where(k => k.StartsWith("DatingReversed/Seen/", StringComparison.Ordinal)).ToList())
                d.Remove(k);
            }

            // 2) COOLDOWN GATE
            if (d.TryGetValue(CooldownDayKey, out var cds)
                && int.TryParse(cds, out var until)
                && until > today)
            {
                d.Remove(DailyNpcKey);
                return;
            }

            // 3) Reuse today's pick if still valid & unseen
            if (d.TryGetValue(DailyNpcKey, out var existing)
                && _npcToEventId.ContainsKey(existing)
                && !d.ContainsKey(SeenThisMonthKey(existing, month)))
            {
                return; // AssetRequested will inject YES when Farm events are requested
            }

            d.Remove(DailyNpcKey);

            // 4) Monthly cap
            int count = (d.TryGetValue(BouquetEventsThisMonthKey, out var cs) && int.TryParse(cs, out var c)) ? c : 0;
            if (count >= Config.MaxBouquetsPerMonth) return;

            // 5) Build candidate pool
            var pool = new List<string>();
            foreach (var name in _npcToEventId.Keys)
            {
                var npcChar = Game1.getCharacterFromName(name);
                if (npcChar is null
                    || !GenderAllowed(npcChar)
                    || !Config.NpcAllowed.TryGetValue(name, out var ok) || !ok
                    || !Game1.player.friendshipData.TryGetValue(name, out var fs)
                    || fs.Points < EligibleMin || fs.Points >= EligibleMaxExclusive
                    || fs.Status == FriendshipStatus.Dating
                    || d.ContainsKey(SeenThisMonthKey(name, month)))
                    continue;

                pool.Add(name);
            }

            if (!pool.Any()) return;
            d[DailyNpcKey] = pool[Game1.random.Next(pool.Count)];
        }

        private void ApplyDating(string npc)
        {
            if (!Game1.player.friendshipData.TryGetValue(npc, out var fs))
                return;

            // don't drop points; just ensure they aren't below the lower bound
            if (fs.Points < EligibleMin)
                fs.Points = EligibleMin;

            fs.Status = FriendshipStatus.Dating;
        }

        private void ApplyCooldownAndCount()
        {
            int cnt = ModData.TryGetValue(BouquetEventsThisMonthKey, out var s)
                      && int.TryParse(s, out var c) ? c : 0;
            ModData[BouquetEventsThisMonthKey] = (cnt + 1).ToString();

            int cd = Config.UseRandomCooldown
                   ? Game1.random.Next(1, 10)
                   : Config.FixedCooldownDays;
            ModData[CooldownDayKey] = (Game1.Date.TotalDays + cd).ToString();
        }

        private void ApplyRejectionPenalty(string npc, int amount = 250)
        {
            if (!Game1.player.friendshipData.TryGetValue(npc, out var fs))
                return;

            int before = fs.Points;
            fs.Points = Math.Max(0, fs.Points - amount);
            Monitor.Log($"Rejected bouquet from {npc}: -{amount} points ( {before} → {fs.Points} )", LogLevel.Trace);
        }

        // ────────── GMCM ──────────
        // builds the status text shown in GMCM
        private string BuildStatusForGmcm()
        {
            var d = ModData;
            string L(string k) => Helper.Translation.Get(k).ToString();
            string none = L("gmcm.status.none");

            string dailyNpc = d.TryGetValue(DailyNpcKey, out var dn) ? dn : none;
            string monthCount = d.TryGetValue(BouquetEventsThisMonthKey, out var mc) ? mc : none;

            string eventMonth;
            if (d.TryGetValue(BouquetEventMonthKey, out var em) && int.TryParse(em, out int m))
            {
                int y = Math.Max(1, m / 100);
                int s = m % 100;
                eventMonth = $"{SeasonName(s)} (Year {y}) [{em}]";
            }
            else eventMonth = none;

            string cooldown;
            if (d.TryGetValue(CooldownDayKey, out var cds) && int.TryParse(cds, out var until))
            {
                int remaining = Math.Max(0, until - Game1.Date.TotalDays);
                cooldown = remaining > 0 ? $"{remaining} day(s) remaining (until idx {until})" : none;
            }
            else cooldown = none;

            return $"{L("gmcm.status.dailyNpc")}: {dailyNpc}\n" +
                   $"{L("gmcm.status.monthCount")}: {monthCount}\n" +
                   $"{L("gmcm.status.eventMonth")}: {eventMonth}\n" +
                   $"{L("gmcm.status.cooldown")}: {cooldown}";
        }

        private void SetupGmcm()
{
    var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
    if (api is null) return;
    _gmcm = api;

    _gmcm.Register(
        mod: ModManifest,
        reset: () => { Config = new ModConfig(); RefreshNpcConfig(); },
        save: () => Helper.WriteConfig(Config)
    );

    _gmcm.AddBoolOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.enableMod.name"),
        tooltip: () => Helper.Translation.Get("gmcm.enableMod.tooltip"),
        getValue: () => Config.EnableMod,
        setValue:  v => Config.EnableMod = v
    );

    _gmcm.AddNumberOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.maxPerMonth.name"),
        tooltip: () => Helper.Translation.Get("gmcm.maxPerMonth.tooltip"),
        getValue: () => Config.MaxBouquetsPerMonth,
        setValue:  v => Config.MaxBouquetsPerMonth = Math.Clamp(v, 1, 99),
        min: 1, max: 20, interval: 1
    );

    _gmcm.AddBoolOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.randomCd.name"),
        tooltip: () => Helper.Translation.Get("gmcm.randomCd.tooltip"),
        getValue: () => Config.UseRandomCooldown,
        setValue:  v => Config.UseRandomCooldown = v
    );

    _gmcm.AddNumberOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.fixedCd.name"),
        tooltip: () => Helper.Translation.Get("gmcm.fixedCd.tooltip"),
        getValue: () => Config.FixedCooldownDays,
        setValue:  v => Config.FixedCooldownDays = Math.Clamp(v, 1, 9),
        min: 1, max: 9, interval: 1
    );

    _gmcm.AddNumberOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.rejectPenalty.name"),
        tooltip: () => Helper.Translation.Get("gmcm.rejectPenalty.tooltip"),
        getValue: () => Config.RejectPenaltyPoints,
        setValue:  v => Config.RejectPenaltyPoints = Math.Clamp(v, 0, 1000),
        min: 0, max: 1000, interval: 50
    );

    _gmcm.AddSectionTitle(ModManifest, text: () => Helper.Translation.Get("gmcm.section.liveStatus"));
    _gmcm.AddParagraph  (ModManifest, text: () => BuildStatusForGmcm());

    _gmcm.AddBoolOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.allowMale.name"),
        getValue: () => Config.AllowMaleSuitors,
        setValue:  v => Config.AllowMaleSuitors = v
    );

    _gmcm.AddBoolOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.allowFemale.name"),
        getValue: () => Config.AllowFemaleSuitors,
        setValue:  v => Config.AllowFemaleSuitors = v
    );

    _gmcm.AddSectionTitle(ModManifest, text: () => Helper.Translation.Get("gmcm.section.npcToggles"));
    AddNpcToggleOptions();

    _gmcm.AddSectionTitle(ModManifest, text: () => Helper.Translation.Get("gmcm.section.advanced"));
    _gmcm.AddBoolOption(ModManifest,
        name:    () => Helper.Translation.Get("gmcm.debugCmds.name"),
        tooltip: () => Helper.Translation.Get("gmcm.debugCmds.tooltip"),
        getValue: () => Config.EnableDebugCommands,
        setValue:  v => Config.EnableDebugCommands = v
    );
}
        private void AddNpcToggleOptions()
        {
            if (_gmcm is null) return;
            foreach (var name in Config.NpcAllowed.Keys.OrderBy(n => n))
            {
                if (_addedNpcToggles.Contains(name)) continue;
		_gmcm.AddBoolOption(
			mod: ModManifest,
			name: () => name, // keep the NPC's proper name as-is
			getValue: () => Config.NpcAllowed[name],
			setValue: v => Config.NpcAllowed[name] = v,
			tooltip: () => Helper.Translation.Get("gmcm.npcToggle.tooltip", new { name }).ToString(),
			fieldId: $"npc_{name}"
		);                
		_addedNpcToggles.Add(name);
            }
        }

        // ────────── Debug console commands ──────────
        private void RegisterConsoleCommands()
        {
            // original
            Helper.ConsoleCommands.Add("dr.dumpstatus", "Show modData (debug)", Guarded(DumpStatus));
            Helper.ConsoleCommands.Add("dr.debugpool", "List eligible NPCs (debug)", Guarded(DebugPool));
            Helper.ConsoleCommands.Add("dr.clearcooldown", "Clear bouquet cooldown (debug)",
                Guarded((_, __) =>
                {
                    ModData.Remove(CooldownDayKey);
                    ModData.Remove(DailyNpcKey);
                    Monitor.Log("Cooldown cleared", LogLevel.Alert);
                }));
            Helper.ConsoleCommands.Add("dr.clearall", "Full reset (debug)", Guarded(ClearAll));
            Helper.ConsoleCommands.Add("dr.forceevent", "dr.forceevent <NPC>  (debug)", Guarded(ForceEvent));
            Helper.ConsoleCommands.Add("dr.playevent", "Play today's bouquet on Farm (debug)",
                Guarded((_, __) =>
                {
                    if (!ModData.TryGetValue(DailyNpcKey, out var n) || string.IsNullOrEmpty(n))
                        return;

                    // 1.6-safe warp using tile from Position
                    if (Game1.currentLocation is not Farm)
                    {
                        int tx = (int)(Game1.player.Position.X / Game1.tileSize);
                        int ty = (int)(Game1.player.Position.Y / Game1.tileSize);
                        Game1.warpFarmer("Farm", tx, ty, false);
                    }

                    Helper.GameContent.InvalidateCache("Data/Events/Farm");
                    Helper.GameContent.Load<Dictionary<string, string>>("Data/Events/Farm");
                    Game1.getFarm().startEvent(new Event(BuildBouquetEvent(n)));
                }));
            Helper.ConsoleCommands.Add("dr.setdating", "dr.setdating <NPC>  (debug)", Guarded((_, a) =>
            {
                if (a.Length > 0) ApplyDating(a[0]);
            }));
            Helper.ConsoleCommands.Add("dr.unsetdating", "dr.unsetdating <NPC>  (debug)", Guarded(UnsetDating));
            Helper.ConsoleCommands.Add("dr.sethall", "dr.sethall <hearts 0-10> (debug)", Guarded(SetAllHeartsCommand));
            Helper.ConsoleCommands.Add("dr.getpoints", "dr.getpoints <NPC>  (debug)", Guarded(GetPointsCommand));
            Helper.ConsoleCommands.Add("dr.setpoints", "dr.setpoints <NPC> <hearts|points>  (debug)", Guarded(SetPointsCommand));

            // new
            Helper.ConsoleCommands.Add("dr.explainpool", "List each NPC with the first exclusion reason", Guarded((_, __) => ExplainPool()));
            Helper.ConsoleCommands.Add("dr.dumpids", "Show count and names in _npcToEventId", Guarded((_, __) =>
            {
                Monitor.Log($"EID map count: {_npcToEventId.Count}", LogLevel.Alert);
                foreach (var k in _npcToEventId.Keys.OrderBy(k => k))
                    Monitor.Log($" - {k}", LogLevel.Trace);
            }));
            Helper.ConsoleCommands.Add("dr.repick", "Clear today & cooldown, then repick", Guarded((_, __) =>
            {
                ModData.Remove(DailyNpcKey);
                ModData.Remove(CooldownDayKey);
                PickDailyBouquetNpc();
                Monitor.Log($"Repicked. DailyNpc = {(ModData.TryGetValue(DailyNpcKey, out var n) ? n : "<none>")}", LogLevel.Alert);
            }));
        }

        // used by dr.explainpool
        private void ExplainPool()
        {
            int month = GetMonthKey();
            foreach (var name in _npcToEventId.Keys.OrderBy(n => n))
            {
                var npc = Game1.getCharacterFromName(name);
                if (npc is null) { LogEx(name, "no instance"); continue; }
                if (!GenderAllowed(npc)) { LogEx(name, "gender disabled in GMCM"); continue; }
                if (!Config.NpcAllowed.TryGetValue(name, out var ok) || !ok) { LogEx(name, "NPC toggle off"); continue; }
                if (!Game1.player.friendshipData.TryGetValue(name, out var fs)) { LogEx(name, "no friendship entry"); continue; }
                if (fs.Points < EligibleMin) { LogEx(name, $"points {fs.Points} < 2000 "); continue; }
                if (fs.Points >= EligibleMaxExclusive) { LogEx(name, $"points {fs.Points} ≥ 2300"); continue; }
                if (fs.Status == FriendshipStatus.Dating) { LogEx(name, "already Dating"); continue; }
                if (ModData.ContainsKey(SeenThisMonthKey(name, month))) { LogEx(name, "seen this month"); continue; }

                Monitor.Log($"OK: {name}", LogLevel.Info);
            }
            void LogEx(string n, string why) => Monitor.Log($"NO: {n} — {why}", LogLevel.Info);
        }

        private void DumpStatus(string _, string[] __)
        {
            var d = ModData;
			
			string L(string k) => Helper.Translation.Get(k).ToString();
			string none = L("gmcm.status.none");

			string dailyNpc  = d.TryGetValue(DailyNpcKey, out var dn) ? dn : none;
			string monthCount= d.TryGetValue(BouquetEventsThisMonthKey, out var mc) ? mc : none;

            Monitor.Log($"DailyNpc: {dailyNpc}", LogLevel.Info);
            Monitor.Log($"MonthCount: {monthCount}", LogLevel.Info);

            // EventMonth pretty print
            if (d.TryGetValue(BouquetEventMonthKey, out var em) && int.TryParse(em, out int m))
            {
                int y = Math.Max(1, m / 100);
                int s = m % 100;
                Monitor.Log($"EventMonth: {SeasonName(s)} (Year {y}) [{em}]", LogLevel.Info);
            }
            else
            {
                Monitor.Log("EventMonth: none", LogLevel.Info);
            }

            // Cooldown pretty print
            if (d.TryGetValue(CooldownDayKey, out var cds) && int.TryParse(cds, out var until))
            {
                int today = Game1.Date.TotalDays;
                int remaining = Math.Max(0, until - today);
                Monitor.Log($"Cooldown: {(remaining > 0 ? $"{remaining} day(s) remaining (until index {until})" : "none")}", LogLevel.Info);
            }
            else
            {
                Monitor.Log("Cooldown: none", LogLevel.Info);
            }
        }
        private void DebugPool(string _, string[] __)
        {
            int month = GetMonthKey();
            var list = new List<string>();
            foreach (var name in _npcToEventId.Keys)
            {
                var npc = Game1.getCharacterFromName(name);
                if (npc is null) continue;
                if (!GenderAllowed(npc)) continue;
                if (!Config.NpcAllowed.TryGetValue(name, out var ok) || !ok) continue;
                if (!Game1.player.friendshipData.TryGetValue(name, out var fs)) continue;
                if (fs.Points < EligibleMin || fs.Points >= EligibleMaxExclusive) continue;
                if (fs.Status == FriendshipStatus.Dating) continue;
                if (ModData.ContainsKey(SeenThisMonthKey(name, month))) continue;

                list.Add(name);
            }
            Monitor.Log("Eligible NPCs: " + string.Join(", ", list), LogLevel.Info);
        }

        private void ForceEvent(string cmd, string[] args)
        {
            if (args.Length < 1) { Monitor.Log("Usage: dr.forceevent <NPC>", LogLevel.Warn); return; }
            var npc = args[0];
            if (!_npcToEventId.ContainsKey(npc)) { Monitor.Log("Unknown NPC", LogLevel.Warn); return; }

            ModData[DailyNpcKey] = npc;
            _applied.Remove(_npcToEventId[npc].ToString());
            Monitor.Log($"Forced {npc}", LogLevel.Alert);
        }

        private void UnsetDating(string cmd, string[] args)
        {
            if (args.Length < 1) { Monitor.Log("Usage: dr.unsetdating <NPC>", LogLevel.Warn); return; }
            if (Game1.player.friendshipData.TryGetValue(args[0], out var fs))
            {
                fs.Status = FriendshipStatus.Friendly;
                fs.Points = Math.Min(fs.Points, 1999);
            }
        }

        private void SetAllHearts(int pts, bool refreshUi)
        {
            int touched = 0;

            foreach (var name in _npcToEventId.Keys)
            {
                // ensure a friendship entry exists
                if (!Game1.player.friendshipData.TryGetValue(name, out var fs))
                    Game1.player.friendshipData[name] = fs = new Friendship();

                // ALWAYS set the raw value (works even if NPC isn't instanced)
                fs.Points = pts;

                // keep non-dating status for pool logic; clear dating if dropping below 2000
                if (fs.Status == FriendshipStatus.Dating && pts < 2000)
                    fs.Status = FriendshipStatus.Friendly;
                else if (fs.Status != FriendshipStatus.Dating)
                    fs.Status = FriendshipStatus.Friendly;

                // OPTIONAL: if NPC is currently loaded, poke Social UI to refresh immediately
                if (refreshUi)
                {
                    var npcObj = Game1.getCharacterFromName(name);
                    if (npcObj != null)
                    {
                        // zero-net nudge forces the UI to recalc now
                        Game1.player.changeFriendship(1, npcObj);
                        Game1.player.changeFriendship(-1, npcObj);
                    }
                }

                touched++;
            }

            Monitor.Log($"All set to {pts} pts ({pts / 250} ♥) — touched {touched} NPC(s).", LogLevel.Alert);
        }
        private void SetAllHeartsCommand(string cmd, string[] args)
        {
            if (args.Length < 1 || !int.TryParse(args[0], out int hearts))
            {
                Monitor.Log("Usage: dr.sethall <hearts 0-10>", LogLevel.Warn);
                return;
            }

            hearts = Math.Clamp(hearts, 0, 10);
            int pts = hearts * 250; // 1 heart = 250 points

            SetAllHearts(pts, true); // reuse your UI-safe setter
            Monitor.Log($"Set all datables to {hearts} ♥ ({pts} pts).", LogLevel.Alert);
        }

        private void ClearAll(string cmd, string[] args)
        {
            // clear per-mod keys
            ModData.Remove(DailyNpcKey);
            ModData.Remove(BouquetEventMonthKey);
            ModData.Remove(BouquetEventsThisMonthKey);
            ModData.Remove(CooldownDayKey);

            // remove per-NPC per-month gates
            var toRemove = ModData.Keys
                .Where(k => k.StartsWith("DatingReversed/Seen/", StringComparison.Ordinal))
                .ToList();
            foreach (var k in toRemove)
                ModData.Remove(k);

            _applied.Clear();

            // reset friendships to pre-bouquet
            foreach (var name in _npcToEventId.Keys)
            {
                if (Game1.player.friendshipData.TryGetValue(name, out var fs))
                {
                    fs.Status = FriendshipStatus.Friendly;
                    fs.Points = Math.Min(fs.Points, 1999);
                }
            }
            Monitor.Log("Full reset", LogLevel.Alert);
        }
        private void GetPointsCommand(string cmd, string[] args)
        {
            if (args.Length < 1) { Monitor.Log("Usage: dr.getpoints <NPC>", LogLevel.Warn); return; }
            var name = args[0];
            if (!Game1.player.friendshipData.TryGetValue(name, out var fs))
            {
                Monitor.Log($"{name}: no friendship entry.", LogLevel.Info);
                return;
            }
            Monitor.Log($"{name}: {fs.Points} pts ({fs.Points / 250} ♥), status={fs.Status}", LogLevel.Alert);
        }

        private void SetPointsCommand(string cmd, string[] args)
        {
            if (args.Length < 2) { Monitor.Log("Usage: dr.setpoints <NPC> <hearts|points>", LogLevel.Warn); return; }

            var name = args[0];
            if (!int.TryParse(args[1], out int val)) { Monitor.Log("2nd arg must be an integer (hearts 0–10 or raw points).", LogLevel.Warn); return; }

            // hearts if 0..10; otherwise treat as raw points
            int pts = (val >= 0 && val <= 10) ? val * 250 : val;

            if (!Game1.player.friendshipData.TryGetValue(name, out var fs))
                Game1.player.friendshipData[name] = fs = new Friendship();

            fs.Points = pts;
            if (fs.Status == FriendshipStatus.Dating && pts < 2000)
                fs.Status = FriendshipStatus.Friendly;

            var npcObj = Game1.getCharacterFromName(name);
            if (npcObj != null)
            {
                // UI nudge so Social page refreshes immediately (no net change)
                Game1.player.changeFriendship(1, npcObj);
                Game1.player.changeFriendship(-1, npcObj);
            }

            Monitor.Log($"Set {name} → {pts} pts ({pts / 250} ♥).", LogLevel.Alert);
        }

        // debug helper
        private string BuildBouquetEvent(string npc) => BuildBouquetEventInternal(npc);

        private string BuildBouquetEventInternal(string npc)
        {
            int eid = _npcToEventId[npc];
            string yesId = (eid * 10 + 1).ToString();

            string Clean(string s) => s.Replace("#", "＃").Replace("\"", "\\\"").Replace("\n", "\\n");

            string ask1 = Clean(SafeI18n("bouquet.ask").Replace("@", Game1.player.Name));
            string ask2 = Clean(SafeI18n("bouquet.q").Replace("@", Game1.player.Name));
            string prompt = Clean(Fallback(Helper.Translation.Get("bouquet.prompt").ToString(), "Will you accept this bouquet?"));
            string yesLabel = Clean(Fallback(Helper.Translation.Get("bouquet.choiceYes").ToString(), "Yes"));
            string noLabel = Clean(Fallback(Helper.Translation.Get("bouquet.choiceNo").ToString(), "Not right now"));
            string reject = Clean(SafeI18n("bouquet.reject").Replace("@", Game1.player.Name));

            int px = (int)(Game1.player.Position.X / Game1.tileSize);
            int py = (int)(Game1.player.Position.Y / Game1.tileSize);

            var parts = new[]
            {
                "none", "-1000 -1000",
                $"farmer {px} {py} 2 {npc} {px} {py + 1} 0",
                $"faceDirection {npc} 0",
                $"faceDirection farmer 2",
                $"speak {npc} \"{ask1}\"",
                "pause 80",
                $"speak {npc} \"{ask2}\"",
                "pause 80",
                $"question fork0 \"{prompt}#{yesLabel}#{noLabel}\"",
                $"fork {yesId}",
                $"emote {npc} 28",
                "pause 400",
                $"speak {npc} \"{reject}\"",
                $"addConversationTopic DR_REJECT_{eid} 1",
                "end"
            };

            Helper.GameContent.InvalidateCache("Data/Events/Farm");
            Helper.GameContent.Load<Dictionary<string, string>>("Data/Events/Farm");

            return string.Join("/", parts);
        }
    }
}
