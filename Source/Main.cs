using System;
using UnityEngine;
using RimWorld;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace KanbanStockpile
{
    [StaticConstructorOnStartup]
    public static class KanbanStockpileLoader
    {
        public static bool IsStockpileRankingLoaded;
        public static bool IsPickUpAndHaulLoaded;

        static KanbanStockpileLoader()
        {
            var harmony = new Harmony("eiennohito.kanbanstockpile.refill");
            harmony.PatchAll();
            IsStockpileRankingLoaded = Loaded("Uuugggg.StockpileRanking") || Loaded("memegoddess.stockpileranking");
            if (Loaded("Dreadarm.PickUpAndHaul"))
            {
                IsPickUpAndHaulLoaded = TryApplyIntegration(
                    new Harmony(harmony.Id + ".reworked"), PickUpAndHaulReworked.ApplyPatch,
                    ex => { PickUpAndHaulReworked.Reset(); Log.Error("[KanbanStockpile] Reworked integration disabled: " + ex); });
            }
            else if (Loaded("Mehni.PickUpAndHaul") || Loaded("Mlie.PickUpAndHaul"))
            {
                IsPickUpAndHaulLoaded = TryApplyIntegration(
                    new Harmony(harmony.Id + ".puah"), PickUpAndHaul_WorkGiver_HaulToInventory_Patch.ApplyPatch,
                    ex => Log.Error("[KanbanStockpile] Pick Up And Haul integration disabled: " + ex));
            }
        }

        // Optional patches use a separate owner so rollback cannot remove the
        // storage controls or the other core rules installed by PatchAll.
        internal static bool TryApplyIntegration(Harmony harmony, Action<Harmony> apply, Action<Exception> failed)
        {
            try { apply(harmony); return true; }
            catch (Exception ex)
            {
                harmony.UnpatchAll(harmony.Id);
                failed(ex);
                return false;
            }
        }

        static bool Loaded(string id) => ModLister.GetActiveModWithIdentifier(id) != null;
    }

    public class KanbanStockpile : Mod
    {
        public static KanbanStockpileSettings Settings;

        public KanbanStockpile(ModContentPack content) : base(content)
        {
            Settings = GetSettings<KanbanStockpileSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            KanbanStockpileSettings.DoWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "KS.ModName".Translate();
        }
    }

    public static class KSUtil
    {
        public static bool TryGetKanbanSettings(this IntVec3 cell, Map map, out KanbanSettings ks, out SlotGroup slotGroup)
        {
            ks = new KanbanSettings();
            slotGroup = null;

            if (map == null) return false;

            slotGroup = cell.GetSlotGroup(map);
            if (slotGroup?.Settings == null) return false;

            ks = State.Get(slotGroup.Settings);

            if (!ks.Enabled) return false;

            return true;
        }

        public static bool IsSimilarStack(Thing t, Thing thing)
        {
            if (t == null || thing == null) return false;
            if (!t.def.EverStorable(false)) return false;
            if (t == thing) return false;
            if (!t.CanStackWith(thing) &&
                 !(KanbanStockpile.Settings.ConsiderDifferentMaterialSimilar && t.def.stackLimit == 1 && t.def.defName == thing.def.defName)) return false;

            return true;
        }

        public static int CountStoredSimilarStacks(SlotGroup slotGroup, Map map, Thing thing, int max, KanbanSettings ks = default)
        {
            int count = 0;
            var stored = StorageIndex.For(slotGroup).Matching(thing.def, map);
            for (int i = 0; i < stored.Count; i++)
                if (IsSimilarStack(stored[i], thing) && ++count >= max) break;
            return count;
        }

        public static bool TryGetStackRefillThresholdDesired(this IntVec3 cell, SlotGroup slotGroup,
            Map map, Thing thing, KanbanSettings ks, out int numDesired)
        {
            numDesired = 0;
            bool matching = false;
            int occupied = 0;
            var things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                var stored = things[i];
                if (!stored.def.EverStorable(false)) continue;
                occupied++;
                if (!stored.CanStackWith(thing)) continue;
                matching = true;
                numDesired = Math.Max(numDesired, RefillCycles.Desired(stored, slotGroup, ks));
            }
            if (matching && numDesired == 0 && occupied < cell.GetMaxItemsAllowedInCell(map)
                && CountStoredSimilarStacks(slotGroup, map, thing, ks.ssl, ks) < ks.ssl) return false;
            return matching;
        }

        public static bool TryGetHaulingDestination(this Job job, out Map map, out IntVec3 dest, out SlotGroup slotGroup)
        {
            return TryGetHaulingDestinationOnMap(job, out map, out dest, out slotGroup, null);
        }

        internal static bool TryGetHaulingDestinationOnMap(Job job, out Map map, out IntVec3 dest, out SlotGroup slotGroup, Map fallbackMap)
        {
            map = null;
            slotGroup = null;
            dest = new IntVec3();

            if (job == null) return false;

            if (job.def == JobDefOf.HaulToContainer)
            {
                Thing container = job.targetB.Thing;
                if (container == null) return false;
                map = container.Map;
                dest = container.Position;
            }
            else
            {
                dest = job.targetB.Cell;
                if (!dest.IsValid || job.targetA.Thing == null) return false;
                map = job.targetA.Thing.Map ?? job.targetA.Thing.MapHeld ?? fallbackMap;
            }

            if (map == null) return false;

            slotGroup = dest.GetSlotGroup(map);
            if (slotGroup == null) return false;

            return true;
        }

        public static int CountReservedSimilarStacks(SlotGroup slotGroup, Map map, Thing thing,
            int max, KanbanSettings ks = default)
        {
            return ReservationIndex.For(map.reservationManager).CountNewStacks(slotGroup, map, thing, max);
        }
    }
}
