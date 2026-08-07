# Handoff: mintplayer-ng-bootstrap components for the Coverage project

Work items for a Claude session running in `C:\Repos\mintplayer-ng-bootstrap`.
Follow that repo's CLAUDE.md conventions strictly (WC-first authoring, codegen-wc after
SCSS edits, aria conformance suite registration, a11y checklist, demo page with
ts-dedent snippets). No custom generators exist — copy the `treeview` component's file
shape (11 files WC-side, 9 Angular-side). No project.json edits needed for plain
components. React/Vue wrappers optional; flag the deviation in the PR if skipped.

Consumer: MintPlayer/CodeCoverage (`Coverage/ClientApp`), currently on
`@mintplayer/ng-bootstrap` ^22.13.0.

## 1. `mp-code-viewer` + `bs-code-viewer` (priority 1)

A source-code viewer with a per-line annotation API. Extend/reuse the highlight.js
integration living in `libs/mintplayer-web-components/code-snippet`
(`mp-code-snippet.element.ts`) — but as a NEW component (code-snippet's contract stays).

Requirements:
- Line numbers with per-line ids/anchors (`#L42`-style deep links; expose a
  `scrollToLine`/`activeLine` property).
- Generic `lineAnnotations` input: `{ line: number; kind: string; label?: string }[]` —
  kind maps to a CSS class (`annotation-<kind>`) with theming hooks, label renders in a
  gutter column. Keep it coverage-agnostic (Coverage feeds kinds covered/partial/uncovered
  with hit-count labels); document theming via CSS custom properties.
- Syntax highlighting via highlight.js with language auto-detect + explicit override
  (same as code-snippet), but **theme must follow `data-bs-theme`** — code-snippet's
  hard-coded a11y-dark theme needs a light counterpart.
- Horizontal scroll inside the component; long files: consider virtualization later,
  plain render is acceptable v1.
- A11y: the gutter is presentational; annotations need an SR-readable alternative
  (e.g. aria-label per annotated line); keyboard navigation to the line anchors.

Consumer integration (in CodeCoverage repo): replace the hand-rolled renderer in
`Coverage/ClientApp/src/app/pages/file/file.component.*` — it already produces
`{ number, text, status, hits, branchInfo }` rows, so the mapping is mechanical.

## 2. Circle-packing / sunburst coverage chart + radial progress (priority 2)

- `coverage-chart-core` (or `hierarchy-chart-core`): headless, DOM-free layout solver
  (sunburst arcs and/or circle packing) — pure TS, unit-tested, following the
  `timeline-core`/`scheduler-core` split convention.
- `mp-sunburst` (or `mp-circle-pack`) WC + `bs-` wrapper: generic input
  `{ name, value, color?, children? }` hierarchy; hover tooltips via the shared
  `OverlayController` (`libs/mintplayer-web-components/overlay/src/overlay-controller.ts`);
  `segment-click` event with the node path (Coverage navigates the folder tree with it);
  keyboard-operable (roving focus across segments) + SR alternative (nested list).
- **Sizing trap** (repo CLAUDE.md): a host with `container-type: inline-size`
  contributes zero intrinsic inline size and collapses to 0px in shrink-to-fit
  contexts — the chart must take an explicit inline size from outside.
- `mp-progress-circle`: radial progress ring (value/min/max/color/label slot) for the
  headline coverage number. Nothing radial exists in the workspace today (verified —
  only the color-picker wheel uses conic-gradient).

Consumer integration: commit page (`pages/commit/commit.component.*`) — diagram beside
the folder-tree card, click-through to `openFolder(path)` / file view.

## 3. Small extras noticed while consuming 22.13 (optional)

- `bsShellTopbar` structural directive: both WebhooksDemo (MintPlayer.Spark) and
  CodeCoverage carry an identical local copy stamping `slot="topbar"` — its own TODO
  says "promote to @mintplayer/ng-bootstrap/shell".
- ng-bootstrap's `_bootstrap.scss` emits Sass `@import` deprecation warnings on every
  Angular build (routed to stderr, so SPA middlewares log them at fail level) —
  migrating to `@use` would silence noise in every consumer.
- `bs-progress-bar` binds its host `class` attribute to the computed color class, so
  consumer-supplied classes on the element are overwritten — worth a note or fix.
