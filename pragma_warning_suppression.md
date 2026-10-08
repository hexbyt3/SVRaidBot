# Warning Suppression

The repository-wide warning suppression is configured in `Directory.Build.props` through the `NoWarn` property. This is the MSBuild/project-wide equivalent of applying `#pragma warning disable`; it applies to every project importing the shared file, including the local `TradebotCore` projects.

## Suppressed IDs

| Warning | Meaning | Why suppressed |
| --- | --- | --- |
| CS0108 | Member hides an inherited member | Existing intentional or legacy member hiding. |
| CS0169 | Private field is never used | Existing unused state retained for compatibility. |
| CS0219 | Local variable is assigned but never used | Existing compatibility code and command handlers. |
| CS0414 | Field is assigned but never used | Existing optional configuration/runtime state. |
| CS0649 | Field is never assigned | Fields populated by serializers or external/runtime code. |
| CS8602 | Possible null dereference | Existing code relies on initialized runtime services/configuration. |
| CS8603 | Possible null reference return | Existing APIs preserve nullable-compatible legacy behavior. |
| CS8604 | Possible null argument | Existing runtime validation/configuration guarantees are outside static analysis. |
| CS8618 | Non-nullable member is not initialized | Members initialized by deserialization or startup wiring. |
| CS8622 | Nullability mismatch in delegate parameter | Existing event-handler signatures required by third-party APIs. |
| CS8629 | Nullable value type may be null | Existing code relies on validated runtime values. |

## Scope and maintenance

These suppressions are intentionally broad so the current solution builds consistently across all projects. New code should still avoid introducing nullable or unused-member warnings. When a warning can be fixed safely at its source, prefer the source fix and remove that ID from `NoWarn` only after all affected projects are clean.

The suppression list was derived from the warning IDs emitted by a full `dotnet build` on 2026-09-25. It suppresses compiler warnings only; build failures and analyzer warnings with other IDs remain visible.
