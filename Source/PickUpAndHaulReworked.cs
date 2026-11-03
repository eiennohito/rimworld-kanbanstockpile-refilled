using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KanbanStockpile
{
    public static class PickUpAndHaulReworked
    {
        // HasJobOnThing calls PrimaryPlan directly. BatchJob calls it and then
        // plans nearby pickups, so both entry points need nested scopes.
        [ThreadStatic]
        private static int planningDepth;
        [ThreadStatic] private static Pawn planningPawn;
        [ThreadStatic] private static object activePlan;
        private static readonly ConditionalWeakTable<object, List<PlannedDelivery>> allocations = new();
        private static readonly ConditionalWeakTable<Job, List<PlannedDelivery>> jobPlans = new();
        private static AccessTools.FieldRef<object, Pawn> planPawn;
        private static AccessTools.FieldRef<object, List<LocalTargetInfo>> planTargets;
        private static Func<Job, bool> isHaulingJob;
        private static Func<Pawn, Thing, int> hauledCount;
        private static JobDef batchDef;
        public static Pawn QueryPawn(Thing thing) => planningPawn ?? HolderPawn(thing);
        public static bool IsBatchJob(Job job) => job != null && batchDef != null && job.def == batchDef;
        public static int HauledCount(Pawn pawn, Thing thing) => Math.Min(thing.stackCount, Math.Max(0, hauledCount?.Invoke(pawn, thing) ?? 0));
        private static readonly MethodInfo nativeCapacity = AccessTools.Method(typeof(GridsUtility),
            nameof(GridsUtility.GetItemStackSpaceLeftFor));
        private static readonly FieldInfo thingDef = AccessTools.Field(typeof(Thing), nameof(Thing.def));

        public static bool IsHaulingJob(Job job)
        {
            return isHaulingJob != null && isHaulingJob(job);
        }

        public static void ApplyPatch(Harmony harmony)
        {
            Type work = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.HaulingWork");
            Type plan = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.BatchPickupPlan");
            Type api = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Api.V1");
            Type mergeGiver = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.WorkGiver_BatchMerge");
            if (work == null || plan == null || api == null || mergeGiver == null)
                throw new MissingMemberException("[KanbanStockpile] Unsupported Pick Up And Haul Reworked API.");

            var classify = AccessTools.Method(api, "IsHaulingJob", new[] { typeof(Job) });
            if (classify == null || classify.ReturnType != typeof(bool))
                throw new MissingMethodException("[KanbanStockpile] Reworked IsHaulingJob API is missing.");
            isHaulingJob = (Func<Job, bool>)Delegate.CreateDelegate(typeof(Func<Job, bool>), classify);
            hauledCount = (Func<Pawn, Thing, int>)Delegate.CreateDelegate(typeof(Func<Pawn, Thing, int>), AccessTools.Method(api, "GetHauledCount"));
            batchDef = DefDatabase<JobDef>.GetNamed("Dreadarm_PickUpAndHaul_Batch");
            planPawn = AccessTools.FieldRefAccess<Pawn>(plan, "pawn");
            planTargets = AccessTools.FieldRefAccess<List<LocalTargetInfo>>(plan, "<Targets>k__BackingField");

            var scopeMethods = new[] { AccessTools.Method(work, "PrimaryPlan"), AccessTools.Method(work, "BatchJob") };
            var merge = AccessTools.Method(work, "TryFindMergeTarget");
            var mergeJob = AccessTools.Method(mergeGiver, "JobOnThing");
            if (scopeMethods.Any(m => m == null) || merge == null || mergeJob == null)
                throw new MissingMethodException("[KanbanStockpile] Reworked planning methods have changed.");

            var capacityMethods = new List<MethodInfo>();
            foreach (Type type in new[] { work, plan })
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var codes = PatchProcessor.GetOriginalInstructions(method);
                    if (!codes.Any(c => c.Calls(nativeCapacity))) continue;
                    CapacityTranspiler(codes).ToList();
                    capacityMethods.Add(method);
                }
            if (capacityMethods.Count == 0)
                throw new MissingMethodException("[KanbanStockpile] Reworked storage capacity calls were not found.");

            foreach (var method in scopeMethods)
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(BeginPlanning)),
                    finalizer: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(EndPlanning)));
            foreach (var method in work.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Where(m => m.Name == "TryPlanPickup"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(BeginPlanning)),
                    finalizer: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(EndPlanning)));
            harmony.Patch(AccessTools.Method(work, "BatchJob"), postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(BatchCreated)));
            harmony.Patch(AccessTools.Method(plan, "Fill"), prefix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(Fill)));
            Type driver = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.JobDriver_BatchHaul");
            harmony.Patch(AccessTools.Method(driver, "TryMakePreToilReservations"),
                postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(Reserved)));
            harmony.Patch(AccessTools.Method(driver, "TryExtendBatch"),
                prefix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(BeginExtension)),
                postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(Extended)),
                finalizer: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(EndPlanning)));
            foreach (var method in capacityMethods)
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(CapacityTranspiler)));
            harmony.Patch(merge, postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(MergePostfix)));
            harmony.Patch(mergeJob, postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(MergeFallbackPostfix)));
            Log.Message("[KanbanStockpile] Patched Pick Up And Haul Reworked planning and delivery capacity ("
                + capacityMethods.Count + " methods).");
        }

        public sealed class PlanningState
        {
            internal int depth;
            internal Pawn pawn;
            internal object plan;
        }

        public static void BeginPlanning(Pawn pawn, out PlanningState __state)
        {
            __state = new PlanningState { depth = planningDepth, pawn = planningPawn, plan = activePlan };
            planningDepth++;
            planningPawn = pawn;
            if (__state.depth == 0) activePlan = null;
        }

        public static Exception EndPlanning(Exception __exception, PlanningState __state)
        {
            planningDepth = __state.depth;
            planningPawn = __state.pawn;
            if (__state.depth == 0) activePlan = __state.plan;
            return __exception;
        }

        public static void BeginExtension(JobDriver __instance, out PlanningState __state)
        {
            BeginPlanning(__instance.pawn, out __state);
            activePlan = null;
        }

        public static void BatchCreated(Pawn pawn, ref Job __result)
        {
            if (__result == null || activePlan == null || planTargets(activePlan) != __result.targetQueueA) return;
            var deliveries = allocations.GetOrCreateValue(activePlan);
            if (!UsefulBatch(__result, deliveries)) { __result = null; return; }
            jobPlans.Remove(__result);
            jobPlans.Add(__result, deliveries);
        }

        // Judge the completed batch, rather than each small source separately:
        // several fragments can make one useful trip, as can other supplies
        // already included in the same route.
        static bool UsefulBatch(Job job, List<PlannedDelivery> deliveries)
        {
            long allocated = 0, pickups = 0;
            foreach (var delivery in deliveries)
            {
                if (delivery.source?.Spawned != true) return true;
                allocated += delivery.count;
            }
            if (job.countQueue != null) foreach (int count in job.countQueue) pickups += count;
            if (pickups > allocated) return true;
            foreach (var delivery in deliveries)
            {
                if (!delivery.cell.TryGetKanbanSettings(delivery.map, out var settings, out _)
                    || settings.srt >= 100) return true;
                bool matching = false;
                int limit = settings.Limit(delivery.def);
                int trigger = (int)((long)limit * settings.srt / 100);
                foreach (var item in delivery.map.thingGrid.ThingsListAt(delivery.cell))
                    if (item.CanStackWith(delivery.source))
                    {
                        matching = true;
                        if (item.stackCount <= trigger) return true;
                    }
                if (!matching) return true;
                long total = 0;
                foreach (var other in deliveries)
                    if (other.map == delivery.map && other.cell == delivery.cell && other.CanMerge(delivery.source))
                        total += other.count;
                if (total >= RefillCycles.MinimumDelivery(limit, settings.srt, limit)) return true;
            }
            return deliveries.Count == 0;
        }

        public static void Reserved(JobDriver __instance, bool __result)
        {
            if (__result && jobPlans.TryGetValue(__instance.job, out var deliveries))
                ReworkedDeliveries.Current?.Commit(__instance.pawn, deliveries);
            jobPlans.Remove(__instance.job);
        }

        public static void Extended(JobDriver __instance, bool __result)
        {
            if (__result && activePlan != null)
                ReworkedDeliveries.Current?.Commit(__instance.pawn, allocations.GetOrCreateValue(activePlan));
        }

        public static bool Fill(object __instance, Thing thing, IntVec3 cell, int count, ref int __result)
        {
            activePlan = __instance;
            if (!cell.TryGetKanbanSettings(planPawn(__instance).Map, out _, out _)) return true;
            var local = allocations.GetOrCreateValue(__instance);
            int capacity = ControlledCapacity(cell, planPawn(__instance).Map, thing, planPawn(__instance), local, thing.Spawned);
            __result = Math.Min(count, capacity);
            if (__result > 0) local.Add(new PlannedDelivery { map = planPawn(__instance).Map, cell = cell,
                def = thing.def, stuff = thing.Stuff, source = thing, sourceId = thing.thingIDNumber, count = __result });
            return false;
        }

        // Rewrites capacity calls in HaulingWork.Storage, TargetCapacity, both
        // StorageCapacity overloads, and BatchPickupPlan.TryReadDestination/Fill:
        //   cell.GetItemStackSpaceLeftFor(map, thing.def) -> CapacityAt(cell, map, thing)
        // Nop the preceding ldfld Thing.def to retain Thing for refill checks,
        // preserving the instruction's branch labels and exception boundaries.
        public static IEnumerable<CodeInstruction> CapacityTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
            for (int i = 0; i < codes.Count; i++)
            {
                if (!codes[i].Calls(nativeCapacity)) continue;
                if (i == 0 || codes[i - 1].opcode != OpCodes.Ldfld || !Equals(codes[i - 1].operand, thingDef))
                    throw new InvalidOperationException("[KanbanStockpile] Reworked capacity call no longer loads Thing.def directly.");
                codes[i - 1].opcode = OpCodes.Nop;
                codes[i - 1].operand = null;
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = AccessTools.Method(typeof(PickUpAndHaulReworked), nameof(CapacityAt));
            }
            return codes;
        }

        public static int CapacityAt(IntVec3 cell, Map map, Thing thing)
        {
            int native = cell.GetItemStackSpaceLeftFor(map, thing.def);
            if (native <= 0 || !cell.TryGetKanbanSettings(map, out var settings, out var group)) return native;
            return ControlledCapacity(cell, map, thing, planningPawn ?? HolderPawn(thing), null, planningDepth > 0 && thing.Spawned);
        }

        public static Pawn HolderPawn(Thing thing)
        {
            for (IThingHolder holder = thing.ParentHolder; holder != null; holder = holder.ParentHolder)
                if (holder is Pawn pawn) return pawn;
            return null;
        }

        internal static int ControlledCapacity(IntVec3 cell, Map map, Thing thing, Pawn pawn,
            List<PlannedDelivery> local, bool startingTrip)
        {
            int native = cell.GetItemStackSpaceLeftFor(map, thing.def);
            if (!cell.TryGetKanbanSettings(map, out var settings, out var group)) return native;
            int limit = settings.Limit(thing.def);
            long available = 0;
            int occupied = 0;
            var stored = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < stored.Count; i++)
            {
                if (!stored[i].def.EverStorable(false)) continue;
                occupied++;
                if (!stored[i].CanStackWith(thing)) continue;
                if (startingTrip && settings.srt < 100)
                    RefillCycles.RecordLoad(stored[i], limit);
                available += startingTrip ? RefillCycles.Desired(stored[i], group, settings)
                    : Math.Max(0, limit - stored[i].stackCount);
            }
            var incoming = KanbanStockpile.Settings.ReservedSimilarStackChecking
                ? ReworkedDeliveries.Current?.Incoming(map, pawn) ?? new List<PlannedDelivery>() : new List<PlannedDelivery>();
            if (local != null) incoming.AddRange(local);
            int promisedHere = 0, newElsewhere = 0, otherSlotsHere = 0, similarSlotsHere = 0;
            var totals = new Dictionary<(IntVec3, ThingDef, ThingDef, bool, int), (long count, Thing template)>();
            foreach (var delivery in incoming)
            {
                if (delivery.map != map || delivery.cell.GetSlotGroup(map) != group) continue;
                bool compatible = delivery.CanMerge(thing);
                var key = (delivery.cell, delivery.def, delivery.stuff, compatible, compatible ? 0 : delivery.sourceId);
                totals.TryGetValue(key, out var amount);
                totals[key] = (amount.count + delivery.count, delivery.source);
            }
            foreach (var entry in totals)
            {
                var (at, def, stuff, compatible, _) = entry.Key;
                var template = entry.Value.template;
                bool similar = def == thing.def && (stuff == thing.Stuff
                    || def.stackLimit == 1 && KanbanStockpile.Settings.ConsiderDifferentMaterialSimilar);
                long merge = 0;
                var contents = map.thingGrid.ThingsListAt(at);
                foreach (var item in contents)
                    if (compatible ? item.CanStackWith(thing) : template != null && !template.Destroyed && item.CanStackWith(template))
                        merge += Math.Max(0, settings.Limit(def) - item.stackCount);
                int slots = (int)Math.Min(int.MaxValue, (Math.Max(0, entry.Value.count - merge) + settings.Limit(def) - 1) / settings.Limit(def));
                if (at == cell)
                {
                    if (compatible) promisedHere = (int)Math.Min(int.MaxValue, entry.Value.count);
                    else
                    {
                        otherSlotsHere += slots;
                        if (similar) similarSlotsHere += slots;
                    }
                }
                else if (similar) newElsewhere += slots;
            }
            int storedCount = KSUtil.CountStoredSimilarStacks(group, map, thing, settings.ssl, settings);
            int vanillaSlots = 0;
            if (KanbanStockpile.Settings.ReservedSimilarStackChecking && map.reservationManager != null)
            {
                vanillaSlots = ReservationIndex.For(map.reservationManager).CountNewStacks(group, map, thing, settings.ssl, cell);
                promisedHere += ReservationIndex.For(map.reservationManager).Incoming(group, map, cell, thing, pawn?.CurJob);
            }
            int free = Math.Min(Math.Max(0, cell.GetMaxItemsAllowedInCell(map) - occupied - otherSlotsHere),
                Math.Max(0, settings.ssl - storedCount - newElsewhere - similarSlotsHere - vanillaSlots));
            available += (long)free * limit;
            return (int)Math.Min(native, Math.Max(0, available - promisedHere));
        }

        // Merge targeting bypasses the native capacity helper; its vanilla
        // fallback also skips HaulToStorageJob. Both need separate postfixes.
        public static void MergePostfix(Pawn pawn, Thing thing, ref LocalTargetInfo target, ref int capacity, ref bool __result)
        {
            if (!__result || pawn == null || target.HasThing || !target.Cell.TryGetKanbanSettings(pawn.Map, out _, out _)) return;
            capacity = Math.Min(capacity, ControlledCapacity(target.Cell, pawn.Map, thing, pawn, null, true));
            if (capacity <= 0) { target = LocalTargetInfo.Invalid; __result = false; }
        }

        public static void MergeFallbackPostfix(Pawn pawn, Thing thing, ref Job __result)
        {
            if (__result == null || IsHaulingJob(__result)) return;
            HaulAIUtility_HaulToStorageJob_Patch.Postfix(ref __result, pawn, thing);
        }
    }
}
