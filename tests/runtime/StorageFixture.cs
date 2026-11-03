using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Steam;

namespace WorkspaceDiagnostics;

public static partial class Diagnostics
{
    sealed class DiagnosticStorage : ISlotGroupParent
    {
        readonly StorageSettings settings;
        readonly SlotGroup group;
        readonly List<IntVec3> cells = new() { new(1, 0, 1), new(2, 0, 1) };
        public DiagnosticStorage(Map map) { Map = map; settings = new StorageSettings(this); group = new SlotGroup(this); }
        public Map Map { get; }
        public IntVec3 Position => cells[0];
        public bool HaulDestinationEnabled => true;
        public bool StorageTabVisible => true;
        public bool IgnoreStoredThingsBeauty => false;
        public string GroupingLabel => "WorkspaceDiagnosticStorage";
        public int GroupingOrder => 0;
        public bool Accepts(Thing thing) => true;
        public StorageSettings GetStoreSettings() => settings;
        public StorageSettings GetParentStoreSettings() => settings;
        public void Notify_SettingsChanged() { }
        public IEnumerable<IntVec3> AllSlotCells() => cells;
        public List<IntVec3> AllSlotCellsList() => cells;
        public void Notify_ReceivedThing(Thing thing) { }
        public void Notify_LostThing(Thing thing) { }
        public string SlotYielderLabel() => GroupingLabel;
        public SlotGroup GetSlotGroup() => group;
        public override string ToString() => "WorkspaceDiagnosticStorage-4ec0ca5c";
    }
}
