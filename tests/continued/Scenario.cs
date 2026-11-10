using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace WorkspaceDiagnostics;

public static partial class Diagnostics
{
    internal static bool ContinuedScenario => GenCommandLine.CommandLineArgPassed("RefilledContinuedSimulation");
    static partial void StartProject(ref bool pending)
    {
        if (!ContinuedScenario) return;
        pending = true;
        StartQuickTest();
    }
    internal static void ScenarioCheck(string name, bool pass) => Results.Add(new { name, pass });
    internal static void ScenarioFinished() => Finish();
}

public sealed class ContinuedSimulation : GameComponent
{
    Map map;
    Zone_Stockpile stockpile;
    Pawn first, second;
    Thing source, otherSource, stored, raidCargo;
    WorkGiver_Scanner giver;
    int phase, ticks, phaseStarted, settled;
    bool finished;

    public ContinuedSimulation(Game game) { }

    public override void StartedNewGame()
    {
        if (Diagnostics.ContinuedScenario) Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
    }

    void Require(string name, bool condition)
    {
        Diagnostics.ScenarioCheck(name, condition);
        if (!condition) throw new InvalidOperationException(name);
    }

    void Configure(int threshold, int similar, int size)
    {
        var settings = AccessTools.TypeByName("KanbanStockpile.KanbanSettings");
        object value = Activator.CreateInstance(settings);
        settings.GetField("srt").SetValue(value, threshold);
        settings.GetField("ssl").SetValue(value, similar);
        settings.GetField("mss").SetValue(value, size);
        AccessTools.Method(AccessTools.TypeByName("KanbanStockpile.State"), "Set")
            .Invoke(null, new[] { stockpile.GetStoreSettings(), value });
    }

    Thing Steel(IntVec3 cell, int count)
    {
        Thing thing = ThingMaker.MakeThing(ThingDefOf.Steel);
        thing.stackCount = count;
        return GenSpawn.Spawn(thing, cell, map);
    }

    Pawn Hauler(IntVec3 cell)
    {
        Pawn pawn = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var candidate = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            if (!candidate.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) { pawn = candidate; break; }
        }
        if (pawn == null) throw new InvalidOperationException("Could not generate a pawn capable of hauling");
        pawn.inventory.innerContainer.ClearAndDestroyContents();
        pawn.workSettings.EnableAndInitialize();
        foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            if (!pawn.WorkTypeIsDisabled(work))
                pawn.workSettings.SetPriority(work, work == WorkTypeDefOf.Hauling ? 1 : 0);
        GenSpawn.Spawn(pawn, cell, map);
        return pawn;
    }

    int StoredCount() => stockpile.GetSlotGroup().HeldThings.Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount);
    static int PawnSteel(Pawn pawn) => pawn.inventory.innerContainer.TotalStackCountOfDef(ThingDefOf.Steel)
        + (pawn.carryTracker.CarriedThing?.def == ThingDefOf.Steel ? pawn.carryTracker.CarriedThing.stackCount : 0);

    void Begin()
    {
        Require("Continued 1.7 is loaded", ModLister.GetActiveModWithIdentifier("Zzyxzz.PickUpAndHaulReworked") != null);
        map = Find.CurrentMap;
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToList()) pawn.drafter.Drafted = true;
        foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
        var center = map.Center + new IntVec3(25, 0, 0);
        foreach (var cell in CellRect.CenteredOn(center, 18))
        {
            foreach (var thing in cell.GetThingList(map).ToList())
                if (thing is not Pawn) thing.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
        }
        stockpile = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
        map.zoneManager.RegisterZone(stockpile);
        for (int offset = 0; offset < 3; offset++) stockpile.AddCell(center + new IntVec3(offset, 0, 0));
        var storage = stockpile.GetStoreSettings();
        storage.filter.SetDisallowAll();
        storage.filter.SetAllow(ThingDefOf.Steel, true);
        storage.Priority = StoragePriority.Critical;
        first = Hauler(center + new IntVec3(0, 0, 7));
        second = Hauler(center + new IntVec3(2, 0, 7));
        giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker;
        source = Steel(center + new IntVec3(0, 0, 10), 20);
        otherSource = Steel(center + new IntVec3(2, 0, 10), 20);
        Configure(100, 0, 0);
        Job job = giver.JobOnThing(first, source, true);
        Require("Off keeps Continued's inventory hauling job", job?.def.defName == "HaulToInventory");
        Require("Off batches multiple source stacks", job.targetQueueA?.Count >= 2);
        first.jobs.StartJob(job, JobCondition.InterruptForced);
        phase = 1;
        phaseStarted = ticks;
    }

    void BeginLimited()
    {
        Require("Off completes pickup and unloading in the live simulation", StoredCount() == 40 && PawnSteel(first) == 0);
        first.jobs.StopAll();
        second.jobs.StopAll();
        foreach (var thing in stockpile.GetSlotGroup().HeldThings.ToList()) thing.Destroy(DestroyMode.Vanish);
        var center = stockpile.Cells[0];
        Configure(50, 1, 60);
        stored = Steel(center, 60);
        source = Steel(center + new IntVec3(0, 0, 10), 75);
        otherSource = Steel(center + new IntVec3(2, 0, 10), 75);
        Require("Full controlled storage offers no hauling job", giver.JobOnThing(first, source, true) == null);
        stored.stackCount = 40;
        Require("A waiting controlled stack above 50% stays untouched", giver.JobOnThing(first, source, true) == null);
        stored.stackCount = 30;
        Job job = giver.JobOnThing(first, source, true);
        Require("At the threshold Continued keeps inventory hauling", job?.def.defName == "HaulToInventory");
        Require("The inventory job carries only the missing 30 steel", job.countQueue.Sum() == 30);
        first.jobs.StartJob(job, JobCondition.InterruptForced);
        Job competing = giver.JobOnThing(second, otherSource, true);
        Require("A second pawn cannot reserve an overfilling delivery", competing == null);
        phase = 2;
        phaseStarted = ticks;
    }

    void BeginInventoryUnload()
    {
        first.drafter.Drafted = true;
        second.drafter.Drafted = true;
        first.jobs.StopAll();
        second.jobs.StopAll();
        foreach (var thing in stockpile.GetSlotGroup().HeldThings.ToList()) thing.Destroy();
        if (!source.Destroyed) source.Destroy();
        if (!otherSource.Destroyed) otherSource.Destroy();
        Configure(100, 1, 60);
        raidCargo = ThingMaker.MakeThing(ThingDefOf.Steel);
        raidCargo.stackCount = 75;
        first.inventory.innerContainer.TryAdd(raidCargo, false);
        var comp = first.AllComps.Single(c => c.GetType().FullName == "PickUpAndHaul.CompHauledToInventory");
        var marked = (System.Collections.Generic.HashSet<Thing>)AccessTools.Method(comp.GetType(), "GetHashSet").Invoke(comp, null);
        marked.Clear();
        Require("returning cargo is untracked inventory", !marked.Contains(raidCargo));
        first.inventory.UnloadEverything = true;
        first.drafter.Drafted = false;
        first.jobs.StartJob(JobMaker.MakeJob(JobDefOf.UnloadYourInventory), JobCondition.InterruptForced);
        phase = 3;
        phaseStarted = ticks;
        settled = 0;
    }

    public override void GameComponentTick()
    {
        if (!Diagnostics.ContinuedScenario || finished) return;
        try
        {
            ticks++;
            if (phase == 0) { Begin(); return; }
            if (ticks - phaseStarted > 5000) throw new TimeoutException($"Hauling scenario stalled in phase {phase}");
            if (phase == 1 && StoredCount() == 40 && PawnSteel(first) == 0) BeginLimited();
            else if (phase == 2)
            {
                int count = StoredCount();
                if (count > 60) throw new InvalidOperationException($"Controlled storage overfilled to {count}");
                if (count == 60 && PawnSteel(first) == 0 && ++settled >= 120)
                {
                    Require("Limited hauling completes at 60 steel without inventory cargo", true);
                    Require("Controlled storage still has only one similar stack",
                        stockpile.GetSlotGroup().HeldThings.Count(t => t.def == ThingDefOf.Steel) == 1);
                    BeginInventoryUnload();
                }
            }
            else if (phase == 3)
            {
                RequireNoOverfill();
                if (StoredCount() == 60 && PawnSteel(first) == 0 && ++settled >= 120)
                {
                    Require("ordinary inventory unloading respects the 60-steel limit", true);
                    Require("excess returning cargo survives on the ground", raidCargo.Spawned && raidCargo.stackCount == 15);
                    finished = true;
                    Diagnostics.ScenarioFinished();
                }
            }
        }
        catch (Exception ex)
        {
            finished = true;
            Diagnostics.Results.Add(new { name = "Continued live hauling scenario", pass = false, error = ex.ToString() });
            Diagnostics.ScenarioFinished();
        }
    }

    void RequireNoOverfill()
    {
        if (StoredCount() > 60) throw new InvalidOperationException("Ordinary inventory unloading overfilled limited storage");
    }
}
