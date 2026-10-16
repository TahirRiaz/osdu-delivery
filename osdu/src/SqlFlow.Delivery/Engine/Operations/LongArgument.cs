using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Compute;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// A text longer than one argument of an operation's payload holds (<see cref="ComputeTaskPayload.MaxArgumentLength"/>),
/// carried in several: <c>name.0</c>, <c>name.1</c> and on, each a piece of it in order, read back whole by the operation.
/// The dimension builder's draft and request travel this way; an operation reads them back exactly as they were written.
/// </summary>
public static class LongArgument
{
    /// <summary>The characters one piece holds: under an argument's limit, whatever the text.</summary>
    public const int PieceLength = 3_900;

    /// <summary>The pieces one text is carried in at most, which leaves a payload room for the arguments every operation takes.</summary>
    public const int MaxPieces = 16;

    /// <summary>The longest text a long argument carries.</summary>
    public const int MaxLength = PieceLength * MaxPieces;

    /// <summary>Puts <paramref name="text"/> into <paramref name="arguments"/> under <paramref name="name"/>, a piece an argument.</summary>
    /// <exception cref="SqlFlowException">The text is longer than <see cref="MaxLength"/>.</exception>
    public static void Put(IDictionary<string, string> arguments, string name, string text)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLength)
        {
            throw new SqlFlowException(string.Create(CultureInfo.InvariantCulture, $"'{name}' is {text.Length:N0} characters; an operation is given at most {MaxLength:N0}."));
        }

        var pieces = Math.Max(1, (text.Length + PieceLength - 1) / PieceLength);
        for (var i = 0; i < pieces; i++)
        {
            var start = i * PieceLength;
            arguments[PieceName(name, i)] = text.Substring(start, Math.Min(PieceLength, text.Length - start));
        }
    }

    /// <summary>The text <paramref name="payload"/> carries under <paramref name="name"/>, its pieces joined in order; null when it carries none.</summary>
    public static string? Read(ComputeTaskPayload payload, string name)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var arguments = payload.Arguments;
        if (arguments is null || !arguments.TryGetValue(PieceName(name, 0), out var first))
        {
            return null;
        }

        var text = new System.Text.StringBuilder(first);
        for (var i = 1; i < MaxPieces && arguments.TryGetValue(PieceName(name, i), out var piece); i++)
        {
            text.Append(piece);
        }

        return text.ToString();
    }

    /// <summary>The text <paramref name="payload"/> carries under <paramref name="name"/>.</summary>
    /// <exception cref="SqlFlowException">It carries none.</exception>
    public static string Require(ComputeTaskPayload payload, string name)
        => Read(payload, name) ?? throw new SqlFlowException($"The operation needs '{name}', and the payload carries none.");

    private static string PieceName(string name, int index) => string.Create(CultureInfo.InvariantCulture, $"{name}.{index}");
}
