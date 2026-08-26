# Floating Writing Window

These rules extend `design-system/vesper/MASTER.md` for the compact floating
editor.

## Surface priority

The text area is the primary surface. Header and footer are utility rails, not
additional cards. Keep input text readable at the default window size and reserve
a stable 34px footer for actions.

## Mode switcher

Use one compact `72×28px` morphing carousel and keep it to a single toolbar slot.
In prompt mode, `提示词` sits on the left and the purple star thumb sits on the
right; in polish mode, the star sits on the left and `润色` sits on the right. During
the short transition, the skin accent surface expands across the capsule and shows
`提示词 | 润色`, then contracts around the star at the destination. The entire
capsule is one click target and cycles mode on click or mouse wheel. The thumb is a
plain circle with no decorative glyph and always uses the active skin's `BrandBrush`.
It never changes
its outer width and it never pushes any footer action onto another row.

## Footer controls

Actions share one baseline at every supported window width and use icon buttons for
clipboard/history/diff plus one accent action button. Do not hide or move core actions
to another row to make the rail fit. Keep at least 8px between groups. Long diagnostic messages
belong in a compact inline status, never a modal that pushes the writing surface.
