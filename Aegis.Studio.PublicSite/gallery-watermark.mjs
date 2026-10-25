import { createHash } from 'node:crypto';
const escapeXml=value=>String(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&apos;'}[c]));
export function watermarkConfig(env){
  const text=String(env.GALLERY_WATERMARK_TEXT||"BRYAN'S").trim();
  const opacity=Number(env.GALLERY_WATERMARK_OPACITY||0.23);
  const maxWidth=Number(env.GALLERY_PREVIEW_MAX_WIDTH||1600);
  if(!text||text.length>80||!Number.isFinite(opacity)||opacity<0.08||opacity>0.7||
     !Number.isSafeInteger(maxWidth)||maxWidth<600||maxWidth>2400)
    throw Error('Invalid gallery watermark configuration.');
  return {text,opacity,maxWidth,version:String(env.GALLERY_WATERMARK_VERSION||'v1')};
}
export async function createPreview(original,env) {
  const settings=watermarkConfig(env);
  // Sharp is installed on the development Mac with npm install sharp; no
  // remote image processor or browser overlay is used.
  const sharp=(await import('sharp')).default;
  const image=sharp(original,{limitInputPixels:90_000_000,failOn:'error'}).rotate();
  const metadata=await image.metadata();
  if(!['jpeg','png','webp'].includes(metadata.format))throw Error('Unsupported photograph type.');
  const width=Math.min(metadata.width,settings.maxWidth);
  const height=Math.max(1,Math.round(metadata.height*width/metadata.width));
  const words=escapeXml(settings.text);
  const tile=340,lines=[];
  for(let y=-height;y<height*2;y+=tile){
    for(let x=-width;x<width*2;x+=tile){
      lines.push('<text x="'+x+'" y="'+y+'" font-family="Arial,sans-serif" font-size="45" font-weight="700" fill="#ffffff" fill-opacity="'+settings.opacity+'">'+words+'</text>');
    }
  }
  const svg=Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="'+width+'" height="'+height+'"><g transform="rotate(-25 '+(width/2)+' '+(height/2)+')">'+lines.join('')+'</g></svg>');
  const content=await sharp(original,{limitInputPixels:90_000_000,failOn:'error'})
    .rotate().resize({width:settings.maxWidth,withoutEnlargement:true})
    .composite([{input:svg}]).jpeg({quality:82,mozjpeg:true}).toBuffer();
  const contentHash=createHash('sha256').update(original).update(JSON.stringify(settings)).digest('hex');
  return {content,type:'image/jpeg',contentHash};
}
