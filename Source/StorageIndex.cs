using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KanbanStockpile
{
    sealed class StorageIndex
    {
        static readonly ConditionalWeakTable<SlotGroup, StorageIndex> indices = new();
        static readonly List<Thing> empty = new();
        readonly Dictionary<ThingDef, List<Thing>> byDef = new();
        readonly SlotGroup group;
        bool dirty = true;

        StorageIndex(SlotGroup group) { this.group = group; }
        public static StorageIndex For(SlotGroup group) => indices.GetValue(group, g => new StorageIndex(g));
        public static void Invalidate(SlotGroup group)
        {
            if (group != null && indices.TryGetValue(group, out var index)) index.dirty = true;
        }

        // Cache membership, not quantities: stackCount is a public field and other
        // mods can change it without a notification. Read matching stacks live.
        public List<Thing> Matching(ThingDef def, Map map)
        {
            if (dirty)
            {
                byDef.Clear();
                var cells = group.CellsList;
                for (int i = 0; i < cells.Count; i++)
                {
                    var things = map.thingGrid.ThingsListAt(cells[i]);
                    for (int j = 0; j < things.Count; j++)
                    {
                        var thing = things[j];
                        if (!thing.def.EverStorable(false)) continue;
                        if (!byDef.TryGetValue(thing.def, out var list)) byDef[thing.def] = list = new();
                        list.Add(thing);
                    }
                }
                dirty = false;
            }
            return byDef.TryGetValue(def, out var result) ? result : empty;
        }
    }

    [HarmonyPatch]
    static class StorageMembershipChanged
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ThingGrid), "RegisterInCell");
            yield return AccessTools.Method(typeof(ThingGrid), "DeregisterInCell");
        }

        static void Postfix(Thing t, IntVec3 c, Map ___map)
        {
            if (t.def.EverStorable(false) && ___map.haulDestinationManager != null && c.InBounds(___map))
                StorageIndex.Invalidate(c.GetSlotGroup(___map));
        }
    }

    [HarmonyPatch]
    static class StorageCellsChanged
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SlotGroup), nameof(SlotGroup.Notify_AddedCell));
            yield return AccessTools.Method(typeof(SlotGroup), nameof(SlotGroup.Notify_LostCell));
        }
        static void Postfix(SlotGroup __instance) => StorageIndex.Invalidate(__instance);
    }

    sealed class ReservationIndex
    {
        static readonly ConditionalWeakTable<ReservationManager, ReservationIndex> indices = new();
        static readonly AccessTools.FieldRef<ReservationManager, List<ReservationManager.Reservation>> reservations =
            AccessTools.FieldRefAccess<ReservationManager, List<ReservationManager.Reservation>>("reservations");
        readonly ReservationManager manager;
        readonly List<Job> jobs = new();
        readonly HashSet<Job> seen = new();
        readonly Dictionary<Job, Pawn> claimants = new();
        bool dirty = true;

        ReservationIndex(ReservationManager manager) { this.manager = manager; }
        public static ReservationIndex For(ReservationManager manager) =>
            indices.GetValue(manager, m => new ReservationIndex(m));
        public static void Invalidate(ReservationManager manager)
        {
            if (indices.TryGetValue(manager, out var index)) index.dirty = true;
        }

        void Refresh()
        {
            if (!dirty) return;
            jobs.Clear();
            seen.Clear();
            claimants.Clear();
            var all = reservations(manager);
            for (int i = 0; i < all.Count; i++)
            {
                var job = all[i].Job;
                if (job == null || !seen.Add(job)) continue;
                if (job.def == JobDefOf.HaulToCell || job.def == JobDefOf.HaulToContainer
                    || PickUpAndHaulReworked.IsHaulingJob(job))
                {
                    jobs.Add(job);
                    claimants[job] = all[i].Claimant;
                }
            }
            dirty = false;
        }

        bool ArrivesAt(Job job, SlotGroup group, Map map, Thing thing)
        {
            return (job.targetA.Thing == thing || KSUtil.IsSimilarStack(job.targetA.Thing, thing))
                && KSUtil.TryGetHaulingDestinationOnMap(job, out var destinationMap, out _, out var destination, map)
                && destinationMap == map && destination == group;
        }

        public int CountNewStacks(SlotGroup group, Map map, Thing thing, int max, IntVec3? validatingCell = null)
        {
            Refresh();
            int count = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (PickUpAndHaulReworked.IsHaulingJob(job) && ReworkedDeliveries.Current?.Tracks(claimants[job]) == true) continue;
                if (!ArrivesAt(job, group, map, thing)) continue;
                // HaulToCell reserves before its walking toil rechecks storage.
                // Its own slot must stay valid; other sources and queued jobs
                // still count. Vanilla reserves this source exclusively.
                if (validatingCell.HasValue && job.targetB.Cell == validatingCell.Value
                    && ReferenceEquals(job.targetA.Thing, thing) && claimants[job]?.CurJob == job) continue;
                if (job.targetB.Cell.IsValid && job.targetB.Cell.InBounds(map))
                {
                    int mergeSpace = 0;
                    var stored = map.thingGrid.ThingsListAt(job.targetB.Cell);
                    for (int j = 0; j < stored.Count; j++)
                        if (stored[j].CanStackWith(thing)) mergeSpace = System.Math.Max(mergeSpace,
                            State.Get(group.Settings).Limit(thing.def) - stored[j].stackCount);
                    if (mergeSpace > 0 && Promised(job, thing) <= mergeSpace) continue;
                }
                if (++count >= max) break;
            }
            return count;
        }

        public int Incoming(SlotGroup group, Map map, IntVec3 cell, Thing thing, Job exclude = null)
        {
            Refresh();
            long count = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (PickUpAndHaulReworked.IsHaulingJob(job) && ReworkedDeliveries.Current?.Tracks(claimants[job]) == true) continue;
                if (job == exclude || job.targetB.Cell != cell || !ArrivesAt(job, group, map, thing)) continue;
                count += Promised(job, thing);
            }
            return (int)System.Math.Min(int.MaxValue, count);
        }

        int Promised(Job job, Thing thing)
        {
            long count = System.Math.Max(0, job.count);
            // Vanilla decrements job.count on pickup; include the part that
            // has moved into the carry tracker until delivery completes.
            var pawn = claimants[job];
            var carried = pawn?.CurJob == job ? pawn.carryTracker?.CarriedThing : null;
            if (carried != null && carried.CanStackWith(thing)) count += carried.stackCount;
            if (count == 0 && PickUpAndHaulReworked.IsHaulingJob(job)) count = job.targetA.Thing?.stackCount ?? 0;
            return (int)System.Math.Min(int.MaxValue, count);
        }
    }

    [HarmonyPatch]
    static class ReservationsChanged
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "Reserve", "Release", "ReleaseAllForTarget", "ReleaseClaimedBy", "ReleaseAllClaimedBy" })
                yield return AccessTools.Method(typeof(ReservationManager), name);
        }
        static void Postfix(ReservationManager __instance) => ReservationIndex.Invalidate(__instance);
    }
}
