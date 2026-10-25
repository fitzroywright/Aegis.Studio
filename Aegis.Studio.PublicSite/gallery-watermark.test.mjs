import { test } from 'node:test';
import assert from 'node:assert/strict';
import sharp from 'sharp';
import { createPreview, watermarkConfig } from './gallery-watermark.mjs';

test('watermark preview is pixel-embedded while original bytes remain untouched',async()=>{
  const original=await sharp({create:{width:1200,height:800,channels:3,background:{r:60,g:100,b:140}}}).png().toBuffer();
  const before=Buffer.from(original);
  const preview=await createPreview(original,{GALLERY_WATERMARK_TEXT:"BRYAN'S",GALLERY_WATERMARK_VERSION:'v1'});
  assert.equal(preview.type,'image/jpeg');
  assert.deepEqual(original,before);
  assert.notDeepEqual(preview.content,original);
  const meta=await sharp(preview.content).metadata();
  assert.equal(meta.width,1200);assert.equal(meta.height,800);
  assert.match(preview.contentHash,/^[0-9a-f]{64}$/);
});
test('watermark configuration version invalidates derivative identity',async()=>{
  const image=await sharp({create:{width:700,height:700,channels:3,background:{r:90,g:80,b:70}}}).jpeg().toBuffer();
  const a=await createPreview(image,{GALLERY_WATERMARK_VERSION:'v1'});
  const b=await createPreview(image,{GALLERY_WATERMARK_VERSION:'v2'});
  assert.notEqual(a.contentHash,b.contentHash);
});
test('invalid watermark settings fail closed',()=>{
  assert.throws(()=>watermarkConfig({GALLERY_WATERMARK_OPACITY:'0'}),/Invalid/);
  assert.throws(()=>watermarkConfig({GALLERY_PREVIEW_MAX_WIDTH:'100'}),/Invalid/);
});
