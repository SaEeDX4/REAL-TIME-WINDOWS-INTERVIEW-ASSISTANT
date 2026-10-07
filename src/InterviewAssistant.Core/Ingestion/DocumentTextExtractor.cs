using System.IO.Compression;
using System.Text;
using System.Xml;

namespace InterviewAssistant.Core.Ingestion;

public sealed record ExtractedText(string Text, bool NeedsOcr, int Pages);

/// <summary>Extracts plain text from PDF (PdfPig, Apache-2.0), DOCX (OpenXML parsed directly, no dependency) and UTF-8 text.</summary>
public static class DocumentTextExtractor
{
    public static ExtractedText Extract(UploadType type, byte[] content) => type switch
    {
        UploadType.Pdf => FromPdf(content),
        UploadType.Docx => new ExtractedText(FromDocx(content), false, 0),
        _ => new ExtractedText(Normalize(new UTF8Encoding(false).GetString(content).TrimStart('﻿')), false, 0),
    };

    public static ExtractedText FromPdf(byte[] content)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(content);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            // Group words into lines by baseline to keep résumé structure (one logical line per visual line).
            var words = page.GetWords().OrderByDescending(w => Math.Round(w.BoundingBox.Bottom, 0)).ThenBy(w => w.BoundingBox.Left).ToList();
            double? lastY = null;
            foreach (var w in words)
            {
                var y = Math.Round(w.BoundingBox.Bottom, 0);
                if (lastY != null && Math.Abs(y - lastY.Value) > 2) sb.AppendLine();
                else if (lastY != null) sb.Append(' ');
                sb.Append(w.Text);
                lastY = y;
            }
            sb.AppendLine().AppendLine();
        }
        var text = Normalize(sb.ToString());
        // Image-only PDFs have pages but no extractable text: flag for OCR rather than silently returning nothing.
        return new ExtractedText(text, doc.NumberOfPages > 0 && text.Trim().Length < 20, doc.NumberOfPages);
    }

    public static string FromDocx(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("No document.xml");
        using var stream = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
        using var reader = XmlReader.Create(stream, settings);
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        var para = new StringBuilder();
        bool inText = false, numbered = false;
        while (reader.Read())
        {
            if ((reader.NodeType == XmlNodeType.Element || reader.NodeType == XmlNodeType.EndElement) && reader.NamespaceURI != W) continue;
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "p": para.Clear(); numbered = false; if (reader.IsEmptyElement) sb.AppendLine(); break;
                    case "numPr": numbered = true; break;
                    case "t": inText = !reader.IsEmptyElement; break;
                    case "tab": para.Append('\t'); break;
                    case "br": case "cr": para.Append('\n'); break;
                }
            }
            else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.SignificantWhitespace)
            {
                if (inText) para.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "t") inText = false;
                else if (reader.LocalName == "p") { sb.Append(numbered ? "• " : "").AppendLine(para.ToString()); }
            }
        }
        return Normalize(sb.ToString());
    }

    public static string Normalize(string text)
    {
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
        var lines = t.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"[ \t]+", " ").TrimEnd());
        return System.Text.RegularExpressions.Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
    }
}
