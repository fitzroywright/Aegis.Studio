using System.IO.Compression;
using System.Text;
using Aegis.Studio.Web.Media;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class RosterFileReaderTests
{
    [Fact]
    public void ReadsSparseXlsxCellsAndIgnoresBlankRows()
    {
        using var workbook=Workbook("<row><c r=\"A1\" t=\"inlineStr\"><is><t>Name</t></is></c><c r=\"C1\" t=\"inlineStr\"><is><t>Subject ID</t></is></c></row><row><c r=\"A2\" t=\"inlineStr\"><is><t>Anna, Marie</t></is></c><c r=\"C2\" t=\"inlineStr\"><is><t>001</t></is></c></row><row><c r=\"A3\"/></row>");
        string csv=RosterFileReader.Read(workbook,"roster.xlsx");
        Assert.Contains("\"Anna, Marie\",\"\",\"001\"",csv);
        Assert.Equal(2,csv.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length);
    }
    [Fact]
    public void DoesNotEvaluateOrAcceptFormulas()
    {
        using var workbook=Workbook("<row><c r=\"A1\"><f>HYPERLINK(\"https://example.test\")</f><v>1</v></c></row>");
        Assert.Throws<ArgumentException>(()=>RosterFileReader.Read(workbook,"roster.xlsx"));
    }
    [Fact]
    public void RejectsLegacyBinarySpreadsheets()
    {
        using var input=new MemoryStream([1,2,3]);
        Assert.Throws<ArgumentException>(()=>RosterFileReader.Read(input,"roster.xls"));
    }
    [Fact]
    public void CsvKeepsLeadingZeroIdentifiers()
    {
        using var input=new MemoryStream(Encoding.UTF8.GetBytes("Name,Subject ID\nAnna,001\n"));
        Assert.Equal("001",MediaProductionRules.ReadRoster(RosterFileReader.Read(input,"roster.csv"))[1][1]);
    }
    private static MemoryStream Workbook(string rows)
    {
        var result=new MemoryStream();
        using(var archive=new ZipArchive(result,ZipArchiveMode.Create,true))
        {
            Add(archive,"xl/workbook.xml","<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Roster\" sheetId=\"1\" r:id=\"r1\"/></sheets></workbook>");
            Add(archive,"xl/_rels/workbook.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Add(archive,"xl/worksheets/sheet1.xml","<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"+rows+"</sheetData></worksheet>");
        }
        result.Position=0;return result;
    }
    private static void Add(ZipArchive zip,string path,string text){using var writer=new StreamWriter(zip.CreateEntry(path).Open());writer.Write(text);}
}
