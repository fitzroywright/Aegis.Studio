import { IntakeError } from './intake-store.mjs';
export async function readRosterFile(bytes,fileName){
 if(bytes.length>5_000_000)throw new IntakeError(413,'roster_limit','Maximum roster file size is 5 MB.');
 if(fileName.toLowerCase().endsWith('.csv'))return bytes.toString('utf8');
 if(!fileName.toLowerCase().endsWith('.xlsx'))throw new IntakeError(400,'roster_type','Choose CSV or XLSX.');
 // Bound the ZIP directory before the workbook parser allocates decompressed content.
 let entries=0,total=0;
 for(let i=0;i+46<=bytes.length;i++)if(bytes.readUInt32LE(i)===0x02014b50){const size=bytes.readUInt32LE(i+24);total+=size;entries++;if(entries>2000||total>30_000_000||size>10_000_000)throw new IntakeError(413,'roster_limit','Workbook exceeds expansion limits.');i+=45+bytes.readUInt16LE(i+28)+bytes.readUInt16LE(i+30)+bytes.readUInt16LE(i+32);}
 if(!entries)throw new IntakeError(400,'invalid_workbook','Invalid XLSX directory.');
 const {default:ExcelJS}=await import('exceljs');const book=new ExcelJS.Workbook();await book.xlsx.load(bytes);
 const sheet=book.worksheets[0];if(!sheet||sheet.rowCount>5001||sheet.columnCount>100)throw new IntakeError(400,'roster_limit','Maximum 5000 rows and 100 columns.');
 const rows=[];sheet.eachRow(row=>{const fields=[];for(let i=1;i<=sheet.columnCount;i++){const c=row.getCell(i);if(c.formula||c.sharedFormula)throw new IntakeError(400,'formula_rejected','Paste formula values before importing.');fields.push('"'+String(c.text||'').replaceAll('"','""')+'"');}rows.push(fields.join(','));});return rows.join('\n');
}
