# Game

## Manual plan selection

The GOAP simulation view now exposes manual planning controls that write the
following attributes for the player pawn:

- `@manual.interact.seq`
- `@manual.interact.x`
- `@manual.interact.y`
- `@manual.interact.hasTarget`
- `@manual.interact.planStep`

The dataset shipped under `Packages/DataDrivenGoap/Runtime/Data` must declare
the corresponding manual-action attributes and facts (including
`manual_interact_target`) so the simulation continues to fail fast if they are
missing. Update your dataset configuration alongside code changes whenever new
manual control channels are introduced.
