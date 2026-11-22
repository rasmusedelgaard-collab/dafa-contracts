using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>
/// Fills Word templates: {{placeholder}} text and content controls whose tag or title matches a
/// field name. A paragraph that contains a placeholder is rewritten into its first run, so mixed
/// formatting inside that paragraph is lost; the rest of the document is untouched.
/// </summary>
public sealed class OpenXmlTemplateEngine : ITemplateEngine
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private const string Blank = "________";

    public IReadOnlyList<string> GetPlaceholders(byte[] template)
    {
        var names = new List<string>();
        using var stream = new MemoryStream(template, false);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return names;
        }

        foreach (var paragraph in body.Descendants<W.Paragraph>())
        {
            foreach (Match match in Placeholder.Matches(paragraph.InnerText))
            {
                AddName(names, match.Groups[1].Value);
            }
        }
        foreach (var control in body.Descendants<W.SdtElement>())
        {
            var name = ControlName(control);
            if (name is not null)
            {
                AddName(names, name);
            }
        }
        return names;
    }

    private static void AddName(List<string> names, string name)
    {
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(name);
        }
    }

    private static string? ControlName(W.SdtElement control)
    {
        var properties = control.GetFirstChild<W.SdtProperties>();
        if (properties is null)
        {
            return null;
        }
        var tag = properties.GetFirstChild<W.Tag>()?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(tag))
        {
            return tag;
        }
        var alias = properties.GetFirstChild<W.SdtAlias>()?.Val?.Value;
        return string.IsNullOrWhiteSpace(alias) ? null : alias;
    }

    public byte[] CreateStarter(string title)
    {
        return DocxBuilder.Create(new[]
        {
            "# {{title}}",
            title,
            "This agreement is entered into between {{entityName}} and {{counterpartyName}}.",
            "1. Term. The agreement takes effect on {{startDate}} and remains in force until {{endDate}}.",
            "2. Termination. Either party may terminate the agreement with {{noticePeriodDays}} days' written notice.",
            "3. Governing law. The agreement is governed by {{governingLaw}}.",
            "Replace this starter text with the real template in Word; keep the {{placeholders}} you need."
        });
    }

    public byte[] Fill(byte[] template, IReadOnlyDictionary<string, string> fields)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in fields)
        {
            values[pair.Key] = pair.Value ?? "";
        }

        using var stream = new MemoryStream();
        stream.Write(template, 0, template.Length);
        stream.Position = 0;
        using (var document = WordprocessingDocument.Open(stream, true))
        {
            var mainPart = document.MainDocumentPart;
            var body = mainPart?.Document?.Body;
            if (mainPart is not null && body is not null)
            {
                foreach (var control in body.Descendants<W.SdtElement>().ToList())
                {
                    var name = ControlName(control);
                    if (name is null || !values.TryGetValue(name, out var value))
                    {
                        continue;
                    }
                    SetTexts(control.Descendants<W.Text>().ToList(), value);
                }

                foreach (var paragraph in body.Descendants<W.Paragraph>().ToList())
                {
                    var texts = paragraph.Descendants<W.Text>().ToList();
                    if (texts.Count == 0)
                    {
                        continue;
                    }
                    var original = string.Concat(texts.Select(t => t.Text));
                    if (!original.Contains("{{", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var replaced = Placeholder.Replace(original, match =>
                    {
                        var key = match.Groups[1].Value;
                        return values.TryGetValue(key, out var value) && value.Length > 0 ? value : Blank;
                    });
                    SetTexts(texts, replaced);
                }

                mainPart.Document.Save();
            }
        }
        return stream.ToArray();
    }

    private static void SetTexts(List<W.Text> texts, string value)
    {
        if (texts.Count == 0)
        {
            return;
        }
        texts[0].Text = value;
        texts[0].Space = SpaceProcessingModeValues.Preserve;
        for (var i = 1; i < texts.Count; i++)
        {
            texts[i].Text = "";
        }
    }
}

/// <summary>Builds simple .docx files in code (seeded templates).</summary>
public static class DocxBuilder
{
    /// <summary>Each line becomes a paragraph. A line starting with "# " is a heading (bold, larger).</summary>
    public static byte[] Create(IEnumerable<string> lines)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            var body = new W.Body();
            foreach (var line in lines)
            {
                var isHeading = line.StartsWith("# ", StringComparison.Ordinal);
                var content = isHeading ? line.Substring(2) : line;
                var run = new W.Run();
                if (isHeading)
                {
                    var runProperties = new W.RunProperties();
                    runProperties.Append(new W.Bold());
                    runProperties.Append(new W.FontSize { Val = "32" });
                    run.Append(runProperties);
                }
                var text = new W.Text(content);
                text.Space = SpaceProcessingModeValues.Preserve;
                run.Append(text);
                var paragraph = new W.Paragraph();
                paragraph.Append(run);
                body.Append(paragraph);
            }
            var documentRoot = new W.Document();
            documentRoot.Append(body);
            mainPart.Document = documentRoot;
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }
}
