# Releasing Refilled

The existing [Workshop item](https://steamcommunity.com/sharedfiles/filedetails/?id=3832323892)
is `3832323892`; the package ID is `eiennohito.kanbanstockpile.refill`.
Keep `About/PublishedFileId.txt` in the package so updates target that item.

## Prepare the release

- Update `About/About.xml` and `Source/Properties/AssemblyInfo.cs` when changing
  the version, and add the release notes to `CHANGELOG.md`.
- Keep the controls and compatibility claims consistent across `About/About.xml`,
  `README.md` and `docs/usage.md`. The About description is also the Workshop text.
- Build the package and run checks appropriate to the change; see [building and testing](BUILDING.md).

## Publish

From the configured workspace, with desktop Steam online:

```sh
tools/rw publish kanban-refilled --dry-run
tools/rw publish kanban-refilled
```

Review the staged description and change note before uploading, then check the
live Workshop item. The [shared publishing guide](https://github.com/eiennohito/rimworld-mods/blob/main/tools/README.md#workshop-updates)
covers authentication, custom notes, receipts and unconfirmed uploads.

`About/Preview.png` is the main Workshop image. The four additional images in
`Screenshots/` are managed separately through the item's
[image manager](https://steamcommunity.com/sharedfiles/managepreviews/?id=3832323892).
They are not included in the mod package.
