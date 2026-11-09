# Using Refilled

Select a stockpile or storage building and open its **Storage** tab.

- **Max similar** sets the number of stacks allowed for each item. **Off** leaves
  hauling to that storage unchanged.
- With **Max similar = 1**, **Refill Until** sets the stack size. Leave it empty
  for **Max Stack**, the item's usual maximum. It can lower that maximum, not
  raise it.
- **Refill From** sets how low a stack must get before pawns refill it.
  **100%** allows normal top-ups. After refilling starts, pawns continue above
  the threshold; they may skip a small final top-up.

For example, set a steel stack to 60 and Refill From to 50%. Pawns start
refilling at 30 steel or fewer. Deliveries already on their way count toward
the limit.

Click Refill Until to replace its value. Enter or clicking elsewhere saves it;
Escape cancels. An empty field restores Max Stack. Scroll over a number to change
it by one. The Max similar slider also accepts the mouse wheel and arrow keys.

## Compatibility and limits

Works with stockpiles, vanilla shelves and Adaptive Storage. Requires RimWorld 1.7
and Harmony. Enable Refilled in place of KanbanStockpile or KanbanStockpile
Continued; their saved storage settings carry over. RimWorld may warn about the
changed mod list when replacing a predecessor.

With **Pick Up And Haul Reworked**, pawns can bring several stacks for limited
storage in one trip, including food, hay and cloth. With original PUAH or Mlie's
version, storage with limits uses ordinary hauling. Construction deliveries
are unchanged.

Multiplayer is not supported. After loading a save, a partly filled stack may
refill once before it resumes waiting for the chosen threshold.

## Screenshots

[Storage controls](../Screenshots/01-storage-controls.png) ·
[Size limit and threshold](../Screenshots/02-single-stack.png) ·
[Refill cycle](../Screenshots/03-refill-cycle.png) ·
[Mod options](../Screenshots/04-mod-options.png)
