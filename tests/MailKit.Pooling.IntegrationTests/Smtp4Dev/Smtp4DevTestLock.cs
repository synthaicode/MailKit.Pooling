using System.IO;

namespace PooledMailKit.IntegrationTests.Smtp4Dev;

internal sealed class Smtp4DevTestLock : IAsyncDisposable
{
    private static readonly string LockFilePath = Path.Combine(Path.GetTempPath(), "MailKit.Pooling.Smtp4DevTests.lock");

    private readonly Semaphore? semaphore;
    private readonly FileStream? lockFileStream;

    private Smtp4DevTestLock(Semaphore? semaphore, FileStream? lockFileStream)
    {
        this.semaphore = semaphore;
        this.lockFileStream = lockFileStream;
    }

    public static async Task<Smtp4DevTestLock> AcquireAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            var semaphore = new Semaphore(1, 1, @"Global\MailKit.Pooling.Smtp4DevTests");
            var acquired = await Task.Run(() => semaphore.WaitOne(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            if (!acquired)
            {
                semaphore.Dispose();
                throw new TimeoutException("Timed out while waiting for the shared smtp4dev test lock.");
            }

            return new Smtp4DevTestLock(semaphore, null);
        }

        var lockFileStream = await AcquireFileLockAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        return new Smtp4DevTestLock(null, lockFileStream);
    }

    public ValueTask DisposeAsync()
    {
        if (semaphore is not null)
        {
            semaphore.Release();
            semaphore.Dispose();
        }

        lockFileStream?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<FileStream> AcquireFileLockAsync(TimeSpan timeout)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            try
            {
                return new FileStream(
                    LockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("Timed out while waiting for the shared smtp4dev test lock.");
    }
}
