namespace PageSmith.Core.Office;

/// <summary>Runs work on a dedicated single-threaded apartment thread, as COM automation of Office requires.</summary>
public static class StaRunner
{
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PageSmith STA worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public static Task RunAsync(Action work, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            work();
            return true;
        }, ct);
}
