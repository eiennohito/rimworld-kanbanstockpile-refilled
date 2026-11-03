# Building and testing

These instructions are for contributors using the RimWorld modding workspace
that contains this checkout. Run the commands from the workspace root.

## Requirements

RimWorld 1.7, the .NET 10 SDK and Harmony's 1.7 assemblies are required.
`tools/dotnet/RimWorld17.props` supplies references to the original game
assemblies. The workspace supports 1.7 only.

```sh
tools/rw build kanban-refilled
tools/rw test kanban-refilled --suite runtime
tools/rw test kanban-refilled --suite runtime --with dreadarm.pickupandhaul
```

The runtime fixture is in `tests/runtime/`. It checks storage settings, refill
quantities, incoming deliveries and the optional Reworked adapter using real
game types. It does not simulate a complete pawn hauling route. See DESIGN.md
for the rules under test, implementation costs and inspected binary versions.

The build prints a validated unpacked package under `outputs/mods/`. Compiler
output and test hosts are leased session files. Test fixtures and screenshots
are not included in the runtime package. Builds do not install or publish the
mod. The owner's installation and Workshop steps are in MAINTAINER.md.

## UI captures

The existing screenshots show native storage controls. Capture new ones when a
control changes or a specific visual question needs checking:

```sh
tools/rw test kanban-refilled --suite screenshots
```

The fixture in `tests/screenshots/` uses the shared isolated launcher, a private
virtual display and muted no-device audio. It produces documentation images;
it does not verify hauling behavior. Use headless checks for quantity and
compatibility changes, and do not repeat them in a graphical scenario without
an unresolved question that requires it.

## Workshop uploader behavior

RimWorld's uploader uses About/About.xml for the description and About/Preview.png
for the main image. A package upload does not attach carousel screenshots.

This was checked against RimWorld **1.7.4995 rev309** on 2026-11-02, with
Assembly-CSharp.dll SHA-256
`56399b6144d9f1695f9c53d054dc82972f7ab35e68d1a19a7129f2f5860ccd21`.
`Verse.Steam.Workshop.SetWorkshopItemDataFrom` calls `SetItemPreview` and
`SetItemContent`, with no additional-preview API calls. This is evidence about
that binary; the owner-only Workshop image manager was not inspected.

Automation for additional images would use `AddItemPreviewFile` on an item
update handle, then `SubmitItemUpdate`. Query existing attachments and use
`UpdateItemPreviewFile` to replace one, rather than appending duplicates.
The [Steamworks preview API](https://partner.steamgames.com/doc/api/ISteamUGC#AddItemPreviewFile)
documents the accepted formats and size limit. The existing screenshots are PNGs
below 167 KB each. Owner instructions for manual uploads are in MAINTAINER.md.
