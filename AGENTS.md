# Refilled editing rules

Read PURPOSE.md before changing behavior and DESIGN.md before changing storage
or hauling code. Use BUILDING.md for validation commands and MAINTAINER.md for
local release work. Follow the workspace's docs/documentation.md audience policy.

- Keep README.md, PURPOSE.md and the About/About.xml description readable by
  players. About/About.xml is also the Workshop upload text. Update the README
  and description together when controls or advertised behavior change.
- Retain the linked continuation chain: Endag's KanbanStockpile Continued and
  ubergarm's original KanbanStockpile. Keep their credits, license and recorded
  source commit. Do not copy stale Workshop IDs from upstream descriptions.
- Describe thresholds as “at or below.” Size overrides apply only at Max similar
  = 1 and cannot exceed the item's stack limit. Refills may stop short of full.
- Keep method names, data structures, work costs and binary hashes in DESIGN.md.
  Keep local build paths, publication state and release checklists in MAINTAINER.md.
- For a text-only change, check links, XML, control labels and package contents.
  Do not launch the game or add tests solely to check prose. Use behavior tests
  when a claim needs verification beyond the existing evidence.
