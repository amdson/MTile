using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Kinds of player input gestures we track. Each input edge (LMB press, release, ...)
// produces at most one intent; the action FSM consumes them when its precondition
// fires. Adding a new gesture means adding a value here + a detector in InputParser.
public enum IntentType
{
    PressEdge,   // LMB went from up → down this frame (starts a Ready)
    Click,       // short press-and-release within ClickMaxHoldFrames (starts a Slash)
    Stab,        // long press + swipe (starts a Stab; Direction = swipe vector)
    Circle,      // long press + roughly-circular drag (starts a Pulse)
    Jump,        // Space went from up → down this frame (movement-side; short window)
}

public struct ActionIntent
{
    public IntentType Type;
    public int        IssuedFrame;
    public Vector2    Direction;     // gesture-specific; unused for PressEdge / Click
    public bool       Consumed;      // set by Consume(); pruned next frame
}

/*
- slightly darken blocks under effect of dragging, with darkness proportional to the strength of the tether (tethers explained later)
- give blocks weights in descending order of toughness, roughly 
representing the amount of energy needed to break them (e.g. stone > dirt > sand) 
- give the joint mass of affected blocks an attachment energy based on their 
connections to the surrounding blocks. (a simple count of adjacent edges to solid blocks)
- Hierarchy of connections
1. Player to GrabBlockGroup
2. GrabBlockGroup to Blocks 
2. GrabBlockGroup to World
3. Blocks to world 
connections have a break threshold based on the strength of the connection
connection from player to GrabBlockGroup has limited strength, and pulls with force that increases 
superlinearly with distance from block group center of mass and player mouse
if the force exceeds strength the connection should break instantly

GrabBlockGroup should have individual tethers to blocks, each with strength proportional to the time the mouse spent over the block
while grabbing, weighted by a fast-die-off distance kernel, probably gaussian
GrabBlockGroup should be modeled as dividing force between each of the blocks inside it and the world
with the total force equal to that exerted by the player and the force on each block/world proportional to the strength of the tether
(GrabBlockGroup - block) tether durability/strength should go down with time at a rate proportional to force. on tether breaking the block should be removed from
the block group

the tether between blockgroup and world should be of fixed strength, determined by the boundary between blocks in the group and the rest of the world. edges with a solid in blockgroup on one side 
and a solid non in blockgroup on the other side should contribute to tether strength. edges between materials of different types should be maybe 0.2 as strong as same material edges. 

blocks should have a baseline tether to world which should be treated as a viscoelastic glue. the strength of the tether should 
go down with rate proportional to force exerted on the block, with a small minimum baseline. 

when force exerted by the player exceeds the strength of block - world and blockgroup - world tethers, the grabbed blocks can break and the move can proceed. 


the goal here is to allow the player to pick up larger or smaller numbers of blocks to bundle into 
their grab throw, but at the cost of trickier physics. if the player picks up too many blocks, they should be 
forced to pull slowly, like someone slowly peeling a sticker off paper, trying their best to avoid tearing the sticker
if the player picks up way too many, it should be impossible to throw at all. block material, and how blocks are connected to the stage
should influence the difficulty of picking them up. 




blocks should have different mass costs, proportional to their strength
- foam should be quite cheap, stone should be expensive enough that buildMoveMeter alone can only support ~4 stone per second
buildMeter: Storage meter for how much block placement the player can execute. 
- Regenerates slowly
buildMoveMeter: Storage meter for block placement moves. 
- Regenerates quickly by pulling from buildMeter
- Placing blocks with normal right click should push mass from buildMoveMeter into world at the rate determined 
by the current mass ball algorithm. 
- Placing blocks with shift click should place single blocks at a time, consuming mass proportional to their cost
eruptionMoveMeter
- Holding right click should charge eruptionMoveMeter, which provides mass for the eruption move mass ball
- eruptionMoveMeter should get a favorable conversion rate when pulling from buildMeter
- currently, there's a sweet spot for eruption charging, beyond which the charge quickly resets to a much smaller value
- this should be continued through the meter mechanism, which should charge linearly with time until reaching a peak, holding for a split second, and then 
losing capacity. holding right click beyond this point should continue pulling from the buildMeter, but should not increase eruptionMoveMeter charge. 
- it should be possible for normal block placement to consume from eruption move meter in addition to the normal move meter
- eruptions should be differentiated from normal mass ball block placement by moderately fast movement from block to air followed shortly after by right click release
*/

// Frame-scoped ring of recent intents. Lifecycle:
//   InputParser.Detect issues new intents each frame.
//   Action preconditions Peek (non-mutating).
//   The winning action's Enter calls Consume to mark its intent used.
//   PlayerCharacter.Update calls Prune at the end to drop Consumed + aged-out entries.
//
// One global age cap (MaxAgeFrames) — matches the 2-second buffer in the plan.
// Per-intent-type caps are easy to add later if a specific gesture needs a shorter
// memory.
public class IntentBuffer
{
    // Loose upper bound on gesture replay (~2 s at 60 fps). Deliberately a frame
    // count, not seconds-derived: it's a safety cap, not a feel knob, and Peek's
    // default parameter must be a compile-time constant.
    private const int MaxAgeFrames = 120;

    // Jump presses use a much shorter window than the global gesture cap: a
    // slightly-early press should buffer onto the next landing/launch, not replay
    // half a second later. Authored in seconds (≈4 frames at the original 30 fps);
    // jump states read the frame count via ctx.JumpBufferFrames, which converts at
    // the actual step rate. Guided states (LedgePull) extend a pending jump's life
    // via Refresh so it fires at the maneuver's natural jump point.
    public const float JumpBufferSeconds = 4f / 30f;

    private readonly List<ActionIntent> _intents = new();
    public IReadOnlyList<ActionIntent> All => _intents;

    public void Issue(in ActionIntent intent) => _intents.Add(intent);

    // First non-consumed, non-expired intent of `type`. Pure peek — no side effect.
    // maxAgeFrames narrows the acceptance window below the global cap (jump states
    // pass JumpBufferFrames).
    public bool Peek(IntentType type, int currentFrame, out ActionIntent intent, int maxAgeFrames = MaxAgeFrames)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > maxAgeFrames) continue;
            intent = it;
            return true;
        }
        intent = default;
        return false;
    }

    // Mark the first matching intent as Consumed. Pruned on the next Prune call.
    public bool Consume(IntentType type, int currentFrame, int maxAgeFrames = MaxAgeFrames)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > maxAgeFrames) continue;
            it.Consumed = true;
            _intents[i] = it;
            return true;
        }
        return false;
    }

    // Bump the IssuedFrame of the first matching live intent to `currentFrame` so it
    // survives past its age window. Guided movement states call this each frame while
    // the body is committed to a maneuver that will honor the input at its natural
    // exit point; the keep-alive stops when the state ends, so the intent then expires
    // on its normal window. Only refreshes intents still inside maxAgeFrames — a
    // long-dead press can't be resurrected.
    public bool Refresh(IntentType type, int currentFrame, int maxAgeFrames = MaxAgeFrames)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > maxAgeFrames) continue;
            it.IssuedFrame = currentFrame;
            _intents[i] = it;
            return true;
        }
        return false;
    }

    // `jumpBufferFrames` is the rate-converted jump window (the caller owns dt).
    public void Prune(int currentFrame, int jumpBufferFrames)
        => _intents.RemoveAll(i => i.Consumed
            || currentFrame - i.IssuedFrame > MaxAgeFrames
            || (i.Type == IntentType.Jump && currentFrame - i.IssuedFrame > jumpBufferFrames));

    // Snapshot/restore (roadmap goal 4 §E). ActionIntent is a struct, so the array
    // copy is a deep copy; restore replaces the live list contents in place.
    public ActionIntent[] Capture() => _intents.ToArray();

    public void Restore(ActionIntent[] intents)
    {
        _intents.Clear();
        if (intents != null) _intents.AddRange(intents);
    }
}
