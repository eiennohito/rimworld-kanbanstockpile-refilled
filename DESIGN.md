# Implementation notes

For contributors working on storage rules, compatibility and performance.
Player instructions are in README.md and PURPOSE.md. Build and test commands
are in BUILDING.md.

## Storage controls

| Control | Values |
| --- | --- |
| Max similar | Slider: Off (0), 1, through the container's stack capacity |
| Refill Until | Optional absolute count; visible only at Max similar = 1 |
| Refill From | Full-width percentage slider; visible while Max similar is enabled |

Max similar and Refill Until share the first row, roughly 70% / 30%. Keep the
Max similar slider's geometry fixed across states. Reserve generous slider space
for Off and the first few counts, then progressively compress larger counts.
One drag switches states; wheel and keyboard steps change the count by one.

Refill Until stores either no override or a positive integer. No override displays
"Max Stack" verbatim and resolves separately for each item's effective stack
maximum; never turn it into one fixed count for the whole storage. Focusing clears
the displayed text for replacement. Commit on Enter or leaving the field; an empty
entry restores Max Stack. Escape cancels the edit. Wheel changes a numeric value
by one, without scrolling the surrounding tab. It must not pick an arbitrary item
maximum when the value is unset; type a number to set an override first.

Off bypasses all custom hauling behavior. Refill From defaults to 100%, meaning
normal top-ups. Hidden settings are retained but do not apply. PUAH support is
automatic. Both focus handling and wheel steps need a small wrapper around the
game's text field; TextFieldNumeric alone does not implement this behavior.

## Refill rule

Let `L` be the smaller of the native and configured stack limits, and
`R = floor(L * threshold / 100)`.

Start a cycle at `count <= R`. Continue above R until full or another useful
delivery no longer fits. Automatic minimum delivery:

```text
max(1, min(ceil(min(L, carrying capacity) / 4),
           ceil(L * 0.15),
           max(1, floor((L - R) / 2))))
```

For L=300, R=210 and carrying capacity=267, the minimum is 45. A stack at 267
waits rather than requesting another trip for 33.

Normal loads need no additional source search. For a small, non-urgent source,
check nearby eligible piles within vanilla's eight-cell opportunistic pickup
radius, stopping once the minimum is available. This is an estimate: reservations
and accessibility can change before pickup. Low-stock deliveries and already
carried cargo bypass the cutoff. Subtract incoming deliveries, including cargo
already picked up, before assigning more work.

Cycle state is runtime-only. On first observation after load, a partial stack may
refill once, subject to the minimum delivery rule. Afterwards the low mark starts
new cycles. There is no load-time storage scan or cycle serialization.

Live checks read stack quantities directly because
other mods can assign stackCount without an event. Full-stack deliveries close
cycles immediately; later consumption is observed by the next haul query.

There are no tick callbacks or periodic scans. Storage membership is indexed by
item definition and invalidated on grid/cell changes. The reservation index is
rebuilt after reservation mutations, then reads live job targets and quantities.
Off and 100% refill avoid cycle bookkeeping. Settings are keyed by StorageSettings
instances, avoiding collisions between equally named zones or different maps.

## Pick Up And Haul Reworked batches

Limited storage participates in Reworked's normal inventory batches. The adapter
caps each cell allocation by compatible partial stacks, effective stack size,
physical free slots and the storage-wide similar-stack allowance. New pickups
use refill cycles; already collected cargo can use remaining space above the
low mark. Reworked retains its source selection, mass allowance and routing.

The completed batch is checked for a useful delivery rather than rejecting each
small source in isolation. Several fragments may meet the minimum together;
urgent supplies, already carried cargo and other useful work in the same batch
allow smaller accompanying refills. A lone tiny non-urgent refill is rejected.

The adapter intercepts `StorageTally.Take`, allocating controlled destinations
cell by cell and retaining promises on that tally. `ReserveCarriedCargo` identifies
the owning batch before allocation starts. Several sources cannot each claim the
same space, and several cells cannot each claim the last allowed similar stack.
Native capacity calls in `HaulingWork.Storage` and `CellCapacity` are also capped, including acquisition rechecks and unloading. The merge-aware `CanHaul`
overload caps the merge count before `TryPlanPickup` records its tally;
`TryPlanPickup` then retains the destination promise. A core postfix on
`WorkGiver_Merge.JobOnThing` limits native merge jobs, including Reworked's
fallback and pending merge jobs. This avoids forcing a vanilla haul for each
stack of light materials. The original/Mlie PUAH adapter is unchanged.

Successful batch reservations commit the planned destinations to a game
component. Batch extensions replace the pawn's plan, including destinations for
its already carried cargo. Queries bound promises by live pickup quantities and
the public `GetHauledCount` API, distributing each item budget once across its
destinations. Direct deposits release their destination quantities immediately.
Failed pickups, cancellations, cargo loss and leaving the map release promises
on the next query. Vanilla haulers also subtract these incoming quantities and
stack slots. Turning off reserved-stack checking disables cross-pawn accounting;
each batch still enforces its own limits.

Promises serialize pawn, map, cell, item/material, source identity and quantity.
Source references distinguish stacks that cannot merge. After a source is
destroyed or its reference is unavailable, its promised stack space is counted
conservatively rather than assuming compatibility with another stack. Live
quantity budgets use item/material totals; this can temporarily withhold extra
space for variants of the same item that cannot stack together. Promises from a
save made with this adapter survive loading. Older saves have no destination
records for batches already in progress: those jobs still receive acquisition
and unloading caps, and their next newly planned batch starts promise tracking.
There is no load-time colony scan or periodic work. Initial loading only restores
the saved active plans. Each capacity query visits active planned destinations
and matching pickup/cargo lists; planning additionally keeps a local allocation
list. Work scales with active batches, their destinations and Reworked's existing
source limits, rather than every item on the map.

Optional integrations use separate Harmony owners. If installation throws,
the loader removes that owner's patches and clears the Reworked API delegates;
core storage patches and the loader remain usable. A diagnostic error identifies
the unavailable integration. This contains startup failures; an unsupported
Reworked version does not retain the adapter's batch-capacity guarantees.

There is no initialization scan. Each controlled `Take` visits destination cells
until the request is satisfied or the group is exhausted. Each visited cell uses
the existing capacity calculation and its local promises; uncontrolled storage
and containers retain Reworked's native tally. This adds no tick work or source
search. Merge jobs also apply the existing refill/minimum-delivery checks.

Inspected installed Reworked 1.7 DLL on 2026-11-04: SHA-256
`317ab30c2b5a51341b4f80aefe3691b6fa3a81ca9903b03853905625d737953a`,
public API contract 3; RimWorld `1.7.4998 rev1299`. The unchanged public API
version does not guarantee stable internal planning methods. The current DLL
removed `TryFindMergeTarget` and `BatchPickupPlan.Fill` and moved cargo ownership
from `CargoState` to `CargoComp`.

The headless runtime fixture exercises the actual patched `StorageTally.Take`
and delivery capacity methods, group allocation, queued promises, failed pickups,
cancellation, current cargo ownership and Scribe round trips. Merge helper checks
cover remaining capacity and repeated allocations. An injected optional patch
failure verifies partial-patch rollback while the core Storage tab patch remains.
The small map has storage grids but no pathfinder: a fixture-only prefix passes
no carrier to native cell acceptance checks, excluding reachability. It does not
simulate a pawn walking a complete hauling route or render the Storage tab.

## Source and license

Refilled is based on [Endag's KanbanStockpile Continued source](https://github.com/Endag/RimWorld-KanbanStockpile)
at commit `ff8f8385a78c2d3a8f6ed105272f068253f52349`. That continuation derives
from ubergarm's KanbanStockpile, with contributions from Tmq5521 and bananasss00.
The repository retains the upstream history and MIT license; see LICENSE.
Workshop links to both predecessors are in README.md and About/About.xml.
