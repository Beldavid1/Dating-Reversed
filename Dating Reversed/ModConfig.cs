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

        public bool EnableDebugCommands = false;

        /// <summary>If true, use a randomized cooldown after events.</summary>
        public bool UseRandomCooldown { get; set; } = true;

        /// <summary>If not randomized, fixed cooldown duration in days (1–9).</summary>
        public int FixedCooldownDays { get; set; } = 3;

        public int RejectPenaltyPoints { get; set; } = 250;

        /// <summary>If <c>true</c>, single male NPCs may initiate bouquet events.</summary>
        public bool AllowMaleSuitors { get; set; } = true;

        /// <summary>If <c>true</c>, single female NPCs may initiate bouquet events.</summary>
        public bool AllowFemaleSuitors { get; set; } = true;

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
        public Dictionary<string, bool> NpcAllowed { get; set; } = new();

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
