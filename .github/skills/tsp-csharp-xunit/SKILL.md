---
name: tsp-csharp-xunit
description: "Comprehensive xUnit testing guide for C# projects. Use when writing unit tests, setting up test projects, creating data-driven tests, mocking dependencies, or organizing test suites."
---

# xUnit Testing — TSP Standards

## Project Setup

- Test project name: `{ProjectName}.Tests`
- Required packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`
- Recommended: `FluentAssertions`, `NSubstitute` (or `Moq`)
- Run tests: `dotnet test` (use `--filter` for targeted runs)

## Test Structure

- No test class attribute required (unlike MSTest/NUnit)
- Use constructor for per-test setup; implement `IDisposable` for teardown
- Use `IClassFixture<T>` for shared context within a test class
- Use `ICollectionFixture<T>` for shared context across test classes
- Use `IAsyncLifetime` for async setup/teardown

```csharp
public class OrderServiceTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _db;

    public OrderServiceTests(DatabaseFixture db)
    {
        _db = db;
    }

    [Fact]
    public void CreateOrder_WithValidItems_ReturnsOrderId()
    {
        // Arrange
        var service = new OrderService(_db.Context);
        var items = new[] { new OrderItem("SKU-1", 2) };

        // Act
        var result = service.CreateOrder(items);

        // Assert
        result.Should().BeGreaterThan(0);
    }

    public void Dispose() { /* cleanup */ }
}
```

## Naming Conventions

- Test class: `{ClassUnderTest}Tests`
- Test method: `{Method}_{Scenario}_{ExpectedResult}`
- Examples:
  - `GetUser_WhenIdIsInvalid_ReturnsNotFound`
  - `CalculateTotal_WithDiscount_AppliesPercentage`
  - `SaveAsync_WhenCancelled_ThrowsOperationCancelled`

## Data-Driven Tests

- `[Theory]` + `[InlineData]` for simple inline values
- `[Theory]` + `[MemberData]` for method/property-based data
- `[Theory]` + `[ClassData]` for reusable data generators
- Use meaningful parameter names that describe the scenario

```csharp
[Theory]
[InlineData(0, false)]
[InlineData(5, true)]
[InlineData(-1, false)]
public void IsValidQuantity_ReturnsExpected(int quantity, bool expected)
{
    var result = OrderValidator.IsValidQuantity(quantity);

    result.Should().Be(expected);
}
```

## Assertions

- Prefer `FluentAssertions` when available:
  - `result.Should().Be(expected)`
  - `collection.Should().HaveCount(3).And.Contain(x => x.IsActive)`
  - `act.Should().ThrowAsync<InvalidOperationException>()`
- Built-in xUnit assertions as fallback:
  - `Assert.Equal`, `Assert.True`, `Assert.Throws<T>`
  - `Assert.Contains` / `Assert.DoesNotContain` for collections
- One logical behavior per test — multiple assertions are fine if testing one concept

## Mocking

- Prefer `NSubstitute` for readability; `Moq` is also acceptable
- Mock interfaces, not concrete classes
- Verify interactions only when the interaction IS the behavior being tested
- Avoid over-mocking — if you're mocking more than 3 dependencies, the class may need refactoring

```csharp
var repo = Substitute.For<IOrderRepository>();
repo.GetByIdAsync(42, Arg.Any<CancellationToken>())
    .Returns(new Order { Id = 42 });

var service = new OrderService(repo);
var result = await service.GetOrderAsync(42, CancellationToken.None);

result.Should().NotBeNull();
result.Id.Should().Be(42);
```

## Test Organization

- Group tests by feature or component
- Use `[Trait("Category", "Integration")]` for categorization
- Skip tests with `Skip = "reason"` when conditionally disabled
- Use `ITestOutputHelper` for diagnostic output (not `Console.WriteLine`)

## Rules

- Tests must run in any order and in parallel — no shared mutable state
- Avoid disk I/O — use in-memory alternatives or mocks
- No `Thread.Sleep` — use `Task.Delay` with cancellation tokens in async tests
- Every new or changed public API must have corresponding tests
