namespace MagicCommunity;

internal interface IDesktopExitCover
{
    void Complete(bool ready);
    Task Finished { get; }
}

// Invoked only by a user exit on the dispatcher. Crash and OS-session shutdown
// still recover immediately. Repeated exit gestures share the same sequence.
internal sealed class InstalledExitSequence(Func<Task<IDesktopExitCover?>> cover, Func<Task> restore, Action shutdown, Action<Exception> report)
{
    private Task? pending;
    internal Task Request() => pending ??= Run();
    private async Task Run()
    {
        IDesktopExitCover? transition = null;
        try
        {
            try { transition = await cover(); }
            catch (Exception error) when (error is not OutOfMemoryException) { report(error); }
            bool restored = false;
            try { await restore(); restored = true; }
            catch (Exception error) when (error is not OutOfMemoryException) { report(error); }
            finally { transition?.Complete(restored); }
            if (transition is not null)
                await transition.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { report(error); }
        finally { shutdown(); }
    }
}
