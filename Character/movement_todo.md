1. a stair-climb test which confirms that the player walking into stairs with slope 45 degrees will walk up smoothly, with continuously chained move-up moves (probably mantlestate)
2. a jump into cave test confirming that when the player holds left/right while falling and runs into the mouth of a cave, the corrector assists them in not running into the upper lip of the cave
3. general principle to never allow moves to push player past max-jump speed
4. edit jump so that it can use ledge corners as a pushing off point, for the case of jumping out of a ledge grab or vault
5. add tests confirming that moves like vault never significantly push the player's horizontal movement speed past normal running speed
6. edit the 2 block arc to only activate when the player is running in with up arrow held down. when the player is still, and within range of a two block ledge, holding up arrow should trigger ledge grab
7. use the reference trajectory system for ledge pull
8. use reference trajectories for the drop down move