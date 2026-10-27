using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Aegis.Studio.Web.Media;

// Read cell values only: no formulas, macros, external links or spreadsheet execution.
public static class RosterFileReader
{
    public static string Read(Stream input, string fileName)
    {
        string extension=Path.GetExtension(fileName).ToLowerInvariant();
        if(extension==".csv")
        {
            using var reader=new StreamReader(input,Encoding.UTF8,true,1024,leaveOpen:true);
            var text=new StringBuilder();char[] buffer=new char[4096];int length;
            while((length=reader.Read(buffer,0,buffer.Length))>0)
            {
                if(text.Length+length>2_000_000)throw new ArgumentException("Roster is too large (maximum 2 MB of text).");
                text.Append(buffer,0,length);
            }
            return text.ToString();
        }
        if(extension!=".xlsx")throw new ArgumentException("Choose a CSV or XLSX roster.");
        try { return ReadWorkbook(input); }
        catch(InvalidDataException){throw new ArgumentException("This XLSX file is damaged or is not a valid workbook.");}
        catch(XmlException){throw new ArgumentException("This XLSX file contains invalid spreadsheet data.");}
    }

    private static string ReadWorkbook(Stream input)
    {
        using var zip=new ZipArchive(input,ZipArchiveMode.Read,true);
        if(zip.Entries.Count>2000 || zip.Entries.Sum(e=>e.Length)>30_000_000)
            throw new ArgumentException("Workbook is too large. Export the roster sheet as CSV.");
        XNamespace spreadsheet="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relation="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var workbook=Xml(zip,"xl/workbook.xml");
        if(workbook.Root?.Element(spreadsheet+"workbookPr")?.Attribute("date1904")?.Value is "1" or "true")
            throw new ArgumentException("This workbook uses Excel's 1904 date system. Export the subject sheet as CSV with YYYY-MM-DD dates before importing the Session Plan.");
        string? sheetId=workbook.Descendants(spreadsheet+"sheet").FirstOrDefault()?.Attribute(relation+"id")?.Value;
        var relationships=Xml(zip,"xl/_rels/workbook.xml.rels");
        var sheetRelation=relationships.Root?.Elements().FirstOrDefault(e=>e.Attribute("Id")?.Value==sheetId);
        string? target=sheetRelation?.Attribute("Target")?.Value;
        if(string.IsNullOrWhiteSpace(target)||sheetRelation?.Attribute("TargetMode")?.Value=="External")
            throw new ArgumentException("Workbook has no readable first sheet.");
        string sheetPath=target.StartsWith('/')?target.TrimStart('/'):"xl/"+target;
        if(sheetPath.Contains("..",StringComparison.Ordinal))throw new ArgumentException("Workbook has an invalid sheet location.");
        string[] strings=zip.GetEntry("xl/sharedStrings.xml") is null?[]:Xml(zip,"xl/sharedStrings.xml")
            .Descendants(spreadsheet+"si").Select(e=>string.Concat(e.Descendants(spreadsheet+"t").Select(t=>t.Value))).ToArray();
        var output=new StringBuilder();int rowCount=0;
        foreach(var row in Xml(zip,sheetPath).Descendants(spreadsheet+"row"))
        {
            var values=new SortedDictionary<int,string>();
            foreach(var cell in row.Elements(spreadsheet+"c"))
            {
                if(cell.Element(spreadsheet+"f") is not null)throw new ArgumentException("Roster cells contain formulas. Paste their values before importing.");
                int index=Column(cell.Attribute("r")?.Value);
                if(index<0||index>=100)throw new ArgumentException("Roster supports at most 100 columns.");
                string type=cell.Attribute("t")?.Value??"";
                string value=type=="inlineStr"?string.Concat(cell.Descendants(spreadsheet+"t").Select(t=>t.Value)):cell.Element(spreadsheet+"v")?.Value??"";
                if(type=="s")
                {
                    if(!int.TryParse(value,out int shared)||shared<0||shared>=strings.Length)throw new ArgumentException("Workbook contains an invalid text cell.");
                    value=strings[shared];
                }
                if(type=="e")throw new ArgumentException("Roster contains a spreadsheet error. Correct the cell before importing.");
                values[index]=value;
            }
            if(!values.Values.Any(v=>!string.IsNullOrWhiteSpace(v)))continue;
            if(++rowCount>5001)throw new ArgumentException("Supply at most 5000 subject rows.");
            int last=values.Keys.Max();
            output.AppendLine(string.Join(",",Enumerable.Range(0,last+1).Select(i=>Quote(values.GetValueOrDefault(i,"")))));
            if(output.Length>2_000_000)throw new ArgumentException("Roster text is too large.");
        }
        return output.ToString();
    }

    private static XDocument Xml(ZipArchive zip,string path)
    {
        var entry=zip.GetEntry(path)??throw new ArgumentException("Workbook is missing its roster sheet.");
        if(entry.Length>10_000_000)throw new ArgumentException("Roster sheet is too large.");
        using var stream=entry.Open();
        using var reader=XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=10_000_000 });
        return XDocument.Load(reader);
    }
    private static int Column(string? reference)
    {
        if(string.IsNullOrEmpty(reference))throw new ArgumentException("Workbook cell is missing its column reference.");
        int value=0;
        foreach(char c in reference.TakeWhile(char.IsAsciiLetter))
        {
            value=value*26+(char.ToUpperInvariant(c)-'A'+1);
            if(value>100)throw new ArgumentException("Roster supports at most 100 columns.");
        }
        return value-1;
    }
    private static string Quote(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
}

