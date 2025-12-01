# Contributing to DRC.EventSourcing

First off, thank you for considering contributing to DRC.EventSourcing! 🎉

## Code of Conduct

This project follows a simple code of conduct: be respectful, be considerate, and help make this a welcoming environment for everyone.

## How Can I Contribute?

### Reporting Bugs

Before creating bug reports, please check existing issues to avoid duplicates. When you create a bug report, include as many details as possible:

- **Use a clear and descriptive title**
- **Describe the exact steps to reproduce the problem**
- **Provide specific examples** (code snippets, test cases)
- **Describe the behavior you observed and what you expected**
- **Include your environment details** (.NET version, OS, database provider)

### Suggesting Enhancements

Enhancement suggestions are welcome! Please provide:

- **Clear and descriptive title**
- **Detailed description of the proposed functionality**
- **Explain why this enhancement would be useful**
- **Provide examples of how it would work**

### Pull Requests

1. **Fork the repository** and create your branch from `main`
2. **Follow the existing code style** (see below)
3. **Add tests** for any new functionality
4. **Update documentation** as needed
5. **Ensure all tests pass** (`dotnet test`)
6. **Write a clear commit message**

## Development Setup

```bash
# Clone your fork
git clone https://github.com/yourusername/DRC.EventSourcing.git
cd DRC.EventSourcing

# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run tests
dotnet test
```

## Project Structure

```
src/        - Source code projects
test/       - Test projects
samples/    - Demo applications
docs/       - Documentation
```

## Coding Guidelines

### C# Style

- Follow standard C# naming conventions
- Use meaningful variable names
- Add XML documentation comments for public APIs
- Keep methods focused and small
- Prefer explicit over implicit

### Example

```csharp
/// <summary>
/// Appends events to a stream with optimistic concurrency control.
/// </summary>
/// <param name="domain">The domain name for the stream.</param>
/// <param name="streamId">The unique identifier of the stream.</param>
/// <param name="expectedVersion">Expected version for concurrency control.</param>
/// <param name="events">Events to append.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The new stream version after appending.</returns>
public async Task<StreamVersion> AppendToStream(
    string domain,
    string streamId,
    StreamVersion expectedVersion,
    IEnumerable<EventData> events,
    CancellationToken ct = default)
{
    // Implementation
}
```

### Testing

- Write tests for all new functionality
- Use descriptive test names: `MethodName_Scenario_ExpectedBehavior`
- Follow AAA pattern: Arrange, Act, Assert
- Use FluentAssertions for readable assertions

```csharp
[Fact]
public async Task AppendToStream_WithValidEvents_ShouldSucceed()
{
    // Arrange
    var events = CreateTestEvents(5);
    
    // Act
    var version = await _eventStore.AppendToStream(
        "test-domain", 
        "test-stream", 
        StreamVersion.New(), 
        events);
    
    // Assert
    version.Should().Be(5);
}
```

### Commits

- Use clear, descriptive commit messages
- Follow conventional commits format:
  - `feat:` for new features
  - `fix:` for bug fixes
  - `docs:` for documentation changes
  - `test:` for test changes
  - `refactor:` for code refactoring
  - `chore:` for maintenance tasks

```bash
# Good commit messages
git commit -m "feat: add support for PostgreSQL provider"
git commit -m "fix: resolve concurrency issue in SQLite archiving"
git commit -m "docs: update README with retention policy examples"

# Bad commit messages
git commit -m "fixed stuff"
git commit -m "updates"
```

## Adding a New Storage Provider

To add a new storage provider (e.g., PostgreSQL):

1. Create a new project: `src/DRC.EventSourcing.PostgreSQL`
2. Implement the required interfaces:
   - `IEventStore`
   - `IArchiveCoordinator`
   - `ISnapshotStore`
   - etc.
3. Inherit from base classes in `Infrastructure/`
4. Add tests in `test/DRC.EventSourcing.Tests/PostgreSQL/`
5. Add samples in `samples/`
6. Update documentation

## Documentation

- Update the README if you change functionality
- Add XML comments for all public APIs
- Update relevant docs in `docs/` folder
- Include code examples where appropriate

## Review Process

1. All submissions require review
2. Maintainers will review your PR
3. Address any feedback
4. Once approved, a maintainer will merge

## Questions?

Feel free to open an issue for questions about contributing!

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

---

Thank you for contributing to DRC.EventSourcing! 🚀

