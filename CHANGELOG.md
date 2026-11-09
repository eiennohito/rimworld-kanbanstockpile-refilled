## 1.1.3 — Adaptive Storage compatibility

- Max similar now follows changes to Adaptive Storage's slot limit.

## 1.1.2 — Continued compatibility

- Fixed storage limits being bypassed with Pick Up and Haul - Continued by Zzyxzz.
- Storage with limits uses ordinary hauling; storage set to Off keeps Continued's inventory batching.

## 1.1.1 — Compatibility fix

- Fixed the Storage tab failing to open with the updated Pick Up And Haul Reworked.
- Updated Reworked batch hauling to respect storage limits with its current planner.
- An optional hauling integration failing at startup no longer breaks storage controls.

## 1.1

- With Pick Up And Haul Reworked, pawns can bring several stacks to storage with limits in one trip.
- Other haulers count those planned deliveries toward the storage limit, including after saving and loading.
- The mod description links both earlier KanbanStockpile versions and explains the storage controls.

## 1.0.0 — Refill

- RimWorld 1.7 and .NET 10 support.
- Added Max similar, Refill Until and Refill From controls to the Storage tab.
- Pawns continue refilling above the chosen threshold and skip small final top-ups.
- Items already carried by Pick Up And Haul Reworked can be delivered to storage with limits.
- Cached storage contents and reservations to reduce repeated searches.
- Active hauls no longer reject their own reserved stack slot.
- Existing KanbanStockpile settings carry over. A partial stack may refill once after loading before it waits for the chosen threshold again.

# Upstream history

## v1.1.1
- #21 Add Stack Size Limit merged from @Tmq5521's fork! All credit to Tmq5521 !!!

## v1.1.0
- Add LoadFolders tags for Rimworld 1.4 compatibility
- Recompile with latest `Lib.Harmony 2.2.2` and `Krafs.Rimworld.Ref 1.4.3537`

## v1.0.9
- #20 SteamDeck update compatibility for RimWorld `v1.3.3311` fixes GUI not drawing issue
- #19 Improve slider widget response when Multiplayer Mod is not loaded (no need to debounce it)
- Update to `v0.4.0` of `0MultiplayerAPI.dll` sha1sum `c26e786f966fda39e7175e3218d58d74c68c1792` for RW >`v1.3.3311`

## v1.0.8
- #16 Add NULL check for map in case thing is coming from inventory etc
- Recompile against `0Harmony.dll` sha1sum `54a9eba73d266f1f475a6ff017d2cabd21986120`
- Recompile against `0MultiplayerAPI.dll` sha1sum `a1b16bdf024407ee22cfc01978c931ff149db9a1`

## v1.0.7
- #13 Just add version tags for Rimworld 1.3 with no code changes prepping for updates.

## v1.0.6
- #12 Stack Refill Threshold works properly now even when not using Similar Stack Limit

## v1.0.5
- #9 Add setting to limit non-stackable items made from different materials
- #10 Patch HaulToStorageJob to properly break a stack when refilling anywhere
- #11 Add setting to patch PUAH to prevent redundant/overhauling
- Fix bug where newly constructed storage did not have correct default values - credits bananasss00
- Renamed some Settings and updated default values and tooltips for best user experience

## v1.0.4
- Remove dependency on IHoldMultipleThings.dll when detecting building/deep storage vs cell storage stockpiles

## v1.0.3
- Aggressive Similar Stockpile Limiting now defaults to *ENABLED* - turn it off if it hits your performance too hard
- Aggressive Similar Stockpile Limiting now scans all outstanding reservations to prevent hauling duplicate stacks

## v1.0.2
- Add settings and feature flags with default to disable aggressive checking similar stack limit

## v1.0.1
- #1 Prevent multiple pawns from simultenously trying to haul too many similar stacks to same stockpile.
- #2 Fixup GUI widgets when installed alongside Stockpile Ranking mod.
- #3 Do not check for deep storage items when LWM Deep Storage is not installed.

## v1.0.0
- initial release
