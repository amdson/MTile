namespace MTile;

// The `[move]` / `[action]` FSM transition trace. Kept as a first-class dev tool — reading
// the transition stream live is how movement/action bugs get diagnosed — but made
// switchable, because the call site is on the sim hot path and Console.WriteLine is not
// free everywhere:
//
//   * On WASM every write is a JS interop hop into the devtools console, orders of
//     magnitude more expensive than the fprintf it is on desktop. Default OFF in browser.
//   * Under rollback the same frames are re-simulated repeatedly, so a single real
//     transition prints once per replay — misleading as well as costly.
//   * MTile.Bench measures µs/tick; a print inside the timed loop is measurement noise.
//
// Not sim-affecting: this gates output only, never state, so toggling it cannot desync a
// match or change a replay.
public static class SimTrace
{
    public static bool Enabled = !System.OperatingSystem.IsBrowser();

    // Takes the already-formatted string only when enabled — call sites use the
    // interpolated form, so the guard must be at the CALL, not just inside. See
    // PlayerCharacter's transitions for the pattern.
    public static void Write(string line)
    {
        if (Enabled) System.Console.WriteLine(line);
    }
}
