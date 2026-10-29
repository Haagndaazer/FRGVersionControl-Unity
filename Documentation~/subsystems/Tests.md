# Tests Module

## Overview

The Tests module contains integration tests for the FRGVersionControl system, validating core functionality and Git operations.

## Test Framework

- **Framework:** Unity Test Framework (UTF)
- **Type:** Integration tests (tests real Git operations)
- **Runner:** Unity Test Runner window
- **Category:** Editor tests

## Test File

### FRGVersionControlIntegrationTests.cs

Comprehensive integration test suite covering all major lock operations.

**Test Setup:**
- Creates isolated test environment
- Initializes FileLockManager
- Uses real Git operations
- Cleans up after each test

## Running Tests

### Via Unity Test Runner
1. Open `Window > General > Test Runner`
2. Select "EditMode" tab
3. Find "FRGVersionControl.Tests" category
4. Click "Run All" or run individual tests

### Via Command Line
```bash
# Run all editor tests
unity -runTests -testPlatform EditMode -batchmode -projectPath .

# Run specific test category
unity -runTests -testPlatform EditMode -editorTestsCategories "FRGVersionControl" -batchmode
```

## Test Coverage

### Core Lock Operations
- ✓ Lock acquisition
- ✓ Lock release
- ✓ Lock validation
- ✓ Conflict detection
- ✓ User ownership checks

### Git Operations
- ✓ Worktree creation
- ✓ Branch initialization
- ✓ Commit creation
- ✓ Push/pull operations
- ✓ Merge conflict resolution

### Configuration
- ✓ Admin config loading
- ✓ Lockable file types config
- ✓ File type filtering
- ✓ Permission validation

### Edge Cases
- ✓ Locking non-existent files
- ✓ Double-locking same file
- ✓ Unlocking others' locks
- ✓ Force unlock as admin
- ✓ Stale lock handling

## Test Structure

### Arrange-Act-Assert Pattern
```csharp
[Test]
public void TestLockAcquisition()
{
    // Arrange
    string testFile = "Assets/test.prefab";

    // Act
    bool result = FileLockManager.Instance.TryLockFile(testFile);

    // Assert
    Assert.IsTrue(result);
    Assert.IsTrue(FileLockManager.Instance.IsFileLocked(testFile));
}
```

### Test Isolation
Each test:
- Runs in isolated Git worktree
- Uses unique test files
- Cleans up locks after completion
- Doesn't affect production lock state

## Test Data

### Test Files
Tests create temporary asset files:
- `Assets/TestAssets/test-scene.unity`
- `Assets/TestAssets/test-prefab.prefab`
- `Assets/TestAssets/test-material.mat`

### Mock Users
Tests simulate multiple users:
- `testuser1@example.com`
- `testuser2@example.com`
- `admin@example.com` (admin privileges)

## Writing New Tests

### Test Method Template
```csharp
[Test]
public void TestYourFeature()
{
    // Arrange: Set up test conditions
    var testAsset = CreateTestAsset();

    // Act: Perform the operation
    var result = FileLockManager.Instance.YourOperation(testAsset);

    // Assert: Verify expected outcome
    Assert.AreEqual(expectedValue, result);

    // Cleanup
    CleanupTestAsset(testAsset);
}
```

### Best Practices
- Test one thing per test method
- Use descriptive test names
- Clean up resources in finally blocks
- Mock external dependencies when possible
- Test both success and failure paths
- Include edge cases

## Continuous Integration

### CI Pipeline Integration
```yaml
# Example GitHub Actions workflow
- name: Run Unity Tests
  run: |
    unity -runTests -testPlatform EditMode \\
          -editorTestsResultFile results.xml \\
          -batchmode -nographics -quit

- name: Publish Test Results
  uses: actions/upload-artifact@v2
  with:
    name: test-results
    path: results.xml
```

## Test Maintenance

### When to Update Tests
- After adding new features
- When fixing bugs (add regression tests)
- When refactoring core logic
- After changing Git workflows
- When updating Unity versions

### Test Health Checks
- All tests should pass on main branch
- Tests should complete within 30 seconds
- No flaky tests (random failures)
- Code coverage > 80% for Core module

## Debugging Tests

### Visual Studio
1. Attach Unity Debugger
2. Set breakpoints in test code
3. Run test from Test Runner
4. Debugger hits breakpoints

### Debug Logs
```csharp
Debug.Log($"Lock state: {FileLockManager.Instance.IsFileLocked(path)}");
```

### Test Isolation
Run single test to isolate failures:
```csharp
[Test]
[Explicit] // Only runs when explicitly selected
public void DebugSpecificScenario()
{
    // Debug-specific test code
}
```

## Known Limitations

- Tests require functional Git installation
- Tests create temporary files in Assets folder
- Some tests may be slow due to Git operations
- Tests assume UTF-8 encoding
- Windows file path length limits may affect tests

## Future Test Additions

- [ ] Performance benchmarks
- [ ] Stress tests with many locks
- [ ] Concurrent operation tests
- [ ] Network failure simulations
- [ ] UI automation tests
