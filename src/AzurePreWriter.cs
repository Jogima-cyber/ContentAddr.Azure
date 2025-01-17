using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.ContentAddr.Azure
{
    /// <summary> Responsible for writing to a writable Azure Blob, but not for committing it. </summary>
    public abstract class AzurePreWriter : StoreWriter
    {
        /// <summary> Temporary blob in the staging container. </summary>
        /// <remarks> Can either be set initially or evaluated from <see cref="_temporaryTask"/>. </remarks>
        protected CloudBlockBlob Temporary { get; private set; }

        /// <summary> If <see cref="Temporary"/> could not be provided when the writer was created. </summary>
        /// <remarks>
        ///     Will be awaited when the first write is performed, in order to populate 
        ///     <see cref="Temporary"/>.
        /// </remarks>
        private readonly Task<CloudBlockBlob> _temporaryTask;

        /// <summary> All sent Azure Blob blocks, to be PUT. </summary>
        private readonly List<string> _blocks = new List<string>();

        /// <summary> All individual upload tasks, to be awaited before the block list PUT. </summary>
        private readonly List<Task> _tasks = new List<Task>();

        protected AzurePreWriter(CloudBlockBlob temporary)
        {
            Temporary = temporary ?? throw new ArgumentNullException(nameof(temporary));
        }

        protected AzurePreWriter(Task<CloudBlockBlob> temporary)
        {
            _temporaryTask = temporary;
        }


        /// <summary> Maximum number of retry if write fail with ServerBusy error </summary>
        private const int CommitRetryCount = 10;

        /// <summary> Interval in milliseconds between retry </summary>
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Wrapper around <see cref="CloudBlockBlob.PutBlockAsync"/> to perform a retry if we
        /// got a "ServerBusy" response.
        /// </summary>
        private static Task PutBlockAsyncRetry(CloudBlockBlob blob, string id, Stream ms, CancellationToken cancel) =>
            ServerBusyRetry(() => blob.PutBlockAsync(id, ms, null, null, null, null, cancel), cancel);

        /// <summary>
        /// Wrapper around <see cref="CloudBlockBlob.PutBlockListAsync"/> to perform a retry if we
        /// got a "ServerBusy" response.
        /// </summary>
        private static Task PutBlockListAsyncRetry(
            CloudBlockBlob blob, IReadOnlyList<string> blocks, CancellationToken cancel) =>
            ServerBusyRetry(() => blob.PutBlockListAsync(blocks, null, null, null, cancel), cancel);

        /// <summary>
        /// Retry a task generating function as long as we receive "ServerBusy" error
        /// message in <see cref="StorageException"/>
        /// </summary>
        private static async Task ServerBusyRetry(Func<Task> toRetry, CancellationToken cancel)
        {
            var retryCount = CommitRetryCount;

            while (retryCount > 0)
            {
                try
                {
                    await toRetry().ConfigureAwait(false);
                    retryCount = 0;
                }
                catch (StorageException ste) when (retryCount > 0)
                {
                    // message from:
                    // https://docs.microsoft.com/en-us/rest/api/storageservices/common-rest-api-error-codes
                    if (ste.RequestInformation.HttpStatusMessage != "ServerBusy")
                        throw;

                    // otherwise continue
                    retryCount--;
                }

                if (retryCount > 0)
                {
                    await Task.Delay(RetryInterval, cancel).ConfigureAwait(false);
                }
            }
        }

        /// <see cref="StoreWriter.DoWriteAsync"/>
        /// <remarks>
        ///     The written data is uploaded as one or more Azure Blob blocks,
        ///     and the identifiers of those blocks are stored in the correct order
        ///     in <see cref="_blocks"/>.
        /// </remarks>
        protected override Task DoWriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancel)
        {
            // We might need to split data into multiple blocks, if the block size 
            // is larger than what Azure allows. 
            const int azureMax = 4 * 1024 * 1024;

            // How many blocks to generate ? We are only guaranteed to be single-threaded
            // up until the first 'await', so we want to update '_blocks' fully before that
            // happens.

            var blocks = count / azureMax + (count % azureMax == 0 ? 0 : 1);

            if (blocks == 1)
            {
                var id = Guid.NewGuid().ToString("N");
                var ms = new MemoryStream(buffer, offset, count);
                _blocks.Add(id);

                var task = Temporary != null
                    ? PutBlockAsyncRetry(Temporary, id, ms, cancel)
                    : Task.Run(async () =>
                    {
                        var t = await _temporaryTask;
                        await PutBlockAsyncRetry(t, id, ms, cancel);
                    }, cancel);

                _tasks.Add(task);
                return task;
            }

            // Multiple blocks
            var uploads = new Task[blocks];

            var block = 0;
            var end = offset + count;
            while (offset < end)
            {
                var id = Guid.NewGuid().ToString("N");
                var written = Math.Min(end - offset, azureMax);
                var ms = new MemoryStream(buffer, offset, written);

                var task = Temporary == null
                    ? Task.Run(async () =>
                        {
                            var t = await _temporaryTask;
                            await PutBlockAsyncRetry(t, id, ms, cancel);
                        }, cancel)
                    : PutBlockAsyncRetry(Temporary, id, ms, cancel);

                _blocks.Add(id);
                _tasks.Add(uploads[block++] = task);

                offset += written;
            }

            return Task.WhenAll(uploads);
        }

        /// <summary> Write the temporary blob to Azure (by putting the block list). </summary>
        protected async Task WriteTemporary(CancellationToken cancel)
        {
            if (Temporary == null) Temporary = await _temporaryTask.ConfigureAwait(false);

            await Task.WhenAll(_tasks).ConfigureAwait(false);

            await PutBlockListAsyncRetry(Temporary, _blocks, cancel).ConfigureAwait(false);
            _blocks.Clear();
        }
    }
}