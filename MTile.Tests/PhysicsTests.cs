using System;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

public class PhysicsTests
{
    private static Polygon CreateTile() => Polygon.CreateRectangle(32f, 32f);
    private static Polygon CreateHexagon() => Polygon.CreateRegular(40f, 6);

    [Fact]
    public void HexagonCollidingWithTileRightSide_ShouldPushHexagonRight()
    {
        // Arrange
        var tile = CreateTile();
        var hex = CreateHexagon();

        // Tile is at origin. Right edge is at X = 16.
        var tilePos = Vector2.Zero;

        // Hexagon left edge is at X = HexPos.X - 34.64. 
        // Placing hex at X = 50 means left edge is at 15.36 (overlapping tile by ~0.64 units).
        var hexPos = new Vector2(50f, 0f);

        // Act
        var result = Collision.Check(hex, hexPos, 0f, tile, tilePos, 0f);

        // Assert
        Assert.True(result.Intersects, "Hexagon should intersect the right side of the tile.");
        
        // MTV should push the hexagon to the right (positive X direction) to resolve the overlap.
        Assert.True(result.MTV.X > 0, $"MTV should point right, but was {result.MTV}");
        Assert.True(Math.Abs(result.MTV.Y) < 0.1f, $"MTV should be mostly horizontal, but was {result.MTV}");
    }

    [Fact]
    public void HexagonCollidingWithTileLeftSide_ShouldPushHexagonLeft()
    {
        // Arrange
        var tile = CreateTile();
        var hex = CreateHexagon();

        // Tile left edge is at X = -16.
        var tilePos = Vector2.Zero;

        // Hexagon right edge is at X = HexPos.X + 34.64.
        // Placing hex at X = -50 means right edge is at -15.36 (overlapping tile by ~0.64 units).
        var hexPos = new Vector2(-50f, 0f);

        // Act
        var result = Collision.Check(hex, hexPos, 0f, tile, tilePos, 0f);

        // Assert
        Assert.True(result.Intersects, "Hexagon should intersect the left side of the tile.");
        
        // MTV should push the hexagon to the left (negative X direction) to resolve the overlap.
        Assert.True(result.MTV.X < 0, $"MTV should point left, but was {result.MTV}");
        Assert.True(Math.Abs(result.MTV.Y) < 0.1f, $"MTV should be mostly horizontal, but was {result.MTV}");
    }

    [Fact]
    public void HexagonGetBounds_WithNegativeCoords_ShouldEncompassLeftmostVertex()
    {
        // This test validates a potential issue where integer truncation shrinks the bounding box 
        // on negative coordinates, which would cause the physics system to skip left-side collision checks.
        var hex = CreateHexagon();
        
        // Position it such that its vertices fall on negative fractional coordinates
        var hexPos = new Vector2(-10.8f, 0f);
        
        var bounds = hex.GetBounds(hexPos);
        var verts = hex.GetVertices(hexPos);

        // Verify the bounding box safely encompasses all vertices
        foreach (var v in verts)
        {
            Assert.True(v.X >= bounds.Left, $"Vertex {v.X} is outside bounds Left {bounds.Left}");
            Assert.True(v.X <= bounds.Right, $"Vertex {v.X} is outside bounds Right {bounds.Right}");
            Assert.True(v.Y >= bounds.Top, $"Vertex {v.Y} is outside bounds Top {bounds.Top}");
            Assert.True(v.Y <= bounds.Bottom, $"Vertex {v.Y} is outside bounds Bottom {bounds.Bottom}");
        }
    }

    [Fact]
    public void HexagonMovingLeftIntoSolidChunk_ShouldNotPhaseThrough()
    {
        // Arrange
        var chunks = new ChunkMap();
        var chunk = new Chunk { ChunkPos = new Point(-1, 0) }; // Chunk covers X from -512 to 0, Y from 0 to 512
        for (int tx = 0; tx < Chunk.Size; tx++)
        for (int ty = 0; ty < Chunk.Size; ty++)
        {
            chunk.Tiles[tx, ty].IsSolid = true;
        }
        chunks[chunk.ChunkPos] = chunk;

        var hex = CreateHexagon();
        // Place just to the right of the chunk (-1, 0) boundaries (X > 0). Radius ~40.
        // Edge is at hexPos.X - 34.64. Let's put it at X = 50.
        var body = new PhysicsBody(hex, new Vector2(50f, 256f)); 
        body.Velocity = new Vector2(-1000f, 0f); // Fast moving left

        var bodies = new System.Collections.Generic.List<PhysicsBody> { body };

        // Act
        // Moving left for 0.1s -> -100px. Unobstructed, it would end up at X = -50 (inside the solid chunk).
        float dt = 0.1f;
        PhysicsWorld.StepSwept(bodies, chunks, dt, Vector2.Zero);

        // Assert
        // Hexagon's leftmost point is -34.64 relative to center. If center is at X = 34.64, left edge is 0.
        // It should NOT be less than 34.64. If it phased through, it would be much lower.
        float minAllowedX = 34.64f - 1f; // small tolerance
        Assert.True(body.Position.X >= minAllowedX, $"Hexagon phased through tiles! Final X position: {body.Position.X}, expected at least {minAllowedX}");
    }

    [Fact]
    public void HexagonMovingLeftIntoWallOfTiles_ShouldStopAndNotSlideUpOrPhase()
    {
        // Arrange
        var chunks = new ChunkMap();
        var chunk = new Chunk { ChunkPos = new Point(0, 0) }; 
        for (int tx = 0; tx < 2; tx++) // 2 columns of tiles
        for (int ty = 0; ty < Chunk.Size; ty++) // A full vertical wall
        {
            chunk.Tiles[tx, ty].IsSolid = true;
        }
        chunks[chunk.ChunkPos] = chunk;

        var hex = CreateHexagon();
        
        // Place the hex at X = 80, moving left.
        var body = new PhysicsBody(hex, new Vector2(80f, 256f)); 
        body.Velocity = new Vector2(-1000f, 0f);

        var bodies = new System.Collections.Generic.List<PhysicsBody> { body };

        // Act
        PhysicsWorld.StepSwept(bodies, chunks, 0.1f, Vector2.Zero);

        // Assert
        Assert.True(body.Position.X >= 66.6f, $"Hexagon phased through the wall! Final position: {body.Position}");
        Assert.True(Math.Abs(body.Position.Y - 256f) < 0.1f, $"Hexagon slid vertically! Final position: {body.Position}");
    }

    [Fact]
    public void HexagonMovingLeftIntoFullChunk_ShouldStopAndNotPhase()
    {
        // Arrange
        var chunks = new ChunkMap();
        var chunk = new Chunk { ChunkPos = new Point(0, 0) }; 
        for (int tx = 0; tx < Chunk.Size; tx++) 
        for (int ty = 0; ty < Chunk.Size; ty++) 
        {
            chunk.Tiles[tx, ty].IsSolid = true;
        }
        chunks[chunk.ChunkPos] = chunk;

        var hex = CreateHexagon();
        
        // Chunk is from X=0 to X=512. Put hex at X=600, moving left.
        var body = new PhysicsBody(hex, new Vector2(600f, 256f)); 
        body.Velocity = new Vector2(-1000f, 0f);

        var bodies = new System.Collections.Generic.List<PhysicsBody> { body };

        // Act
        PhysicsWorld.StepSwept(bodies, chunks, 0.2f, Vector2.Zero);

        // Assert
        Assert.True(body.Position.X >= 512f - 2f, $"Hexagon phased into the chunk! Final position: {body.Position}");
    }

    [Fact]
    public void HexagonMovingLeftIntoGame1SolidWall_ShouldNotPhase()
    {
        // Recreate the exact geometry from Game1 around the left wall.
        var chunks = new ChunkMap();
        for (int cx = -2; cx <= 0; cx++)
        for (int cy = -1; cy <= 1; cy++)
        {
            var chunk = new Chunk { ChunkPos = new Point(cx, cy) };
            for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++) 
            {
                chunk.Tiles[tx, ty].IsSolid = (cy * Chunk.Size + ty) >= 0;
                chunk.Tiles[tx, ty].IsSolid = chunk.Tiles[tx, ty].IsSolid || ((cx == -1) && (tx < Chunk.Size / 2) && (cy * Chunk.Size + ty) <= -10);
                chunk.Tiles[tx, ty].IsSolid = chunk.Tiles[tx, ty].IsSolid || (cx < -1);
            }
            chunks[new Point(cx, cy)] = chunk;
        }

        var hex = CreateHexagon();
        
        // In Game1 falling on the left wall:
        // The left wall is tx < 16 in cx = -1. That means X < -512 + 256 = -256.
        // It's a vertical wall at X = -256. 
        // We start hex at X = -200, Y = -200. Velocity is -1000, 300 (falling).
        var body = new PhysicsBody(hex, new Vector2(-200f, -200f)); 
        body.Velocity = new Vector2(-1000f, 300f);

        var bodies = new System.Collections.Generic.List<PhysicsBody> { body };

        PhysicsWorld.StepSwept(bodies, chunks, 0.1f, Vector2.Zero);

        // Hexagon left edge = -256. Center = -256 + 34.64 = -221.36.
        // It should NOT be further left than -222f.
        Assert.True(body.Position.X >= -222f, $"Hexagon phased through Game1 left wall! Final position: {body.Position}");
    }

    [Fact]
    public void HexagonMovingLeftWhileOnGround_ShouldNotPhaseThroughWall()
    {
        // Recreate the user's specific state: touching the ground, moving left into a wall.
        var chunks = new ChunkMap();
        var chunk = new Chunk { ChunkPos = new Point(0, 0) }; 
        chunks[new Point(0, 0)] = chunk;

        // Build a floor from X=0 to 512 at ty=16 (Y=256)
        // Build a wall at tx=2 (X=32 to 48) from ty=0 to 16 (Y=0 to 256)
        for (int tx = 0; tx < Chunk.Size; tx++) chunk.Tiles[tx, 16].IsSolid = true;
        for (int ty = 0; ty <= 16; ty++) chunk.Tiles[2, ty].IsSolid = true;

        var hex = CreateHexagon();
        
        // Hexagon radius 40. Floor surface is at Y=256. Hexagon center should be at Y=216 to touch the floor.
        // Wall right edge is at X=48. Hexagon center at X=100.
        var body = new PhysicsBody(hex, new Vector2(100f, 216f)); 
        
        // body.Constraints.Add(new SurfaceDistance(new Vector2(100f, 256f), new Vector2(0f, -1f), 0f));

        // Moving left fast
        body.Velocity = new Vector2(-1000f, 0f);

        var bodies = new System.Collections.Generic.List<PhysicsBody> { body };

        // Act
        PhysicsWorld.StepSwept(bodies, chunks, 0.1f, Vector2.Zero);

        // Assert
        // Hexagon left edge (center.X - 34.64). Should hit wall at X=48. 
        // center.X should not go below 48 + 34.64 = 82.64.
        Assert.True(body.Position.X >= 80f, $"Hexagon phased through the wall while grounded! Final position: {body.Position}");
    }
}
