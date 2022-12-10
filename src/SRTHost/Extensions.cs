using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SRTHost
{
    internal static class Extensions
    {
        internal static TaskAwaiter GetAwaiter(this CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> taskCompletionSource = new TaskCompletionSource<bool>();
            Task task = taskCompletionSource.Task;

            if (cancellationToken.IsCancellationRequested)
                taskCompletionSource.SetResult(true);
            else
                cancellationToken.Register((object? tcs) => ((TaskCompletionSource<bool>)tcs!).SetResult(true), taskCompletionSource);

            return task.GetAwaiter();
        }
    }
}
