using StardewModdingAPI.Utilities;
using System.Collections.Generic;

namespace Dating_Reversed
{
    /// <summary>
    /// Saves all configurable settings for the mod, loaded & written by SMAPI.
    /// </summary>
    public sealed class ModConfig
    {
        /***************
         * Top-level switches
         ***************/
        /// <summary>Master enable/disable for the entire Dating Reversed logic.</summary>
        public bool EnableMod { get; set; } = true;

        /// <summary>Whether debug console commands and debug menu access are enabled.</summary>
        public bool EnableDebugCommands { get; set; } = false;

        /// <summary>Keybind used to open the debug menu when debug commands are enabled.</summary>
        public KeybindList OpenDebugMenuKey { get; set; } = KeybindList.Parse("LeftControl + D");

        /// <summary>If true, use a randomized cooldown after bouquet events.</summary>
        public bool UseRandomCooldown { get; set; } = true;

        /// <summary>If random cooldown is enabled, the minimum cooldown in days.</summary>
        public int RandomCooldownMinDays { get; set; } = 1;

        /// <summary>If random cooldown is enabled, the maximum cooldown in days.</summary>
        public int RandomCooldownMaxDays { get; set; } = 9;

        /// <summary>If randomized cooldown is disabled, fixed cooldown duration in days.</summary>
        public int FixedCooldownDays { get; set; } = 3;

        /// <summary>Friendship points lost when rejecting a bouquet event.</summary>
        public int RejectPenaltyPoints { get; set; } = 250;

        /// <summary>If true, single male NPCs may initiate bouquet events.</summary>
        public bool AllowMaleSuitors { get; set; } = true;

        /// <summary>If true, single female NPCs may initiate bouquet events.</summary>
        public bool AllowFemaleSuitors { get; set; } = true;

        /// <summary>If true, no bouquet events happen while the farmer is engaged or married to anyone (roommates don't count).</summary>
        public bool PauseWhileFarmerEngagedOrMarried { get; set; } = true;

        /***************
         * Eligibility / trigger pacing
         ***************/
        /// <summary>Minimum hearts required before an NPC can initiate a bouquet event.</summary>
        public int MinEligibleHearts { get; set; } = 8;

        /// <summary>
        /// Maximum hearts exclusive for bouquet eligibility.
        /// Default 11 means NPCs are eligible from 8.0 hearts up to, but not including, 11.0 hearts.
        /// </summary>
        public int MaxEligibleHeartsExclusive { get; set; } = 11;

        /// <summary>
        /// Percent chance each eligible day that a bouquet event will be assigned after cooldown is over.
        /// 100 = always try, 0 = never trigger.
        /// </summary>
        public int DailyTriggerChancePercent { get; set; } = 100;

        /***************
         * Limits
         ***************/
        /// <summary>Maximum bouquet events the farmer can receive in one in-game month.</summary>
        public int MaxBouquetsPerMonth { get; set; } = 4;

        /***************
         * Per-NPC toggle dictionary
         *   key   = NPC.Name
         *   value = whether that NPC is allowed to propose
         ***************/
        public Dictionary<string, bool> NpcAllowed { get; set; }
            = new Dictionary<string, bool>(System.StringComparer.Ordinal);

        /***************
         * Helpers
         ***************/
        /// <summary>
        /// Ensures the dictionary contains an entry for the supplied NPC name,
        /// defaulting to <c>true</c> (allowed).
        /// </summary>
        public void EnsureNpcKey(string npcName)
        {
            if (!NpcAllowed.ContainsKey(npcName))
                NpcAllowed[npcName] = true;
        }
    }
}
