using System;
using System.Collections.Generic;

namespace MTile.Tests.Sim;

public delegate PlayerInput InputFactory(int frame, SimFrame? previous);

// Builds a scripted input sequence. Each segment runs for a fixed number of frames
// or until a condition on the simulated state is met, then falls through to the next.
// The final segment without an explicit end repeats forever.
//
// Usage:
//   InputScript.Always(new PlayerInput { Right = true })
//
//   new InputScript()
//       .For(30, new PlayerInput { Right = true })
//       .Then(new PlayerInput { Right = true, Space = true }).For(2)
//       .Then(new PlayerInput { Right = true })
public class InputScript
{
    private readonly List<Segment> _segments = new();

    public record Segment(PlayerInput Input, int Frames, Func<SimFrame, bool>? Until);

    // Convenience: constant input for all frames
    public static InputScript Always(PlayerInput input)
        => new InputScript().Forever(input);

    public InputScript For(int frames, PlayerInput input)
    {
        _segments.Add(new Segment(input, frames, null));
        return this;
    }

    // Begins a builder chain for a segment: .Then(input).For(n) or .Then(input).Until(...)
    public SegmentBuilder Then(PlayerInput input) => new SegmentBuilder(this, input);

    // Adds a segment that lasts until a state condition is met (max 9999 frames)
    public InputScript Until(PlayerInput input, Func<SimFrame, bool> condition)
    {
        _segments.Add(new Segment(input, 9999, condition));
        return this;
    }

    // Adds an open-ended final segment (repeats for all remaining frames)
    public InputScript Forever(PlayerInput input)
    {
        _segments.Add(new Segment(input, int.MaxValue, null));
        return this;
    }

    public PlayerInput Get(int frame, SimFrame? previous)
    {
        int offset = 0;
        foreach (var seg in _segments)
        {
            // Check early-exit condition using previous frame data
            if (seg.Until != null && previous != null && seg.Until(previous))
            {
                offset += seg.Frames; // treat as exhausted, move to next
                continue;
            }
            if (frame < offset + seg.Frames)
                return seg.Input;
            offset += seg.Frames;
        }
        // Past all segments: return last segment's input or empty
        return _segments.Count > 0 ? _segments[^1].Input : default;
    }

    public class SegmentBuilder
    {
        private readonly InputScript _script;
        private readonly PlayerInput _input;

        internal SegmentBuilder(InputScript script, PlayerInput input)
        {
            _script = script;
            _input  = input;
        }

        public InputScript For(int frames) { _script._segments.Add(new Segment(_input, frames, null)); return _script; }
        public InputScript Forever()       { _script._segments.Add(new Segment(_input, int.MaxValue, null)); return _script; }
        public InputScript Until(Func<SimFrame, bool> cond)
        {
            _script._segments.Add(new Segment(_input, 9999, cond));
            return _script;
        }
    }
}
