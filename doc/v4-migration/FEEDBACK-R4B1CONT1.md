# Continuation feedback

Read FEEDBACK-R4B1.md for all current findings. The working tree does not compile; last evidence R4B1-compile-interim.log and R4B1-protocol-interim3.log. Expected independent suite now97 (accepted89 +6 lobby +2 runtime), not yet run against completed B1. Original R4A89/776 acceptance applies only to recorded earlier source. Current product remains V3.

22:41 reviewer added 3 further runtime tests (cumulative target100): version3 room push followed by version2 and duplicate3 must notify only3; pending getRoomInfo returning old1 after push2 must return latest2; successful leave must immediately fail pending old-room query and suppress captured old-room callbacks while unbound. Same transport connection epoch alone is insufficient for room-binding changes. Do not edit reviewer tests.
