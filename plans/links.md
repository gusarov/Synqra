# Plan: Links — SUPERSEDED

This as-built links plan has been folded into the unified model-substrate design.

**See [`../docs/model.md`](../docs/model.md).**

Links are no longer a subsystem of their own: under the ECS refactor a link is a **component with
a `Target`**. The design, the store-once + reverse-view adjacency, directed/undirected +
`LinkKey` folding, `[To]`/`[From]`/`[Related]` navigation, and every earlier decision (including
the ones this file used to document as current — links-as-own-kind, `Ref`, edges-as-objects) now
live in `docs/model.md`, with the superseded ones in its "Historical / superseded ideas" section.

The full prior content of this file remains available in git history.
