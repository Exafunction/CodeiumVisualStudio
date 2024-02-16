using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeiumVS
{
public static class StringCompare
{

    // returns the number of times letter c appears in s
    public static int GetOccurenceOfLetter(String s, char c)
    {
        int n = 0;
        for (int i = 0; (i = s.IndexOf(c, i)) >= 0; i++, n++) {}
        return n;
    }

    // skips whitespace
    public static int nextNonWhitespace(String s, int index)
    {
        for (; index < s.Length && Char.IsWhiteSpace(s[index]); index++)
            ;
        ;
        return index;
    }

    public static bool IsNameChar(char c) { return Char.IsLetterOrDigit(c) || c == '_'; }

    // Compares the two strings to see if a is a prefix of b ignoring whitespace
    public static Tuple<int, int> CompareStrings(String a, String b)
    {
        int a_index = 0, b_index = 0;
        while (a_index < a.Length && b_index < b.Length)
        {
            char aChar = a[a_index];
            char bChar = b[b_index];
            if (aChar == bChar)
            {
                a_index++;
                b_index++;
            }
            else
            {
                if (Char.IsWhiteSpace(bChar))
                {
                    b_index = nextNonWhitespace(b, b_index);

                    continue;
                }

                if (Char.IsWhiteSpace(aChar) && b_index >= 1 &&
                    (!IsNameChar(b[b_index]) || !IsNameChar(b[b_index - 1])))
                {
                    a_index = nextNonWhitespace(a, a_index);

                    continue;
                }

                break;
            }
        }

        return new Tuple<int, int>(a_index, b_index);
    }

    // Check if the text in the editor is a substring of the the suggestion text
    // If it matches return the line number of the suggestion text that matches the current line in
    // the editor else return -1
    public static int CheckSuggestion(String suggestion, String line, bool isTextInsertion = false,
                                      int insertionPoint = -1)
    {
        if (line.Length == 0) { return 0; }

        int index = suggestion.IndexOf(line);
        int endPos = index + line.Length;
        int firstLineBreak = suggestion.IndexOf('\n');

        if (index > -1 && (firstLineBreak == -1 || endPos < firstLineBreak))
        {
            return index == 0 ? line.Length : -1;
        }
        else
        {
            Tuple<int, int> res = CompareStrings(line, suggestion);
            int endPoint = isTextInsertion ? line.Length - insertionPoint : line.Length;
            return res.Item1 >= endPoint ? res.Item2 : -1;
        }
    }
}
}
