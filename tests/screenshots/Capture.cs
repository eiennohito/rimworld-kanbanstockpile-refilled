using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace RefilledScreenshots;

[StaticConstructorOnStartup]
public static class Capture
{
    static Capture()
    {
        if (!GenCommandLine.CommandLineArgPassed("RefilledScreenshots")) return;
        new Harmony("local.refilled.screenshots").Patch(AccessTools.Method(typeof(UIRoot_Entry), nameof(UIRoot_Entry.Init)),
            postfix: new HarmonyMethod(typeof(Capture), nameof(Open)));
    }

    static void Open()
    {
        Prefs.UIScale = 1f;
        Prefs.RunInBackground = true;
        Application.runInBackground = true;
        Screen.SetResolution(1280, 800, false);
        foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
        Find.WindowStack.Add(new CaptureWindow());
    }
}

sealed class CaptureTab : ITab_Storage
{
    readonly IStoreSettingsParent parent;
    public CaptureTab(IStoreSettingsParent parent) { this.parent = parent; }
    protected override IStoreSettingsParent SelStoreSettingsParent => parent;
}

sealed class Storage : ISlotGroupParent
{
    readonly StorageSettings settings;
    readonly SlotGroup group;
    readonly List<IntVec3> cells = new() { new(1, 0, 1), new(2, 0, 1) };
    public Storage(Map map)
    {
        Map = map;
        settings = new StorageSettings(this) { Priority = StoragePriority.Preferred };
        settings.filter.SetAllowAll(null);
        group = new SlotGroup(this);
    }
    public Map Map { get; }
    public IntVec3 Position => cells[0];
    public bool HaulDestinationEnabled => true;
    public bool StorageTabVisible => true;
    public bool IgnoreStoredThingsBeauty => false;
    public string GroupingLabel => "Storage";
    public int GroupingOrder => 0;
    public bool Accepts(Thing thing) => true;
    public StorageSettings GetStoreSettings() => settings;
    public StorageSettings GetParentStoreSettings() => settings;
    public void Notify_SettingsChanged() { }
    public IEnumerable<IntVec3> AllSlotCells() => cells;
    public List<IntVec3> AllSlotCellsList() => cells;
    public void Notify_ReceivedThing(Thing thing) { }
    public void Notify_LostThing(Thing thing) { }
    public string SlotYielderLabel() => "Storage";
    public SlotGroup GetSlotGroup() => group;
}

sealed class CaptureWindow : Window
{
    readonly Mod mod;
    readonly MethodInfo draw, height;
    readonly Storage[] storage = new Storage[3];
    readonly CaptureTab[] tabs = new CaptureTab[3];
    readonly ThingFilterUI.UIState[] filters = new ThingFilterUI.UIState[3];
    readonly Game filterGame;
    readonly string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, "RefilledScreenshots");
    readonly string[] names = { "01-storage-controls", "02-single-stack", "03-refill-cycle", "04-mod-options" };
    int page;
    int lastPageFrame = Time.frameCount;
    bool requested;
    public override Vector2 InitialSize => new(1280, 800);
    protected override float Margin => 0;

    public CaptureWindow()
    {
        doCloseX = false;
        closeOnCancel = false;
        closeOnAccept = false;
        doWindowBackground = false;
        layer = WindowLayer.Super;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "KanbanStockpile");
        var controls = assembly.GetType("KanbanStockpile.StorageControls");
        draw = AccessTools.Method(controls, "Draw");
        height = AccessTools.Method(controls, "Height");
        mod = LoadedModManager.ModHandles.Single(m => m.Content.PackageIdPlayerFacing == "eiennohito.kanbanstockpile.refill");
        // The native filter tree needs its discovery manager even at the menu.
        // Supply it only during filter drawing; no colony is created or loaded.
        filterGame = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        filterGame.hiddenItemsManager = new HiddenItemsManager();
        var map = new Map();
        map.info.Size = new IntVec3(4, 1, 4);
        map.cellIndices = new CellIndices(map);
        map.edificeGrid = new EdificeGrid(map);
        foreach (var cell in new[] { new IntVec3(1, 0, 1), new IntVec3(2, 0, 1) })
            map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(cell)] = new Building { def = ThingDefOf.Shelf };
        var type = assembly.GetType("KanbanStockpile.KanbanSettings");
        var set = AccessTools.Method(assembly.GetType("KanbanStockpile.State"), "Set");
        for (int i = 0; i < 3; i++)
        {
            storage[i] = new Storage(map);
            tabs[i] = new CaptureTab(storage[i]);
            filters[i] = new ThingFilterUI.UIState();
            var settings = Activator.CreateInstance(type);
            type.GetField("ssl").SetValue(settings, new[] { 0, 1, 4 }[i]);
            type.GetField("mss").SetValue(settings, i == 1 ? 300 : 0);
            type.GetField("srt").SetValue(settings, i == 1 ? 70 : i == 2 ? 80 : 100);
            set.Invoke(null, new[] { storage[i].GetStoreSettings(), settings });
        }
        Directory.CreateDirectory(folder);
    }

    protected override void SetInitialSizeAndPosition() => windowRect = new Rect(0, 0, UI.screenWidth, UI.screenHeight);

    public override void DoWindowContents(Rect rect)
    {
        GUI.color = Color.white;
        Widgets.DrawBoxSolid(rect, new Color(0.10f, 0.105f, 0.11f));
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(40, 25, 1200, 42), "KanbanStockpile-Refilled");
        Text.Font = GameFont.Small;
        if (page == 0) Hero();
        if (page == 1) Single();
        if (page == 2) Cycle();
        if (page == 3) Options();
        if (Event.current.type == EventType.Repaint && Time.frameCount - lastPageFrame > 35)
        {
            string path = Path.Combine(folder, names[page] + ".png");
            if (!requested)
            {
                ScreenCapture.CaptureScreenshot(path);
                requested = true;
            }
            else if (File.Exists(path) && Time.frameCount - lastPageFrame > 55)
            {
                page++;
                requested = false;
                lastPageFrame = Time.frameCount;
                if (page == names.Length)
                {
                    File.WriteAllText(Path.Combine(folder, "capture.json"), JsonSerializer.Serialize(new
                    {
                        audioMasterVolume = Prefs.VolumeMaster,
                        steamActive = SteamManager.Active,
                        steamInitialized = SteamManager.Initialized,
                        steamNativeLibraryMapped = OperatingSystem.IsLinux() && File.ReadLines("/proc/self/maps").Any(line => line.Contains("libsteam_api.so") || line.Contains("steamclient.so")),
                        iconWidth = mod.Content.ModMetaData.Icon.width,
                        iconHeight = mod.Content.ModMetaData.Icon.height,
                        files = names.Select(n => n + ".png").ToArray()
                    }));
                    Application.Quit();
                    page = 0;
                }
            }
        }
    }

    void Panel(int index, float x, float y, bool filter = true)
    {
        var panel = new Rect(x, y, 300, filter ? 480 : 145);
        Widgets.DrawMenuSection(panel);
        Widgets.BeginGroup(panel.ContractedBy(10));
        Widgets.ButtonText(new Rect(0, 0, 160, 29), "Priority: Preferred");
        draw.Invoke(null, new object[] { tabs[index] });
        if (filter)
        {
            float top = 35 + (float)height.Invoke(null, new object[] { tabs[index] });
            var previous = Current.Game;
            try
            {
                Current.Game = filterGame;
                ThingFilterUI.DoThingFilterConfigWindow(new Rect(0, top, 280, 460 - top), filters[index],
                    storage[index].GetStoreSettings().filter, null, 8);
            }
            finally { Current.Game = previous; }
        }
        Widgets.EndGroup();
    }

    void Label(float x, float y, float width, string text, bool title = false)
    {
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = title ? GameFont.Medium : GameFont.Small;
        Widgets.Label(new Rect(x, y, width, title ? 40 : 100), text);
        Text.Font = GameFont.Small;
    }

    void Hero()
    {
        Label(40, 82, 1200, "Three example settings. At one stack per item, you can set a size limit too.");
        string[] headings = { "Off", "One stack per item", "Several stacks per item" };
        string[] notes = { "Vanilla storage. No limits or refill rules.", "For a 300-item stack, refilling starts at 210 (70%).", "Up to four stacks of each item.\nEach keeps its normal maximum size." };
        for (int i = 0; i < 3; i++)
        {
            float x = 40 + i * 410;
            Label(x, 128, 360, headings[i], true);
            Panel(i, x, 178);
            Label(x, 677, 360, notes[i]);
        }
        Label(40, 745, 1190, "Refills continue above the low mark. Small final trips are skipped, and incoming deliveries count toward the target.");
    }

    void Single()
    {
        Panel(1, 50, 125);
        Label(415, 130, 790, "Max similar: 1", true);
        Label(415, 177, 730, "One stack of each item in this storage. The limit applies across all its cells.");
        Label(415, 270, 790, "Refill Until: 300", true);
        Label(415, 317, 730, "An absolute cap; it does not raise an item's normal stack maximum.\nLeave the field empty for Max Stack. Click to replace a value; Enter or clicking away saves it. Scroll a number to adjust by one.");
        Label(415, 448, 790, "Refill From: 70%", true);
        Label(415, 495, 730, "Start refilling at 210. Continue toward 300, skipping small extra trips.\n100% keeps normal top-ups.");
    }

    void Cycle()
    {
        Panel(1, 50, 125, false);
        Label(415, 130, 790, "Refill cycle: 300 at 70%", true);
        Label(415, 180, 730, "Refill toward full, then wait for the low mark again.\nThe automatic minimum haul avoids a separate trip for a small remainder.");
        string[] steps = { "210 or less", "A useful load arrives", "267 in storage", "Back to 210" };
        string[] detail = { "Start refilling.", "Keep refilling above 70%.", "The last 33 can wait.\nSkip a separate small trip.", "Start the next refill cycle." };
        for (int i = 0; i < 4; i++)
        {
            float x = 50 + i * 305;
            Widgets.DrawMenuSection(new Rect(x, 365, 280, 195));
            Label(x + 16, 390, 250, steps[i], true);
            Label(x + 16, 450, 245, detail[i]);
        }
        Label(50, 625, 1140, "Incoming deliveries count toward the target. Pawns can still unload items they already carry.\nPartial stacks may refill once after loading a save, then follow the threshold again.");
    }

    void Options()
    {
        var icon = mod.Content.ModMetaData.Icon;
        Widgets.DrawMenuSection(new Rect(50, 115, 440, 52));
        GUI.DrawTexture(new Rect(60, 125, 32, 32), icon);
        Label(105, 128, 380, mod.SettingsCategory());
        Label(535, 121, 650, "Options → Mods → KanbanStockpile-Refilled");
        Widgets.DrawMenuSection(new Rect(50, 220, 1180, 300));
        mod.DoSettingsWindowContents(new Rect(75, 250, 1130, 230));
        Label(50, 575, 1150, "Count incoming deliveries: avoid sending several haulers for the same space.\nGroup non-stackable items across materials: for example, different leather hats share one item limit.");
    }
}
