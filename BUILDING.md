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
in a headless host, plus Continued's capacity adapter when it is loaded. They
do not simulate a pawn's complete hauling route.
[Implementation notes](DESIGN.md) describe the rules, work costs and test limits.

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
