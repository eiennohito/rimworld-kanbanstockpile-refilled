# Building and testing

Development uses the [shared RimWorld workspace tooling](https://github.com/eiennohito/rimworld-mods/tree/main/tools).
Its guide covers prerequisites, initial configuration and working with Codex or
Claude Code. Clone this mod into `projects/kanban-refilled/` inside that workspace.

From the workspace root:

```sh
tools/rw build kanban-refilled
tools/rw test kanban-refilled --suite runtime
tools/rw test kanban-refilled --suite runtime --with dreadarm.pickupandhaul
```

The build produces an unpacked mod in `outputs/mods/kanban-refilled/`.
Test fixtures are excluded from the package.

The fixtures in `tests/runtime/` check storage settings, refill quantities,
incoming deliveries and the optional Reworked adapter against real game types
in a headless host, plus the original/Continued capacity adapter when loaded. They
do not simulate a pawn's complete hauling route.
[Implementation notes](DESIGN.md) describe the rules, work costs and test limits.

The `hauling` suite generates a colony and runs complete pickup and unloading
jobs. It uses the loaded hauling mod's actual work giver, or vanilla hauling
when none is loaded. It tests stockpiles and shelves, plus a building using the
installed Adaptive Storage class when that framework is loaded:

```sh
tools/rw test kanban-refilled --suite hauling --with dreadarm.pickupandhaul --with adaptive.storage.framework
tools/rw test kanban-refilled --suite hauling --with mehni.pickupandhaul --with adaptive.storage.framework
tools/rw test kanban-refilled --suite hauling --with zzyxzz.pickupandhaulreworked --with adaptive.storage.framework
```

Checks cover Off batching, refill thresholds, a competing pawn, the 60-steel
size limit, and a two-stack limit for cloth, hay and meals. With Reworked,
limited storage must retain inventory batching and deliver two stacks in one
trip. Original PUAH and Continued use their documented ordinary-hauling fallback
for limited storage. A package ID without its actual runtime types cannot pass
the optional integration checks. Supplies retained by the native unloading policy
are excluded from the remaining hauling cargo.

These headless routes do not test rendering, construction, Combat Extended,
caravans or a full save/reload during an active haul. The runtime suite separately
checks delivery-promise serialization. The Adaptive Storage fixture uses the
game's shelf definition with the framework's real building class; it does not
cover every furniture pack or per-cell capacity configuration.

For Pick Up and Haul - Continued by Zzyxzz, the `continued` suite generates a
colony and runs actual pickup and delivery jobs. It checks inventory batching
with Refilled Off, refill thresholds, ordinary hauling into limited storage and
competing pawn reservations. With the continuation installed:

```sh
tools/rw test kanban-refilled --suite continued --with zzyxzz.pickupandhaulreworked
```

Use `--with-mod PATH` instead of `--with` for a local runtime copy in the workspace.
The fixture is in `tests/continued/`; it runs headless and does not cover other
hauling add-ons, Combat Extended, caravans or save/load during an active haul.

To check a changed control's appearance, the screenshot fixture is:

```sh
tools/rw test kanban-refilled --suite screenshots
```

This requires the shared tooling's graphical dependencies. It captures the
Storage tab and mod options; it does not verify hauling behavior.

[Release checklist](MAINTAINER.md)
