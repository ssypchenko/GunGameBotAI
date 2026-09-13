# Knife Rush

Knife Rush is an opportunistic, bounded state machine. Eligibility requires an
enabled runtime, a live bot, a visible opposing enemy, a valid distance, no
stuck recovery, and no cooldown. The random chance is rolled exactly once when
the bot enters a valid target encounter. A rejected roll is not rerolled until
the encounter ends.

An accepted rush records the target, start time, last visible time, current
weapon, and switch attempts. It activates a knife through the public command
backend, then uses short lateral movement and attack pulses in the fast
actuator. Target change, lost sight, excessive distance, timeout, death,
takeover, disable, round reset, or map change aborts the rush. Abort clears
buttons, starts cooldown, and attempts a safe weapon restore where possible.

Knife level is separate: it has mandatory knife chase semantics, no random
roll, and no opportunistic timeout/restore behaviour. Grenade level does not
start Knife Rush unless explicitly enabled in configuration.
