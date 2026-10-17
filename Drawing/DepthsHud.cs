using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Co-op "depths" readout: room name and number, wave n/m while fighting, a short
// "Door open" notice when a room clears, and a victory banner after the last room.
// Render-only: reads Simulation.StageVars (written by the stage ticker, rolled back with
// the sim) and Stages.DepthsPlan; the only state here is the notice timer, which is
// keyed off sim frames and never feeds back.
public sealed class DepthsHud
{
    private const int NoticeFrames = 180;   // 3 s at 60 fps
    private int _lastRoom = -1;
    private int _noticeFrame = int.MinValue;

    public void Draw(SpriteBatch sb, SpriteFont font, Viewport vp, Simulation sim)
    {
        var plan = Stages.DepthsPlan;
        var v    = sim.StageVars;
        int room = v[0], frame = sim.Frame;

        // A room index that advanced means the previous room just cleared. A rollback can
        // move the index back; resync without a notice.
        if (_lastRoom >= 0 && room > _lastRoom) _noticeFrame = frame;
        _lastRoom = room;
        bool victory = room >= plan.Length;

        string head, sub = null;
        if (victory) head = "THE DEPTHS ARE CLEARED";
        else
        {
            var r = plan[room];
            head = r.Name;
            sub  = v[1] == 1 ? $"Room {room + 1}/{plan.Length}   Wave {v[2] + 1}/{r.Waves.Length}"
                             : $"Room {room + 1}/{plan.Length}";
        }

        sb.Begin();
        Centered(sb, font, head, vp, 10, victory ? Color.Gold : Color.White);
        if (sub != null) Centered(sb, font, sub, vp, 28, Color.LightGray);
        if (victory)
            Centered(sb, font, "Victory", vp, vp.Height / 3, Color.Gold);
        else if (frame - _noticeFrame >= 0 && frame - _noticeFrame < NoticeFrames)
            Centered(sb, font, "Door open", vp, 52, Color.LimeGreen);
        sb.End();
    }

    private static void Centered(SpriteBatch sb, SpriteFont font, string text, Viewport vp, float y, Color c)
    {
        var size = font.MeasureString(text);
        var pos  = new Vector2(MathF.Round((vp.Width - size.X) * 0.5f), y);
        sb.DrawString(font, text, pos + new Vector2(1, 1), Color.Black);
        sb.DrawString(font, text, pos, c);
    }
}
