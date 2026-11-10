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
        private static AccessTools.FieldRef<object, object> planTally;
        [ThreadStatic] private static LocalTargetInfo pickupMergeTarget;
        private static AccessTools.FieldRef<object, List<LocalTargetInfo>> planTargets;
        private static Func<Job, bool> isHaulingJob;
        private static Func<Pawn, Thing, int> hauledCount;
        private static JobDef batchDef;
        public static Pawn QueryPawn(Thing thing) => planningPawn ?? InventoryHauling.PlanningPawn ?? HolderPawn(thing);
        public static bool IsBatchJob(Job job) => job != null && batchDef != null && job.def == batchDef || InventoryHauling.IsBatchJob(job);
        public static int HauledCount(Pawn pawn, Thing thing) => Math.Min(thing.stackCount, Math.Max(0, hauledCount?.Invoke(pawn, thing) ?? InventoryHauling.HauledCount(pawn, thing)));
        private static readonly MethodInfo nativeCapacity = AccessTools.Method(typeof(GridsUtility),
            nameof(GridsUtility.GetItemStackSpaceLeftFor));
        private static readonly FieldInfo thingDef = AccessTools.Field(typeof(Thing), nameof(Thing.def));

        public static bool IsHaulingJob(Job job)
        {
            return isHaulingJob != null && isHaulingJob(job) || InventoryHauling.IsHaulingJob(job);
        }

        public static void Reset()
        {
            isHaulingJob = null;
            hauledCount = null;
            batchDef = null;
            planTargets = null;
            planTally = null;
        }

        private static List<PlannedDelivery> Deliveries(object plan) => allocations.GetOrCreateValue(planTally(plan));

        public static void ApplyPatch(Harmony harmony)
        {
            Type work = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.HaulingWork");
            Type plan = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.BatchPickupPlan");
            Type api = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Api.V1");
            Type tally = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.StorageTally");
            if (work == null || plan == null || api == null || tally == null)
                throw new MissingMemberException("[KanbanStockpile] Unsupported Pick Up And Haul Reworked API.");

            var classify = AccessTools.Method(api, "IsHaulingJob", new[] { typeof(Job) });
            if (classify == null || classify.ReturnType != typeof(bool))
                throw new MissingMethodException("[KanbanStockpile] Reworked IsHaulingJob API is missing.");
            isHaulingJob = (Func<Job, bool>)Delegate.CreateDelegate(typeof(Func<Job, bool>), classify);
            hauledCount = (Func<Pawn, Thing, int>)Delegate.CreateDelegate(typeof(Func<Pawn, Thing, int>), AccessTools.Method(api, "GetHauledCount"));
            batchDef = DefDatabase<JobDef>.GetNamed("Dreadarm_PickUpAndHaul_Batch");
            planTally = AccessTools.FieldRefAccess<object>(plan, "tally");
            planTargets = AccessTools.FieldRefAccess<List<LocalTargetInfo>>(plan, "<Targets>k__BackingField");

            var scopeMethods = new[] { AccessTools.Method(work, "PrimaryPlan"), AccessTools.Method(work, "BatchJob") };
            var canHaul = AccessTools.Method(work, "CanHaul", new[] { typeof(Pawn), typeof(Thing), typeof(bool),
                typeof(Job), typeof(LocalTargetInfo).MakeByRefType(), typeof(int).MakeByRefType() });
            var take = AccessTools.Method(tally, "Take");
            if (scopeMethods.Any(m => m == null) || canHaul == null || take == null)
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
                    postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(PickupPlanned)),
                    finalizer: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(EndPlanning)));
            harmony.Patch(AccessTools.Method(work, "BatchJob"), postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(BatchCreated)));
            harmony.Patch(take, prefix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(Take)));
            harmony.Patch(AccessTools.Method(plan, "ReserveCarriedCargo"),
                prefix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(PlanStarted)));
            Type driver = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.JobDriver_BatchHaul");
            harmony.Patch(AccessTools.Method(driver, "TryMakePreToilReservations"),
                postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(Reserved)));
            harmony.Patch(AccessTools.Method(driver, "TryExtendBatch"),
                prefix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(BeginExtension)),
                postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(Extended)),
                finalizer: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(EndPlanning)));
            foreach (var method in capacityMethods)
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(CapacityTranspiler)));
            harmony.Patch(canHaul, postfix: new HarmonyMethod(typeof(PickUpAndHaulReworked), nameof(MergePostfix)));
            Log.Message("[KanbanStockpile] Patched Pick Up And Haul Reworked planning and delivery capacity ("
                + capacityMethods.Count + " methods).");
        }

        public sealed class PlanningState
        {
            internal int depth;
            internal Pawn pawn;
            internal object plan;
            internal LocalTargetInfo mergeTarget;
        }

        public static void BeginPlanning(Pawn pawn, out PlanningState __state)
        {
            __state = new PlanningState { depth = planningDepth, pawn = planningPawn, plan = activePlan, mergeTarget = pickupMergeTarget };
            pickupMergeTarget = LocalTargetInfo.Invalid;
            planningDepth++;
            planningPawn = pawn;
            if (__state.depth == 0) activePlan = null;
        }

        public static Exception EndPlanning(Exception __exception, PlanningState __state)
        {
            planningDepth = __state.depth;
            planningPawn = __state.pawn;
            pickupMergeTarget = __state.mergeTarget;
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
            var deliveries = Deliveries(activePlan);
            if (!UsefulBatch(__result, deliveries)) { __result = null; return; }
            jobPlans.Remove(__result);
            jobPlans.Add(__result, deliveries);
        }

        // Judge the completed batch, rather than each small source separately:
        // several fragments can make one useful trip, as can other supplies
        // already included in the same route.
        internal static bool UsefulBatch(Job job, List<PlannedDelivery> deliveries)
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
                ReworkedDeliveries.Current?.Commit(__instance.pawn, Deliveries(activePlan));
        }

        // Rewrites native capacity calls in HaulingWork.Storage and CellCapacity:
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

        // CanHaul now derives merges from WorkGiver_Merge instead of a custom
        // target finder. Clamp before TryPlanPickup records its tally quantity.
        public static void MergePostfix(Pawn pawn, Thing thing, ref LocalTargetInfo mergeTarget, ref int mergeCount, ref bool __result)
        {
            if (!__result || pawn == null || !mergeTarget.IsValid || mergeTarget.HasThing
                || !mergeTarget.Cell.TryGetKanbanSettings(pawn.Map, out _, out _)) return;
            mergeCount = Math.Min(mergeCount, ControlledCapacity(mergeTarget.Cell, pawn.Map, thing, pawn,
                activePlan == null ? null : Deliveries(activePlan), true));
            if (mergeCount <= 0) { mergeTarget = LocalTargetInfo.Invalid; __result = false; }
            else pickupMergeTarget = mergeTarget;
        }

        public static void PickupPlanned(Pawn pawn, Thing thing, object tally, int count, bool __result)
        {
            if (!__result || tally == null || !pickupMergeTarget.IsValid) return;
            allocations.GetOrCreateValue(tally).Add(Delivery(pawn, thing, pickupMergeTarget.Cell, count));
        }

        private static PlannedDelivery Delivery(Pawn pawn, Thing thing, IntVec3 cell, int count) => new()
        {
            map = pawn.Map, cell = cell, def = thing.def, stuff = thing.Stuff,
            source = thing, sourceId = thing.thingIDNumber, count = count
        };

        public static void PlanStarted(object __instance) => activePlan = __instance;

        // Reworked moved cell allocation from BatchPickupPlan.Fill to
        // StorageTally.Take. Keep cell promises per tally, shared by all sources.
        public static bool Take(object __instance, Pawn pawn, Thing thing, IHaulDestination destination, int count, ref int __result)
        {
            if (destination is not ISlotGroupParent parent) return true;
            var group = parent.GetSlotGroup();
            if (group == null || !State.Get(group.Settings).Enabled) return true;
            var local = allocations.GetOrCreateValue(__instance);
            __result = 0;
            foreach (var cell in group.StorageGroup?.CellsList ?? group.CellsList)
            {
                if (__result >= count) break;
                if (thing.Spawned && cell == thing.Position) continue;
                if (!StoreUtility.IsGoodStoreCell(cell, pawn.Map, thing, pawn, pawn.Faction)) continue;
                int amount = Math.Min(count - __result,
                    ControlledCapacity(cell, pawn.Map, thing, pawn, local, thing.Spawned));
                if (amount <= 0) continue;
                local.Add(Delivery(pawn, thing, cell, amount));
                __result += amount;
            }
            return false;
        }
    }
}
