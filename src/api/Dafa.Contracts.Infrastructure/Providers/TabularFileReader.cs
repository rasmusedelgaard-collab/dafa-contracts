using System.Globalization;
using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>
/// Reads import files: .csv (UTF-8, BOM allowed, separator auto-detected among ',', ';' and tab,
/// RFC 4180 quotes) and .xlsx (first worksheet, shared strings, date cells as yyyy-MM-dd). The first
/// row is the header. Throws InputValidationException ("file") for an unreadable file.
/// </summary>
public sealed class TabularFileReader : ITabularFileReader
{
    public const int MaxColumns = 200;

    public TabularData Read(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        try
        {
            if (extension == ".csv" || extension == ".txt")
            {
                return ReadCsv(content);
            }
            if (extension == ".xlsx")
            {
                return ReadXlsx(content);
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InputValidationException("file", "The file could not be read. Use a .csv (UTF-8) or .xlsx file.");
        }
        throw new InputValidationException("file", "Use a .csv or .xlsx file.");
    }

    // ---------- CSV ----------

    public static TabularData ReadCsv(byte[] content)
    {
        var text = new UTF8Encoding(false, false).GetString(content);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text.Substring(1);
        }
        var separator = DetectSeparator(text);
        var rows = ParseCsv(text, separator);
        return ToTable(rows);
    }

    private static char DetectSeparator(string text)
    {
        // Count candidates in the header line, outside quotes.
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (ch == '\n' || ch == '\r')) break;
            else if (!inQuotes && counts.ContainsKey(ch)) counts[ch]++;
        }
        var best = counts.OrderByDescending(p => p.Value).First();
        return best.Value == 0 ? ',' : best.Key;
    }

    private static List<List<string>> ParseCsv(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }
                continue;
            }
            if (ch == '"' && cell.Length == 0)
            {
                inQuotes = true;
            }
            else if (ch == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else
            {
                cell.Append(ch);
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static TabularData ToTable(List<List<string>> rows)
    {
        // Leading empty lines are skipped; the first non-empty row is the header.
        var index = 0;
        while (index < rows.Count && rows[index].All(string.IsNullOrWhiteSpace))
        {
            index++;
        }
        if (index >= rows.Count)
        {
            throw new InputValidationException("file", "The file is empty.");
        }
        var header = rows[index].Select(h => (h ?? "").Trim()).ToList();
        while (header.Count > 0 && header[header.Count - 1].Length == 0)
        {
            header.RemoveAt(header.Count - 1);
        }
        if (header.Count == 0 || header.Count > MaxColumns)
        {
            throw new InputValidationException("file", "The first row must hold the column headers (at most " + MaxColumns + ").");
        }
        if (header.Any(h => h.Length == 0))
        {
            throw new InputValidationException("file", "Every column needs a header; one is empty.");
        }
        var duplicate = header.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InputValidationException("file", "The header '" + duplicate.Key + "' appears twice.");
        }
        var data = new TabularData { Columns = header };
        for (var i = index + 1; i < rows.Count; i++)
        {
            var cells = new string[header.Count];
            for (var c = 0; c < header.Count; c++)
            {
                cells[c] = c < rows[i].Count ? (rows[i][c] ?? "").Trim() : "";
            }
            data.Rows.Add(cells);
        }
        return data;
    }

    // ---------- XLSX ----------

    private static TabularData ReadXlsx(byte[] content)
    {
        using var stream = new MemoryStream(content, false);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ?? throw new InputValidationException("file", "The workbook has no sheets.");
        var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InputValidationException("file", "The workbook has no sheets.");
        var sheetId = sheet.Id?.Value ?? throw new InputValidationException("file", "The workbook has no sheets.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetId);
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList()
            ?? new List<string>();
        var dateStyles = DateStyleIndexes(workbookPart);

        var rows = new List<List<string>>();
        var sheetData = worksheetPart.Worksheet?.GetFirstChild<SheetData>();
        if (sheetData is null)
        {
            throw new InputValidationException("file", "The first sheet is empty.");
        }
        foreach (var row in sheetData.Elements<Row>())
        {
            var values = new List<string>();
            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnIndex(cell.CellReference?.Value);
                if (column < 0)
                {
                    column = values.Count;
                }
                if (column >= MaxColumns)
                {
                    continue;
                }
                while (values.Count < column)
                {
                    values.Add("");
                }
                var text = CellText(cell, sharedStrings, dateStyles);
                if (values.Count == column)
                {
                    values.Add(text);
                }
                else
                {
                    values[column] = text;
                }
            }
            rows.Add(values);
        }
        return ToTable(rows);
    }

    /// <summary>Zero-based column of a reference like "C12"; -1 when missing.</summary>
    public static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return -1;
        }
        var index = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (ch >= 'A' && ch <= 'Z')
            {
                index = index * 26 + (ch - 'A' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }
        return letters == 0 ? -1 : index - 1;
    }

    /// <summary>Style indexes whose number format is a date (built-in 14-22, 45-47, or a custom format with d/m/y).</summary>
    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbookPart)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var formats = stylesheet?.CellFormats?.Elements<CellFormat>().ToList();
        if (formats is null)
        {
            return result;
        }
        var custom = new Dictionary<uint, string>();
        foreach (var format in stylesheet!.NumberingFormats?.Elements<NumberingFormat>() ?? Enumerable.Empty<NumberingFormat>())
        {
            if (format.NumberFormatId?.Value is uint id)
            {
                custom[id] = format.FormatCode?.Value ?? "";
            }
        }
        for (var i = 0; i < formats.Count; i++)
        {
            var id = formats[i].NumberFormatId?.Value ?? 0;
            var isDate = (id >= 14 && id <= 22) || (id >= 45 && id <= 47);
            if (!isDate && custom.TryGetValue(id, out var code))
            {
                var lower = code.ToLowerInvariant();
                isDate = (lower.Contains('d') || lower.Contains('y')) && !lower.Contains('h') && !lower.Contains("0.0");
            }
            if (isDate)
            {
                result.Add((uint)i);
            }
        }
        return result;
    }

    private static string CellText(Cell cell, List<string> sharedStrings, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text ?? cell.InnerText ?? "";
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index].Trim()
                : "";
        }
        if (type == CellValues.InlineString)
        {
            return (cell.InlineString?.InnerText ?? raw).Trim();
        }
        if (type == CellValues.Boolean)
        {
            return raw == "1" ? "true" : "false";
        }
        var styleIndex = cell.StyleIndex?.Value;
        if (styleIndex is not null && dateStyles.Contains(styleIndex.Value)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 0 && serial < 2958466)
        {
            return DateTime.FromOADate(serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return raw.Trim();
    }
}
