# Refilled maintainer notes

For the workspace owner. Player instructions are in README.md; implementation
and test details are in DESIGN.md and BUILDING.md. Agent rules are in AGENTS.md.

## Release files

The Workshop item is [3832323892](https://steamcommunity.com/sharedfiles/filedetails/?id=3832323892).
Keep `About/PublishedFileId.txt` in packages so an upload updates that item.
The package ID is `eiennohito.kanbanstockpile.refill`. RimWorld's uploader takes
the description from `About/About.xml`; it is also shown in the game's mod list.

Release 1.1.1 fixes compatibility with the updated Reworked planner and contains
optional integration startup failures. About/About.xml records version 1.1.1; the
assembly version is 1.1.1.0. Release notes are in the 1.1.1 section of CHANGELOG.md.
Check the installed and published versions when selecting a release package.

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

## Steam change notes

Uploading CHANGELOG.md does not publish its contents in Steam's Change Notes.
The inspected RimWorld uploader submits a dated, auto-generated update message
instead. BUILDING.md records the code and API evidence.

To publish the 1.1 notes with the package, use an uploader that accepts a change
note. Valve's [SteamCMD upload instructions](https://partner.steamgames.com/doc/features/workshop/implementation#SteamCmd)
support a `changenote` field in a Workshop VDF. For this item, set:

- `appid`: `294100`
- `publishedfileid`: `3832323892`, to update the existing item
- `contentfolder`: the absolute package path printed by `tools/rw latest kanban-refilled`
- `changenote`: the text of the 1.1 entry in CHANGELOG.md, with a plain `1.1` heading
- `description`: the text inside About/About.xml's description element, when updating the description too

Run `steamcmd +login ACCOUNT +workshop_build_item /absolute/path/to/release.vdf +quit`
with the owning Steam account. This command publishes the supplied files and
metadata. Keep an upload VDF in leased session scratch, outside the mod package.
Existing fields omitted from the VDF are left unchanged.

After a successful upload, check the item's
[Change Notes](https://steamcommunity.com/sharedfiles/filedetails/changelog/3832323892).
The 1.1 text should appear there as the update note, separately from the main
description. The owner-only controls and a live upload were not tested here.

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

## Version 1.1.1 — 2026-11-04

Compatibility repair for the current Reworked planner. The repair passed 86
headless checks with Reworked and 53 without it on RimWorld 1.7.4998 rev1299;
DESIGN.md records coverage and limits. Only release metadata changed afterwards.

Version 1.1.1 was published to item 3832323892 using validated package
`20261103T234538-bc2aaee9`. SteamCMD authenticated with cached credentials,
committed the update successfully and exited zero. The wrapper verified that the
Workshop item ID remained unchanged and saved a published receipt. The upload
included the 1.1.1 compatibility notes, description and main preview; tags,
visibility and carousel images were left unchanged.

The [publication receipt](../../state/publications/kanban-refilled/kanban-refilled-20261103T234841-5d3e9e3e.json)
records the package, release note and result. The first attempt stopped at login
before upload; the retry succeeded after local authentication. The public Change
Notes page could not be independently verified through the available web access.
Leased upload evidence is in `sessions/refilled-1-1-1-release/`.
Local installation and activation were not changed.
