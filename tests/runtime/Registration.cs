using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
namespace WorkspaceDiagnostics;
public static partial class Diagnostics
{
    static partial void RunProjectChecks(List<object> tests) => RunRefillChecks(AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "KanbanStockpile"), tests);
}
