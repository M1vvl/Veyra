namespace GlassDock.Shell;

/// <summary>Keeps Explorer's taskbars hidden for one native Start interaction.</summary>
internal static class StartTaskbarSuppression
{
    public static IDisposable Begin(TaskbarLease sharedLease)
    {
        ArgumentNullException.ThrowIfNull(sharedLease);
        return sharedLease.SuppressForStart();
    }
}
