Character Control Physics

Character operates through game physics. While character has animations, it has no hard-coded movement in the physics environment. 

In the physics environment, character is represented as a polygon with mass, position, and velocity. The environment is treated as a static mesh. (For now, I should think about how to include moving platforms)

The character's surrounding environment generates active and available contacts. Some are constraints, such as the ground preventing downward movement, or walls preventing sideways movement. Others are support points, like a ledge corner allowing a character to vault upwards. 

Some physics interactions are completely mandatory. The character itself contains a core physics body, smaller than their user-presenting volume, and if this makes contact with a surface it is mandatory for it to generate a constraint. Other physics contacts are optional, such as a longer range one that keeps the character standing upright. In a given movement state, contacts determine what transitions and actions are available. 

The goal is for character control to be as flexible and intuitive as possible. It should be possible to interrupt movement transitions, chain them, and seamlessly use them when appropriate. The character will be jumping around in weird block environments all the time, so it's important they don't get stuck everywhere. But it's also important that the controls are followed specifically, so that the game isn't overly determining what the character does. In a lot of ways this'll be similar to a parkour engine. 

All movement is ultimately determined by physics forces/constraints applied to the player physics body. There are no hard-coded movement sequences, etc. Animations are built on top of physics and player movement states/actions, but never influence either. 

Player movement is as state-based as possible, with each movement state a subclass, and the complete set of movement states defining a FSM. The character is always in exactly one movement state.

Movement states have pre-conditions, conditions, and post-conditions. 
Movement states may only be entered under pre-conditions. 
Movement states are automatically exited when they no longer meet conditions. 
Movement states automatically apply post-conditions on exiting

Movement states are ordered. If multiple movement states meet criteria for initialization, 
the highest priority movement is selected. 

Heuristically, moves should be ordered to prioritize specificity and freedom. If a move like ledge grabbing is only possible when next to a ledge, pressing up, and pressing right, while normal jumping is possible whenever on ground, ledge grabbing should be prioritized. While falling is almost always possible, standing is preferrable since it offers the action of walking, etc

It should be generally possible to exit movement states into other movement states. For instance, if the up arrow is tapped again while grabbing the ledge, the character should enter a jump from the ledge. If the player is jumping, but taps down, the jump should be replaced by a drop. If the player is rolling, but taps jump, the player should exit into a jump, or roll jump if I code that. 

It should be as easy as possible to build new movement states. They should largely be bundles of properties, with a few optional state variables tracking things like percent progress through move, and a very few global variables. (tracking time since last roll, availability of double jump, etc) 

- Stand
- Walk
- Run
- Skid
- Falling
- Walk/Stand Jump
- Run jump
- Air jump
- Wall slide
- Wall grab
- Wall jump
- Wall push (upper jump) 
- Ceiling push
- Crouch
- Crouch walk
- Crouch jump
- Crouch into
- Vault over
- Ledge grab
- Ledge jump
- Ledge roll
- Ledge drop down
- Ledge drop from
- Ledge swing through
- Dash
- Dash jump
- Guard 
- Roll 
- Roll jump
- Land
- Hard land
- Launched
- Drop
- Slide down

In addition to their physics state (pos, vel, and contacts), and MovementState, characters should track some basic global information, such as whether double jump is available. Characters should also maintain a list of physics contact "checkers" which define whether a physics contact is available, but not necessarily active. For instance if the character is in the air, they should check each time step for whether a flat surface is close enough below them to count as available. Availability checkers should include:

- Flat ground (walk/jump support)
- Ceiling (ceiling push support)
- Lower corner (ledge drop down support)
- Mid height upper corner (vault over support) 
- High height upper corner (ledge grab / jump / roll support)
- Wall (wall jump support)
- Upper wall (wall push support) 

The basic loop is:
1. Read player inputs into controller buffer (arrow keys, spacebar, mouse, etc)
2. Iterate through availability checkers to build list
3. Feed input buffer, available, existent, and mandatory physics contacts, player physics state, and current movement state into MovementState FSM to generate next state, and any changes in available/existent contacts. 
4. Generate user applied forces on character from MovementState
5. Execute physics update

CASESTUDY
- Standing is a default activity, and players enter the standing state with no inputs. If player is dropped from a decent height onto a surface, they will iteratively check whether there is ground near enough to meet the requirements for generating a surface contact one unit or less below the player. If a surface contact can be generated, and no other movement state has higher priority, the player will generate the contact, and attempt to push themselves to the base standing height through it. If the surfaces generating the contact are destroyed or otherwise invalidated, the contact will be destroyed and the player will check whether it can be recreated elsewhere. At each timestep, the set of available contacts for generating the standing height may change. The player will pick which one to apply using priority, with a slight bias for height of contact, but also for continuity. 

The trouble with movestates is that I want conflicting properties from them. It's nice for move states to buffer, because this decreases the amount of input accuracy you're demanding from the player. For instance, if shift-right press triggers a roll to the right, but only on contact with the ground, then it's nice if triggering this in the air will buffer the move so that it activates instantly on contact with the ground. Then the player can easily execute frame perfect rolls on contact with the ground. 

At the same time, buffering is at odds with move canceling. I'd like as many moves to be cancellable as possible. So if shift-right can do something in the air, such as putting the player into guard state and moving them to the right, it should do that immediately. So the question is, what do I do here? I could attempt to predict what the player wants based on context, but that's complicated and likely annoying. 

PhysicsState: pos, vel, contacts
SurfaceForceContact: pos, norm, force
SurfaceConstraintContact: pos, norm, distance
PointContact: pos, distance, active, force

-In some cases, the number of available contacts may be extremely large, in which case it would be better to have a set of availability checker functions which movement states, etc can query. 

---

## TODO — First Implementation: Standing and Walking with Floating Contact

Goal: replace the raw hexagon test with a minimal character that can stand and walk on flat ground using the floating contact system.

### Step 1 — Make `FloatingSurfaceDistance` do something in the physics step

`FloatingSurfaceDistance` is defined but ignored. Add handling in `PhysicsWorld.StepSwept` that mirrors `SurfaceDistance` exactly:
- If the body's distance to the contact surface along its normal is less than `minDistance`, zero out velocity into the normal (prevent penetration).
- Nothing else. The physics step does not apply upward push, spring forces, or decide when to drop the contact. That is all the character logic's responsibility.

### Step 2 — Ground availability checker

Add a static probe function: `GroundChecker.TryFind(PhysicsBody body, ChunkMap chunks, float standHeight, out FloatingSurfaceDistance contact)`.
- Cast a thin downward strip from the body's bottom edge, up to `standHeight` pixels below.
- If any solid flat-topped tile surface is found, return a `FloatingSurfaceDistance` targeting that surface at the standing height.
- "Flat-topped" means the tile surface normal is (0, -1) — upward-facing. Sloped surfaces are out of scope for this step.
- This checker is called every frame to refresh or create the floating contact.

### Step 3 — `PlayerCharacter` class and minimal movement state wiring

Replace the hexagon in `Game1` with a `PlayerCharacter` that wraps a `PhysicsBody` and owns a `MovementState`. Start with two states:

**`Falling` state (default / lowest priority)**
- Always valid.
- Applies no horizontal forces.
- Each frame: runs ground checker. If ground found within range, transitions to `Standing`.

**`Standing` state**
- Pre-condition: ground checker returns a contact.
- On enter: adds the `FloatingSurfaceDistance` to the body's constraint list.
- Each frame, before the physics step:
  - Re-runs ground checker to refresh the floating contact (update its `Position` and `MinDistance` if the surface changed).
  - If ground checker fails (no surface found), remove the contact and transition to `Falling`.
  - Compute signed gap: `gap = (body.Position - contact.Position) · contact.Normal - contact.MinDistance`. Negative gap means body is above target height; positive means below. Apply an upward velocity correction proportional to the gap (or a fixed snap velocity) to drive the body to the float height. This is the only place upward maintenance force is applied.
  - If `Left` or `Right` is held, apply a horizontal force (e.g., 500 px/s²). Clamp horizontal velocity to a walk speed cap.
  - Apply a horizontal damping force to slow the body when no input is given (friction-like, not a hard stop).

### Step 4 — Wire the loop in `Game1`

Replace the direct force-from-keys logic with:
```
1. controller.Update(mouseWorldPos)
2. playerCharacter.UpdateMovementState(controller.Current, chunks)   // runs checkers, transitions, applies forces
3. PhysicsWorld.StepSwept(bodies, chunks, dt, gravity)
4. camera.TrackTarget(playerCharacter.Body.Position, screenCenter)
```

### Out of scope for this step
- Sloped ground, partial tiles
- Jump
- Any state other than Standing and Falling
- Wall/ceiling contacts
- Animation
