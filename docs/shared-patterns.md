# Shared patterns

The plugin consumes the optional `gungame:api` capability when it is present.
The capability supplies current and maximum GunGame levels, including the
knife-level predicate. Missing capability data does not disable the core bot
runtime; weapon inventory classification remains the fallback.

Only valid connected bots are eligible: human controllers, HLTV clients,
invalid handles, dead pawns, and taken-over bots are excluded. All writes run
on the game thread and are guarded by current validity checks.

Weapon classification is based on the designer name, with optional VData gear
slot fallback. `taser` is never classified as a knife. The active weapon is
verified from `WeaponServices.ActiveWeapon`; direct writes to its handle are
not permitted.
