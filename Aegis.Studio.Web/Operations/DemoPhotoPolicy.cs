namespace Aegis.Studio.Web.Operations;

public static class DemoPhotoPolicy
{
    public const int MinimumLongEdge=2400;
    public const long MaximumPixels=60_000_000;
    public const long MaximumBytes=100_000_000;
    public static void ValidateDimensions(int width,int height)
    {
        if(width<=0||height<=0||Math.Max(width,height)<MinimumLongEdge||(long)width*height>MaximumPixels)
            throw new InvalidOperationException($"Demo photograph is {width} x {height}. Use a genuine original with at least {MinimumLongEdge} pixels on its longest edge and at most {MaximumPixels:N0} pixels; low-resolution substitutes are not accepted.");
    }
}
