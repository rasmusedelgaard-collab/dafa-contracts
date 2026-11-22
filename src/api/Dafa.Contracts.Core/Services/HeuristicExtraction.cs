using System.Globalization;
using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Regex and keyword extraction from contract text. Used by the test-mode analyzer and as the
/// fallback of the Azure OpenAI analyzer. Results are suggestions the user accepts one by one.
/// </summary>
public static class HeuristicExtraction
{
    private const RegexOptions RegexFlags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private const string DatePattern =
        @"(\d{4}-\d{2}-\d{2}|\d{1,2}[./]\d{1,2}[./]\d{4}|\d{1,2}(?:st|nd|rd|th)?\.?\s+(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}|(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4})";

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "d.M.yyyy", "dd.MM.yyyy", "d/M/yyyy", "dd/MM/yyyy",
        "d MMMM yyyy", "MMMM d yyyy", "MMMM d, yyyy"
    };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["eighteen"] = 18,
        ["twenty-four"] = 24, ["twenty four"] = 24, ["thirty"] = 30, ["thirty-six"] = 36, ["sixty"] = 60, ["ninety"] = 90
    };

    private const string NumberPattern =
        @"(\d{1,4}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|eighteen|twenty[- ]four|thirty[- ]six|thirty|sixty|ninety)";

    private static readonly (string Keyword, string Law)[] Laws =
    {
        ("Denmark", "Danish law"), ("Danish", "Danish law"), ("Sweden", "Swedish law"), ("Swedish", "Swedish law"),
        ("Germany", "German law"), ("German", "German law"), ("England", "English law"), ("English", "English law"),
        ("Poland", "Polish law"), ("Polish", "Polish law"), ("Italy", "Italian law"), ("Italian", "Italian law"),
        ("People's Republic of China", "Chinese law"), ("Chinese", "Chinese law"), ("PRC", "Chinese law"),
        ("New York", "New York law"), ("Delaware", "Delaware law"), ("Norway", "Norwegian law"), ("Norwegian", "Norwegian law"),
        ("Netherlands", "Dutch law"), ("Dutch", "Dutch law"), ("Switzerland", "Swiss law"), ("Swiss", "Swiss law")
    };

    public static ContractAnalysis Analyze(string? rawText, string fileName, int pages)
    {
        var analysis = new ContractAnalysis { Pages = pages };
        var text = Normalize(rawText);
        if (text.Length < 40)
        {
            analysis.Summary = "No readable text was found in the file. If it is a scanned document, enter the fields manually.";
            return analysis;
        }

        var title = FindTitle(text, fileName);
        Suggest(analysis, "title", title, 0.5);

        var counterparty = FindCounterparty(text);
        Suggest(analysis, "counterpartyName", counterparty, 0.6);

        var startDate = FindDate(text, @"(?:effective\s+date|effective\s+as\s+of|effective\s+from|commenc\w+\s+on|enters?\s+into\s+force\s+on|with\s+effect\s+from|dated|made\s+on)\D{0,40}?" + DatePattern);
        Suggest(analysis, "startDate", FormatDate(startDate), 0.6);

        var endDate = FindDate(text, @"(?:expir\w+\s+on|terminat\w+\s+on|remain\w*\s+in\s+(?:force|effect)\s+until|until|ends?\s+on|end\s+date)\D{0,30}?" + DatePattern);
        int? termMonths = FindPeriodMonths(text, @"(?:initial\s+)?(?:term|period)\s+of\s+" + NumberPattern + @"\s*(?:\(\d+\)\s*)?(year|month)s?");
        if (endDate is null && startDate is not null && termMonths is not null)
        {
            endDate = startDate.Value.AddMonths(termMonths.Value).AddDays(-1);
        }
        if (endDate is not null && (startDate is null || endDate.Value > startDate.Value))
        {
            Suggest(analysis, "endDate", FormatDate(endDate), 0.55);
        }

        var noticeDays = FindNoticeDays(text);
        Suggest(analysis, "noticePeriodDays", noticeDays?.ToString(CultureInfo.InvariantCulture), 0.65);

        var autoRenew = Regex.IsMatch(text, @"automatic(?:ally)?\s+(?:be\s+)?renew|auto[- ]?renew|renew(?:s|ed)?\s+automatically|tacit(?:ly)?\s+renew|successive\s+(?:periods?|terms?)|shall\s+be\s+extended\s+automatically", RegexFlags, RegexTimeout);
        Suggest(analysis, "autoRenew", autoRenew ? "true" : "false", autoRenew ? 0.75 : 0.4);
        int? renewalMonths = null;
        if (autoRenew)
        {
            renewalMonths = FindPeriodMonths(text, @"(?:successive|additional|further|renewal|subsequent)\s+(?:periods?|terms?)\s+of\s+" + NumberPattern + @"\s*(?:\(\d+\)\s*)?(year|month)s?")
                ?? FindPeriodMonths(text, @"renew\w*\s+(?:automatically\s+)?for\s+(?:successive\s+|additional\s+|further\s+)?(?:periods?\s+of\s+|terms?\s+of\s+)?" + NumberPattern + @"\s*(?:\(\d+\)\s*)?(year|month)s?");
            Suggest(analysis, "renewalMonths", renewalMonths?.ToString(CultureInfo.InvariantCulture), 0.6);
        }

        var law = FindGoverningLaw(text);
        Suggest(analysis, "governingLaw", law, 0.7);

        var liabilityCap = FindFirst(text, @"liabilit\w+[^.]{0,200}?(?:shall\s+not\s+exceed|limited\s+to|capped\s+at|not\s+exceed)\s+([^.;]{3,140})");
        Suggest(analysis, "liabilityCap", liabilityCap, 0.55);
        int? capMonths = null;
        if (liabilityCap is not null)
        {
            capMonths = FindPeriodMonths(liabilityCap, NumberPattern + @"\s*(?:\(\d+\)\s*)?(year|month)s?");
        }

        var changeOfControl = Regex.IsMatch(text, @"change\s+of\s+control|change\s+in\s+control", RegexFlags, RegexTimeout);
        Suggest(analysis, "changeOfControl", changeOfControl ? "true" : "false", changeOfControl ? 0.8 : 0.45);

        var paymentDays = FindFirst(text, @"(?:within\s+|net\s+)(\d{1,3})\s*(?:\(\w+\)\s*)?days?(?:\s+(?:net|from|after|of)\b)?[^.]{0,40}?invoice")
            ?? FindFirst(text, @"net\s+(\d{1,3})\s+days");
        Suggest(analysis, "paymentTerms", paymentDays is null ? null : "Net " + paymentDays + " days", 0.55);

        var currency = FindFirst(text, @"\b(DKK|EUR|USD|SEK|NOK|GBP|PLN|CNY)\b");
        Suggest(analysis, "currency", currency?.ToUpperInvariant(), 0.5);

        // Price adjustment
        string? priceAdjustment = null;
        var fixedIncrease = FindFirst(text, @"(?:prices?|fees?|rates?|charges?)[^.]{0,120}?(?:increase\w*|adjust\w*|rais\w*)[^.]{0,60}?(\d{1,2}(?:[.,]\d{1,2})?)\s*(?:%|per\s*cent|percent)");
        var indexLinked = Regex.IsMatch(text, @"(?:consumer\s+price\s+index|net\s+price\s+index|\bCPI\b|\bHICP\b|index[- ]linked|adjust\w*[^.]{0,60}?\bindex\b|\bindex\w*\s+(?:regulat|adjust))", RegexFlags, RegexTimeout);
        if (indexLinked)
        {
            priceAdjustment = "Index-linked";
        }
        else if (fixedIncrease is not null)
        {
            priceAdjustment = "Fixed increase of " + fixedIncrease + "% per year";
        }
        Suggest(analysis, "priceAdjustment", priceAdjustment, 0.5);

        var exclusive = Regex.IsMatch(text, @"\bexclusiv(?:e|ity)\b", RegexFlags, RegexTimeout)
            && !Regex.IsMatch(text, @"non[- ]exclusive", RegexFlags, RegexTimeout);
        if (exclusive)
        {
            Suggest(analysis, "exclusivity", "Exclusivity clause found", 0.5);
        }

        analysis.Deviations = FindDeviations(law, liabilityCap, capMonths, autoRenew, renewalMonths, indexLinked, fixedIncrease);
        analysis.Summary = BuildSummary(title, counterparty, startDate, endDate, noticeDays, autoRenew, renewalMonths, law, changeOfControl);
        return analysis;
    }

    /// <summary>Deviations from the DAFA standard position (placeholder standard until Legal confirms it).</summary>
    public static List<Deviation> FindDeviations(
        string? governingLaw, string? liabilityCap, int? capMonths, bool autoRenew, int? renewalMonths,
        bool indexLinked, string? fixedIncreasePercent)
    {
        var deviations = new List<Deviation>();
        const string capStandard = "Liability cap of at least 12 months' fees";
        if (capMonths is not null && capMonths.Value < 12)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.High, Text = "Liability is capped at " + capMonths.Value + " months, below the standard.", Standard = capStandard });
        }
        else if (capMonths is not null)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.None, Text = "Liability cap of " + capMonths.Value + " months.", Standard = capStandard });
        }
        else if (liabilityCap is not null)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.Low, Text = "Liability cap found but not expressed in months: " + Shorten(liabilityCap, 90), Standard = capStandard });
        }

        const string renewalStandard = "Automatic renewal for at most 12 months at a time";
        if (autoRenew && renewalMonths is not null && renewalMonths.Value > 12)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.Medium, Text = "Renews automatically for " + renewalMonths.Value + " months at a time.", Standard = renewalStandard });
        }
        else if (autoRenew && renewalMonths is not null)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.None, Text = "Renews automatically for " + renewalMonths.Value + " months at a time.", Standard = renewalStandard });
        }

        const string priceStandard = "Price changes only by a published index";
        if (!indexLinked && fixedIncreasePercent is not null)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.Medium, Text = "Price increase of " + fixedIncreasePercent + "% that is not linked to an index.", Standard = priceStandard });
        }
        else if (indexLinked)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.None, Text = "Price adjustment follows an index.", Standard = priceStandard });
        }

        const string lawStandard = "Governing law stated (Danish law preferred)";
        if (governingLaw is null)
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.Medium, Text = "No governing law clause was found.", Standard = lawStandard });
        }
        else
        {
            deviations.Add(new Deviation { Severity = DeviationSeverity.None, Text = "Governing law: " + governingLaw + ".", Standard = lawStandard });
        }

        return deviations.OrderBy(d => (int)d.Severity).ToList();
    }

    // ---------- helpers ----------

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        var cleaned = text.Replace(' ', ' ').Replace('’', '\'').Replace('“', '"').Replace('”', '"');
        cleaned = Regex.Replace(cleaned, @"[ \t\r\f]+", " ", RegexOptions.None, RegexTimeout);
        // Keep the analysis bounded on very large files.
        return cleaned.Length > 400000 ? cleaned.Substring(0, 400000) : cleaned.Trim();
    }

    private static void Suggest(ContractAnalysis analysis, string field, string? value, double confidence)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            analysis.Suggestions.Add(new FieldSuggestion { Field = field, Value = value.Trim(), Confidence = confidence });
        }
    }

    private static string? FindFirst(string text, string pattern)
    {
        try
        {
            var match = Regex.Match(text, pattern, RegexFlags | RegexOptions.Singleline, RegexTimeout);
            if (match.Success && match.Groups.Count > 1)
            {
                var value = match.Groups[1].Value.Trim();
                return value.Length == 0 ? null : value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return null;
    }

    private static DateOnly? FindDate(string text, string pattern)
    {
        return ParseDate(FindFirst(text, pattern));
    }

    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var cleaned = Regex.Replace(value.Trim(), @"(?<=\d)(st|nd|rd|th)\b", "", RegexFlags, RegexTimeout);
        cleaned = cleaned.Replace(". ", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.None, RegexTimeout);
        if (DateOnly.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            && date.Year >= 1990 && date.Year <= 2100)
        {
            return date;
        }
        return null;
    }

    private static string? FormatDate(DateOnly? date) =>
        date is null ? null : date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static int? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var cleaned = value.Trim();
        if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }
        return NumberWords.TryGetValue(cleaned, out var word) ? word : (int?)null;
    }

    /// <summary>Matches "N year(s)/month(s)" with the number in group 1 and the unit in group 2.</summary>
    private static int? FindPeriodMonths(string text, string pattern)
    {
        try
        {
            var match = Regex.Match(text, pattern, RegexFlags | RegexOptions.Singleline, RegexTimeout);
            if (!match.Success || match.Groups.Count < 3)
            {
                return null;
            }
            var number = ParseNumber(match.Groups[1].Value);
            if (number is null || number.Value <= 0)
            {
                return null;
            }
            var unit = match.Groups[2].Value.ToLowerInvariant();
            return unit.StartsWith("year", StringComparison.Ordinal) ? number.Value * 12 : number.Value;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static int? FindNoticeDays(string text)
    {
        try
        {
            var match = Regex.Match(
                text,
                NumberPattern + @"\s*(?:\(\d+\)\s*)?(day|month|year)s?'?\s*(?:prior\s+|advance\s+)?(?:written\s+)?notice",
                RegexFlags | RegexOptions.Singleline,
                RegexTimeout);
            if (!match.Success)
            {
                match = Regex.Match(
                    text,
                    @"notice\s+(?:period\s+)?of\s+(?:at\s+least\s+|not\s+less\s+than\s+)?" + NumberPattern + @"\s*(?:\(\d+\)\s*)?(day|month|year)s?",
                    RegexFlags | RegexOptions.Singleline,
                    RegexTimeout);
            }
            if (!match.Success)
            {
                return null;
            }
            var number = ParseNumber(match.Groups[1].Value);
            if (number is null || number.Value <= 0)
            {
                return null;
            }
            var unit = match.Groups[2].Value.ToLowerInvariant();
            if (unit.StartsWith("month", StringComparison.Ordinal)) return number.Value * 30;
            if (unit.StartsWith("year", StringComparison.Ordinal)) return number.Value * 365;
            return number.Value;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? FindGoverningLaw(string text)
    {
        var clause = FindFirst(text, @"(governed\s+by[^.]{0,160}|governing\s+law[^.]{0,160}|subject\s+to\s+the\s+laws?\s+of[^.]{0,80}|construed\s+in\s+accordance\s+with[^.]{0,120})");
        if (clause is null)
        {
            return null;
        }
        foreach (var (keyword, law) in Laws)
        {
            if (clause.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return law;
            }
        }
        var country = FindFirst(clause, @"laws?\s+of\s+(?:the\s+)?([A-Z][A-Za-z]+(?:\s+[A-Z][A-Za-z]+){0,3})");
        return country is null ? null : "Law of " + country;
    }

    private static string? FindCounterparty(string text)
    {
        var head = text.Length > 4000 ? text.Substring(0, 4000) : text;
        try
        {
            var match = Regex.Match(
                head,
                @"between[:\s]+(?:\(1\)\s*)?(.{2,120}?)(?:\s*\(|,|\s+hereinafter|\s+a\s+company)[\s\S]{0,400}?\band\b[:\s]+(?:\(2\)\s*)?(.{2,120}?)(?:\s*\(|,|\s+hereinafter|\s+a\s+company|\.|\n)",
                RegexFlags,
                RegexTimeout);
            if (!match.Success)
            {
                return null;
            }
            var first = CleanParty(match.Groups[1].Value);
            var second = CleanParty(match.Groups[2].Value);
            var firstIsDafa = first.Contains("DAFA", StringComparison.OrdinalIgnoreCase);
            var secondIsDafa = second.Contains("DAFA", StringComparison.OrdinalIgnoreCase);
            if (firstIsDafa && !secondIsDafa) return Empty(second);
            if (secondIsDafa && !firstIsDafa) return Empty(first);
            return Empty(second) ?? Empty(first);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string CleanParty(string value)
    {
        var cleaned = value.Replace("\n", " ").Trim().Trim(',', ';', ':', '"', '\'').Trim();
        return cleaned.Length > 100 ? cleaned.Substring(0, 100).Trim() : cleaned;
    }

    private static string? Empty(string value) => value.Length < 2 ? null : value;

    private static string? FindTitle(string text, string fileName)
    {
        var head = text.Length > 600 ? text.Substring(0, 600) : text;
        var match = Regex.Match(head, @"\b((?:[A-Z][A-Za-z&\-]*\s+){0,5}(?:AGREEMENT|Agreement|CONTRACT|Contract|NDA|LEASE|Lease|ADDENDUM|Addendum))\b", RegexOptions.CultureInvariant, RegexTimeout);
        if (match.Success)
        {
            var title = match.Groups[1].Value.Trim();
            if (title.Length >= 3 && title.Length <= 90)
            {
                return ToSentenceCase(title);
            }
        }
        var name = Path.GetFileNameWithoutExtension(fileName ?? "").Replace('_', ' ').Replace('-', ' ').Trim();
        return name.Length == 0 ? null : name;
    }

    private static string ToSentenceCase(string value)
    {
        if (value.Any(char.IsLower))
        {
            return value;
        }
        var lower = value.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max).TrimEnd() + "...";

    private static string BuildSummary(
        string? title, string? counterparty, DateOnly? startDate, DateOnly? endDate, int? noticeDays,
        bool autoRenew, int? renewalMonths, string? law, bool changeOfControl)
    {
        var culture = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        var subject = string.IsNullOrWhiteSpace(title) ? "The document" : title!;
        parts.Add(counterparty is null ? subject + "." : subject + " with " + counterparty + ".");

        if (startDate is not null && endDate is not null)
        {
            parts.Add("It runs from " + startDate.Value.ToString("d MMM yyyy", culture) + " to " + endDate.Value.ToString("d MMM yyyy", culture) + ".");
        }
        else if (startDate is not null)
        {
            parts.Add("It starts on " + startDate.Value.ToString("d MMM yyyy", culture) + "; no end date was found.");
        }

        if (autoRenew)
        {
            parts.Add(renewalMonths is null
                ? "It renews automatically."
                : "It renews automatically for " + renewalMonths.Value + " months at a time.");
        }
        if (noticeDays is not null)
        {
            parts.Add("The notice period is " + noticeDays.Value + " days.");
        }
        if (law is not null)
        {
            parts.Add("Governing law: " + law + ".");
        }
        if (changeOfControl)
        {
            parts.Add("It contains a change of control clause.");
        }
        return string.Join(" ", parts);
    }
}
