using System;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace KanbanStockpile
{
    public struct KanbanSettings
    {
        public int srt;
        public int ssl;
        public int mss;
        public bool Enabled => ssl > 0;
        public int Limit(ThingDef def) => ssl == 1 && mss > 0 ? Math.Min(def.stackLimit, mss) : def.stackLimit;
    }

    static class State
    {
        sealed class Entry
        {
            public KanbanSettings settings = new KanbanSettings { srt = 100 };
            public int revision;
        }

        static readonly ConditionalWeakTable<StorageSettings, Entry> entries = new();
        public static KanbanSettings Get(StorageSettings storage) => entries.TryGetValue(storage, out var entry) ? entry.settings : new KanbanSettings { srt = 100 };
        public static int Revision(StorageSettings storage) => entries.TryGetValue(storage, out var entry) ? entry.revision : 0;
        public static void Set(StorageSettings storage, KanbanSettings settings)
        {
            settings.ssl = Math.Max(0, settings.ssl);
            settings.srt = Math.Clamp(settings.srt, 0, 100);
            settings.mss = Math.Max(0, settings.mss);
            var entry = entries.GetValue(storage, _ => new Entry());
            entry.settings = settings;
            entry.revision++;
        }
    }
}
