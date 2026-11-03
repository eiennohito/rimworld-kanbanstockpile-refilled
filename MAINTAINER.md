# Refilled maintainer notes

For the workspace owner. Player instructions are in README.md; implementation
and test details are in DESIGN.md and BUILDING.md. Agent rules are in AGENTS.md.

## Release files

The Workshop item is [3832323892](https://steamcommunity.com/sharedfiles/filedetails/?id=3832323892).
Keep `About/PublishedFileId.txt` in packages so an upload updates that item.
The package ID is `eiennohito.kanbanstockpile.refill`. RimWorld's uploader takes
the description from `About/About.xml`; it is also shown in the game's mod list.

Reworked batching is listed under Unreleased in CHANGELOG.md. The description
in About/About.xml includes that change. Check the installed and published
versions when selecting the next release package.

From the workspace root:

```sh
tools/rw build kanban-refilled
tools/rw latest kanban-refilled
tools/rw install kanban-refilled --dry-run
tools/rw install kanban-refilled
```

Builds produce a validated unpacked mod under `outputs/mods/`. Installation
replaces local copies in the configured RimSort instance and retains rollback
backups. It does not change the active mod list or update Workshop. Publish the
selected package through RimWorld's uploader when making a release.

## Verification notes — 2026-11-03

The Reworked integration passed 78 headless checks with Reworked and 49
without it. The tests cover storage quantities,
batch allocations, vanilla incoming quantities, failed pickups, cancellation,
carried cargo and saved destination records. They do not execute a pawn's
complete route from collection to delivery.

Leased evidence is in `sessions/refilled-puah-batching` and
`sessions/refilled-puah-optional`. It expires under the workspace lifecycle rules;
pin or deliberately save a report if it is needed for a release record.

Housekeeper Cats were previously reported to work in a modded colony. That was
an informal observation, not a dedicated compatibility test, so it is not listed
as tested support in player documentation.

## Workshop carousel screenshots

RimWorld's uploader uses `About/Preview.png` as the main preview. Upload the
additional carousel images separately from `Screenshots/`; they are not included
in the mod package.

1. Open the Workshop item and choose **Owner Controls → Add/edit images & videos**.
   The direct management URL is
   <https://steamcommunity.com/sharedfiles/managepreviews/?id=3832323892>.
2. Choose and upload the four PNGs from this repository's `Screenshots/` directory,
   in filename order (`01-storage-controls.png` through `04-mod-options.png`).
3. Confirm their order in the image manager, then choose **Save and Continue**
   (or the equivalent save control) and check the item's carousel.

Carousel images are separate from the downloadable mod files. Update changed
screenshots through the image manager when releasing new ones. BUILDING.md
records the uploader behavior and API references for future automation.
