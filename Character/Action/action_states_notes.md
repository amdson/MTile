Null
Ready
Recovery
Slash1 -> Slash2 -> Slash3
AirSlash1 -> AirSlash2
Stab

- Make player actions as free as possible
    - Don't lock players into moves. 
    - When possible, avoid ignoring inputs. 
    - More subtly, don't have high timing requirements for movements. Things like chaining attacks shouldn't have microsecond timing requirements. E.g. Three rapid clicks should trigger three slashes, even if they're not applied at the same time. 
    - 2 second input buffer. 
    - Allow for smooth transitions between moves, and avoid fully cancelling active actions when possible. E.g. when the player lands while executing an air slash, it should convert to a ground slash, and vice-versa. 
- Make actions as intuitive as possible
    - Predictable results from inputs
- Don't let players spam moves. 
    - Start-up time for bigger moves
    - Small recovery time for moves, even when they are cancelled. E.g. don't let player immediately swap from large stab attack to a slash. 

Scenario:
Frame 0: Player is in Null, Mouse up -> Mouse down
Frame 3: Mouse up
Frame 5: Player enters Slash state
Frame 6-9: Player executes Slash
Frame 7-9: Mouse up -> down -> up 
Frame 10: Player chains into Slash2. 

Scenario 2:
Player rapid clicks 3x times
Player runs S1, S2, and S3 in quick succession

Scenario 3:S
Player rapid clicks 4x times
Player runs S1, S2, and S3 in quick succession
Player runs S1 again

Scenario 4:
1. Player initiates stab
2. Midway through stab player releases left mouse
3. Player goes to Recovery?

Scenario 5:
1. Player initiates stab
2. Midway through stab player releases left mouse
3. Player immediately fast left clicks
4. Player transitions into Recovery
5. As soon as possible, player transitions into Slash

Scenario 6:
1. Player initiates slash
2. Midway through slash player initiates stab
3. Stab is initiated immediately **after** slash executes. 

Decisions/Problems:
1. Whether to add input lag as a buffer
2. Whether a Ready state is necessary. (And does it eliminate the need for a buffer?)
- It seems like it would store a "readiness" value dynamically, increasing with time, and moves would have certain readiness values as a precondition. Sometimes a high readiness would allow a move to begin at a more advanced stage? 
3. Whether a Recovery state is necessary. 
- It's a problem if moves can run immediately after each other, or if it's signifianctly more DPS to do some weird input cancel loop with slashes and stabs or whatever. 
- Recovery states could fix that. Moves could transition into Recovery states, with parametrized values that tick down over time. Big moves, like Stab, could transition into higher valued recovery states, even when cancelled midway through. 
- If I want to be clever I can allow recovery to transition into ready. Maybe at a discount, so that higher valued recovery transitions into lower valued readiness? I could also fuse the Recovery and Ready states. 
- I'd need to cleanly handle parametrized states. Moves would be capable of initiating from different values of Readiness. 
4. How to handle multi-stage results from inputs. 
- E.g. suppose the stab attack always stars with the Ready state. The player initiates Stab. How should I program the transition from Null -> Ready -> Stab
- I think in most cases I can simply pick next-states greedily (e.g. left click down starts Ready state, left-click down-up allows player to enter Ready, and then stay in it until left click release enables slash attack. Longterm I could run an A* like algorithm to predict the best sequence of transitions, but that's pretty fucked up so I'd like to avoid it for now. 
5. How to handle chains of attacks. 
- Suppose the player fast left clicks three times in a row. How should the game determine that it should chain three slashes in a row, instead of starting a slash, preempting it, and then preempting it again? Probably based on priority. A new Slash should have lower priority than a running slash, so that the running slash completes before the new slash can execute. We can set a "S2-Ready" flag in the final steps of the first slash to allow transition into the second slash, and a similar one to allow transition into the third. 
6. How to handle transitions between similar attacks. 
- Crouch slash, slash, and air slash should all be partially interoperable. 
- I could work out a system for allowing one to transition into the other in a partially complete state, but this is pretty complicated. For now I'd like to make slash attacks simply continue, mostly regardless of movement state, but depend on movement state to determine which one starts initially. 
7. How to prevent the input buffer parsing from repeatedly triggering attacks. E.g. if I fast click once, a slash attack plays. Then, if I naively pattern match the buffer (saying, say, that slash can buffer for 1s), slash will retrigger from the same input. 
- I could try parsing the entire buffer into a sequence of actions all at once, but this is pretty complicated. 
- Unfortunately, some inputs truely should correspond to multiple actions. E.g. entering ready with left click down, then slashing with left-click down-up. 


Slash: Mouse click (Mouse up -> down -> up within four frames)
An annoying thing about slash is that I'd like it to be very fast, but I can only decide whether to execute it after seeing that the player released the left mouse. 

One workaround would be to replace the very short initial phase of slash with a "Ready" state that the player enters immediately upon pressing left mouse down. Then when left mouse is released, player is in ready state, and they can immediately execute the rest of the slash attack (as long as they clicked and released fast enough for it to qualify as a slash attack input). This means the time the player takes to fast click is irrelevant. 

I kind of like the idea of a ready state, because it opens the possibility of holding the ready state and chaining into other actions, like a strong stab attack (hold and drag), a burst of block creation (right click drag), a ranged attack, etc. 

End of S1 should expose an S2-ready flag in ConditionState, end of S2 should expose S3-ready. 

Stab should be executed by holding down left and swiping. The stab attack direction should match the direction of the swipe (not the angle between mouse and player), and be constrained to the same hemisphere as the player face direction in the same manner as for slash. I should use a simple FSM pattern matcher to take in a sequence of inputs / movement /action states to recognize when Stab is triggered, and extract its data for hurtbox/animation position/velocity/damage etc. 

Recovery shouldn't be able to preempted by most moves (with some possible exceptions, such as Guard in late stages of recovery)