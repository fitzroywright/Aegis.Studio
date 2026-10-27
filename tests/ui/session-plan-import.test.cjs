const {test}=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),path=require('node:path'),{webcrypto}=require('node:crypto');
const window={};vm.runInNewContext(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.PublicSite/public/roster-editor.js'),'utf8'),{window,crypto:webcrypto,Date});const engine=window.StudioRosterEditor;
const existing=[{subjectId:'s1',name:'Alicia',reference:'A1',collection:'Class 1',contactEmail:'parent@example.com',notes:'Keep this',pickupNotification:'Email'}];
const plain=x=>JSON.parse(JSON.stringify(x));
test('repeated imports without IDs reuse an unambiguous person and preserve unmapped data',()=>{
 const result=engine.planImport([['Name','Group'],['Alicia','Class 1']],{name:0,collection:1},existing);
 assert.equal(result.errors.length,0);assert.equal(result.rows.length,1);assert.equal(result.rows[0].subjectId,'s1');assert.equal(result.rows[0].contactEmail,'parent@example.com');assert.equal(result.rows[0].pickupNotification,'Email');
});
test('changed external references require a decision and never silently replace the stable reference',()=>{
 const cells=[['Name','ID','Group'],['Alicia','NEWID','Class 1']],mapping={name:0,reference:1,collection:2};
 assert.ok(engine.planImport(cells,mapping,existing).errors.length);
 const accepted=engine.planImport(cells,mapping,existing,{0:'A1'});assert.equal(accepted.errors.length,0);assert.equal(accepted.rows.length,1);assert.equal(accepted.rows[0].reference,'A1');assert.equal(accepted.rows[0].subjectId,'s1');
 const separate=engine.planImport(cells,mapping,existing,{0:'new'});assert.equal(separate.rows.length,2);assert.equal(separate.rows[1].reference,'NEWID');
});
test('duplicate input references and repeated unnumbered names cannot silently collapse',()=>{
 const duplicates=engine.planImport([['Name','ID'],['One','X'],['Two','X']],{name:0,reference:1},[]);assert.match(duplicates.errors.join(' '),/more than once|Duplicate/);
 const names=engine.planImport([['Name'],['Same'],['Same']],{name:0},[]);assert.match(names.errors.join(' '),/duplicate name/);
});
test('group changes for a person without an ID need explicit identity review',()=>{
 const result=engine.planImport([['Name','Group'],['Alicia','Class 2']],{name:0,collection:1},existing);assert.equal(result.changes[0].unresolved,true);
 const accepted=engine.planImport([['Name','Group'],['Alicia','Class 2']],{name:0,collection:1},existing,{0:'A1'});assert.equal(accepted.rows.length,1);assert.equal(accepted.rows[0].collection,'Class 2');
});
test('spreadsheet dates accept selected day/month order, Excel numbers and AM/PM times',()=>{
 assert.equal(engine.plannedTime('03/11/2026','2:30 PM','DMY'),new Date(2026,10,3,14,30).toISOString());
 assert.equal(engine.plannedTime('03/11/2026','14:30','MDY'),new Date(2026,2,11,14,30).toISOString());
 const serial=(Date.UTC(2026,10,3)-Date.UTC(1899,11,30))/86400000;
 assert.equal(engine.plannedTime(String(serial),'0.5'),new Date(2026,10,3,12).toISOString());
 assert.equal(engine.plannedTime('2026-11-03T09:00:00-05:00'),'2026-11-03T14:00:00.000Z');
});
test('invalid dates, missing times and invalid contact channels block the preview',()=>{
 for(const [date,time] of [['31/02/2026','09:00'],['2026-11-03','25:00'],['2026-11-03',''],['2026-02-30T09:00:00Z','']])assert.throws(()=>engine.plannedTime(date,time));
 const result=engine.planImport([['Name','Date','Time','Email'],['One','31/02/2026','09:00','bad']],{name:0,scheduleDate:1,scheduleTime:2,contactEmail:3},[]);assert.match(result.errors.join(' '),/Invalid date/);assert.match(result.errors.join(' '),/Invalid email/);
});
test('missing name mapping is an actionable error and the existing array is not mutated',()=>{
 const copy=plain(existing);const result=engine.planImport([['Email'],['new@example.com']],{contactEmail:0},existing);assert.match(result.errors.join(' '),/Map the subject name/);assert.deepEqual(existing,copy);
});
test('a full 5000-person re-import preserves every identity and contact without inserting duplicates',()=>{
 const people=Array.from({length:5000},(_,i)=>({name:'Student '+i,reference:'S'+i,subjectId:String(i),collection:'Class '+i%10,contactEmail:'parent'+i+'@example.com'}));
 const cells=[['Name','Reference'],...people.map(r=>[r.name,r.reference])],result=engine.planImport(cells,{name:0,reference:1},people);
 assert.equal(result.rows.length,5000);assert.equal(result.errors.length,0);assert.equal(result.changes.filter(c=>c.kind==='New').length,0);assert.equal(result.rows[4999].contactEmail,'parent4999@example.com');
});
