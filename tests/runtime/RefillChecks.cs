using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace WorkspaceDiagnostics;

public static partial class Diagnostics
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int IntegrationSentinel() => 3;
    static void IntegrationSentinelPostfix(ref int __result) => __result = 99;

    static Map capacityFixtureMap;
    // This fixture has grids and storage, but no pathfinder. Keep native cell
    // acceptance checks while excluding carrier reachability from this test.
    static void CapacityFixtureCell(Map map, ref Pawn carrier)
    {
        if (map == capacityFixtureMap) carrier = null;
    }

    static void RunRefillChecks(Assembly assembly, List<object> tests)
    {
        void Check(string name, int expected, int actual) => tests.Add(new { name, expected, actual, pass = expected == actual });
        var loader = assembly.GetType("KanbanStockpile.KanbanStockpileLoader");
        Check("Storage loader initialized without a cached type failure", 1,
            AccessTools.Field(loader, "IsPickUpAndHaulLoaded").GetValue(null) is bool ? 1 : 0);
        var optional = new Harmony("refilled.tests.failed-integration");
        var sentinel = AccessTools.Method(typeof(Diagnostics), nameof(IntegrationSentinel));
        Exception failure = null;
        Action<Harmony> brokenIntegration = h => {
            h.Patch(sentinel, postfix: new HarmonyMethod(typeof(Diagnostics), nameof(IntegrationSentinelPostfix)));
            throw new MissingMethodException("Simulated optional API change");
        };
        bool applied = (bool)AccessTools.Method(loader, "TryApplyIntegration").Invoke(null,
            new object[] { optional, brokenIntegration, (Action<Exception>)(ex => failure = ex) });
        Check("Optional integration failure is contained", 1, !applied && failure is MissingMethodException ? 1 : 0);
        Check("Optional integration failure rolls back partial patches", 3, IntegrationSentinel());
        Check("Core storage controls survive optional integration rollback", 1,
            Harmony.GetPatchInfo(AccessTools.Method(typeof(ITab_Storage), "FillTab")).Owners
                .Contains("eiennohito.kanbanstockpile.refill") ? 1 : 0);
        var originalGame = Current.Game;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        game.components = new List<GameComponent>();
        var deliveriesType = assembly.GetType("KanbanStockpile.ReworkedDeliveries");
        var deliveries = Activator.CreateInstance(deliveriesType, game);
        game.components.Add((GameComponent)deliveries);
        var map = new Map();
        var fixturePatches = new Harmony("refilled.tests.capacity-cells");
        capacityFixtureMap = map;
        fixturePatches.Patch(AccessTools.Method(typeof(StoreUtility), "IsGoodStoreCell"),
            prefix: new HarmonyMethod(typeof(Diagnostics), nameof(CapacityFixtureCell)));
        map.info.Size = new IntVec3(4, 1, 4);
        map.cellIndices = new CellIndices(map);
        map.thingGrid = new ThingGrid(map);
        map.edificeGrid = new EdificeGrid(map);
        map.haulDestinationManager = new HaulDestinationManager(map);
        map.reservationManager = new ReservationManager(map);
        map.physicalInteractionReservationManager = new PhysicalInteractionReservationManager();
        map.spawnedThings = new ThingOwner<Thing>(map);
        AccessTools.Field(typeof(Game), "maps").SetValue(game, new List<Map> { map });
        Current.Game = game;
        var destination = new DiagnosticStorage(map);
        var storage = destination.GetStoreSettings();
        var group = destination.GetSlotGroup();
        var cell = destination.Position;
        foreach (var c in destination.AllSlotCellsList()) map.haulDestinationManager.SetCellFor(c, group);
        map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(cell)] = new Building { def = ThingDefOf.Shelf };
        var source = new Thing { def = ThingDefOf.Steel, stackCount = 75, thingIDNumber = 100001 };
        var stored = new Thing { def = ThingDefOf.Steel, stackCount = 60, thingIDNumber = 100002 };
        map.thingGrid.ThingsListAt(cell).Add(stored);
        var state = assembly.GetType("KanbanStockpile.State");
        var settingsType = assembly.GetType("KanbanStockpile.KanbanSettings");
        var get = AccessTools.Method(state, "Get");
        var set = AccessTools.Method(state, "Set");
        var cycles = assembly.GetType("KanbanStockpile.RefillCycles");
        var desired = AccessTools.Method(cycles, "Desired");
        var minimum = AccessTools.Method(cycles, "MinimumDelivery");
        var countStored = AccessTools.Method(assembly.GetType("KanbanStockpile.KSUtil"), "CountStoredSimilarStacks");
        var blockers = AccessTools.Method(assembly.GetType("KanbanStockpile.StoreUtility_NoStorageBlockersIn_Patch"), "Postfix");
        object Configure(int threshold, int similar, int size)
        {
            object settings = Activator.CreateInstance(settingsType);
            settingsType.GetField("srt").SetValue(settings, threshold);
            settingsType.GetField("ssl").SetValue(settings, similar);
            settingsType.GetField("mss").SetValue(settings, size);
            set.Invoke(null, new[] { storage, settings });
            return settings;
        }
        int Desired(object settings) => (int)desired.Invoke(null, new[] { stored, group, settings });
        bool Allowed(Thing thing)
        {
            object[] args = { true, cell, map, thing };
            blockers.Invoke(null, args);
            return (bool)args[0];
        }
        try
        {
            var defaults = get.Invoke(null, new[] { storage });
            Check("New storage is Off", 0, (int)settingsType.GetField("ssl").GetValue(defaults));
            Check("New storage refill defaults to 100%", 100, (int)settingsType.GetField("srt").GetValue(defaults));
            Check("New storage has no absolute override", 0, (int)settingsType.GetField("mss").GetValue(defaults));
            string legacyPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "Diagnostics", "legacy-storage.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath));
            File.WriteAllText(legacyPath, "<storage><stackRefillThreshold>70</stackRefillThreshold><similarStackLimit>1</similarStackLimit><maxStackSize>300</maxStackSize></storage>");
            Scribe.loader.InitLoading(legacyPath);
            AccessTools.Method(assembly.GetType("KanbanStockpile.StorageSettingsSave"), "Postfix").Invoke(null, new object[] { storage });
            Scribe.loader.FinalizeLoading();
            var legacy = get.Invoke(null, new[] { storage });
            Check("Legacy save retains refill threshold", 70, (int)settingsType.GetField("srt").GetValue(legacy));
            Check("Legacy save retains similar-stack limit", 1, (int)settingsType.GetField("ssl").GetValue(legacy));
            Check("Legacy save retains absolute stack size", 300, (int)settingsType.GetField("mss").GetValue(legacy));
            Configure(1, 0, 1);
            Check("Off ignores hidden size and threshold", 1, Allowed(source) ? 1 : 0);
            Configure(70, 1, 70);
            var equallyNamed = new DiagnosticStorage(map).GetStoreSettings();
            Check("Equally named storages have independent settings", 0,
                (int)settingsType.GetField("ssl").GetValue(get.Invoke(null, new object[] { equallyNamed })));

            var settings = Configure(70, 1, 70);
            stored.stackCount = 55;
            Check("Fresh runtime can refill a partial stack after loading", 15, Desired(settings));
            stored.stackCount = 65;
            Check("Fresh runtime skips a small remaining delivery", 0, Desired(settings));
            stored.stackCount = 55;
            Check("Waiting stack above low mark does not start", 0, Desired(settings));
            stored.stackCount = 49;
            Check("Cycle starts at exactly 70%", 21, Desired(settings));
            stored.stackCount = 55;
            Check("Active cycle continues above low mark", 15, Desired(settings));
            stored.stackCount = 65;
            Check("Near-full remainder closes the cycle", 0, Desired(settings));
            stored.stackCount = 55;
            Check("Closed cycle stays waiting above low mark", 0, Desired(settings));
            stored.stackCount = 48;
            Check("Consumption below low mark starts next cycle", 22, Desired(settings));
            stored.stackCount = 55;
            Desired(settings);

            var table = AccessTools.Field(cycles, "cycles").GetValue(null);
            table.GetType().GetMethod("Remove", new[] { typeof(Thing) }).Invoke(table, new[] { stored });
            Check("Discarded cycle history is recomputed from current stock", 15, Desired(settings));

            settings = Configure(70, 1, 70);
            Check("Settings edit invalidates previous cycle", 0, Desired(settings));
            settings = Configure(100, 1, 70);
            stored.stackCount = 69;
            Check("100% retains one-item top-ups", 1, Desired(settings));
            settings = Configure(70, 2, 1);
            var limit = AccessTools.Method(settingsType, "Limit");
            Check("Absolute override applies only with one similar stack", source.def.stackLimit,
                (int)limit.Invoke(settings, new object[] { source.def }));

            Check("300/70 minimum delivery", 45, (int)minimum.Invoke(null, new object[] { 300, 70, 267 }));
            Check("Low carrying capacity reduces minimum", 3, (int)minimum.Invoke(null, new object[] { 300, 70, 10 }));
            Check("100% minimum delivery is one", 1, (int)minimum.Invoke(null, new object[] { 300, 100, 267 }));

            var originalStored = stored;
            var bulkDef = new ThingDef { defName = "WorkspaceBulk", category = ThingCategory.Item,
                thingClass = typeof(Thing), stackLimit = 300 };
            stored = new Thing { def = bulkDef, stackCount = 200, thingIDNumber = 100004 };
            settings = Configure(70, 1, 300);
            Check("300-item stack starts replenishing below 210", 100, Desired(settings));
            stored.stackCount = 267;
            Check("267/300 skips a separate delivery for 33", 0, Desired(settings));
            stored.stackCount = 211;
            Check("267/300 cycle waits until the low mark", 0, Desired(settings));
            stored.stackCount = 210;
            Check("267/300 cycle restarts at 210", 90, Desired(settings));
            stored.stackCount = 300;
            AccessTools.Method(cycles, "Observe").Invoke(null, new object[] { stored });
            stored.stackCount = 250;
            Check("Delivery event closes full cycle before later consumption", 0, Desired(settings));
            stored = originalStored;

            settings = Configure(70, 1, 70);
            Check("Initial storage membership index", 1, (int)countStored.Invoke(null, new object[] { group, map, source, 10, settings }));
            var extra = new Thing { def = source.def, stackCount = 5, thingIDNumber = 100003 };
            AccessTools.Method(typeof(ThingGrid), "RegisterInCell").Invoke(map.thingGrid, new object[] { extra, cell });
            Check("Grid registration invalidates membership", 2, (int)countStored.Invoke(null, new object[] { group, map, source, 10, settings }));
            extra.Position = cell;
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(extra, (sbyte)0);
            AccessTools.Method(typeof(ThingGrid), "DeregisterInCell").Invoke(map.thingGrid, new object[] { extra, cell });
            Check("Grid deregistration invalidates membership", 1, (int)countStored.Invoke(null, new object[] { group, map, source, 10, settings }));

            var reservations = map.reservationManager.ReservationsReadOnly;
            var pawn = new Pawn { def = ThingDefOf.Human };
            ((ThingOwner<Thing>)map.spawnedThings).InnerListForReading.Add(pawn);
            pawn.holdingOwner = map.spawnedThings;
            var hauling = new Job { def = JobDefOf.HaulToCell, targetA = source, targetB = cell, count = 20 };
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)0);
            reservations.Add(new ReservationManager.Reservation(pawn, hauling, 1, -1, cell, null));
            reservations.Add(new ReservationManager.Reservation(pawn, hauling, 1, -1, source, null));
            var reservationIndex = assembly.GetType("KanbanStockpile.ReservationIndex");
            var index = AccessTools.Method(reservationIndex, "For").Invoke(null, new object[] { map.reservationManager });
            var incoming = AccessTools.Method(reservationIndex, "Incoming");
            int Incoming() => (int)incoming.Invoke(index, new object[] { group, map, cell, source, null });
            Check("Incoming quantity deduplicates source and cell reservations", 20, Incoming());
            hauling.count = 7;
            Check("Incoming quantity reads live job counts", 7, Incoming());
            pawn.jobs = new Pawn_JobTracker(pawn) { curJob = hauling };
            pawn.carryTracker = new Pawn_CarryTracker(pawn);
            var carried = new Thing { def = source.def, stackCount = 13, thingIDNumber = 100005 };
            pawn.carryTracker.innerContainer.InnerListForReading.Add(carried);
            carried.holdingOwner = pawn.carryTracker.innerContainer;
            Check("Incoming quantity includes cargo after pickup decrements job count", 20, Incoming());
            map.reservationManager.ReleaseClaimedBy(pawn, hauling);
            Check("Reservation release invalidates job index", 0, Incoming());
            Configure(100, 1, 0);
            var wood = new Thing { def = ThingDefOf.WoodLog, stackCount = 20, thingIDNumber = 100006 };
            wood.Position = new IntVec3(0, 0, 0);
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(wood, (sbyte)0);
            Check("Destination accepts a new item before the haul reserves it", 1, cell.IsValidStorageFor(map, wood) ? 1 : 0);
            var woodJob = new Job { def = JobDefOf.HaulToCell, targetA = wood, targetB = cell, count = 20 };
            pawn.jobs.curJob = woodJob;
            reservations.Add(new ReservationManager.Reservation(pawn, woodJob, 1, -1, cell, null));
            reservations.Add(new ReservationManager.Reservation(pawn, woodJob, 1, -1, wood, null));
            AccessTools.Method(reservationIndex, "Invalidate").Invoke(null, new object[] { map.reservationManager });
            Check("Active haul remains valid after reserving its own stack slot", 1, cell.IsValidStorageFor(map, wood) ? 1 : 0);
            var otherWood = new Thing { def = wood.def, stackCount = 20, thingIDNumber = 100007 };
            Check("Another source cannot consume the reserved stack slot", 0, cell.IsValidStorageFor(map, otherWood) ? 1 : 0);
            pawn.jobs.curJob = hauling;
            Check("Queued haul reservations still count toward the limit", 0, cell.IsValidStorageFor(map, wood) ? 1 : 0);
            map.reservationManager.ReleaseClaimedBy(pawn, woodJob);
            Check("Canceling the haul makes its slot available again", 1, cell.IsValidStorageFor(map, wood) ? 1 : 0);
            settings = Configure(100, 1, 70);
            stored.stackCount = 60;
            var haul = new Job { def = JobDefOf.HaulToCell, targetA = source, targetB = cell, count = 75 };
            object[] haulArgs = { haul, pawn, source };
            AccessTools.Method(assembly.GetType("KanbanStockpile.HaulAIUtility_HaulToStorageJob_Patch"), "Postfix").Invoke(null, haulArgs);
            Check("Vanilla haul count respects remaining absolute capacity", 10, ((Job)haulArgs[0]).count);
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)-1);

            var slider = assembly.GetType("KanbanStockpile.StorageControls");
            var position = AccessTools.Method(slider, "SliderPosition");
            var count = AccessTools.Method(slider, "SliderCount");
            bool roundtrip = true;
            for (int i = 0; i <= 1000; i++)
            {
                float at = (float)position.Invoke(null, new object[] { i, 1000 });
                if ((int)count.Invoke(null, new object[] { at, 1000 }) != i) roundtrip = false;
            }
            Check("Slider preserves every integer across nonlinear scale", 1, roundtrip ? 1 : 0);

            if (ModLister.GetActiveModWithIdentifier("Zzyxzz.PickUpAndHaulReworked") != null
                || ModLister.GetActiveModWithIdentifier("Mehni.PickUpAndHaul") != null
                || ModLister.GetActiveModWithIdentifier("Mlie.PickUpAndHaul") != null)
            {
                var continued = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
                var continuedCapacity = AccessTools.Method(continued, "CapacityAt");
                Check("Legacy PUAH integration initializes", 1,
                    (bool)AccessTools.Field(loader, "IsPickUpAndHaulLoaded").GetValue(null) ? 1 : 0);
                Check("Legacy PUAH capacity method has Refilled's adapter", 1,
                    Harmony.GetPatchInfo(continuedCapacity)?.Owners.Contains("eiennohito.kanbanstockpile.refill.puah") == true ? 1 : 0);
                int ContinuedCapacity(IntVec3 target) => (int)continuedCapacity.Invoke(null, new object[] { source, target, map });
                Configure(100, 1, 70);
                stored.stackCount = 60;
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)0);
                Check("Legacy PUAH inventory planning uses the remaining controlled capacity", 10, ContinuedCapacity(cell));
                Configure(70, 1, 70);
                Check("Legacy PUAH inventory planning does not bypass refill thresholds", 0, ContinuedCapacity(cell));
                Configure(100, 0, 0);
                Check("Legacy PUAH retains native capacity when Refilled is Off",
                    cell.GetItemStackSpaceLeftFor(map, source.def), ContinuedCapacity(cell));
                Configure(100, 1, 70);
                var ordinaryCell = new IntVec3(0, 0, 0);
                Check("Legacy PUAH retains native capacity outside controlled storage",
                    ordinaryCell.GetItemStackSpaceLeftFor(map, source.def), ContinuedCapacity(ordinaryCell));

                var inventoryAdapter = assembly.GetType("KanbanStockpile.InventoryHauling");
                var batchDef = DefDatabase<JobDef>.GetNamed("HaulToInventory");
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(pawn, (sbyte)0);
                pawn.carryTracker.innerContainer.Clear();
                pawn.inventory = new Pawn_InventoryTracker(pawn);
                pawn.jobs.curJob = null;
                Configure(100, 1, 70);
                var batch = new Job { def = batchDef, targetB = cell,
                    targetQueueA = new List<LocalTargetInfo> { source }, countQueue = new List<int> { 75 } };
                object[] planning = { pawn, null };
                AccessTools.Method(inventoryAdapter, "Begin").Invoke(null, planning);
                object[] planned = { pawn, batch };
                try { AccessTools.Method(inventoryAdapter, "Planned").Invoke(null, planned); }
                finally { AccessTools.Method(inventoryAdapter, "End").Invoke(null, new object[] { null, planning[1] }); }
                batch = (Job)planned[1];
                Check("Native inventory batch is capped to the missing ten units", 10, batch.countQueue[0]);
                var driver = (JobDriver)RuntimeHelpers.GetUninitializedObject(AccessTools.TypeByName("PickUpAndHaul.JobDriver_HaulToInventory"));
                driver.pawn = pawn;
                driver.job = batch;
                pawn.jobs.curJob = batch;
                AccessTools.Method(inventoryAdapter, "Reserved").Invoke(null, new object[] { driver, true });
                var quantity = AccessTools.Method(deliveriesType, "IncomingQuantity");
                int Promised() => (int)quantity.Invoke(deliveries, new object[] { map, cell, source, null });
                Check("Legacy batch promises survive successful reservations", 10, Promised());
                var duplicate = new Job { def = JobDefOf.HaulToCell, targetA = source, targetB = cell, count = 10 };
                object[] duplicateArgs = { duplicate, pawn, source };
                AccessTools.Method(assembly.GetType("KanbanStockpile.HaulAIUtility_HaulToStorageJob_Patch"), "Postfix").Invoke(null, duplicateArgs);
                Check("A pawn cannot queue an extra ordinary haul over its own inventory promise", 1, duplicateArgs[0] == null ? 1 : 0);

                string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "Diagnostics", "inventory-promises.xml");
                GameComponent saved = (GameComponent)deliveries;
                Scribe.saver.InitSaving(path, "save");
                Scribe_Deep.Look(ref saved, "deliveries", game);
                Scribe.saver.FinalizeSaving();
                saved = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref saved, "deliveries", game);
                var directory = AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory").GetValue(Scribe.loader.crossRefs);
                var register = AccessTools.Method(directory.GetType(), "RegisterLoaded");
                foreach (var reference in new ILoadReferenceable[] { pawn, map, source }) register.Invoke(directory, new object[] { reference });
                Scribe.loader.FinalizeLoading();
                game.components.Remove((GameComponent)deliveries);
                deliveries = saved;
                game.components.Add(saved);
                Check("Save/load preserves native inventory destination promises", 10, Promised());
                batch.count = 10;
                batch.countQueue[0] = 3;
                Check("A queued target is not counted twice as the current target", 3, Promised());
                pawn.jobs.curJob = null;
                Check("Cancelled legacy batch releases all uncollected promises", 0, Promised());
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)-1);
            }

            var adapter = assembly.GetType("KanbanStockpile.PickUpAndHaulReworked");
            var capacity = AccessTools.Method(adapter, "CapacityAt");
            settings = Configure(70, 1, 70);
            stored.stackCount = 60;
            Check("Waiting stack rejects new hauling", 0, Allowed(source) ? 1 : 0);
            Check("Already collected cargo can use remaining space", 10,
                (int)capacity.Invoke(null, new object[] { cell, map, source }));
            Configure(70, 2, 0);
            stored.stackCount = source.def.stackLimit;
            Check("Two similar stacks can share a multi-stack shelf cell", 1, Allowed(source) ? 1 : 0);
            Check("Delivery can start the next allowed stack in the same cell", source.def.stackLimit,
                (int)capacity.Invoke(null, new object[] { cell, map, source }));
            Configure(70, 1, 70);
            stored.stackCount = 60;
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)0);
            object[] lookup = { pawn, null };
            AccessTools.Method(adapter, "BeginPlanning").Invoke(null, lookup);
            Check("PUAH planning respects a waiting refill cycle", 0,
                (int)capacity.Invoke(null, new object[] { cell, map, source }));
            stored.stackCount = 40;
            Check("PUAH planning uses remaining controlled capacity below low mark", 30,
                (int)capacity.Invoke(null, new object[] { cell, map, source }));
            AccessTools.Method(adapter, "EndPlanning").Invoke(null, new object[] { null, lookup[1] });
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)-1);
            Configure(0, 0, 1);
            Check("Off restores native multi-stack capacity", cell.GetItemStackSpaceLeftFor(map, source.def),
                (int)capacity.Invoke(null, new object[] { cell, map, source }));
            var work = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.HaulingWork");
            if (work != null)
            {
                Configure(70, 1, 70);
                stored.stackCount = 60;
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(pawn, (sbyte)0);
                pawn.drafter = (Pawn_DraftController)RuntimeHelpers.GetUninitializedObject(typeof(Pawn_DraftController));
                AccessTools.Field(typeof(Pawn_DraftController), "draftedInt").SetValue(pawn.drafter, true);
                var inventory = new ThingOwner<Thing>(pawn);
                inventory.InnerListForReading.Add(source);
                source.holdingOwner = inventory;
                Check("Delivery fixture cargo resolves to its pawn's map", 1, source.MapHeld == map ? 1 : 0);
                Check("Collected cargo passes storage acceptance above the low mark", 1,
                    cell.IsValidStorageFor(map, source) ? 1 : 0);
                Check("Collected cargo passes the native good-cell check", 1,
                    StoreUtility.IsGoodStoreCell(cell, map, source, null, null) ? 1 : 0);
                Check("Actual Reworked delivery uses the rewritten capacity call", 10,
                    (int)AccessTools.Method(work, "TargetCapacity").Invoke(null, new object[] { pawn, source, new LocalTargetInfo(cell), false }));

                Configure(100, 2, 0);
                stored.stackCount = 60;
                pawn.inventory = new Pawn_InventoryTracker(pawn);
                var planType = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.BatchPickupPlan");
                var tallyType = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.StorageTally");
                var take = AccessTools.Method(tallyType, "Take");
                object NewPlan()
                {
                    var result = RuntimeHelpers.GetUninitializedObject(planType);
                    AccessTools.Field(planType, "pawn").SetValue(result, pawn);
                    AccessTools.Field(planType, "tally").SetValue(result, Activator.CreateInstance(tallyType, true));
                    AccessTools.Field(planType, "<Targets>k__BackingField").SetValue(result, new List<LocalTargetInfo>());
                    return result;
                }
                int Allocate(object planning, Thing item, IntVec3 at, int requested)
                {
                    AccessTools.Method(adapter, "PlanStarted").Invoke(null, new[] { planning });
                    // Restrict this query to one cell to exercise cell promises;
                    // a separate query below covers the complete group.
                    var saved = new List<IntVec3>(group.CellsList);
                    group.CellsList.Clear();
                    group.CellsList.Add(at);
                    try { return (int)take.Invoke(AccessTools.Field(planType, "tally").GetValue(planning),
                        new object[] { pawn, item, destination, requested }); }
                    finally { group.CellsList.Clear(); group.CellsList.AddRange(saved); }
                }
                object plan = NewPlan();
                Check("Actual PUAH plan fills partial and additional shelf stacks", 90,
                    Allocate(plan, source, cell, 200));
                var anotherSource = new Thing { def = source.def, stackCount = 75, thingIDNumber = 100008 };
                Check("Another source cannot exceed the same batch's stack allowance", 0,
                    Allocate(plan, anotherSource, cell, 75));

                Configure(100, 3, 0);
                var secondCell = destination.AllSlotCellsList()[1];
                map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(secondCell)] = new Building { def = ThingDefOf.Shelf };
                Check("Same batch can allocate another stack at another cell", 75,
                    Allocate(plan, anotherSource, secondCell, 75));
                Check("Storage-wide stack limit applies across planned cells", 0,
                    Allocate(plan, anotherSource, secondCell, 75));

                // Exercise the real adapter's commit boundary with queued pickup
                // quantities, before any inventory cargo has been collected.
                var targets = (List<LocalTargetInfo>)AccessTools.PropertyGetter(planType, "Targets").Invoke(plan, null);
                var batchDef = DefDatabase<JobDef>.GetNamed("Dreadarm_PickUpAndHaul_Batch");
                var batch = new Job { def = batchDef, targetQueueA = targets, countQueue = new List<int> { 75, 75, 15 } };
                targets.Add(source);
                targets.Add(anotherSource);
                targets.Add(new Thing { def = source.def, stackCount = 15, thingIDNumber = 100009 });
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)0);
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(anotherSource, (sbyte)0);
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(targets[2].Thing, (sbyte)0);
                AccessTools.Method(adapter, "BatchCreated").Invoke(null, new object[] { pawn, batch });
                pawn.jobs.curJob = batch;
                var driver = (JobDriver)RuntimeHelpers.GetUninitializedObject(AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Hauling.JobDriver_BatchHaul"));
                driver.pawn = pawn;
                driver.job = batch;
                AccessTools.Method(adapter, "Reserved").Invoke(null, new object[] { driver, true });
                var quantity = AccessTools.Method(deliveriesType, "IncomingQuantity");
                int Promised(IntVec3 at) => (int)quantity.Invoke(deliveries, new object[] { map, at, anotherSource, null });
                Check("Queued batch promises include all sources at first destination", 90, Promised(cell));
                Check("Queued batch promises include its second destination", 75, Promised(secondCell));
                Check("Other haulers cannot claim the same remaining shelf space", 0,
                    (int)capacity.Invoke(null, new object[] { cell, map, anotherSource }));
                var otherPawn = new Pawn { def = ThingDefOf.Human };
                var otherHaul = new Job { def = JobDefOf.HaulToCell, targetA = anotherSource, targetB = cell, count = 75 };
                object[] otherHaulArgs = { otherHaul, otherPawn, anotherSource };
                AccessTools.Method(assembly.GetType("KanbanStockpile.HaulAIUtility_HaulToStorageJob_Patch"), "Postfix").Invoke(null, otherHaulArgs);
                Check("Vanilla haulers also subtract PUAH batch promises", 1, otherHaulArgs[0] == null ? 1 : 0);
                Check("Queued batch stack slots apply to storage acceptance", 0,
                    secondCell.IsValidStorageFor(map, anotherSource) ? 1 : 0);

                string promisePath = Path.Combine(GenFilePaths.SaveDataFolderPath, "Diagnostics", "delivery-promises.xml");
                GameComponent serialized = (GameComponent)deliveries;
                Scribe.saver.InitSaving(promisePath, "save");
                Scribe_Deep.Look(ref serialized, "deliveries", game);
                Scribe.saver.FinalizeSaving();
                serialized = null;
                Scribe.loader.InitLoading(promisePath);
                Scribe_Deep.Look(ref serialized, "deliveries", game);
                var directory = AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory").GetValue(Scribe.loader.crossRefs);
                var register = AccessTools.Method(directory.GetType(), "RegisterLoaded");
                register.Invoke(directory, new object[] { pawn });
                register.Invoke(directory, new object[] { map });
                foreach (var at in targets) register.Invoke(directory, new object[] { at.Thing });
                Scribe.loader.FinalizeLoading();
                game.components.Remove((GameComponent)deliveries);
                deliveries = serialized;
                game.components.Add(serialized);
                Check("Save/load preserves first destination and all source quantities", 90, Promised(cell));
                Check("Save/load preserves second destination and references", 75, Promised(secondCell));
                targets.RemoveAt(2);
                batch.countQueue.RemoveAt(2);
                Check("Failed pickup reduces promised deliveries", 60, Promised(secondCell));

                var cargoType = AccessTools.TypeByName("Dreadarm.PickUpAndHaul.Cargo.CargoComp");
                var cargo = (ThingComp)Activator.CreateInstance(cargoType);
                cargo.parent = pawn;
                AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(pawn, new List<ThingComp> { cargo });
                AccessTools.Method(cargoType, "Add").Invoke(cargo, new object[] { source });
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(source, (sbyte)-1);
                pawn.inventory.innerContainer.InnerListForReading.Add(source);
                source.holdingOwner = pawn.inventory.innerContainer;
                targets.RemoveAt(0);
                batch.countQueue.RemoveAt(0);
                Check("Picked-up cargo keeps its first destination promise", 90, Promised(cell));
                Check("Picked-up cargo is counted once alongside queued sources", 60, Promised(secondCell));
                AccessTools.Method(deliveriesType, "Delivered").Invoke(deliveries,
                    new object[] { pawn, map, cell, source.def, source.Stuff, 75 });
                source.stackCount = 0;
                Check("Completed delivery releases its quantity immediately", 15, Promised(cell));
                pawn.jobs.curJob = null;
                Check("Cancelling a batch releases promises at first destination", 0, Promised(cell));
                Check("Cancelling a batch releases promises at second destination", 0, Promised(secondCell));

                foreach (string defName in new[] { "RawRice", "Hay", "Cloth" })
                {
                    var def = DefDatabase<ThingDef>.GetNamed(defName);
                    var material = new Thing { def = def, stackCount = def.stackLimit, thingIDNumber = 110000 + def.shortHash };
                    object resourcePlan = NewPlan();
                    Check(defName + " batches two full stacks into controlled shelf space", 2 * def.stackLimit,
                        Allocate(resourcePlan, material, cell, 2 * def.stackLimit));
                    Check(defName + " cannot overfill its allocated shelf space", 0,
                        Allocate(resourcePlan, material, cell, def.stackLimit));
                }

                Configure(100, 3, 0);
                stored.stackCount = 60;
                var groupPlan = NewPlan();
                AccessTools.Method(adapter, "PlanStarted").Invoke(null, new[] { groupPlan });
                var groupTally = AccessTools.Field(planType, "tally").GetValue(groupPlan);
                Check("Actual group tally allocates partial and new stacks across cells", 165,
                    (int)take.Invoke(groupTally, new object[] { pawn, anotherSource, destination, 300 }));
                Check("Actual group tally cannot allocate the same stack slots twice", 0,
                    (int)take.Invoke(groupTally, new object[] { pawn, anotherSource, destination, 75 }));

                Configure(100, 1, 70);
                stored.stackCount = 60;
                var mergePlan = NewPlan();
                AccessTools.Method(adapter, "PlanStarted").Invoke(null, new[] { mergePlan });
                object[] mergeArgs = { pawn, anotherSource, new LocalTargetInfo(cell), 50, true };
                AccessTools.Method(adapter, "MergePostfix").Invoke(null, mergeArgs);
                Check("Merge planning respects the remaining absolute capacity", 10, (int)mergeArgs[3]);
                AccessTools.Method(adapter, "PickupPlanned").Invoke(null, new object[] { pawn, anotherSource,
                    AccessTools.Field(planType, "tally").GetValue(mergePlan), 10, true });
                mergeArgs = new object[] { pawn, anotherSource, new LocalTargetInfo(cell), 50, true };
                AccessTools.Method(adapter, "MergePostfix").Invoke(null, mergeArgs);
                Check("Merge planning subtracts earlier allocations in the same batch", 0, (bool)mergeArgs[4] ? 1 : 0);

                Configure(70, 1, 70);
                stored.stackCount = 40;
                lookup = new object[] { pawn, null };
                AccessTools.Method(adapter, "BeginPlanning").Invoke(null, lookup);
                capacity.Invoke(null, new object[] { cell, map, anotherSource });
                AccessTools.Method(adapter, "EndPlanning").Invoke(null, new object[] { null, lookup[1] });
                stored.stackCount = 55;
                object fragments = NewPlan();
                var tiny = new Thing { def = source.def, stackCount = 5, thingIDNumber = 100010 };
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(tiny, (sbyte)0);
                var fragmentTargets = new List<LocalTargetInfo> { tiny };
                AccessTools.Field(planType, "<Targets>k__BackingField").SetValue(fragments, fragmentTargets);
                Allocate(fragments, tiny, cell, 5);
                var fragmentJob = new Job { def = batchDef, targetQueueA = fragmentTargets, countQueue = new List<int> { 5 } };
                object[] fragmentArgs = { pawn, fragmentJob };
                AccessTools.Method(adapter, "BatchCreated").Invoke(null, fragmentArgs);
                Check("A lone tiny non-urgent refill does not start another trip", 1, fragmentArgs[1] == null ? 1 : 0);
                var tiny2 = new Thing { def = source.def, stackCount = 5, thingIDNumber = 100011 };
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(tiny2, (sbyte)0);
                fragmentTargets.Add(tiny2);
                fragmentJob.countQueue.Add(5);
                Allocate(fragments, tiny2, cell, 5);
                fragmentArgs = new object[] { pawn, fragmentJob };
                AccessTools.Method(adapter, "BatchCreated").Invoke(null, fragmentArgs);
                Check("Several small sources together can make a useful refill batch", 1, fragmentArgs[1] != null ? 1 : 0);
            }
        }
        finally
        {
            if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop();
            fixturePatches.UnpatchAll(fixturePatches.Id);
            capacityFixtureMap = null;
            Current.Game = originalGame;
        }
    }
}
