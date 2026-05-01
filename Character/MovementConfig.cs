using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTile;

public class MovementConfig
{
    // General Air Movement
    public float AirAccel { get; set; } = 1500f;
    public float MaxAirSpeed { get; set; } = 150f;
    public float AirDrag { get; set; } = 500f;

    // Jumping
    public float JumpVelocity { get; set; } = -100f;
    public float JumpHoldForce { get; set; } = -1500f;
    public float JumpInitForce { get; set; } = 0f;
    public float MaxJumpHoldTime { get; set; } = 0.12f;

    // Wall Sliding
    public float SlideTerminalSpeed { get; set; } = 40f;
    public float SlideDrag { get; set; } = 300f;

    // Wall Jumping
    public float WallJumpInitialVelX { get; set; } = 250f;
    public float WallJumpInitialVelY { get; set; } = -150f;
    public float WallJumpAirAccel { get; set; } = 1500f;
    public float WallJumpMaxAirSpeed { get; set; } = 150f;
    public float WallJumpAirDrag { get; set; } = 500f;
    public float WallJumpHoldForce { get; set; } = -1000f;
    public float WallJumpMaxHoldTime { get; set; } = 0.12f;

    // Ground Movement
    public float SpringK { get; set; } = 300f;
    public float WalkAccel { get; set; } = 3000f;
    public float MaxWalkSpeed { get; set; } = 100f;
    public float BrakingForce { get; set; } = 3000f;
    
    // Crouching
    public float CrouchMaxWalkSpeed { get; set; } = 50f;
    public float CrouchWalkAccel { get; set; } = 1500f;
    
    // Fast Falling / Sliding
    public float FastFallForce { get; set; } = 1000f;
    public float FastSlideTerminalSpeed { get; set; } = 200f;

    // Running Jump
    public float RunJumpVelocity { get; set; } = -120f;
    public float RunJumpHoldForce { get; set; } = -1200f;
    public float RunJumpMinSpeed { get; set; } = 80f;

    // Double Jump
    public float DoubleJumpVelocity { get; set; } = -100f;
    public float DoubleJumpHoldForce { get; set; } = -1500f;
    public float DoubleJumpInitForce { get; set; } = 0f;
    public float DoubleJumpMaxHoldTime { get; set; } = 0.12f;

    private static MovementConfig _current = new MovementConfig();
    
    [JsonIgnore]
    public static MovementConfig Current => _current;

    public static void Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                _current = JsonSerializer.Deserialize<MovementConfig>(json, options) ?? new MovementConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MovementConfig] Load failed: {ex.Message}");
            }
        }
        else
        {
            Save(path);
        }
    }

    public static void Save(string path)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_current, options);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MovementConfig] Save failed: {ex.Message}");
        }
    }
}