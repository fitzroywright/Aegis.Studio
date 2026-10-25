import { createHash } from 'node:crypto';
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
export class GalleryObjectStorage {
  constructor(env,store){
    this.env=env;this.store=store;
    this.s3=env.GALLERY_STORAGE_MODE==='s3';
    this.bucket=String(env.GALLERY_S3_BUCKET||'');
    this.region=String(env.AWS_REGION||'');
    this.prefix=String(env.GALLERY_S3_PREFIX||'bryans-galleries').replace(/^\/+|\/+$/g,'');
    if(this.s3&&(!/^[a-z0-9][a-z0-9.-]{2,62}$/.test(this.bucket)||!this.region||!this.prefix))
      throw Error('GALLERY_S3_BUCKET, AWS_REGION and a safe prefix are required for private S3 storage.');
    store.db.exec("CREATE TABLE IF NOT EXISTS gallery_media_objects(gallery_id TEXT NOT NULL,photo_id TEXT NOT NULL,representation TEXT NOT NULL,object_key TEXT NOT NULL,content_hash TEXT NOT NULL,content_type TEXT NOT NULL,PRIMARY KEY(gallery_id,photo_id,representation))");
    this.client=null;
  }
  key(galleryId,photoId,kind){return this.prefix+'/'+galleryId+'/'+kind+'/'+photoId;}
  async sdk(){
    if(!this.client){
      const {S3Client}=await import('@aws-sdk/client-s3');
      this.client=new S3Client({region:this.region,maxAttempts:3});
    }
    return this.client;
  }
  async storeObject(galleryId,photoId,representation,type,bytes){
    if(!this.s3)return null;
    const {PutObjectCommand}=await import('@aws-sdk/client-s3');
    const key=this.key(galleryId,photoId,representation),contentHash=hash(bytes);
    const old=this.store.db.prepare('SELECT content_hash FROM gallery_media_objects WHERE gallery_id=? AND photo_id=? AND representation=?').get(galleryId,photoId,representation);
    if(old&&old.content_hash===contentHash)return {key,contentHash};
    if(old&&representation==='original')throw Object.assign(new Error('Original media version cannot change.'),{status:409});
    await(await this.sdk()).send(new PutObjectCommand({Bucket:this.bucket,Key:key,Body:bytes,
      ContentType:type,CacheControl:'private, no-store',ServerSideEncryption:'AES256'}));
    return {key,contentHash};
  }
  remember(galleryId,photoId,representation,type,record){
    if(!record)return;
    this.store.db.prepare('INSERT INTO gallery_media_objects VALUES(?,?,?,?,?,?) ON CONFLICT(gallery_id,photo_id,representation) DO UPDATE SET object_key=excluded.object_key,content_hash=excluded.content_hash,content_type=excluded.content_type')
      .run(galleryId,photoId,representation,record.key,record.contentHash,type);
  }
  async get(galleryId,photoId,representation){
    const row=this.store.db.prepare('SELECT object_key,content_type FROM gallery_media_objects WHERE gallery_id=? AND photo_id=? AND representation=?').get(galleryId,photoId,representation);
    if(!row)return null;
    if(!this.s3)throw Error('Private gallery object store is not enabled.');
    const {GetObjectCommand}=await import('@aws-sdk/client-s3');
    const response=await(await this.sdk()).send(new GetObjectCommand({Bucket:this.bucket,Key:row.object_key}));
    if(!response.Body)throw Error('Gallery object is missing.');
    return {type:row.content_type,stream:response.Body};
  }
  async delete(galleryId,photoId,representation){
    const row=this.store.db.prepare('SELECT object_key FROM gallery_media_objects WHERE gallery_id=? AND photo_id=? AND representation=?').get(galleryId,photoId,representation);
    if(!row||!this.s3)return;
    const {DeleteObjectCommand}=await import('@aws-sdk/client-s3');
    await(await this.sdk()).send(new DeleteObjectCommand({Bucket:this.bucket,Key:row.object_key}));
  }
}
