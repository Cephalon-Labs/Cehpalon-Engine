using System.Diagnostics;

namespace Cehpalon.Engine.Core;

/// <summary>Experimental (M0) W3C version 00 trace context. Flags are the raw byte, not a sampling decision.</summary>
public readonly record struct TraceParent(ActivityTraceId TraceId, ActivitySpanId ParentSpanId, byte Flags)
{
    /// <summary>Parse exactly one version 00 traceparent value, or return null for missing or invalid input.</summary>
    /// <remarks>Supply all raw values; do not select the first, split combined values or trim them.
    /// This validates syntax only and does not establish trust in the sender.</remarks>
    public static TraceParent? ParseOrNull(ReadOnlySpan<string?> values)
    {
        if (values is not [{ Length: 55 } value]) return null;
        if (value[0] != '0' || value[1] != '0' || value[2] != '-' || value[35] != '-' || value[52] != '-')
            return null;

        var traceId = value.AsSpan(3, 32);
        var parentSpanId = value.AsSpan(36, 16);
        var flagsHigh = value[53];
        var flagsLow = value[54];
        if (!IsNonZeroLowerHex(traceId) || !IsNonZeroLowerHex(parentSpanId) ||
            !char.IsAsciiHexDigitLower(flagsHigh) || !char.IsAsciiHexDigitLower(flagsLow))
            return null;

        var flags = (byte)((HexValue(flagsHigh) << 4) | HexValue(flagsLow));
        return new(ActivityTraceId.CreateFromString(traceId), ActivitySpanId.CreateFromString(parentSpanId), flags);
    }

    private static bool IsNonZeroLowerHex(ReadOnlySpan<char> value)
    {
        var nonZero = false;
        foreach (var digit in value)
        {
            if (!char.IsAsciiHexDigitLower(digit)) return false;
            nonZero |= digit != '0';
        }
        return nonZero;
    }

    private static int HexValue(char digit) => digit <= '9' ? digit - '0' : digit - 'a' + 10;
}
