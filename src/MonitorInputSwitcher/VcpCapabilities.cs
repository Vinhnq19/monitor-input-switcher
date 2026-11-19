using System;
using System.Collections.Generic;

namespace MonitorInputSwitcher;

public static class VcpCapabilities
{
    private const byte InputSourceCode = 0x60;

    public static (bool AdvertisesVcp60, IReadOnlyList<byte>? Inputs) Parse(string? capabilities)
    {
        if (string.IsNullOrEmpty(capabilities))
        {
            return (false, null);
        }

        if (!HasBalancedParentheses(capabilities))
        {
            return (false, null);
        }

        var open = FindVcpParenthesis(capabilities);
        if (open < 0)
        {
            return (false, null);
        }

        var close = FindMatchingParenthesis(capabilities, open);
        if (close < 0)
        {
            return (false, null);
        }

        var advertisesVcp60 = false;
        List<byte>? inputs = null;
        var hasMalformedInputList = false;

        var index = open + 1;
        while (index < close)
        {
            while (index < close && char.IsWhiteSpace(capabilities[index]))
            {
                index++;
            }

            if (index >= close)
            {
                break;
            }

            var tokenStart = index;
            while (index < close &&
                   !char.IsWhiteSpace(capabilities[index]) &&
                   capabilities[index] != '(' &&
                   capabilities[index] != ')')
            {
                index++;
            }

            if (index == tokenStart)
            {
                return (false, null);
            }

            var token = capabilities[tokenStart..index];

            var next = index;
            while (next < close && char.IsWhiteSpace(capabilities[next]))
            {
                next++;
            }

            var hasList = false;
            var listStart = 0;
            var listEnd = 0;

            if (next < close && capabilities[next] == '(')
            {
                var listClose = FindMatchingParenthesis(capabilities, next);
                if (listClose < 0 || listClose >= close)
                {
                    return (false, null);
                }

                hasList = true;
                listStart = next + 1;
                listEnd = listClose;
                index = listClose + 1;
            }
            else
            {
                index = next;
            }

            if (!TryParseCode(token, out var code) || code != InputSourceCode)
            {
                continue;
            }

            advertisesVcp60 = true;

            if (!hasList)
            {
                continue;
            }

            if (TryParseInputList(capabilities, listStart, listEnd, out var parsedInputs) &&
                parsedInputs.Count > 0)
            {
                inputs ??= new List<byte>();
                foreach (var input in parsedInputs)
                {
                    if (!inputs.Contains(input))
                    {
                        inputs.Add(input);
                    }
                }
            }
            else
            {
                hasMalformedInputList = true;
            }
        }

        return hasMalformedInputList
            ? (advertisesVcp60, null)
            : (advertisesVcp60, inputs);
    }

    private static bool TryParseCode(string token, out byte code)
    {
        code = 0;
        if (token.Length != 2)
        {
            return false;
        }

        var high = HexValue(token[0]);
        var low = HexValue(token[1]);
        if (high < 0 || low < 0)
        {
            return false;
        }

        code = (byte)((high << 4) | low);
        return true;
    }

    private static bool TryParseInputList(string text, int start, int end, out List<byte> inputs)
    {
        inputs = new List<byte>();
        var index = start;

        while (index < end)
        {
            while (index < end && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= end)
            {
                break;
            }

            var tokenStart = index;
            while (index < end && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var token = text[tokenStart..index];
            if (!TryParseCode(token, out var input))
            {
                inputs.Clear();
                return false;
            }

            inputs.Add(input);
        }

        return inputs.Count > 0;
    }

    private static bool HasBalancedParentheses(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static int FindVcpParenthesis(string text)
    {
        var rootDepth = GetRootDepth(text);
        var depth = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                if (depth == rootDepth &&
                    i >= 3 &&
                    (text[i - 3] == 'v' || text[i - 3] == 'V') &&
                    string.Compare(text, i - 3, "vcp", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (i == 3 || !IsIdentifierChar(text[i - 4])))
                {
                    return i;
                }

                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
        }

        return -1;
    }

    private static int GetRootDepth(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        if (index >= text.Length || text[index] != '(')
        {
            return 0;
        }

        var close = FindMatchingParenthesis(text, index);
        if (close < 0)
        {
            return 0;
        }

        for (var trailing = close + 1; trailing < text.Length; trailing++)
        {
            if (!char.IsWhiteSpace(text[trailing]))
            {
                return 0;
            }
        }

        return 1;
    }

    private static int FindMatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

    private static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}
