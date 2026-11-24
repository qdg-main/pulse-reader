// Portions adapted from number-to-words 1.2.5, MIT license.
// Copyright (c) 2015 Martin Eneqvist <marlun78@hotmail.com>
// https://github.com/marlun78/number-to-words/tree/02b3cc45829fbe21d8166fcf1df0d5b33e616b7e
// Changes: C# implementation, exact BigInteger arithmetic, British conjunctions,
// additional short-scale labels, observed year/decimal speech policies.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace MisakiSharp;

internal static class PermissiveNumberWords
{
    private static readonly string[] Small =
    ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
     "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens =
    ["zero", "ten", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    // Short-scale lexical facts, ascending in groups of three decimal digits.
    // Ordinary names/powers corroborated by Russ Rowlett's "Names for Large
    // Numbers", https://www.ibiblio.org/units/large.html, and Wolfram MathWorld,
    // https://mathworld.wolfram.com/LargeNumber.html. Higher Latin naming facts:
    // https://www.themathdoctors.org/names-of-large-numbers-vigintillion-and-beyond/
    // Historical spelling variants below preserve measured DLL speech tokens.
    // This is an explicit lexical mapping, not an imported naming algorithm.
    private static readonly string[] Scales =
    [
        "", "thousand", "million", "billion", "trillion", "quadrillion", "quintillion", "sextillion", "septillion", "octillion", "nonillion",
        "decillion", "undecillion", "duodecillion", "tredecillion", "quattuordecillion", "quindecillion", "sexdecillion", "septdecillion", "octodecillion", "novemdecillion",
        "vigintillion", "unvigintillion", "duovigintillion", "trevigintillion", "quattuorvigintillion", "quinvigintillion", "sexvigintillion", "septvigintillion", "octovigintillion", "novemvigintillion",
        "trigintillion", "untrigintillion", "duotrigintillion", "tretrigintillion", "quattuortrigintillion", "quintrigintillion", "sextrigintillion", "septtrigintillion", "octotrigintillion", "novemtrigintillion",
        "quadragintillion", "unquadragintillion", "duoquadragintillion", "trequadragintillion", "quattuorquadragintillion", "quinquadragintillion", "sexquadragintillion", "septquadragintillion", "octoquadragintillion", "novemquadragintillion",
        "quinquagintillion", "unquinquagintillion", "duoquinquagintillion", "trequinquagintillion", "quattuorquinquagintillion", "quinquinquagintillion", "sexquinquagintillion", "septquinquagintillion", "octoquinquagintillion", "novemquinquagintillion",
        "sexagintillion", "unsexagintillion", "duosexagintillion", "tresexagintillion", "quattuorsexagintillion", "quinsexagintillion", "sexsexagintillion", "septsexagintillion", "octosexagintillion", "novemsexagintillion",
        "septuagintillion", "unseptuagintillion", "duoseptuagintillion", "treseptuagintillion", "quattuorseptuagintillion", "quinseptuagintillion", "sexseptuagintillion", "septseptuagintillion", "octoseptuagintillion", "novemseptuagintillion",
        "octogintillion", "unoctogintillion", "duooctogintillion", "treoctogintillion", "quattuoroctogintillion", "quinoctogintillion", "sexoctogintillion", "septoctogintillion", "octooctogintillion", "novemoctogintillion",
        "nonagintillion", "unnonagintillion", "duononagintillion", "trenonagintillion", "quattuornonagintillion", "quinnonagintillion", "sexnonagintillion", "septnonagintillion", "octononagintillion", "novemnonagintillion",
        "centillion"
    ];
    private static readonly BigInteger Ceiling = BigInteger.Pow(1000, Scales.Length);

    public static string Cardinal(BigInteger number)
    {
        var magnitude = BigInteger.Abs(number);
        if (magnitude >= Ceiling) throw new OverflowException("Number exceeds the supported short-scale range.");
        if (magnitude.IsZero) return Small[0];
        var groups = new List<int>();
        while (!magnitude.IsZero)
        {
            magnitude = BigInteger.DivRem(magnitude, 1000, out var group);
            groups.Add((int)group);
        }
        var result = new StringBuilder(number.Sign < 0 ? "minus " : "");
        var hasPrevious = false;
        for (var scale = groups.Count - 1; scale >= 0; scale--)
        {
            var group = groups[scale];
            if (group == 0) continue;
            if (hasPrevious) result.Append(scale == 0 && group < 100 ? " and " : ", ");
            result.Append(UnderThousand(group));
            if (scale > 0) result.Append(' ').Append(Scales[scale]);
            hasPrevious = true;
        }
        return result.ToString();
    }

    private static string UnderThousand(int number)
    {
        if (number < 20) return Small[number];
        if (number < 100) return Tens[number / 10] + (number % 10 == 0 ? "" : "-" + Small[number % 10]);
        return Small[number / 100] + " hundred" + (number % 100 == 0 ? "" : " and " + UnderThousand(number % 100));
    }

    public static string Ordinal(BigInteger number)
    {
        var cardinal = Cardinal(number);
        var last = Math.Max(cardinal.LastIndexOf(' '), cardinal.LastIndexOf('-')) + 1;
        var word = cardinal[last..];
        var ordinal = word switch
        {
            "zero" => "zeroth", "one" => "first", "two" => "second", "three" => "third",
            "four" => "fourth", "five" => "fifth", "six" => "sixth", "seven" => "seventh",
            "eight" => "eighth", "nine" => "ninth", "ten" => "tenth", "eleven" => "eleventh", "twelve" => "twelfth",
            _ => word.EndsWith('y') ? word[..^1] + "ieth" : word + "th"
        };
        return cardinal[..last] + ordinal;
    }

    public static string Year(BigInteger number)
    {
        // Observable policy: opening millennium decades use cardinal speech;
        // other three/four-digit values use century/remainder pairs.
        if (BigInteger.Abs(number) < 100 || number >= 10000) return Cardinal(number);
        if (number.Sign < 0 && BigInteger.Abs(number / 100) % 10 == 0) return Cardinal(number);
        if (number >= 1000)
        {
            for (var opening = 1000; opening <= 9000; opening += 1000)
                if (number >= opening && number <= opening + 9) return Cardinal(number);
        }
        var century = BigInteger.DivRem(number, 100, out var remainder);
        var ending = remainder.IsZero ? "hundred" : remainder < 10 ? "oh-" + Cardinal(remainder) : Cardinal(remainder);
        return Cardinal(century) + " " + ending;
    }

    public static string Float(double number)
    {
        if (!double.IsFinite(number)) throw new OverflowException("A finite value is required.");
        var magnitude = Math.Abs(number);
        var text = magnitude.ToString("R", CultureInfo.InvariantCulture);
        var exponentOffset = text.IndexOf('E');
        var exponent = exponentOffset < 0 ? 0 : int.Parse(text[(exponentOffset + 1)..], CultureInfo.InvariantCulture);
        var mantissa = exponentOffset < 0 ? text : text[..exponentOffset];
        var decimalOffset = mantissa.IndexOf('.');
        var integerLength = decimalOffset < 0 ? mantissa.Length : decimalOffset;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var point = integerLength + exponent;
        string fractionDigits;
        if (point <= 0)
        {
            fractionDigits = new string('0', -point) + digits;
        }
        else if (point >= digits.Length)
        {
            fractionDigits = "";
        }
        else
        {
            fractionDigits = digits[point..];
        }
        if (fractionDigits.Length > 308) throw new OverflowException("Fractional precision exceeds the observable conversion range.");
        // Speak the actual binary floating-point integer, rather than the
        // rounded decimal mantissa. Observations distinguish these above 2^53.
        var integer = new BigInteger(magnitude);
        if (fractionDigits.Length > 0)
        {
            // The observed output quantizes the binary fractional remainder
            // to the displayed precision. Snap a remainder within one
            // hundredth of a decimal digit to the nearest integer digit;
            // otherwise keep the digits below it. This deliberately retains
            // measured arithmetic artifacts, rather than normalizing the text.
            // Parse the exact decimal power instead of Math.Pow: the latter
            // differs by one ULP at precisions 23 and 126 on this runtime.
            var factor = double.Parse("1E" + fractionDigits.Length.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var scaledFraction = (magnitude - Math.Truncate(magnitude)) * factor;
            var nearest = Math.Round(scaledFraction);
            var quantized = Math.Abs(scaledFraction - nearest) < 0.01 ? nearest : Math.Floor(scaledFraction);
            fractionDigits = new BigInteger(quantized).ToString(CultureInfo.InvariantCulture).PadLeft(fractionDigits.Length, '0');
        }
        // Preserve observed loss of the negative sign on subunit values.
        var result = new StringBuilder(Cardinal(number < 0 ? -integer : integer));
        if (fractionDigits.Length > 0)
        {
            result.Append(" point");
            foreach (var digit in fractionDigits) result.Append(' ').Append(Small[digit - '0']);
        }
        return result.ToString();
    }
}
