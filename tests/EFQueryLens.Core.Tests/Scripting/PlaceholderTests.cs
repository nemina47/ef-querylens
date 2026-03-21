using System.Reflection;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;

namespace EFQueryLens.Core.Tests.Scripting;

public class PlaceholderTests
{
    private static readonly MethodInfo s_buildScalar =
        typeof(QueryEvaluator).GetMethod("BuildScalarPlaceholderExpression", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find BuildScalarPlaceholderExpression via reflection.");

    private static readonly MethodInfo s_buildContains =
        typeof(QueryEvaluator).GetMethod("BuildContainsPlaceholderValues", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find BuildContainsPlaceholderValues via reflection.");

    private static readonly MethodInfo s_tryBuildReflection =
        typeof(QueryEvaluator).GetMethod("TryBuildReflectionPlaceholder", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find TryBuildReflectionPlaceholder via reflection.");

    private static string Scalar(Type t) => (string)s_buildScalar.Invoke(null, [t])!;

    private static string Contains(Type elementType) => (string)s_buildContains.Invoke(null, [elementType])!;

    private static bool TryReflection(Type t, out string placeholder)
    {
        var args = new object?[] { t, null };
        var ok = (bool)s_tryBuildReflection.Invoke(null, args)!;
        placeholder = (string)(args[1] ?? string.Empty);
        return ok;
    }

    // ─── Scalar — built-in primitives ─────────────────────────────────────────

    [Fact] public void Scalar_String_ReturnsStubLiteral()   => Assert.Equal("\"__ql_stub_0\"", Scalar(typeof(string)));
    [Fact] public void Scalar_Int_ReturnsOne()               => Assert.Equal("1",              Scalar(typeof(int)));
    [Fact] public void Scalar_UInt_ReturnsOneU()             => Assert.Equal("1U",             Scalar(typeof(uint)));
    [Fact] public void Scalar_Long_ReturnsOneL()             => Assert.Equal("1L",             Scalar(typeof(long)));
    [Fact] public void Scalar_ULong_ReturnsOneUL()           => Assert.Equal("1UL",            Scalar(typeof(ulong)));
    [Fact] public void Scalar_Short_ReturnsCastOne()         => Assert.Equal("(short)1",       Scalar(typeof(short)));
    [Fact] public void Scalar_UShort_ReturnsCastOne()        => Assert.Equal("(ushort)1",      Scalar(typeof(ushort)));
    [Fact] public void Scalar_Byte_ReturnsCastOne()          => Assert.Equal("(byte)1",        Scalar(typeof(byte)));
    [Fact] public void Scalar_SByte_ReturnsCastOne()         => Assert.Equal("(sbyte)1",       Scalar(typeof(sbyte)));
    [Fact] public void Scalar_Bool_ReturnsTrue()             => Assert.Equal("true",           Scalar(typeof(bool)));
    [Fact] public void Scalar_Char_ReturnsA()                => Assert.Equal("'a'",            Scalar(typeof(char)));
    [Fact] public void Scalar_Decimal_ReturnsOneM()          => Assert.Equal("1m",             Scalar(typeof(decimal)));
    [Fact] public void Scalar_Double_ReturnsOneD()           => Assert.Equal("1d",             Scalar(typeof(double)));
    [Fact] public void Scalar_Float_ReturnsOneF()            => Assert.Equal("1f",             Scalar(typeof(float)));
    [Fact] public void Scalar_DateTime_ReturnsUnixEpoch()    => Assert.Equal("System.DateTime.UnixEpoch", Scalar(typeof(DateTime)));

    [Fact]
    public void Scalar_Guid_ReturnsNonDefaultGuidExpression()
    {
        var result = Scalar(typeof(Guid));
        Assert.Contains("00000000-0000-0000-0000-000000000001", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Scalar_Enum_ReturnsCastToIntExpression()
    {
        var result = Scalar(typeof(DayOfWeek));
        Assert.StartsWith("(", result, StringComparison.Ordinal);
        Assert.Contains("DayOfWeek", result, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("1", result, StringComparison.Ordinal);
    }

    // ─── Scalar — nullable strips wrapper ────────────────────────────────────

    [Fact] public void Scalar_NullableInt_ReturnsSameAsNonNullable()   => Assert.Equal(Scalar(typeof(int)),  Scalar(typeof(int?)));
    [Fact] public void Scalar_NullableBool_ReturnsSameAsNonNullable()  => Assert.Equal(Scalar(typeof(bool)), Scalar(typeof(bool?)));
    [Fact] public void Scalar_NullableLong_ReturnsSameAsNonNullable()  => Assert.Equal(Scalar(typeof(long)), Scalar(typeof(long?)));
    [Fact] public void Scalar_NullableGuid_ReturnsSameAsNonNullable()  => Assert.Equal(Scalar(typeof(Guid)), Scalar(typeof(Guid?)));

    // ─── Scalar — unknown types ───────────────────────────────────────────────

    [Fact]
    public void Scalar_TypeWithParameterlessCtor_ReturnsNewExpression()
    {
        var result = Scalar(typeof(FakeWithParameterlessCtor));
        Assert.StartsWith("new ", result, StringComparison.Ordinal);
        Assert.EndsWith("()", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Scalar_TypeWithComplexCtorOnly_ReturnsDefault()
    {
        var result = Scalar(typeof(FakeWithComplexCtor));
        Assert.StartsWith("default(", result, StringComparison.Ordinal);
    }

    // ─── Contains placeholder values ─────────────────────────────────────────

    [Fact]
    public void Contains_String_ReturnsTwoDistinctStubLiterals()
    {
        var result = Contains(typeof(string));
        Assert.Contains("__ql_stub_0", result, StringComparison.Ordinal);
        Assert.Contains("__ql_stub_1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Contains_Guid_ReturnsTwoDistinctGuids()
    {
        var result = Contains(typeof(Guid));
        Assert.Contains("Guid.Empty", result, StringComparison.Ordinal);
        Assert.Contains("00000000-0000-0000-0000-000000000001", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Contains_Int_ReturnsZeroAndOne()
    {
        var result = Contains(typeof(int));
        Assert.Equal("0, 1", result);
    }

    [Fact]
    public void Contains_Bool_ReturnsFalseAndTrue()
    {
        var result = Contains(typeof(bool));
        Assert.Equal("false, true", result);
    }

    [Fact]
    public void Contains_Char_ReturnsTwoChars()
    {
        var result = Contains(typeof(char));
        Assert.Equal("'a', 'b'", result);
    }

    [Fact]
    public void Contains_Long_ReturnsTwoLongs()
    {
        var result = Contains(typeof(long));
        Assert.Equal("0L, 1L", result);
    }

    [Fact]
    public void Contains_Decimal_ReturnsTwoDecimals()
    {
        var result = Contains(typeof(decimal));
        Assert.Equal("0m, 1m", result);
    }

    [Fact]
    public void Contains_Enum_ReturnsTwoCastExpressions()
    {
        var result = Contains(typeof(DayOfWeek));
        Assert.Contains("DayOfWeek", result, StringComparison.OrdinalIgnoreCase);
        // Must have two values (separated by ", ")
        var parts = result.Split(", ");
        Assert.Equal(2, parts.Length);
    }

    [Fact]
    public void Contains_NullableInt_ReturnsSameAsNonNullable()
    {
        Assert.Equal(Contains(typeof(int)), Contains(typeof(int?)));
    }

    // ─── TryBuildReflectionPlaceholder ───────────────────────────────────────

    [Fact]
    public void TryBuildReflectionPlaceholder_TypeWithParameterlessCtor_ReturnsNewExpr()
    {
        var ok = TryReflection(typeof(FakeWithParameterlessCtor), out var placeholder);

        Assert.True(ok);
        Assert.StartsWith("new ", placeholder, StringComparison.Ordinal);
        Assert.EndsWith("()", placeholder, StringComparison.Ordinal);
        Assert.Contains(nameof(FakeWithParameterlessCtor), placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildReflectionPlaceholder_TypeWithFloatArrayCtor_ReturnsArrayInit()
    {
        var ok = TryReflection(typeof(FakeWithFloatArrayCtor), out var placeholder);

        Assert.True(ok);
        Assert.Contains("new float[1]", placeholder, StringComparison.Ordinal);
        Assert.Contains(nameof(FakeWithFloatArrayCtor), placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildReflectionPlaceholder_TypeWithAllNumericParamsCtor_ReturnsNewWithArgs()
    {
        var ok = TryReflection(typeof(FakeWithNumericParamsCtor), out var placeholder);

        Assert.True(ok);
        Assert.StartsWith("new ", placeholder, StringComparison.Ordinal);
        Assert.Contains(nameof(FakeWithNumericParamsCtor), placeholder, StringComparison.Ordinal);
        // Numeric args should be present
        Assert.Contains("1", placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildReflectionPlaceholder_AbstractClass_ReturnsFalse()
    {
        var ok = TryReflection(typeof(AbstractFake), out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryBuildReflectionPlaceholder_Interface_ReturnsFalse()
    {
        var ok = TryReflection(typeof(IFakeInterface), out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryBuildReflectionPlaceholder_TypeWithComplexCtorOnly_ReturnsFalse()
    {
        // No parameterless, no array-of-primitives, not all-primitive params
        var ok = TryReflection(typeof(FakeWithComplexCtor), out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryBuildReflectionPlaceholder_SecondCall_ReturnsSamePlaceholderFromCache()
    {
        TryReflection(typeof(FakeWithParameterlessCtor), out var first);
        TryReflection(typeof(FakeWithParameterlessCtor), out var second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void TryBuildReflectionPlaceholder_SecondCallAbstract_StillReturnsFalse()
    {
        TryReflection(typeof(AbstractFake), out _);
        var ok = TryReflection(typeof(AbstractFake), out _);
        Assert.False(ok);
    }

    // ─── Test doubles ─────────────────────────────────────────────────────────

    private sealed class FakeWithParameterlessCtor
    {
        // ReSharper disable once EmptyConstructor
        public FakeWithParameterlessCtor() { }
    }

    private sealed class FakeWithFloatArrayCtor
    {
        // ReSharper disable once UnusedParameter.Local
        public FakeWithFloatArrayCtor(float[] data) { }
    }

    private sealed class FakeWithNumericParamsCtor
    {
        // ReSharper disable once UnusedParameter.Local
        public FakeWithNumericParamsCtor(int x, double y) { }
    }

    private abstract class AbstractFake { }

    private interface IFakeInterface { }

    private sealed class FakeWithComplexCtor
    {
        // string and object are not primitive/decimal → should not match rule 3
        // ReSharper disable once UnusedParameter.Local
        public FakeWithComplexCtor(string s, object o) { }
    }
}
