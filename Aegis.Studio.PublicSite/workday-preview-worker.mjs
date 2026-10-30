import sharp from 'sharp';
try{
  await sharp(process.argv[2],{limitInputPixels:120_000_000}).rotate().resize({width:1600,height:1600,fit:'inside',withoutEnlargement:true})
    .jpeg({quality:80}).toFile(process.argv[3]);
}catch{process.exitCode=2;}
