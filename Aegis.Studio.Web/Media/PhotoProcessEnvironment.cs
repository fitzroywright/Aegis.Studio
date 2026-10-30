using System.Diagnostics;
namespace Aegis.Studio.Web.Media;

internal static class PhotoProcessEnvironment
{
    // Untrusted image containers must not put application credentials in a decoder's environment.
    public static void Configure(ProcessStartInfo start)
    {
        start.Environment.Clear();
        foreach(string name in new[]{"PATH","TMPDIR","SystemRoot","LANG"})
            if(Environment.GetEnvironmentVariable(name) is string value)start.Environment[name]=value;
        start.Environment["OMP_NUM_THREADS"]="1";
        start.Environment["OPENBLAS_NUM_THREADS"]="1";
    }
}
