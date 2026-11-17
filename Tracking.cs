// Finds shipping tracking numbers in text, so they can open the carrier's tracking page.
// Formats and check digits follow github.com/jkeen/tracking_number_data (MIT). Numbers made only of
// digits must pass their check digit, and FedEx numbers also need the word "FedEx" just before them
// (FedEx's check digit alone lets one in ten random 12-digit numbers through). Nothing is looked up by the app itself; a click opens the browser.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfReaderApp
{
    class TrackingNumber
    {
        public int Start, End;      // In the searched text, including any spaces inside the number.
        public string Number;       // Without spaces.
        public string Carrier;
        public string Url;
    }

    static class Tracking
    {
        // Letters and digits, allowing single spaces between them ("1Z 999 AA1 01 2345 6784").
        static readonly Regex Ups = new Regex(@"(?<![0-9A-Za-z])1 ?Z(?: ?[0-9A-Z]){16}(?![0-9A-Za-z])");
        static readonly Regex Amazon = new Regex(@"(?<![0-9A-Za-z])TB[ACM] ?(?:[0-9] ?){11}[0-9](?![0-9A-Za-z])");
        static readonly Regex UspsInternational = new Regex(@"(?<![0-9A-Za-z])[A-Z]{2} ?(?:[0-9] ?){8}[0-9] ?US(?![0-9A-Za-z])");
        static readonly Regex Digits = new Regex(@"(?<![0-9A-Za-z])[0-9](?: ?[0-9]){11,33}(?![0-9A-Za-z])");
        static readonly Regex FedExWord = new Regex(@"fed ?ex", RegexOptions.IgnoreCase);

        public static string UpsUrl(string n) { return "https://www.ups.com/track?tracknum=" + n; }
        public static string UspsUrl(string n) { return "https://tools.usps.com/go/TrackConfirmAction?tLabels=" + n; }
        public static string FedExUrl(string n) { return "https://www.fedex.com/fedextrack/?trknbr=" + n; }
        public static string AmazonUrl(string n) { return "https://track.amazon.com/tracking/" + n; }

        public static List<TrackingNumber> Find(string text)
        {
            var found = new List<TrackingNumber>();
            if (string.IsNullOrEmpty(text)) return found;

            foreach (Match m in Ups.Matches(text))
            {
                string n = Strip(m.Value);
                if (UpsValid(n)) Add(found, m, n, "UPS", UpsUrl(n));
            }
            foreach (Match m in Amazon.Matches(text))
            {
                string n = Strip(m.Value);
                Add(found, m, n, "Amazon", AmazonUrl(n));
            }
            foreach (Match m in UspsInternational.Matches(text))
            {
                string n = Strip(m.Value);
                if (S10Valid(n)) Add(found, m, n, "USPS", UspsUrl(n));
            }
            foreach (Match m in Digits.Matches(text))
            {
                string n = Strip(m.Value);
                int from = Math.Max(0, m.Index - 50);
                bool fedexContext = FedExWord.IsMatch(text.Substring(from, m.Index - from));
                string carrier = DigitsCarrier(n, fedexContext);
                if (carrier == "USPS") Add(found, m, n, carrier, UspsUrl(n));
                else if (carrier == "FedEx") Add(found, m, n, carrier, FedExUrl(n));
            }
            found.Sort(delegate(TrackingNumber a, TrackingNumber b) { return a.Start.CompareTo(b.Start); });
            return found;
        }

        static void Add(List<TrackingNumber> list, Match m, string number, string carrier, string url)
        {
            // Regex matches can end with the space allowed between characters.
            int end = m.Index + m.Value.TrimEnd().Length;
            foreach (TrackingNumber t in list) if (m.Index < t.End && t.Start < end) return;
            list.Add(new TrackingNumber { Start = m.Index, End = end, Number = number, Carrier = carrier, Url = url });
        }

        static string Strip(string s) { return s.Replace(" ", ""); }

        // Which carrier a run of digits belongs to, or null. Order matters: the most specific formats first.
        public static string DigitsCarrier(string n, bool fedexContext)
        {
            if (n.Replace(n[0].ToString(), "").Length == 0) return null;   // "000000000000" passes the check digits.
            // USPS: 22 or 26 digits, sometimes after "420" and a 5 or 9 digit ZIP code. Newer numbers start 92 to 95,
            // older ones 91; 20-digit numbers are checked as if they started with 91.
            var candidates = new List<string> { n };
            if (n.StartsWith("420")) { if (n.Length > 8) candidates.Add(n.Substring(8)); if (n.Length > 12) candidates.Add(n.Substring(12)); }
            foreach (string u in candidates)
            {
                if ((u.Length == 22 || u.Length == 26) && u[0] == '9' && u[1] >= '1' && u[1] <= '5' && CheckLast(u, 3, 1)) return "USPS";
                if (u.Length == 20 && (CheckLast(u, 3, 1) || CheckLast("91" + u, 3, 1))) return "USPS";
            }
            if (!fedexContext) return null;
            if (n.Length == 12 && Weighted(n.Substring(0, 11), new[] { 3, 1, 7 }) == n[11] - '0') return "FedEx";
            if (n.Length == 15 && CheckLast(n, 1, 3)) return "FedEx";
            if (n.Length == 22 && n.StartsWith("96") && Mod10(n.Substring(7, 14), 1, 3, false) == n[21] - '0') return "FedEx";
            return null;
        }

        static bool CheckLast(string n, int evens, int odds)
        {
            return Mod10(n.Substring(0, n.Length - 1), evens, odds, false) == n[n.Length - 1] - '0';
        }

        // UPS: 1Z, then 15 letters or digits, then a check digit.
        public static bool UpsValid(string n)
        {
            return n.Length == 18 && char.IsDigit(n[17]) && Mod10(n.Substring(2, 15), 1, 2, false) == n[17] - '0';
        }

        // Universal Postal Union S10 numbers, like "EA123456785US".
        static bool S10Valid(string n)
        {
            if (n.Length != 13) return false;
            int[] w = { 8, 6, 4, 2, 3, 5, 9, 7 };
            int sum = 0;
            for (int i = 0; i < 8; i++) sum += (n[2 + i] - '0') * w[i];
            int check = 11 - sum % 11;
            if (check == 10) check = 0; else if (check == 11) check = 5;
            return check == n[10] - '0';
        }

        // Even/odd weighted sum mod 10, as in tracking_number_data (letters count as (code - 3) mod 10).
        static int Mod10(string s, int evens, int odds, bool reverse)
        {
            if (reverse) { var c = s.ToCharArray(); Array.Reverse(c); s = new string(c); }
            int total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int x = char.IsDigit(s[i]) ? s[i] - '0' : (s[i] - 3) % 10;
                total += x * (i % 2 == 0 ? evens : odds);
            }
            int check = total % 10;
            return check == 0 ? 0 : 10 - check;
        }

        // Repeating weights, then mod 11 and mod 10 (FedEx Express).
        static int Weighted(string s, int[] weights)
        {
            int total = 0;
            for (int i = 0; i < s.Length; i++) total += (s[i] - '0') * weights[i % weights.Length];
            return total % 11 % 10;
        }
    }
}
