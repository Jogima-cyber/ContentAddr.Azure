﻿using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.ContentAddr.Azure
{
    /// <summary> Persistent content-addressable store backed by Azure blobs. </summary>
    /// <see cref="AzureReadOnlyStore"/>
    /// <remarks>
    ///     Supports uploading blobs, as well as committing blobs uploaded to a staging
    ///     container.
    /// </remarks>
    public sealed class AzureStore : AzureReadOnlyStore, IAzureStore
    {
        /// <summary> Called when a new blob is committed. </summary>
        private readonly AzureWriter.OnCommit _onCommit;

        /// <summary> The container where temporary blobs are staged for a short while. </summary>
        private CloudBlobContainer Staging { get; }

        /// <param name="realm"> <see cref="AzureReadOnlyStore"/> </param>
        /// <param name="persistent"> 
        ///     Blobs are stored here, named according to 
        ///     <see cref="AzureReadOnlyStore.AzureBlobName"/>.
        /// </param>
        /// <param name="staging"> Temporary blobs are stored here. </param>
        /// <param name="onCommit"> Called when a blob is committed. </param>
        public AzureStore(
            string realm,
            CloudBlobContainer persistent,
            CloudBlobContainer staging,
            AzureWriter.OnCommit onCommit = null) : base(realm, persistent)
        {
            _onCommit = onCommit;
            Staging = staging;
        }

        /// <see cref="IStore{TBlobRef}.StartWriting"/>
        public StoreWriter StartWriting() =>
            new AzureWriter(Realm, Persistent, TempBlob(), _onCommit);

        /// <summary> A reference to a temporary blob in the staging container. </summary>
        private CloudBlockBlob TempBlob() =>
            Staging.GetBlockBlobReference(
                DateTime.UtcNow.ToString("yyyy-MM-dd") + "/" + Realm + "/" + Guid.NewGuid());

        /// <summary> Get the URL of a temporary blob where data can be uploaded. </summary>
        /// <remarks> 
        ///     Commit blob with <see cref="CommitTemporaryBlob"/>.
        /// 
        ///     The name should be a valid Azure Blob name, but its contents are not important
        ///     (although it is recommended that a "date/realm/guid" format is used to make
        ///     cleanup easier, to prevent cross-realm contamination, and to avoid collisions).
        /// </remarks>
        public Uri GetSignedUploadUrl(string name, TimeSpan life)
        {
            var blob = Staging.GetBlockBlobReference(name);
            var token = blob.GetSharedAccessSignature(new SharedAccessBlobPolicy
            {
                Permissions = SharedAccessBlobPermissions.Write | SharedAccessBlobPermissions.Delete,
                SharedAccessExpiryTime = new DateTimeOffset(DateTime.UtcNow + life)
            },
            new SharedAccessBlobHeaders
            {
                CacheControl = "private"
            });

            return new Uri(blob.Uri.AbsoluteUri + token);
        }

        /// <summary> Commit a blob from staging to the persistent store. </summary>
        /// <remarks>
        ///     Computes the hash of the blob before committing it.
        /// </remarks>
        /// <param name="name"> The full name of the temporary blob. </param>
        /// <param name="cancel"> Cancellation token. </param>
        public async Task<IAzureReadBlobRef> CommitTemporaryBlob(string name, CancellationToken cancel) 
        {
            var sw = Stopwatch.StartNew();

            var temporary = Staging.GetBlockBlobReference(name);
            if (!await temporary.ExistsAsync(null, null, cancel).ConfigureAwait(false))
                throw new CommitBlobException(Realm, name, "temporary blob does not exist.");

            var md5 = MD5.Create();

            // We use buffered async reading, so determine a good buffer size.
            var bufferSize = 4 * 1024 * 1024;

            long? blobLength = temporary.Properties?.Length;
            if (blobLength < bufferSize)
                bufferSize = (int)blobLength.Value;

            var buffer = new byte[bufferSize];

            int nbRead = 1;
            long position = 0;

            using (var stream = await temporary.OpenReadAsync(null, null, null, cancel).ConfigureAwait(false))
            {
                int read = 0;
                do
                {
                    read = await stream.ReadAsync(buffer, 0, bufferSize, cancel)
                        .ConfigureAwait(false);

                    position += read;
                    nbRead++;

                    md5.TransformBlock(buffer, 0, read, buffer, 0);

                } while (read > 0);
            }

            md5.TransformFinalBlock(buffer, 0, 0);

            var hash = new Hash(md5.Hash);

            var final = Persistent.GetBlockBlobReference(AzureBlobName(Realm, hash));

            try
            {
                var exists = await AzureRetry.OrFalse(() => final.ExistsAsync(null, null, cancel))
                    .ConfigureAwait(false);

                if (!exists)
                {
                    await CopyToPersistent(temporary, final, cancel).ConfigureAwait(false);
                }

                _onCommit?.Invoke(sw.Elapsed, Realm, hash, final.Properties.Length, exists);
            }
            finally
            {
                // Always delete the blob.
                DeleteBlob(temporary, TimeSpan.FromMinutes(10));
            }

            return new AzureBlobRef(Realm, hash, final);
        }

        /// <summary> Delete a block after a short wait. </summary>
        /// <remarks> This schedules the deletion but does not wait for it. </remarks>
        public static void DeleteBlob(CloudBlockBlob temporary, TimeSpan wait)
        {
            // After a short while, delete the staging blob. Don't do it immediately, just
            // in case another thread (or server) is currently touching it as well.
            Task.Delay(wait).ContinueWith(_ => temporary.DeleteIfExistsAsync());
        }

        /// <summary> Copy a temporary blob to a persistent final blob. </summary>
        /// <remarks>
        ///     The task completes when the blob has been copied, or the copy has
        ///     failed. 
        /// </remarks>
        public static async Task CopyToPersistent(
            CloudBlockBlob temporary,
            CloudBlockBlob final,
            CancellationToken cancel)
        {
            // Copy the blob over.
            await AzureRetry.Do(
                c => final.StartCopyAsync(temporary, null, null, null, null, c),
                cancel).ConfigureAwait(false);

            // Wait for copy to finish.
            var delay = 250;
            while (true)
            {
                await AzureRetry.Do(
                    c => final.FetchAttributesAsync(null, null, null, c),
                    cancel).ConfigureAwait(false);

                switch (final.CopyState.Status)
                {
                    case CopyStatus.Pending:
                        if (delay <= 120000) delay *= 2;
                        await Task.Delay(TimeSpan.FromMilliseconds(delay), cancel);
                        continue;
                    case CopyStatus.Aborted:
                    case CopyStatus.Failed:
                    case CopyStatus.Invalid:
                        throw new Exception("Internal copy for '" + final.Name + "' failed (" + final.CopyState.Status + ")");
                    case CopyStatus.Success:
                        return;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
    }
}