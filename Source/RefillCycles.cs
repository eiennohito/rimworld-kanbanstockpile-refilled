using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KanbanStockpile
{
    sealed class RefillCycle
    {
        public bool active;
        public int limit;
        public int threshold;
        public int minimum;
        public StorageSettings storage;
        public int revision;
    }

    public static class RefillCycles
    {
        static readonly ConditionalWeakTable<Thing, RefillCycle> cycles = new();

        public static int MinimumDelivery(int limit, int threshold, int carryingCapacity)
        {
            if (threshold >= 100) return 1;
            int gap = limit - (int)((long)limit * threshold / 100);
            return Math.Max(1, Math.Min((int)Math.Ceiling(Math.Min(limit, carryingCapacity) / 4.0),
                Math.Min((int)Math.Ceiling(limit * 0.15), Math.Max(1, gap / 2))));
        }

        public static int Desired(Thing thing, SlotGroup group, KanbanSettings settings)
        {
            int limit = settings.Limit(thing.def);
            if (!settings.Enabled || settings.srt >= 100) return Math.Max(0, limit - thing.stackCount);
            var cycle = cycles.GetValue(thing, _ => new RefillCycle());
            int revision = State.Revision(group.Settings);
            if (cycle.storage != group.Settings || cycle.revision != revision
                || cycle.limit != limit || cycle.threshold != settings.srt)
            {
                // A fresh runtime has no cycle history. Refill partial stacks
                // once, lazily, then use the low mark for subsequent cycles.
                cycle.active = cycle.storage == null;
                cycle.storage = group.Settings;
                cycle.revision = revision;
                cycle.limit = limit;
                cycle.threshold = settings.srt;
                cycle.minimum = MinimumDelivery(limit, settings.srt, limit);
            }
            int trigger = (int)((long)limit * settings.srt / 100);
            int remaining = Math.Max(0, limit - thing.stackCount);
            if (thing.stackCount <= trigger) cycle.active = true;
            if (remaining < cycle.minimum) cycle.active = false;
            return cycle.active ? remaining : 0;
        }

        internal static void RecordLoad(Thing thing, int carryingCapacity)
        {
            if (cycles.TryGetValue(thing, out var cycle) && cycle.active)
                cycle.minimum = MinimumDelivery(cycle.limit, cycle.threshold, carryingCapacity);
        }

        public static void Arrived(Thing thing, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || !thing.Spawned || !thing.def.EverStorable(false)) return;
            if (!thing.Position.TryGetKanbanSettings(thing.Map, out var settings, out var group)) return;
            Desired(thing, group, settings);
            if (settings.srt < 100 && thing.stackCount < settings.Limit(thing.def)
                && cycles.TryGetValue(thing, out var cycle)) cycle.active = true;
            Observe(thing);
        }

        public static void Observe(Thing thing)
        {
            if (cycles.TryGetValue(thing, out var cycle)
                && cycle.limit - thing.stackCount < cycle.minimum)
                cycle.active = false;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    static class RefillStackArrived
    {
        static void Postfix(Thing __instance, bool respawningAfterLoad) => RefillCycles.Arrived(__instance, respawningAfterLoad);
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TryAbsorbStack))]
    static class RefillStackDelivered
    {
        static void Postfix(Thing __instance) => RefillCycles.Observe(__instance);
    }
}
