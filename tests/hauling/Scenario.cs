using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace WorkspaceDiagnostics;

public static partial class Diagnostics
{
    internal static bool HaulingChecks => GenCommandLine.CommandLineArgPassed("RefilledHaulingChecks");
    static partial void StartProject(ref bool pending)
    {
        if (!HaulingChecks) return;
        pending = true;
        StartQuickTest();
    }
    internal static void HaulingFinished() => Finish();
}

// Real map, pathfinder, work giver, reservations, pickup and unloading drivers.
// Optional mods must have loaded their types; a package ID alone is insufficient.
public sealed class HaulingChecks : GameComponent
{
    Map map;
    ISlotGroupParent storage;
    Pawn first, second;
    Thing stored, source, other;
    readonly List<Thing> sources = new();
    WorkGiver_Scanner giver;
    int storageKind, phase, ticks, started, settled, materialIndex;
    bool finished, reworked;
    string batchName, label;
    IntVec3 center;
    Type adaptive;
    ThingDef[] materials;
    ThingDef Material => materials[materialIndex];

    public HaulingChecks(Game game) { }
    public override void StartedNewGame()
    {
        if (Diagnostics.HaulingChecks) Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
    }

    void Require(string name, bool pass)
    {
        Diagnostics.Results.Add(new { name = label + ": " + name, pass });
        if (!pass) throw new InvalidOperationException(name);
    }

    void Configure(int threshold, int similar, int size)
    {
        Type type = AccessTools.TypeByName("KanbanStockpile.KanbanSettings");
        object value = Activator.CreateInstance(type);
        type.GetField("srt").SetValue(value, threshold);
        type.GetField("ssl").SetValue(value, similar);
        type.GetField("mss").SetValue(value, size);
        AccessTools.Method(AccessTools.TypeByName("KanbanStockpile.State"), "Set")
            .Invoke(null, new[] { storage.GetStoreSettings(), value });
    }

    Thing Spawn(ThingDef def, IntVec3 cell, int count)
    {
        var thing = ThingMaker.MakeThing(def);
        thing.stackCount = count;
        return GenSpawn.Spawn(thing, cell, map);
    }

    Thing Source(ThingDef def, int x, int count)
    {
        var thing = Spawn(def, center + new IntVec3(x, 0, 10), count);
        sources.Add(thing);
        return thing;
    }

    Pawn Hauler(int offset)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, developmentalStages: DevelopmentalStage.Adult,
                fixedBiologicalAge: 30, fixedChronologicalAge: 30));
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) continue;
            pawn.inventory.innerContainer.ClearAndDestroyContents();
            pawn.equipment.DestroyAllEquipment();
            foreach (var apparel in pawn.apparel.WornApparel.ToList()) apparel.Destroy();
            pawn.workSettings.EnableAndInitialize();
            foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, work == WorkTypeDefOf.Hauling ? 1 : 0);
            GenSpawn.Spawn(pawn, center + new IntVec3(offset, 0, 7), map);
            // Keep background work from changing observations between phases.
            pawn.drafter.Drafted = true;
            return pawn;
        }
        throw new InvalidOperationException("No capable hauler generated");
    }

    int Count(ThingDef def) => storage.GetSlotGroup().HeldThings.Where(t => t.def == def).Sum(t => t.stackCount);
    int Incoming(Thing thing)
    {
        var type = AccessTools.TypeByName("KanbanStockpile.ReworkedDeliveries");
        var component = AccessTools.PropertyGetter(type, "Current").Invoke(null, null);
        return (int)AccessTools.Method(type, "IncomingQuantity").Invoke(component,
            new object[] { map, storage.GetSlotGroup().CellsList[0], thing, null });
    }
    int Cargo(Pawn pawn)
    {
        int count = pawn.carryTracker.CarriedThing?.stackCount ?? 0;
        // Colonists may keep a meal for themselves. Count cargo eligible for
        // unloading under the hauling mod's own retention rules.
        if (reworked)
        {
            var hauled = AccessTools.Method(AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Api.V1"), "GetHauledCount");
            var allowance = AccessTools.Method(AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Compatibility.NativeInventoryRetention"), "GetAllowance");
            var required = AccessTools.Method(AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Compatibility.InventoryRetention"), "GetRequiredCount");
            foreach (var thing in pawn.inventory.innerContainer)
            {
                object[] args = { pawn, thing };
                count += Math.Min((int)hauled.Invoke(null, args), Math.Min((int)allowance.Invoke(null, args),
                    Math.Max(0, thing.stackCount - (int)required.Invoke(null, args))));
            }
        }
        else if (batchName != null)
        {
            var comp = pawn.AllComps.Single(c => c.GetType().FullName == "PickUpAndHaul.CompHauledToInventory");
            var hauled = (HashSet<Thing>)AccessTools.Method(comp.GetType(), "GetHashSet").Invoke(comp, null);
            foreach (var thing in pawn.inventory.innerContainer)
                if (hauled.Contains(thing)) count += thing.stackCount;
        }
        return count;
    }
    Job Job(Pawn pawn, Thing thing)
    {
        bool drafted = pawn.Drafted;
        pawn.drafter.Drafted = false;
        try { return giver.JobOnThing(pawn, thing, true); }
        finally { pawn.drafter.Drafted = drafted; }
    }
    void Start(Job job)
    {
        first.drafter.Drafted = false;
        first.jobs.StartJob(job, JobCondition.InterruptForced);
        started = ticks;
        settled = 0;
    }

    void Reset()
    {
        first.drafter.Drafted = second.drafter.Drafted = true;
        first.jobs.StopAll();
        second.jobs.StopAll();
        Require("previous delivery leaves no unloadable cargo", Cargo(first) == 0 && Cargo(second) == 0);
        foreach (var thing in storage.GetSlotGroup().HeldThings.ToList()) thing.Destroy();
        foreach (var thing in sources) if (!thing.Destroyed) thing.Destroy();
        sources.Clear();
    }

    void Begin()
    {
        reworked = ModLister.GetActiveModWithIdentifier("dreadarm.pickupandhaul") != null;
        bool legacy = new[] { "mehni.pickupandhaul", "mlie.pickupandhaul", "zzyxzz.pickupandhaulreworked" }
            .Any(id => ModLister.GetActiveModWithIdentifier(id) != null);
        batchName = reworked ? "Dreadarm_PickUpAndHaul_Batch" : legacy ? "HaulToInventory" : null;
        giver = reworked ? (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("Dreadarm_PickUpAndHaul_BatchHaul").Worker
            : legacy ? (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker : new WorkGiver_HaulGeneral();
        adaptive = AccessTools.TypeByName("AdaptiveStorage.ThingClass");
        materials = new[] { ThingDefOf.Cloth, DefDatabase<ThingDef>.GetNamed("Hay"), ThingDefOf.MealSimple };
        label = "loaded mods";
        if (legacy) Require("legacy inventory work giver is loaded", giver.GetType().FullName == "PickUpAndHaul.WorkGiver_HaulToInventory");
        if (ModLister.GetActiveModWithIdentifier("adaptive.storage.framework") != null)
            Require("Adaptive Storage assembly is loaded", adaptive != null);
        map = Find.CurrentMap;
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToList()) pawn.drafter.Drafted = true;
        foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
        center = map.Center + new IntVec3(25, 0, 0);
        foreach (var cell in CellRect.CenteredOn(center, 20))
        {
            foreach (var thing in cell.GetThingList(map).ToList())
                if (thing is not Pawn)
                {
                    if (thing.def.destroyable) thing.Destroy();
                    else thing.DeSpawn();
                }
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
        }
        first = Hauler(0);
        second = Hauler(2);
        BeginStorage();
    }

    void BeginStorage()
    {
        label = storageKind == 0 ? "stockpile" : storageKind == 1 ? "vanilla shelf" : "Adaptive Storage";
        if (storageKind == 0)
        {
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            for (int i = 0; i < 3; i++) zone.AddCell(center + new IntVec3(i, 0, 0));
            storage = zone;
        }
        else
        {
            var def = ThingDefOf.Shelf;
            if (storageKind == 2)
            {
                // Use the installed framework's real building class with the
                // game's shelf definition; no mock capacity or path patches.
                def = (ThingDef)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(def, null);
                def.defName = "RefilledTestAdaptiveShelf";
                def.thingClass = adaptive;
                DefDatabase<ThingDef>.Add(def);
            }
            var shelf = (Building_Storage)ThingMaker.MakeThing(def, ThingDefOf.WoodLog);
            shelf.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(shelf, center, map);
            storage = shelf;
            Require("building has multiple stack slots per cell", shelf.MaxItemsInCell >= 3);
            if (storageKind == 2)
            {
                Require("real Adaptive Storage building spawned", shelf.GetType() == adaptive);
                var capacity = AccessTools.Method(AccessTools.TypeByName("KanbanStockpile.StorageControls"), "Capacity");
                int original = (int)capacity.Invoke(null, new object[] { shelf });
                var slotLimit = AccessTools.Property(adaptive, "CurrentSlotLimit");
                slotLimit.SetValue(shelf, original - 1);
                Require("slider capacity follows Adaptive Storage's adjustable slot limit",
                    (int)capacity.Invoke(null, new object[] { shelf }) == original - 1);
                slotLimit.SetValue(shelf, original);
            }
        }
        storage.GetStoreSettings().filter.SetDisallowAll();
        storage.GetStoreSettings().filter.SetAllow(ThingDefOf.Steel, true);
        foreach (var material in materials) storage.GetStoreSettings().filter.SetAllow(material, true);
        storage.GetStoreSettings().Priority = StoragePriority.Critical;
        Configure(100, 0, 0);
        source = Source(ThingDefOf.Steel, 0, 20);
        other = Source(ThingDefOf.Steel, 2, 20);
        Job job = Job(first, source);
        Require("Off provides a hauling job", job != null);
        if (batchName != null)
        {
            Require("Off keeps native inventory job", job.def.defName == batchName);
            Require("Off batches two sources", job.targetQueueA?.Count >= 2);
            Require("Off plans both 20-steel stacks", job.countQueue.Sum() == 40);
        }
        Start(job);
        phase = 1;
    }

    void BeginLimited()
    {
        Require("Off completes real pickup and unloading", Count(ThingDefOf.Steel) == 40);
        Reset();
        Configure(50, 1, 60);
        stored = Spawn(ThingDefOf.Steel, storage.GetSlotGroup().CellsList[0], 60);
        source = Source(ThingDefOf.Steel, 0, 15);
        other = Source(ThingDefOf.Steel, 2, 75);
        Require("full storage refuses hauling", Job(first, source) == null);
        stored.stackCount = 40;
        Require("40 of 60 waits above the 50% threshold", Job(first, source) == null);
        stored.stackCount = 30;
        Job job = Job(first, source);
        Require("threshold offers a refill", job != null);
        if (batchName != null)
        {
            Require("limited storage keeps native inventory batching", job.def.defName == batchName);
            Require("two sources share the remaining 30 capacity", job.targetQueueA.Count >= 2 && job.countQueue.Sum() == 30);
        }
        else Require("ordinary refill count is capped", job.count <= 30);
        Start(job);
        var competing = Job(second, other);
        Require("competing pawn cannot overfill committed capacity", competing == null || batchName == null && competing.count <= 15);
        if (batchName != null)
        {
            Require("batch commits the 30 incoming steel before pickup", Incoming(source) == 30);
            Require("ordinary haulers also respect the committed batch",
                new WorkGiver_HaulGeneral().JobOnThing(second, other, true) == null);
            first.drafter.Drafted = true;
            first.jobs.StopAll();
            Require("cancelling an uncollected batch releases its promises", Incoming(source) == 0);
            job = Job(first, source);
            Require("cancelled space is available to another inventory batch", job?.def.defName == batchName && job.countQueue.Sum() == 30);
            Start(job);
        }
        phase = 2;
    }

    void BeginStacks()
    {
        if (phase == 2)
        {
            Require("refill finishes at exactly 60 in one similar stack", Count(ThingDefOf.Steel) == 60
                && storage.GetSlotGroup().HeldThings.Count(t => t.def == ThingDefOf.Steel) == 1);
            if (batchName != null) Require("completed refill releases all destination promises", Incoming(other) == 0);
        }
        Reset();
        Configure(100, 2, 0);
        source = Source(Material, 0, Material.stackLimit);
        other = Source(Material, 2, Material.stackLimit);
        Source(Material, 4, Material.stackLimit);
        var job = Job(first, source);
        Require(Material.defName + " two-stack limit offers a job", job != null);
        if (batchName != null)
            Require("native inventory hauling batches two " + Material.defName + " stacks in one trip", job.def.defName == batchName
                && job.targetQueueA.Count >= 2 && job.countQueue.Sum() == Material.stackLimit * 2);
        Start(job);
        phase = 3;
    }

    void FinishStorage()
    {
        materialIndex = 0;
        Reset();
        if (storage is Zone zone) zone.Delete(); else ((Thing)storage).Destroy();
        if (++storageKind < (adaptive != null ? 3 : 2)) BeginStorage();
        else { finished = true; Diagnostics.HaulingFinished(); }
    }

    public override void GameComponentTick()
    {
        if (!Diagnostics.HaulingChecks || finished) return;
        try
        {
            ticks++;
            if (phase == 0) { Begin(); return; }
            // Keep eating from consuming the meal stacks being measured.
            if (first.needs?.food != null) first.needs.food.CurLevelPercentage = 1f;
            if (second.needs?.food != null) second.needs.food.CurLevelPercentage = 1f;
            if (first.needs?.mood != null) first.needs.mood.CurLevelPercentage = 1f;
            if (second.needs?.mood != null) second.needs.mood.CurLevelPercentage = 1f;
            if (ticks - started > 10000) throw new TimeoutException($"Phase {phase}, steel={Count(ThingDefOf.Steel)}, material={Material}:{Count(Material)}, cargo={Cargo(first)}, inventory={string.Join(",", first.inventory.innerContainer.Select(t => t.ToString()))}, carried={first.carryTracker.CarriedThing}, job={first.CurJob}");
            if (phase == 2 && Count(ThingDefOf.Steel) > 60)
                throw new InvalidOperationException($"Overfilled steel: {Count(ThingDefOf.Steel)}, job={first.CurJob}, cargo={Cargo(first)}");
            if (phase == 2 && batchName != null && Count(ThingDefOf.Steel) + Incoming(other) > 60)
                throw new InvalidOperationException("Batch promises count delivered cargo twice");
            if (phase == 3 && (Count(Material) > Material.stackLimit * 2 || storage.GetSlotGroup().HeldThings.Count(t => t.def == Material) > 2))
                throw new InvalidOperationException("Exceeded similar-stack limit");
            int expected = phase == 1 ? 40 : phase == 2 ? 60 : Material.stackLimit * 2;
            var def = phase == 3 ? Material : ThingDefOf.Steel;
            if (Count(def) != expected || Cargo(first) != 0) { settled = 0; return; }
            if (++settled < 120) return;
            if (phase == 1) BeginLimited();
            else if (phase == 2) BeginStacks();
            else if (phase == 3)
            {
                Require(Material.defName + " two-stack limit survives completed hauling", true);
                if (++materialIndex < materials.Length) { BeginStacks(); return; }
                FinishStorage();
            }
        }
        catch (Exception ex)
        {
            finished = true;
            Diagnostics.Results.Add(new { name = label + ": live hauling", pass = false, error = ex.ToString() });
            Diagnostics.HaulingFinished();
        }
    }
}
