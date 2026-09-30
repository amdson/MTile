using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MTile;

// FighterGenome → C# source shaped like a FighterRoster entry: a `public static
// FighterSpec Name() => new() { … };` factory plus one private helper per action that
// differs from its ActionSpec.Default row (a struct can't be `with`-edited inside a
// collection initializer, so the roster's helper-method idiom is the paste-able form).
// Paste both into FighterRoster.cs and give the fighter its own EntityKind.
//
// Floats are written round-trip ("R"), so the pasted spec is bit-for-bit the one that
// scored. Tool code (the forge's output); nothing in the game loop calls it.
public static class FighterSpecPrinter
{
    public static string ToCSharp(FighterGenome g, string name = null)
    {
        var s     = g.Spec;
        name    ??= s.Name;
        var sb    = new StringBuilder();
        var extra = new StringBuilder();

        sb.AppendLine($"    public static FighterSpec {name}() => new()");
        sb.AppendLine("    {");
        Line(sb, "Name",          $"\"{name}\"");
        sb.AppendLine($"        {"Kind".PadRight(18)} = EntityKind.{s.Kind},   // TODO: give it its own EntityKind");
        Line(sb, "Radius",        F(s.Radius));
        Line(sb, "Sides",         I(s.Sides));
        Line(sb, "Health",        F(s.Health));
        Line(sb, "Strength",      F(s.Strength));
        Line(sb, "Armor",         F(s.Armor));
        Line(sb, "EnergyReserve", F(s.EnergyReserve));
        Line(sb, "EnergyRegen",   F(s.EnergyRegen));
        Line(sb, "GroundPower",   F(s.GroundPower));
        Line(sb, "JumpImpulse",   F(s.JumpImpulse));
        Line(sb, "Thrust",        F(s.Thrust));
        Line(sb, "Cling",         B(s.Cling));
        Line(sb, "TargetMemory",  B(s.TargetMemory));
        Line(sb, "Rooted",        B(s.Rooted));
        Line(sb, "Team",          I(s.Team));
        Line(sb, "Color",         $"new Color({s.Color.R}, {s.Color.G}, {s.Color.B})");
        // Sprite left null ⇒ the compiler picks a stock sprite from the body.
        sb.AppendLine($"        {"Actions".PadRight(18)} =");
        sb.AppendLine("        {");
        for (int i = 0; i < s.Actions.Count; i++)
        {
            var a    = s.Actions[i];
            var diff = Diff(a, ActionSpec.Default(a.Kind));
            if (diff.Count == 0)
            {
                sb.AppendLine($"            ActionSpec.Default(ActionKind.{a.Kind}),   // {i}");
                continue;
            }
            string helper = $"{name}Action{i}";
            sb.AppendLine($"            {helper}(),   // {i} — {a.Kind}");
            extra.AppendLine();
            extra.AppendLine($"    private static ActionSpec {helper}()");
            extra.AppendLine("    {");
            extra.AppendLine($"        var a = ActionSpec.Default(ActionKind.{a.Kind});");
            foreach (var d in diff) extra.AppendLine($"        a.{d};");
            extra.AppendLine("        return a;");
            extra.AppendLine("    }");
        }
        sb.AppendLine("        },");
        Line(sb, "EngageRange",        F(s.EngageRange));
        Line(sb, "StandoffRange",      F(s.StandoffRange));
        Line(sb, "HoverHeight",        F(s.HoverHeight));
        Line(sb, "AlertRange",         F(s.AlertRange));
        Line(sb, "RetreatBelowHealth", F(s.RetreatBelowHealth));
        Line(sb, "PreferredAction",    I(s.PreferredAction));
        Line(sb, "Brain",              $"s => new {BrainClass(g.Brain)}(s)");
        sb.AppendLine("    };");
        sb.Append(extra);
        return sb.ToString();
    }

    public static string BrainClass(ForgeBrain b) => b switch
    {
        ForgeBrain.Kiter     => nameof(FighterKiterBrain),
        ForgeBrain.HoverDive => nameof(FighterHoverDiveBrain),
        _                    => nameof(FighterCloserBrain),
    };

    // Every ActionSpec field, as `Field = value` assignments where it differs.
    private static List<string> Diff(in ActionSpec a, in ActionSpec d)
    {
        var o = new List<string>();
        void Fl(string n, float x, float y) { if (x != y) o.Add($"{n} = {F(x)}"); }
        void In(string n, int x, int y)     { if (x != y) o.Add($"{n} = {I(x)}"); }
        Fl("Windup", a.Windup, d.Windup);
        Fl("Active", a.Active, d.Active);
        Fl("Recovery", a.Recovery, d.Recovery);
        Fl("MinRange", a.MinRange, d.MinRange);
        Fl("MaxRange", a.MaxRange, d.MaxRange);
        Fl("VerticalSlack", a.VerticalSlack, d.VerticalSlack);
        Fl("Damage", a.Damage, d.Damage);
        Fl("DamageMax", a.DamageMax, d.DamageMax);
        if (a.Knockback != d.Knockback) o.Add($"Knockback = new Vector2({F(a.Knockback.X)}, {F(a.Knockback.Y)})");
        Fl("KnockbackMax", a.KnockbackMax, d.KnockbackMax);
        Fl("Reach", a.Reach, d.Reach);
        Fl("HalfWidth", a.HalfWidth, d.HalfWidth);
        Fl("HalfHeight", a.HalfHeight, d.HalfHeight);
        Fl("Speed", a.Speed, d.Speed);
        Fl("FallSpeedMin", a.FallSpeedMin, d.FallSpeedMin);
        Fl("FallSpeedRef", a.FallSpeedRef, d.FallSpeedRef);
        In("Penetration", a.Penetration, d.Penetration);
        if (a.Material != d.Material) o.Add($"Material = TileType.{a.Material}");
        Fl("EnergyCost", a.EnergyCost, d.EnergyCost);
        In("ActivePriority", a.ActivePriority, d.ActivePriority);
        In("PassivePriority", a.PassivePriority, d.PassivePriority);
        return o;
    }

    private static void Line(StringBuilder sb, string field, string value)
        => sb.AppendLine($"        {field.PadRight(18)} = {value},");

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture) + "f";
    private static string I(int v)   => v.ToString(CultureInfo.InvariantCulture);
    private static string B(bool v)  => v ? "true" : "false";
}
