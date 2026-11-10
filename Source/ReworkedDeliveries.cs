using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace KanbanStockpile
{
    public sealed class PlannedDelivery : IExposable
    {
        public Map map;
        public IntVec3 cell;
        public ThingDef def;
        public ThingDef stuff;
        public Thing source;
        public int sourceId;
        public int count;

        public bool Matches(Thing thing) => thing.def == def && thing.Stuff == stuff;
        public bool CanMerge(Thing thing) => source != null && !source.Destroyed
            && (source == thing || source.CanStackWith(thing));

        public void ExposeData()
        {
            Scribe_References.Look(ref map, "map");
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref stuff, "stuff");
            var reference = source?.Destroyed == true ? null : source;
            Scribe_References.Look(ref reference, "source");
            if (Scribe.mode != LoadSaveMode.Saving) source = reference;
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref sourceId, "sourceId");
        }
    }

    public sealed class ReworkedDeliveryPlan : IExposable
    {
        public Pawn pawn;
        public List<PlannedDelivery> deliveries = new();

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Collections.Look(ref deliveries, "deliveries", LookMode.Deep);
        }
    }

    // Destination promises survive the transition from queued pickups to cargo
    // and unloading. Quantities are bounded by live jobs and PUAH-owned cargo;
    // cancellation, failed pickups and completed deliveries release promises.
    public sealed class ReworkedDeliveries : GameComponent
    {
        List<ReworkedDeliveryPlan> plans = new();
        public ReworkedDeliveries(Game game) { }
        public static ReworkedDeliveries Current => Verse.Current.Game?.GetComponent<ReworkedDeliveries>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref plans, "refilledDeliveries", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) plans ??= new();
        }

        public void Commit(Pawn pawn, List<PlannedDelivery> deliveries)
        {
            plans.RemoveAll(p => p.pawn == pawn);
            if (deliveries.Count > 0) plans.Add(new ReworkedDeliveryPlan { pawn = pawn, deliveries = deliveries });
        }

        public bool Tracks(Pawn pawn) => plans.Exists(p => p.pawn == pawn);

        internal List<PlannedDelivery> ForPawn(Pawn pawn)
        {
            Incoming(pawn.MapHeld, null);
            var plan = plans.Find(p => p.pawn == pawn);
            return plan == null ? new List<PlannedDelivery>() : plan.deliveries.Select(d => new PlannedDelivery
            {
                map = d.map, cell = d.cell, def = d.def, stuff = d.stuff,
                source = d.source, sourceId = d.sourceId, count = d.count
            }).ToList();
        }

        public int IncomingQuantity(Map map, IntVec3 cell, Thing thing, Pawn exclude)
        {
            long count = 0;
            foreach (var delivery in Incoming(map, exclude))
                if (delivery.cell == cell && delivery.Matches(thing)) count += delivery.count;
            return (int)Math.Min(int.MaxValue, count);
        }

        public int NewStacks(Map map, SlotGroup group, Thing thing, Pawn exclude)
        {
            var totals = new Dictionary<(IntVec3 cell, bool compatible, int variant), long>();
            foreach (var delivery in Incoming(map, exclude))
                if (delivery.cell.GetSlotGroup(map) == group && delivery.def == thing.def
                    && (delivery.stuff == thing.Stuff || thing.def.stackLimit == 1 && KanbanStockpile.Settings.ConsiderDifferentMaterialSimilar))
                {
                    bool compatible = delivery.CanMerge(thing);
                    var key = (delivery.cell, compatible, compatible ? 0 : delivery.sourceId);
                    totals.TryGetValue(key, out long amount);
                    totals[key] = amount + delivery.count;
                }
            int limit = State.Get(group.Settings).Limit(thing.def);
            long count = 0;
            foreach (var entry in totals)
            {
                long merge = 0;
                foreach (var item in map.thingGrid.ThingsListAt(entry.Key.cell))
                    if (entry.Key.compatible && item.CanStackWith(thing)) merge += Math.Max(0, limit - item.stackCount);
                count += (Math.Max(0, entry.Value - merge) + limit - 1) / limit;
            }
            return (int)Math.Min(int.MaxValue, count);
        }

        static int Pending(Job job, PlannedDelivery delivery)
        {
            if (!PickUpAndHaulReworked.IsBatchJob(job)) return 0;
            long count = 0;
            if (job.targetA.Thing is Thing source && delivery.Matches(source) && source.Spawned
                && (job.targetQueueA == null || !job.targetQueueA.Contains(job.targetA)))
                count += Math.Min(Math.Max(0, job.count), source.stackCount);
            if (job.targetQueueA != null && job.countQueue != null)
                for (int i = 0; i < Math.Min(job.targetQueueA.Count, job.countQueue.Count); i++)
                {
                    source = job.targetQueueA[i].Thing;
                    if (source != null && source.Spawned && delivery.Matches(source))
                        count += Math.Min(Math.Max(0, job.countQueue[i]), source.stackCount);
                }
            return (int)Math.Min(int.MaxValue, count);
        }

        static int Remaining(Pawn pawn, PlannedDelivery delivery)
        {
            if (pawn == null || pawn.Destroyed || pawn.MapHeld != delivery.map) return 0;
            long count = Pending(pawn.CurJob, delivery);
            if (pawn.jobs?.jobQueue != null)
                foreach (var queued in pawn.jobs.jobQueue) count += Pending(queued.job, delivery);
            var inventory = pawn.inventory?.innerContainer;
            if (inventory != null)
                foreach (var thing in inventory)
                    if (delivery.Matches(thing)) count += PickUpAndHaulReworked.HauledCount(pawn, thing);
            var carried = pawn.carryTracker?.CarriedThing;
            if (carried != null && delivery.Matches(carried)) count += PickUpAndHaulReworked.HauledCount(pawn, carried);
            return (int)Math.Min(int.MaxValue, count);
        }

        public List<PlannedDelivery> Incoming(Map map, Pawn exclude)
        {
            var result = new List<PlannedDelivery>();
            for (int i = plans.Count - 1; i >= 0; i--)
            {
                var plan = plans[i];
                if (plan.pawn == exclude) continue;
                // Read live quantities once per item kind, then distribute that
                // budget across its destinations. Never multiply cargo by cells.
                var budgets = new Dictionary<(ThingDef, ThingDef), int>();
                foreach (var delivery in plan.deliveries)
                {
                    var key = (delivery.def, delivery.stuff);
                    if (!budgets.TryGetValue(key, out int budget)) budget = Remaining(plan.pawn, delivery);
                    int count = Math.Min(delivery.count, budget);
                    budgets[key] = budget - count;
                    delivery.count = count;
                    if (count > 0 && delivery.map == map && plan.pawn != exclude) result.Add(delivery);
                }
                plan.deliveries.RemoveAll(d => d.count <= 0);
                if (plan.deliveries.Count == 0) plans.RemoveAt(i);
            }
            return result;
        }

        public void Delivered(Pawn pawn, Map map, IntVec3 cell, ThingDef def, ThingDef stuff, int count)
        {
            foreach (var plan in plans)
            {
                if (plan.pawn != pawn) continue;
                foreach (var delivery in plan.deliveries)
                    if (delivery.map == map && delivery.cell == cell && delivery.def == def && delivery.stuff == stuff)
                    {
                        int used = Math.Min(count, delivery.count);
                        delivery.count -= used;
                        count -= used;
                    }
            }
        }
    }

    [HarmonyPatch]
    static class ReworkedDeliveryCompleted
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(Pawn_CarryTracker),
            nameof(Pawn_CarryTracker.TryDropCarriedThing), new[] { typeof(IntVec3), typeof(ThingPlaceMode),
                typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>) });
        public sealed class DropState
        {
            public Thing thing;
            public int count;
            public ThingDef def, stuff;
        }

        static void Prefix(Pawn_CarryTracker __instance, out DropState __state)
        {
            if (!PickUpAndHaulReworked.IsHaulingJob(__instance.pawn.CurJob)
                || ReworkedDeliveries.Current?.Tracks(__instance.pawn) != true) { __state = null; return; }
            var thing = __instance.CarriedThing;
            __state = thing == null ? null : new DropState { thing = thing, count = thing.stackCount, def = thing.def, stuff = thing.Stuff };
        }

        static void Postfix(Pawn_CarryTracker __instance, IntVec3 dropLoc, ThingPlaceMode mode, DropState __state)
        {
            if (__state == null || mode != ThingPlaceMode.Direct) return;
            var left = __instance.CarriedThing;
            int count = __state.count - (left == __state.thing ? left.stackCount : 0);
            if (count > 0) ReworkedDeliveries.Current?.Delivered(__instance.pawn, __instance.pawn.MapHeld,
                dropLoc, __state.def, __state.stuff, count);
        }
    }
}
