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

## TODO
Right now MovementStates follow an explicitly designed FSM, in which each MovementState update must consider all possible transitions to other movement states
based on player controls. 

I'd like to update this to an implicit FSM in which MovementStates return control to a hander function after every update. The handler function should have access to player position and velocity, to mandatory physics contacts (those forced by the existence of the player polygon), and to a list of all available physics contacts, as well as their active or inactive status. It should also have access to the current MovementState, and associated data such as time in the movement state, whether the movement state has marked itself as completed, etc. 

Each MovementState subclass should be implemented with a list of PreConditions, Conditions, and PostEffects (such as marking double jump unavailable). For instance, Standing requires an available surface to activate GroundChecker as a PreCondition to enter the Standing state, as well as as a Condition to remain in the Standing state. Jumping on the other hand requires ground as a PreCondition, but not as a Condition. DoubleJumping has "in air" as a PreCondition, and should remove double jump availability as a post-effect. After activation, the player may continue jumping to modulate jump height as currently implemented. 

Each MovementState should also implement (or use default values for) its ActivePriority and PassivePriority. When in progress, movements should have priority ActivePriority. When considered as options, movements should have priority PassivePriority. 

# Implicit Priority-Driven FSM Refactor

Based on the `physics_outline.md` todo section and the current state of the codebase, this document outlines the fundamental architectural shift from an **Explicit FSM** (where states are responsible for deciding what state comes next) to an **Implicit, Priority-Driven FSM** (where a central handler continuously evaluates environmental conditions and chooses the highest-priority state).

## 1. Environmental Context and Physics Contacts
Rather than evaluating abstract flags like `IsGrounded` up-front, the central handler will provide an `EnvironmentContext` that models spatial interactions as concrete `PhysicsContact` objects. Because generating all possible contacts for every tile near the character is computationally wasteful, this context should categorize contacts and generate them on-demand:

* **Mandatory Contacts**: These are hard constraints resulting from the character polygon directly colliding with world geometry. These are automatically accumulated during the physics sweep step and made available to the handler so states know what the character is physically bounded by.
* **Available Contacts (Checkers)**: A suite of lazy-evaluation functions (like `GroundChecker` or `WallChecker`) accessible through the `EnvironmentContext`. These functions probe the space around the polygon and return transient contacts (e.g., a `FloatingSurfaceDistance` contact if ground is within step-down range, or a wall contact for sliding). 
* **Active Contacts**: The subset of Available Contacts that a movement state explicitly chooses to adopt and apply to the `PhysicsBody` constraints. For instance, when `StandingState` evaluates `CheckPreConditions`, it queries the ground checker; if an available ground contact exists, it claims it, moving it to Active status, which allows the physics engine to apply the spring push-back force.

* **Create a `PlayerState` / `AbilityTracker`**: A container holding persistent movement flags (e.g., `TimeInCurrentState`, `CanDoubleJump`, `HasDashed`).

## 2. Refactoring the `MovementState` Base Class
The `MovementState.cs` base class will be heavily expanded from a simple `Update` method to a full lifecycle pipeline. You will need to add:

* **Properties**:
  * `ActivePriority` (int/enum): How hard this state fights to stay active once it has started (e.g., a hard-locked animation like a vault might have ultra-high active priority).
  * `PassivePriority` (int/enum): How eagerly this state wants to take over when considered from the background (e.g., `Standing` beats `Falling` if ground is present, `WallJump` beats `WallSlide` if input is pressed).
* **Methods**:
  * `CheckPreConditions(...)`: Evaluates the input buffer, `EnvironmentContext`, and `PlayerState` to return a `bool` determining if the move can *begin*.
  * `CheckConditions(...)`: Evaluates if the move is allowed to *continue* (e.g., `Standing` fails if `IsGrounded` becomes false).
  * `ApplyPostEffects(...)`: Fired strictly when exiting the state (e.g., stripping the `CanDoubleJump` flag, clearing specific physics constraints).
  * `Update(...)`: Will no longer return a `MovementState`. It will now return `void` or a status enum (like `InProgress`, `Finished`) and solely focus on injecting `AppliedForce` to the `PhysicsBody`.

## 3. Creating the `MovementHandler`
This will likely act as the brain inside `PlayerCharacter.Update` (or separated into a dedicated handler class).

* **State Registry**: The handler needs a list of all possible movement states instantiated in memory (or a factory to spin them up).
* **The Evaluation Loop**:
  1. Gather inputs and populate the `EnvironmentContext`. Also include the set of available physics contacts (ground contacts, wall contacts, etc) in EnvironmentContext. 
  2. Check if the *Current State* meets its `CheckConditions()`. If it fails, call its `ApplyPostEffects()` and set current state to a null/fallback state (like `FallingState`). 
  3. Iterate through all states in the registry. For those whose `CheckPreConditions()` return true, evaluate their `PassivePriority`.
  4. Compare the highest eligible `PassivePriority` against the current state's `ActivePriority`. If the challenger wins, call `ApplyPostEffects()` on the old state, and swap to the new state.
  5. Run the new/surviving State's `Update()` to apply physical forces.

## 4. Adapting Existing States
You will need to decouple your current states from each other.

* **`FallingState`**:
  * *PreConditions*: Always true (acts as the ultimate fallback).
  * *Conditions*: Always true.
  * *Priority*: Lowest passive, lowest active.
* **`StandingState`**:
  * *PreConditions*: `EnvironmentContext.GroundChecker.TryFind(...)` returns an available `FloatingSurfaceDistance` contact.
  * *Conditions*: `EnvironmentContext.GroundChecker.TryFind(...)` continues to return a valid contact.
  * *Priority*: Medium passive (beats falling).
* **`JumpingState` & `WallJumpingState`**:
  * *PreConditions*: `Input.Jump` == true && (`StandingState` provides its active ground contact, OR `EnvironmentContext.WallChecker` provides an available wall contact).
  * *Conditions*: `Input.JumpHold` == true && `TimeInState < MaxHoldTime`.
  * *PostEffects*: Mark jump as consumed for this air-cycle.
  * *Priority*: High passive (interrupts standing/falling instantly), High active (prevents being overridden by falling until the jump arc is complete).

## 5. Implementation Roadmap
To avoid breaking the rendering or physics loop during this transition, consider doing it iteratively:

1. **Data Structures First**: Create `EnvironmentContext` and `AbilityTracker`. Update `PlayerCharacter` to build this context at the top of the frame.
2. **Expand the Base Class**: Add the Priorities, PreConditions, and PostEffects to `MovementState` without changing the `Update` signature just yet.
3. **Build the Handler**: Add the priority-evaluation loop into `PlayerCharacter.cs`. Let it pick the next state, but temporarily allow states to force-return transitions to ensure backward compatibility while you migrate.
4. **Port States One by One**: Remove the hard-coded `return new XXXState()` calls from your states one at a time, moving that logic into `CheckPreConditions` and setting up their priorities.
5. **Cleanup**: Change `MovementState.Update` to return `void` and rely 100% on the Implicit Handler.

Adding new movement states like a `LedgeGrabState` later will only involve creating the class with a high `PassivePriority`, writing a `CheckPreCondition` looking for ledge geometry, and dropping it into the registry. The Handler will securely manage the transitions.