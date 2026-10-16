using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// <see cref="TodoToastArguments"/> battery (task 10-04 stage ③): the launch
/// argument codec that carries the task uuid through OS toast activation —
/// build/parse round trip, multi-segment tolerance, and everything malformed
/// that must yield null (a deep link never partially trusted).
/// </summary>
public sealed class TodoToastArgumentsTests
{
    private const string Uuid = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Build_ThenParse_RoundTrips()
    {
        var args = TodoToastArguments.Build(Uuid);

        Assert.Equal($"todo-task={Uuid}", args);
        Assert.Equal(Uuid, TodoToastArguments.TryParseTaskUuid(args));
    }

    [Theory]
    [InlineData("todo-task=0123456789abcdef0123456789abcdef")]                      // exact
    [InlineData("todo-task=0123456789abcdef0123456789abcdef;action=view")]           // trailing segment
    [InlineData("action=view todo-task=0123456789abcdef0123456789abcdef")]           // leading segment (space)
    [InlineData("action=view;todo-task=0123456789abcdef0123456789abcdef;extra=1")]   // middle segment
    [InlineData("todo-task=0123456789ABCDEF0123456789ABCDEF")]                       // uppercase hex tolerated
    public void TryParseTaskUuid_Accepts_KnownForms(string arguments) =>
        Assert.NotNull(TodoToastArguments.TryParseTaskUuid(arguments));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("action=view")]
    [InlineData("todo-task=")]                                // empty value
    [InlineData("todo-task=0123456789abcdef0123456789abcde")] // 31 chars
    [InlineData("todo-task=0123456789abcdef0123456789abcdeff")] // 33 chars
    [InlineData("todo-task=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 32 non-hex
    [InlineData("todo-task=0123456789abcdef-0123456789abcdef")] // separator inside
    public void TryParseTaskUuid_Rejects_Malformed(string? arguments) =>
        Assert.Null(TodoToastArguments.TryParseTaskUuid(arguments));

    [Fact]
    public void TryParseTaskUuid_MalformedValue_IsNeverPartiallyTrusted()
    {
        // A matched prefix with a junk value must not fall through to a later
        // (possibly valid-looking) segment.
        Assert.Null(TodoToastArguments.TryParseTaskUuid($"todo-task=junk;todo-task={Uuid}"));
    }
}
