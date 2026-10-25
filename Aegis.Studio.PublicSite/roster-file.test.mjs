import { test } from 'node:test';
import assert from 'node:assert/strict';
import ExcelJS from 'exceljs';
import { readRosterFile } from './roster-file.mjs';

test('real XLSX roster preserves text and reads only the first worksheet through the worker', async () => {
  const book = new ExcelJS.Workbook();
  const sheet = book.addWorksheet('Roster');
  sheet.addRows([
    ['name', 'reference', 'contactEmail', 'pickupNotification'],
    ['Renée "Ray", Brown', '000123', 'renee@example.invalid', 'Email'],
    ['李明\nSecond line', '000124', '', 'None'],
  ]);
  book.addWorksheet('Notes').addRow(['Do not import this sheet']);

  const bytes = Buffer.from(await book.xlsx.writeBuffer());
  assert.equal(await readRosterFile(bytes, 'roster.XLSX'),
    '"name","reference","contactEmail","pickupNotification"\n' +
    '"Renée ""Ray"", Brown","000123","renee@example.invalid","Email"\n' +
    '"李明\nSecond line","000124","","None"');
});

test('XLSX extended conditional formatting generates UUID v4 IDs and remains importable', async () => {
  const book = new ExcelJS.Workbook();
  const sheet = book.addWorksheet('Roster');
  sheet.addRows([['name', 'reference'], ['Subject', '001']]);
  const rule = {
    type: 'iconSet', iconSet: '3Stars',
    cfvo: [{ type: 'percent', value: 0 }, { type: 'percent', value: 33 }, { type: 'percent', value: 67 }],
  };
  sheet.addConditionalFormatting({ ref: 'B2', rules: [rule] });

  // ExcelJS calls require('uuid').v4() while serializing this extension.
  const bytes = Buffer.from(await book.xlsx.writeBuffer());
  assert.match(rule.x14Id, /^\{[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\}$/);
  const reloaded = new ExcelJS.Workbook();
  await reloaded.xlsx.load(bytes);
  assert.equal(reloaded.worksheets[0].conditionalFormattings[0].rules[0].x14Id, rule.x14Id);
  assert.equal(await readRosterFile(bytes, 'formatted.xlsx'), '"name","reference"\n"Subject","001"');
});

test('real XLSX formulas are rejected by the worker even with a cached value', async () => {
  const book = new ExcelJS.Workbook();
  const sheet = book.addWorksheet('Roster');
  sheet.addRow(['name', 'reference']);
  sheet.addRow(['Subject', { formula: '1+1', result: 2 }]);

  const bytes = Buffer.from(await book.xlsx.writeBuffer());
  await assert.rejects(readRosterFile(bytes, 'formula.xlsx'),
    error => error.status === 400 && error.code === 'formula_rejected');
});
