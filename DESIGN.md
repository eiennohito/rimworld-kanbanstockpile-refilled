# Development

## Building and testing

Use the [shared RimWorld workspace tooling](https://github.com/eiennohito/rimworld-mods/blob/main/tools/README.md) for prerequisites and setup.
Clone this repository into `projects/kanban-refilled/` and run from the configured workspace root:

```sh
tools/rw build kanban-refilled
tools/rw test kanban-refilled --suite runtime
```

Select additional suites from [mod.toml](mod.toml) and include dependencies relevant to the change with `--with PACKAGE_ID` or `--with-mod PATH`.
For releases, use the shared guide's [Workshop update procedure](https://github.com/eiennohito/rimworld-mods/blob/main/tools/README.md#workshop-updates).

## Architecture

The mod adds per-storage limits to the game's existing hauling decisions and optional inventory-hauling planners.
Settings belong to `StorageSettings` instances, so equally named zones and different maps remain independent.

## Code map

| File | Responsibility |
| --- | --- |
| [State.cs](Source/State.cs) | Settings, effective limits and revision tracking |
| [StorageControls.cs](Source/StorageControls.cs) | Storage-tab controls and cached storage capacity, including Adaptive Storage's slot limit |
| [RefillCycles.cs](Source/RefillCycles.cs) | Refill-cycle state and minimum useful delivery size |
| [StorageIndex.cs](Source/StorageIndex.cs) | Stored-item membership and incoming ordinary-haul reservations |
| [Patches.cs](Source/Patches.cs) | Core storage acceptance, haul/merge quantities and settings persistence |
| [ReworkedDeliveries.cs](Source/ReworkedDeliveries.cs) | Serialized destination promises shared by the inventory-hauling adapters |
| [PickUpAndHaulReworked.cs](Source/PickUpAndHaulReworked.cs) | Reworked planning, tally allocation and cargo APIs |
| [InventoryHauling.cs](Source/InventoryHauling.cs) | Original/Continued pickup queues, unloading and ordinary inventory unloading |
| [Main.cs](Source/Main.cs) | Initialization and optional integration selection |

## Capacity and refill decisions

Capacity combines physical space, compatible partial stacks, the storage-wide similar-stack allowance and incoming deliveries.
Refill cycles distinguish starting a new trip from depositing cargo already collected.
Cycle state is lazy and runtime-only; settings and active inventory-delivery promises are serialized.
Stored-item membership is invalidated by grid and storage-cell changes, while quantities are read live because other mods can change stack counts without notifications.

## Inventory hauling

Each adapter retains its hauling mod's source selection, mass allowance and job drivers while bounding allocations by controlled capacity.
Planning keeps local allocations so sources and cells cannot independently claim the same space.
Successful reservations commit destination promises, whose quantities are bounded by live queued pickups and owned cargo.
Queries release cancelled, failed or lost deliveries, and direct deposits release completed quantities.
Source identity preserves conservative accounting for stacks that cannot merge.
Ordinary inventory unloading uses the same capacity rules without requiring cargo to have been collected by a hauling job.

## Work and failure boundaries

There are no periodic callbacks or colony-wide initialization scans.
Membership indexes rebuild after invalidation; capacity queries visit relevant stored items, reservations and active delivery plans, and planning visits candidate destinations.
Small non-urgent ordinary refills may inspect nearby sources, while nearby unloading placement validates candidate cells against storage capacity.
Optional adapters use separate Harmony owners; startup failure rolls back that adapter's patches and bindings while leaving core controls usable.
