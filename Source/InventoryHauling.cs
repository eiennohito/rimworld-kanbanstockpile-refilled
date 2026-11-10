using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KanbanStockpile
{
    // Original PUAH and Continued plan pickups together, but unload in a
    // separate job. Keep destination promises through that transition.
    static class InventoryHauling
    {
        internal sealed class Planning
        {
            internal Pawn pawn;
            internal List<PlannedDelivery> deliveries;
        }
        [ThreadStatic] internal static Planning current;
        [ThreadStatic] static Pawn droppingPawn;
        static readonly ConditionalWeakTable<Job, List<PlannedDelivery>> jobs = new();
        static Type cargoType;
        static MethodInfo cargoItems, registerItem;
        static AccessTools.FieldRef<object, int> countToDrop;
        static readonly AccessTools.FieldRef<JobDriver_UnloadYourInventory, int> nativeCountToDrop =
            AccessTools.FieldRefAccess<JobDriver_UnloadYourInventory, int>("countToDrop");
        static JobDef pickup, unload;
        internal static Pawn PlanningPawn => current?.pawn;
        internal static bool IsBatchJob(Job job) => job != null && pickup != null && job.def == pickup;
        internal static bool IsHaulingJob(Job job) => IsBatchJob(job) || job != null && unload != null && job.def == unload;

        internal static void Reset()
        {
            cargoType = null;
            cargoItems = registerItem = null;
            pickup = unload = null;
            countToDrop = null;
            current = null;
            droppingPawn = null;
        }

        static ThingComp Cargo(Pawn pawn) => cargoType == null ? null : pawn?.AllComps?.FirstOrDefault(cargoType.IsInstanceOfType);
        internal static int HauledCount(Pawn pawn, Thing thing)
        {
            if (thing == null || thing.Destroyed || cargoType == null) return 0;
            if (pawn.carryTracker?.CarriedThing == thing && IsHaulingJob(pawn.CurJob)) return thing.stackCount;
            var comp = Cargo(pawn);
            if (comp == null) return 0;
            var marked = (HashSet<Thing>)cargoItems.Invoke(comp, null);
            if (marked.Contains(thing)) return thing.stackCount;
            // Original PUAH's unloading driver also recognizes the inventory
            // replacement of a merged, stale hauled stack by definition.
            return marked.Any(t => t != null && t.def == thing.def && !pawn.inventory.innerContainer.Contains(t))
                ? thing.stackCount : 0;
        }

        internal static void ApplyPatch(Harmony harmony)
        {
            var work = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
            var driver = AccessTools.TypeByName("PickUpAndHaul.JobDriver_HaulToInventory");
            var unloading = AccessTools.TypeByName("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");
            cargoType = AccessTools.TypeByName("PickUpAndHaul.CompHauledToInventory");
            var capacity = work == null ? null : AccessTools.Method(work, "CapacityAt");
            var plan = work == null ? null : AccessTools.Method(work, "JobOnThing");
            var reserve = driver == null ? null : AccessTools.Method(driver, "TryMakePreToilReservations");
            var target = unloading == null ? null : AccessTools.Method(unloading, "FindTargetOrDrop");
            cargoItems = cargoType == null ? null : AccessTools.Method(cargoType, "GetHashSet");
            registerItem = cargoType == null ? null : AccessTools.Method(cargoType, "RegisterHauledItem");
            if (capacity == null || plan == null || reserve == null || target == null || cargoItems == null || registerItem == null)
                throw new MissingMethodException("Pick Up And Haul pickup/unloading API has changed.");
            countToDrop = AccessTools.FieldRefAccess<int>(unloading, "_countToDrop");
            pickup = DefDatabase<JobDef>.GetNamed("HaulToInventory");
            unload = DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
            HarmonyMethod Patch(string name) => new(typeof(InventoryHauling), name);
            harmony.Patch(capacity, postfix: Patch(nameof(Capacity)));
            harmony.Patch(plan, prefix: Patch(nameof(Begin)), postfix: Patch(nameof(Planned)), finalizer: Patch(nameof(End)));
            harmony.Patch(reserve, postfix: Patch(nameof(Reserved)));
            harmony.Patch(target, postfix: Patch(nameof(UnloadTarget)));
        }

        static void Begin(Pawn pawn, out Planning __state)
        {
            __state = current;
            var owned = ReworkedDeliveries.Current?.ForPawn(pawn) ?? new List<PlannedDelivery>();
            current = new Planning { pawn = pawn, deliveries = owned };
        }
        static Exception End(Exception __exception, Planning __state) { current = __state; return __exception; }

        internal static void Capacity(Thing thing, IntVec3 storeCell, Map map, ref int __result)
        {
            if (__result <= 0 || !storeCell.TryGetKanbanSettings(map, out _, out _)) return;
            __result = Math.Min(__result, PickUpAndHaulReworked.ControlledCapacity(storeCell, map, thing,
                current?.pawn ?? PickUpAndHaulReworked.HolderPawn(thing), current?.deliveries, thing.Spawned));
        }

        static PlannedDelivery Delivery(Pawn pawn, Thing thing, IntVec3 cell, int count) => new()
        {
            map = pawn.Map, cell = cell, def = thing.def, stuff = thing.Stuff,
            source = thing, sourceId = thing.thingIDNumber, count = count
        };

        static void Planned(Pawn pawn, ref Job __result)
        {
            var job = __result;
            if (!IsBatchJob(job) || job.targetQueueA == null || job.countQueue == null || current == null) return;
            var targets = new List<LocalTargetInfo> { job.targetB };
            if (job.targetQueueB != null) targets.AddRange(job.targetQueueB);
            targets = targets.Distinct().ToList();
            if (!targets.Any(t => !t.HasThing && t.Cell.TryGetKanbanSettings(pawn.Map, out _, out _))) return;

            var local = current.deliveries;
            int previous = local.Count;
            var containerCounts = new Dictionary<Thing, int>();
            var acceptedTargets = new List<LocalTargetInfo>();
            for (int i = 0; i < Math.Min(job.targetQueueA.Count, job.countQueue.Count); i++)
            {
                var thing = job.targetQueueA[i].Thing;
                int remaining = thing?.Spawned == true ? Math.Min(thing.stackCount, Math.Max(0, job.countQueue[i])) : 0;
                int assigned = 0;
                foreach (var target in targets)
                {
                    if (remaining <= 0) break;
                    int capacity;
                    if (target.HasThing)
                    {
                        var owner = target.Thing.TryGetInnerInteractableThingOwner();
                        containerCounts.TryGetValue(target.Thing, out int used);
                        capacity = Math.Max(0, (owner?.GetCountCanAccept(thing) ?? 0) - used);
                    }
                    else
                    {
                        if (!target.Cell.InBounds(pawn.Map) || !StoreUtility.IsGoodStoreCell(target.Cell, pawn.Map, thing, pawn, pawn.Faction)) continue;
                        if (target.Cell.TryGetKanbanSettings(pawn.Map, out _, out _))
                            capacity = PickUpAndHaulReworked.ControlledCapacity(target.Cell, pawn.Map, thing, pawn, local, true);
                        else
                            capacity = Math.Max(0, target.Cell.GetItemStackSpaceLeftFor(pawn.Map, thing.def)
                                - local.Where(d => d.cell == target.Cell && d.CanMerge(thing)).Sum(d => d.count));
                    }
                    int amount = Math.Min(remaining, capacity);
                    if (amount <= 0) continue;
                    if (target.HasThing)
                    {
                        containerCounts.TryGetValue(target.Thing, out int used);
                        containerCounts[target.Thing] = used + amount;
                    }
                    else local.Add(Delivery(pawn, thing, target.Cell, amount));
                    if (!acceptedTargets.Contains(target)) acceptedTargets.Add(target);
                    remaining -= amount;
                    assigned += amount;
                }
                job.countQueue[i] = assigned;
            }
            for (int i = job.countQueue.Count - 1; i >= 0; i--)
                if (job.countQueue[i] <= 0) { job.countQueue.RemoveAt(i); job.targetQueueA.RemoveAt(i); }
            if (job.countQueue.Count == 0 || previous == 0 && !PickUpAndHaulReworked.UsefulBatch(job, local))
            { __result = null; return; }
            job.targetA = job.targetQueueA[0];
            job.targetB = acceptedTargets[0];
            job.targetQueueB = acceptedTargets.Skip(1).ToList();
            jobs.Add(job, local);
        }

        static void Reserved(JobDriver __instance, bool __result)
        {
            if (__result && jobs.TryGetValue(__instance.job, out var deliveries))
                ReworkedDeliveries.Current?.Commit(__instance.pawn, deliveries);
            jobs.Remove(__instance.job);
        }

        static void UnloadTarget(JobDriver __instance, Toil __result)
        {
            var native = __result.initAction;
            __result.initAction = () =>
            {
                var previous = droppingPawn;
                droppingPawn = __instance.pawn;
                try { native(); }
                finally { droppingPawn = previous; }
                var job = __instance.job;
                var pawn = __instance.pawn;
                if (job == null || pawn?.CurJob != job) return;
                var thing = job.targetA.Thing;
                if (thing == null || job.targetB.HasThing
                    || !job.targetB.Cell.TryGetKanbanSettings(pawn.Map, out _, out _)) return;
                int capacity = PickUpAndHaulReworked.ControlledCapacity(job.targetB.Cell, pawn.Map, thing, pawn, null, false);
                countToDrop(__instance) = Math.Min(countToDrop(__instance), capacity);
                job.count = countToDrop(__instance);
                if (job.count <= 0) __instance.EndJobWith(JobCondition.Incompletable);
            };
        }

        internal static void NearbyDrop(Thing thing, Map map, ThingPlaceMode mode, ref Predicate<IntVec3> extraValidator)
        {
            var pawn = droppingPawn;
            if (pawn?.Map != map || mode != ThingPlaceMode.Near || !thing.def.EverStorable(false)) return;
            var native = extraValidator;
            extraValidator = cell => (native == null || native(cell))
                && (!cell.TryGetKanbanSettings(map, out _, out _)
                    || PickUpAndHaulReworked.ControlledCapacity(cell, map, thing, pawn, null, false) >= thing.stackCount);
        }

        static bool IsNativeUnload(Job job) => job?.def.driverClass == typeof(JobDriver_UnloadYourInventory);

        internal static IEnumerable<Toil> NativeUnloadToils(JobDriver_UnloadYourInventory driver, IEnumerable<Toil> toils)
        {
            foreach (var toil in toils)
            {
                var native = toil.initAction;
                if (native != null)
                    toil.initAction = () =>
                    {
                        var previous = droppingPawn;
                        droppingPawn = driver.pawn;
                        try
                        {
                            if (!CapNativeUnload(driver)) { driver.EndJobWith(JobCondition.Incompletable); return; }
                            native();
                            if (driver.job != null && driver.pawn?.CurJob == driver.job && !CapNativeUnload(driver))
                                driver.EndJobWith(JobCondition.Incompletable);
                        }
                        finally { droppingPawn = previous; }
                    };
                yield return toil;
            }
        }

        static bool CapNativeUnload(JobDriver_UnloadYourInventory driver)
        {
            var job = driver.job;
            var pawn = driver.pawn;
            var thing = job?.targetA.Thing;
            if (thing == null || !pawn.inventory.innerContainer.Contains(thing) || job.targetB.HasThing
                || nativeCountToDrop(driver) <= 0 || !job.targetB.Cell.TryGetKanbanSettings(pawn.Map, out _, out _)) return true;
            int capacity = PickUpAndHaulReworked.ControlledCapacity(job.targetB.Cell, pawn.Map, thing, pawn, null, false);
            nativeCountToDrop(driver) = Math.Min(nativeCountToDrop(driver), capacity);
            job.count = nativeCountToDrop(driver);
            return job.count > 0;
        }

        internal static bool BeforeDrop(Pawn_CarryTracker __instance, IntVec3 dropLoc, ThingPlaceMode mode, ref bool __result, out Pawn __state)
        {
            __state = droppingPawn;
            var pawn = __instance.pawn;
            var thing = __instance.CarriedThing;
            bool unloading = IsHaulingJob(pawn.CurJob) || IsNativeUnload(pawn.CurJob);
            if (unloading) droppingPawn = pawn;
            if (thing == null || mode != ThingPlaceMode.Direct || !unloading
                || !dropLoc.TryGetKanbanSettings(pawn.Map, out _, out _)) return true;
            int capacity = PickUpAndHaulReworked.ControlledCapacity(dropLoc, pawn.Map, thing, pawn, null, false);
            if (capacity <= 0) { __result = false; return false; }
            if (thing.stackCount > capacity)
            {
                int excess = thing.stackCount - capacity;
                if (__instance.innerContainer.TryTransferToContainer(thing, pawn.inventory.innerContainer, excess, out var kept, false) != excess)
                { __result = false; return false; }
                if (IsHaulingJob(pawn.CurJob)) registerItem.Invoke(Cargo(pawn), new object[] { kept });
            }
            return true;
        }

        internal static Exception AfterDrop(Exception __exception, Pawn __state)
        {
            droppingPawn = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(JobDriver_UnloadYourInventory), "MakeNewToils")]
    static class NativeInventoryUnloading
    {
        static void Postfix(JobDriver_UnloadYourInventory __instance, ref IEnumerable<Toil> __result) =>
            __result = InventoryHauling.NativeUnloadToils(__instance, __result);
    }

    [HarmonyPatch]
    static class InventoryDepositLimits
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryDropCarriedThing),
            new[] { typeof(IntVec3), typeof(ThingPlaceMode), typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>) });
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Pawn_CarryTracker __instance, IntVec3 dropLoc, ThingPlaceMode mode, ref bool __result, out Pawn __state) =>
            InventoryHauling.BeforeDrop(__instance, dropLoc, mode, ref __result, out __state);
        static Exception Finalizer(Exception __exception, Pawn __state) => InventoryHauling.AfterDrop(__exception, __state);
    }

    [HarmonyPatch]
    static class InventoryNearbyPlacement
    {
        static MethodBase TargetMethod() => typeof(GenPlace).GetMethods().Single(m => m.Name == nameof(GenPlace.TryPlaceThing)
            && m.GetParameters().Any(p => p.IsOut));
        static void Prefix(Thing thing, Map map, ThingPlaceMode mode, ref Predicate<IntVec3> extraValidator) =>
            InventoryHauling.NearbyDrop(thing, map, mode, ref extraValidator);
    }
}
