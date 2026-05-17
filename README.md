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


