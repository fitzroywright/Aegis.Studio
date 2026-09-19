using System.Security.Cryptography;
using System.Text;

namespace Aegis.Studio.WorkDay;

public sealed class WorkDayDataProtector
{
    private const string KeyName="Aegis.Studio.WorkDay.LocalDataKey";
    private byte[]? cachedKey;
    private async Task<byte[]> KeyAsync()
    {
        if(cachedKey is not null)return cachedKey;
        string? existing=null;
        try{existing=await SecureStorage.Default.GetAsync(KeyName);}catch{}
        if(!string.IsNullOrWhiteSpace(existing)){cachedKey=Convert.FromBase64String(existing);return cachedKey;}
        cachedKey=RandomNumberGenerator.GetBytes(32);
        await SecureStorage.Default.SetAsync(KeyName,Convert.ToBase64String(cachedKey));
        return cachedKey;
    }
    public async Task<string?> ProtectAsync(string? value)
    {
        if(string.IsNullOrEmpty(value))return value;
        byte[] key=await KeyAsync(),nonce=RandomNumberGenerator.GetBytes(12),plain=Encoding.UTF8.GetBytes(value),cipher=new byte[plain.Length],tag=new byte[16];
        using AesGcm aes=new(key,16);aes.Encrypt(nonce,plain,cipher,tag);
        byte[] all=new byte[nonce.Length+tag.Length+cipher.Length];Buffer.BlockCopy(nonce,0,all,0,nonce.Length);Buffer.BlockCopy(tag,0,all,nonce.Length,tag.Length);Buffer.BlockCopy(cipher,0,all,nonce.Length+tag.Length,cipher.Length);
        return "enc:"+Convert.ToBase64String(all);
    }
    public async Task<string?> UnprotectAsync(string? value)
    {
        if(string.IsNullOrEmpty(value)||!value.StartsWith("enc:",StringComparison.Ordinal))return value;
        byte[] key=await KeyAsync(),all=Convert.FromBase64String(value[4..]),nonce=all[..12],tag=all[12..28],cipher=all[28..],plain=new byte[cipher.Length];
        using AesGcm aes=new(key,16);aes.Decrypt(nonce,cipher,tag,plain);return Encoding.UTF8.GetString(plain);
    }
}
