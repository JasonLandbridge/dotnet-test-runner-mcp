using System.Runtime.CompilerServices;

namespace DotnetTest.Mcp;

public static class NotNullExtensions
{
    public static T ValidateNotNull<T>(
        this T? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(argument, paramName);
        return argument;
    }
}