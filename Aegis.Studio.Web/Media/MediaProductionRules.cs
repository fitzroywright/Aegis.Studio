using System.Xml.Linq;
using System.Text;

namespace Aegis.Studio.Web.Media;

public static class MediaProductionRules
{
    public static readonly string[] States = ["Photographed", "Needs Sorting", "Needs Editing", "Needs Review", "Needs Approval", "Needs Retake", "Ready", "Ready for Production", "Needs Print", "Needs QC", "Needs Delivery", "Completed", "Blocked"];
    public static void ValidateState(string value)
    {
        if (!States.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Unknown production state.");
    }
    // Parse quoted CSV, including embedded commas/newlines and escaped quotes. No formula evaluation.
    public static IReadOnlyList<string[]> ReadRoster(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv) || csv.Length > 2_000_000) throw new ArgumentException("Roster is too large.");
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (c == '"') { if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && (c == ',' || c == '\r' || c == '\n'))
            {
                row.Add(cell.ToString().Trim().TrimStart('\uFEFF')); cell.Clear();
                if (c != ',') { if (row.Any(x => x.Length > 0)) rows.Add(row.ToArray()); row.Clear(); if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++; }
            }
            else cell.Append(c);
        }
        if (quoted) throw new ArgumentException("Roster has an unclosed quoted field.");
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().Trim()); rows.Add(row.ToArray()); }
        if (rows.Count < 2 || rows.Count > 5001) throw new ArgumentException("Supply a header and 1–5000 subject rows.");
        if(rows[0].Any(string.IsNullOrWhiteSpace)||rows[0].Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rows[0].Length||rows.Skip(1).Any(x=>x.Length>rows[0].Length))throw new ArgumentException("Roster headers must be unique and each row must fit the header columns.");
        return rows;
    }

    // JPEG APP1 XMP carries an opaque export token without recompressing pixels.
    // Other formats use the retained manifest and explicit matching in the return dialog.
    public static byte[] StampJpeg(byte[] bytes, Guid token)
    {
        if (bytes.Length < 2 || bytes[0] != 0xff || bytes[1] != 0xd8) return bytes;
        const string prefix = "http://ns.adobe.com/xap/1.0/\0";
        // Update an existing XMP packet rather than adding a second packet that editors may discard.
        int offset = 2;
        while (offset + 4 <= bytes.Length && bytes[offset] == 0xff)
        {
            int marker = bytes[offset + 1]; if (marker is 0xda or 0xd9) break;
            int size = (bytes[offset + 2] << 8) | bytes[offset + 3];
            if (size < 2 || offset + 2 + size > bytes.Length) return bytes;
            string payload = marker == 0xe1 ? Encoding.UTF8.GetString(bytes, offset + 4, size - 2) : "";
            if (payload.StartsWith(prefix, StringComparison.Ordinal))
            {
                try
                {
                    var document = System.Xml.Linq.XDocument.Parse(payload[prefix.Length..]);
                    System.Xml.Linq.XNamespace studio = "urn:aegis:studio:editing";
                    System.Xml.Linq.XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
                    foreach(var element in document.Descendants(studio + "ExportToken").ToArray()) element.Remove();
                    foreach(var attribute in document.Descendants().Attributes(studio + "ExportToken").ToArray()) attribute.Remove();
                    var description = document.Descendants(rdf + "Description").FirstOrDefault();
                    if(description is null) return bytes;
                    description.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "studio", studio.NamespaceName);
                    description.SetAttributeValue(studio + "ExportToken", token.ToString("D"));
                    return ReplacePacket(bytes, offset, size + 2, Encoding.UTF8.GetBytes(prefix + document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)));
                }
                catch(System.Xml.XmlException) { return bytes; } // Preserve malformed metadata; manual matching remains available.
            }
            offset += size + 2;
        }
        byte[] packet = Encoding.UTF8.GetBytes(prefix + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:studio=\"urn:aegis:studio:editing\" studio:ExportToken=\"" + token.ToString("D") + "\"/></rdf:RDF></x:xmpmeta>");
        return ReplacePacket(bytes, 2, 0, packet);
    }
    private static byte[] ReplacePacket(byte[] bytes, int offset, int removed, byte[] packet)
    {
        int length = packet.Length + 2; if(length > ushort.MaxValue) return bytes;
        using var output = new MemoryStream(); output.Write(bytes, 0, offset); output.Write([0xff, 0xe1, (byte)(length >> 8), (byte)length]); output.Write(packet); output.Write(bytes, offset + removed, bytes.Length - offset - removed); return output.ToArray();
    }
    public static Guid? ReadExportToken(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return null;
        var tokens = new HashSet<Guid>(); int offset = 2;
        while (offset + 4 <= bytes.Length && bytes[offset] == 0xff)
        {
            int marker = bytes[offset + 1]; if (marker == 0xda || marker == 0xd9) break;
            int length = (bytes[offset + 2] << 8) | bytes[offset + 3]; if (length < 2 || offset + 2 + length > bytes.Length) break;
            if (marker == 0xe1)
            {
                string text = Encoding.UTF8.GetString(bytes, offset + 4, length - 2);
                const string prefix="http://ns.adobe.com/xap/1.0/\0";
                if(text.StartsWith(prefix,StringComparison.Ordinal))try{
                    var document=XDocument.Parse(text[prefix.Length..]);XNamespace studio="urn:aegis:studio:editing";
                    foreach(string value in document.Descendants().Attributes(studio+"ExportToken").Select(x=>x.Value).Concat(document.Descendants(studio+"ExportToken").Select(x=>x.Value)))
                        if(Guid.TryParse(value,out Guid token))tokens.Add(token);
                }catch(System.Xml.XmlException){return null;}
            }
            offset += 2 + length;
        }
        return tokens.Count == 1 ? tokens.Single() : null;
    }
}
