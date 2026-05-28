namespace MTile;

// Flat, plain-data snapshot of one PlayerCharacter (roadmap goal 4 §A/§D-F).
// Built and consumed by PlayerCharacter.CaptureState/RestoreState, which have
// private access to the fields this mirrors. The two FSMs are stored as registry
// indices (states/actions are flyweight instances constructed in a fixed order, so
// an index is stable across a snapshot/restore); the per-activation data rides in
// the MovementVars/ActionVars value structs. Reference members are all standalone
// copies (cloned abilities, deep-copied gesture buffer, value-struct arrays) — no
// aliases into the live player.
//
// The Controller is NOT here: it's owned by Simulation (one per player channel) and
// captured at the sim level (see SimSnapshot).
public struct PlayerSnapshot
{
    public EntityId Id;

    public BodyState Body;

    public float Health;
    public float HitInvulnRemaining;
    public int   LastCrushFrame;
    public int   Frame;

    // FSM current selection + history rings, as registry indices.
    public int   StateIndex;
    public int   ActionIndex;
    public int[] StateHistory;
    public int[] ActionHistory;
    public int   HistoryHead;
    public int   ActionHistoryHead;

    // Per-activation FSM data.
    public MovementVars MoveVars;
    public ActionVars   ActionVars;

    // Helper objects.
    public PlayerAbilityState Abilities;   // deep clone
    public InputParserState   Parser;
    public ActionIntent[]     Intents;
    public EruptionGestureState Eruption;  // BlockEruptionAction's _pen/_samples

    // Player-local selections.
    public TileType            ActiveBlockType;
    public EruptionPlannerMode EruptionMode;
    public bool                WasPDown;
}
