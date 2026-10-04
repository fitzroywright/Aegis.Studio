namespace Aegis.Studio.Web.Operations;

public sealed record DemoSeedProgress(int Percent, string Stage, int Current, int Total, string Message, bool Completed = false, bool Failed = false);
