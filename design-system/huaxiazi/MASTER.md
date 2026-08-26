# Huaxiazi Native Desktop Design System

> Implementation brief for the WPF desktop client. Web-only tokens, remote fonts,
> CSS components and responsive breakpoints are not part of the product stack.

**Product:** Huaxiazi — a compact desktop writing assistant
**Design direction:** restrained dark utility / quiet focus
**Primary goal:** keep the writing surface dominant and every control legible,
without modal noise or layout shifts.

## Visual language

- Preserve the existing dark Huaxiazi visual language; do not introduce a second
  cream/amber theme on individual pages.
- Use semantic WPF resources: `BgBrush`, `PanelBrush`, `InputBrush`, `DockBrush`,
  `BrandBrush`, `MutedBrush`, `TextBrush`, `InputBorderBrush` and `DangerBrush`.
- Lavender is an action/selection accent, not a blanket fill. Error and destructive
  actions use `DangerBrush`; status must never rely on color alone.
- Avoid decorative gradients, large shadows, emoji-as-icons and hover transforms
  that move neighboring controls.

## Type hierarchy

Use the app's `AppFont` (no online font import):

| Role | Size | Weight | Use |
|---|---:|---|---|
| Window title | 15–16 | SemiBold | one per page/window |
| Section title | 13–14 | SemiBold | major settings groups |
| Body/control | 12–13 | Regular | labels, buttons, input text |
| Helper/status | 11–12 | Regular | concise explanation and state |

Use sentence-case Chinese labels. Remove redundant helper copy and expose details
only in a secondary/advanced state.

## Spacing and geometry

Keep a 4/8/12/16/24 rhythm. A container's outer padding must be at least as large
as its internal control gap. Text inputs use 10–12px horizontal padding and 6–8px
vertical padding; never let text touch a border or become clipped.

- Settings shell: fixed left navigation, content frame with 28px left, 18px top,
  18px right padding, and one owner for vertical scrolling.
- Cards: 16px outer inset, 12px internal gap, 1px border.
- Buttons: 28–32px high, 10–14px horizontal padding, compact labels.
- Floating toolbar: 34px high; controls share a baseline and do not reflow when a
  mode changes.

## Interaction principles

- Prefer one clear action per region; make selected, disabled and busy states
  visible without moving the layout.
- Use `Button`/`ToggleButton` semantics instead of clickable containers. Keyboard
  focus must be visible.
- Keep mutually exclusive states mutually exclusive in the visual tree (for
  example, a skill detail pane and its empty state must never render together).
- Scrollbars belong to the content owner and have a visible inset from the shell;
  never overlay the navigation border or a card edge.
- Respect reduced-motion preferences. Transitions are optional and never required
  for correctness.

## Release checklist

- No overlapping empty/detail/editor states.
- No fixed-width control squeezes the writing surface at the supported window size.
- Text is readable at the default scale and remains visible at enlarged scale.
- All controls have a visible focus/disabled state and a concise accessible name.
- WPF smoke tests and the release packaging script pass before delivery.
