# MTile Tests

This project contains automated tests for the MTile physics system, specifically verifying that polygon collisions generate the correct Minimum Translation Vector (MTV) to push bodies out of geometry correctly.

## Running the Physics Tests

You can run the tests using the .NET CLI from the solution root or the test project folder.

### Run tests from the root directory
1. Open your terminal or command prompt.
2. Navigate to the `MTile` project root directory (where the `MTile.sln` or test folder is located).
3. Run the following command:
```bash
dotnet test MTile.Tests/MTile.Tests.csproj
```

### Run tests continuously (Watch Mode)
If you are iterating on the `Collision.cs` logic to fix the left-movement phase-through bug, you can run the tests in watch mode so they automatically re-run whenever a file is changed:
```bash
dotnet watch test --project MTile.Tests/MTile.Tests.csproj
```

## Structure
- `PhysicsTests.cs`: Includes cases to assert that a hexagon overlapping a tile from different directions (e.g. left vs right) calculates an MTV that pushes the hexagon OUT correctly. The test `HexagonCollidingWithTileLeftSide_ShouldPushHexagonLeft` reproduces/validates the scenario where moving left causes issues if the MTV points the wrong way.
- Also includes integration-style test `HexagonMovingLeftIntoGame1SolidWall_ShouldNotPhase` and `HexagonMovingLeftIntoFullChunk_ShouldStopAndNotPhase` to ensure sweeping operations don't miscalculate collision normals when sliding/moving against a chunk entirely filled with solid tiles.
- **Found Issue**: The test `HexagonGetBounds_WithNegativeCoords_ShouldEncompassLeftmostVertex` explicitly catches a bug causing the polygon to phase through tiles when moving left. The bounding box uses `(int)` cast which truncates towards zero (e.g. `-45.44` becomes `-45`). This means the physics engine gets a tighter bounding box on the left size and skips collision checks entirely for that side! You can resolve this issue in `Polygon.cs` by swapping the casts to `Math.Floor` and `Math.Ceiling`.

