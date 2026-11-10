using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KanbanStockpile
{
    [HarmonyPatch(typeof(ITab_Storage), "TopAreaHeight", MethodType.Getter)]
    static class StorageTabHeight
    {
        static void Postfix(ITab_Storage __instance, ref float __result) => __result += StorageControls.Height(__instance);
    }

    [HarmonyPatch(typeof(ITab_Storage), "FillTab")]
    static class StorageTabControls
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var begin = AccessTools.Method(typeof(Widgets), nameof(Widgets.BeginGroup), new[] { typeof(UnityEngine.Rect) });
            var height = AccessTools.PropertyGetter(typeof(ITab_Storage), "TopAreaHeight");
            bool priorityHeight = true;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.Calls(begin))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return CodeInstruction.Call(typeof(StorageControls), nameof(StorageControls.Draw));
                }
                // Only the first height read sizes the priority button. Later
                // reads place the filter below our controls and keep the extra height.
                if (priorityHeight && instruction.Calls(height))
                {
                    priorityHeight = false;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return CodeInstruction.Call(typeof(StorageControls), nameof(StorageControls.Height));
                    yield return new CodeInstruction(OpCodes.Sub);
                }
            }
        }
    }

    [HarmonyPatch(typeof(StoreUtility), "NoStorageBlockersIn")]
    public static class StoreUtility_NoStorageBlockersIn_Patch
    {
        public static void Postfix(ref bool __result, IntVec3 c, Map map, Thing thing)
        {
            if (!__result || !c.TryGetKanbanSettings(map, out var settings, out var group)) return;
            if (c.TryGetStackRefillThresholdDesired(group, map, thing, settings, out int desired))
            {
                if (!thing.Spawned && thing.MapHeld == map)
                {
                    var stored = map.thingGrid.ThingsListAt(c);
                    for (int i = 0; i < stored.Count; i++)
                        if (stored[i].CanStackWith(thing) && stored[i].stackCount < settings.Limit(thing.def)) return;
                }
                __result = desired > 0;
                return;
            }
            int storedCount = KSUtil.CountStoredSimilarStacks(group, map, thing, settings.ssl, settings);
            if (storedCount >= settings.ssl) { __result = false; return; }
            if (KanbanStockpile.Settings.ReservedSimilarStackChecking && map.reservationManager != null)
                __result = storedCount + ReservationIndex.For(map.reservationManager)
                    .CountNewStacks(group, map, thing, settings.ssl - storedCount, c)
                    + (ReworkedDeliveries.Current?.NewStacks(map, group, thing, PickUpAndHaulReworked.QueryPawn(thing)) ?? 0) < settings.ssl;
        }
    }

    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToCellStorageJob))]
    public static class HaulAIUtility_HaulToStorageJob_Patch
    {
        public static void Postfix(ref Job __result, Pawn p, Thing t)
        {
            if (__result == null || !__result.TryGetHaulingDestination(out var map, out var cell, out var group)
                || !cell.TryGetKanbanSettings(map, out var settings, out _)) return;
            int carryingCapacity = settings.srt < 100 ? p.carryTracker.MaxStackSpaceEver(t.def) : t.def.stackLimit;
            if (settings.srt < 100)
            {
                var stored = map.thingGrid.ThingsListAt(cell);
                for (int i = 0; i < stored.Count; i++)
                    if (stored[i].CanStackWith(t)) RefillCycles.RecordLoad(stored[i], carryingCapacity);
            }
            int desired = settings.Limit(t.def);
            bool existing = cell.TryGetStackRefillThresholdDesired(group, map, t, settings, out int refill);
            if (existing) desired = refill;
            if (existing && !t.Spawned && t.MapHeld == map)
            {
                var stored = map.thingGrid.ThingsListAt(cell);
                for (int i = 0; i < stored.Count; i++)
                    if (stored[i].CanStackWith(t)) desired = Math.Max(desired, settings.Limit(t.def) - stored[i].stackCount);
            }
            if (KanbanStockpile.Settings.ReservedSimilarStackChecking && map.reservationManager != null)
            {
                desired -= ReservationIndex.For(map.reservationManager).Incoming(group, map, cell, t, __result);
                // A new pickup is additional to this pawn's inventory cargo.
                // Only an unloading job may exclude its own existing promise.
                desired -= ReworkedDeliveries.Current?.IncomingQuantity(map, cell, t, t.Spawned ? null : p) ?? 0;
            }
            __result.count = Math.Min(__result.count, Math.Max(0, desired));
            if (existing && settings.srt < 100)
            {
                int limit = settings.Limit(t.def);
                int trigger = (int)((long)limit * settings.srt / 100);
                int minimum = RefillCycles.MinimumDelivery(limit, settings.srt, carryingCapacity);
                var things = map.thingGrid.ThingsListAt(cell);
                bool urgent = false;
                for (int i = 0; i < things.Count; i++)
                    if (things[i].CanStackWith(t) && things[i].stackCount <= trigger) { urgent = true; break; }
                // Only inspect nearby piles for a small, non-urgent source.
                // Normal loads and low-stock deliveries take no extra search.
                if (!urgent && p.carryTracker.CarriedThing == null
                    && (Math.Min(desired, carryingCapacity) < minimum
                        || t.stackCount < minimum && !HaulBatchEstimate.HasMinimumNearby(p, t, cell, minimum)))
                    __result.count = 0;
            }
            if (__result.count <= 0) __result = null;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Merge), nameof(WorkGiver_Merge.JobOnThing))]
    static class MergeStorageLimits
    {
        static void Postfix(Pawn pawn, Thing t, ref Job __result) =>
            HaulAIUtility_HaulToStorageJob_Patch.Postfix(ref __result, pawn, t);
    }

    static class HaulBatchEstimate
    {
        public static bool HasMinimumNearby(Pawn pawn, Thing source, IntVec3 destination, int minimum)
        {
            int count = source.stackCount;
            int cells = GenRadial.NumCellsInRadius(8f);
            for (int i = 0; i < cells && count < minimum; i++)
            {
                var cell = source.Position + GenRadial.RadialPattern[i];
                if (cell == destination || !cell.InBounds(pawn.Map)) continue;
                var things = pawn.Map.thingGrid.ThingsListAt(cell);
                for (int j = 0; j < things.Count && count < minimum; j++)
                {
                    var candidate = things[j];
                    if (candidate == source || candidate.def != source.def || !candidate.CanStackWith(source)
                        || candidate.stackCount >= candidate.def.stackLimit || candidate.IsForbidden(pawn)
                        || !candidate.IsSociallyProper(pawn) || !pawn.CanReserveAndReach(candidate, PathEndMode.ClosestTouch, pawn.NormalMaxDanger())) continue;
                    count += candidate.stackCount;
                }
            }
            return count >= minimum;
        }
    }

    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.ExposeData))]
    static class StorageSettingsSave
    {
        static void Postfix(StorageSettings __instance)
        {
            var settings = State.Get(__instance);
            Scribe_Values.Look(ref settings.srt, "stackRefillThreshold", 100);
            Scribe_Values.Look(ref settings.ssl, "similarStackLimit", 0);
            Scribe_Values.Look(ref settings.mss, "maxStackSize", 0);
            if (Scribe.mode == LoadSaveMode.LoadingVars) State.Set(__instance, settings);
        }
    }

    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.CopyFrom))]
    static class StorageSettingsCopy
    {
        static void Postfix(StorageSettings __instance, StorageSettings other) => State.Set(__instance, State.Get(other));
    }
}
