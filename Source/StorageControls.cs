using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KanbanStockpile
{
    public static class StorageControls
    {
        const string inputName = "KanbanRefillUntil";
        static readonly PropertyInfo selected = AccessTools.Property(typeof(ITab_Storage), "SelStoreSettingsParent");
        static readonly PropertyInfo topHeight = AccessTools.Property(typeof(ITab_Storage), "TopAreaHeight");
        static StorageSettings editing;
        static string buffer = "";
        static bool wasFocused;
        static ISlotGroupParent capacityParent;
        static int capacityCells, capacityPerCell, cachedCapacity;

        static IStoreSettingsParent Parent(ITab_Storage tab) => selected.GetValue(tab) as IStoreSettingsParent;
        static void Commit(ref KanbanSettings settings)
        {
            if (string.IsNullOrWhiteSpace(buffer)) settings.mss = 0;
            else if (int.TryParse(buffer, out int value) && value > 0) settings.mss = value;
        }
        public static void EndEdit()
        {
            if (wasFocused && editing != null)
            {
                var settings = State.Get(editing);
                int previous = settings.mss;
                Commit(ref settings);
                if (settings.mss != previous) State.Set(editing, settings);
            }
            wasFocused = false;
            GUI.FocusControl(null);
            capacityParent = null;
        }
        public static float Height(ITab_Storage tab)
        {
            var storage = Parent(tab)?.GetStoreSettings();
            if (storage == null || Parent(tab) is not ISlotGroupParent) return 0;
            var settings = State.Get(storage);
            return settings.ssl == 1 ? 76f : settings.Enabled ? 70f : 36f;
        }

        public static float SliderPosition(int count, int max)
        {
            if (max <= 8) return max == 0 ? 0 : (float)count / max;
            return count <= 4 ? count / 8f : 0.5f + 0.5f * (float)Math.Sqrt((double)(count - 4) / (max - 4));
        }

        public static int SliderCount(float position, int max)
        {
            if (max <= 8) return Mathf.RoundToInt(position * max);
            if (position <= 0.5f) return Mathf.RoundToInt(position * 8);
            double tail = (position - 0.5) * 2;
            return Math.Clamp(4 + (int)Math.Round(tail * tail * (max - 4)), 0, max);
        }

        public static int Capacity(ISlotGroupParent parent)
        {
            var cells = parent.AllSlotCellsList();
            int perCell = parent is Building building ? Math.Max(1, building.MaxItemsInCell) : 1;
            if (parent == capacityParent && cells.Count == capacityCells && perCell == capacityPerCell) return cachedCapacity;
            capacityParent = parent;
            capacityCells = cells.Count;
            capacityPerCell = perCell;
            long max = (long)cells.Count * perCell;
            var adaptiveCapacity = AccessTools.Method(parent.GetType(), "GetMaxItemsForCell",
                new[] { typeof(IntVec3).MakeByRefType() });
            if (adaptiveCapacity != null && adaptiveCapacity.ReturnType == typeof(int))
            {
                max = 0;
                for (int i = 0; i < cells.Count; i++) max += Math.Max(0, (int)adaptiveCapacity.Invoke(parent, new object[] { cells[i] }));
            }
            return cachedCapacity = (int)Math.Min(int.MaxValue, Math.Max(1, max));
        }

        public static void Draw(ITab_Storage tab)
        {
            if (Parent(tab) is not ISlotGroupParent parent) return;
            var storage = parent.GetStoreSettings();
            var settings = State.Get(storage);
            var before = settings;
            float y = (float)topHeight.GetValue(tab) - Height(tab);
            if (KanbanStockpileLoader.IsStockpileRankingLoaded) y -= 26;
            Text.Font = GameFont.Small;
            var label = new Rect(0, y, 190, 20);
            Widgets.Label(label, "KS.MaxSimilar".Translate(settings.Enabled ? settings.ssl.ToString() : "KS.Off".Translate().ToString()));
            TooltipHandler.TipRegion(label, "KS.MaxSimilarTip".Translate());
            var slider = new Rect(0, y + 20, 190, 14);
            int max = Math.Max(Capacity(parent), settings.ssl);
            float position = Widgets.HorizontalSlider(slider, SliderPosition(settings.ssl, max), 0, 1);
            settings.ssl = SliderCount(position, max);
            var ev = Event.current;
            if (Mouse.IsOver(slider))
            {
                if (ev.type == EventType.ScrollWheel)
                {
                    settings.ssl = Math.Clamp(settings.ssl - Math.Sign(ev.delta.y), 0, max);
                    ev.Use();
                }
                else if (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.LeftArrow || ev.keyCode == KeyCode.RightArrow))
                {
                    settings.ssl = Math.Clamp(settings.ssl + (ev.keyCode == KeyCode.RightArrow ? 1 : -1), 0, max);
                    ev.Use();
                }
            }
            if (settings.ssl == 1)
            {
                Widgets.Label(new Rect(198, y, 82, 20), "KS.RefillUntil".Translate());
                DrawUntil(new Rect(198, y + 20, 82, 24), storage, ref settings);
            }
            else if (wasFocused)
            {
                if (editing == storage) Commit(ref settings);
                else EndEdit();
                GUI.FocusControl(null);
                wasFocused = false;
            }
            if (settings.Enabled)
            {
                float refillY = y + (settings.ssl == 1 ? 40f : 34f);
                Widgets.Label(new Rect(0, refillY, 190, 20), "KS.RefillFrom".Translate(settings.srt));
                var threshold = new Rect(0, refillY + 20, 280, 14);
                settings.srt = Mathf.RoundToInt(Widgets.HorizontalSlider(threshold, settings.srt, 0, 100, roundTo: 1));
                TooltipHandler.TipRegion(new Rect(0, refillY, 280, 34),
                    "KS.RefillFromTip".Translate());
            }
            if (settings.ssl != before.ssl || settings.srt != before.srt || settings.mss != before.mss)
                State.Set(storage, settings);
        }

        static void DrawUntil(Rect rect, StorageSettings storage, ref KanbanSettings settings)
        {
            if (editing != storage)
            {
                EndEdit();
                editing = storage;
                wasFocused = false;
                buffer = "";
            }
            var ev = Event.current;
            bool focus = GUI.GetNameOfFocusedControl() == inputName;
            if (ev.type == EventType.ScrollWheel && Mouse.IsOver(rect))
            {
                int current = int.TryParse(buffer, out int typed) && wasFocused ? typed : settings.mss;
                if (current > 0)
                {
                    settings.mss = (int)Math.Clamp((long)current - Math.Sign(ev.delta.y), 1, int.MaxValue);
                    buffer = settings.mss.ToString();
                }
                ev.Use();
            }
            if (wasFocused && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                buffer = settings.mss > 0 ? settings.mss.ToString() : "";
                GUI.FocusControl(null);
                wasFocused = false;
                focus = false;
                ev.Use();
            }
            else if (wasFocused && (!focus || (ev.rawType == EventType.MouseDown && !rect.Contains(ev.mousePosition))
                || (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter))))
            {
                Commit(ref settings);
                GUI.FocusControl(null);
                wasFocused = false;
                focus = false;
            }
            bool starting = !wasFocused && ((ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition)) || focus);
            if (starting) { buffer = ""; wasFocused = true; }
            GUI.SetNextControlName(inputName);
            string text = Widgets.TextField(rect, wasFocused ? buffer : settings.mss > 0 ? settings.mss.ToString() : "");
            if (wasFocused && (text.Length == 0 || int.TryParse(text, out int positive) && positive > 0)) buffer = text;
            if (!wasFocused && settings.mss == 0) Widgets.Label(rect.ContractedBy(4, 0), "KS.MaxStack".Translate());
            TooltipHandler.TipRegion(rect, "KS.RefillUntilTip".Translate());
        }
    }

    [HarmonyPatch(typeof(ITab_Storage), nameof(ITab_Storage.Notify_ClickOutsideWindow))]
    static class StorageControlsUnfocus
    {
        static void Postfix() => StorageControls.EndEdit();
    }

    [HarmonyPatch(typeof(ITab_Storage), nameof(ITab_Storage.OnOpen))]
    static class StorageControlsOpen
    {
        static void Postfix() => StorageControls.EndEdit();
    }
}
