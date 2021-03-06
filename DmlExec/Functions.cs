﻿using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.WindowsAzure.Storage.DataMovement;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Threading;
using System.Text;
using System.Configuration;
using System.Net;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Globalization;
using Shared;

namespace DmlExec
{
    public static class Functions
    {
        // Azure Storage Retry Policy
        private static TimeSpan _deltaBackOff = TimeSpan.FromMilliseconds(100);
        private static int _maxRetries = 5;
        private static IRetryPolicy _retryPolicy = new ExponentialRetry(_deltaBackOff, _maxRetries);
        // Retries (in MS) 100, 200, 400, 800, 1600 (+/- 20%)

        private static TextWriter _log;

        // This function will get triggered/executed when a new message is written on the Azure WebJobs Queue called backupqueue
        // This version uses CopyDirectoryAsync in DML 0.1. Blobs are copied in parallel using ForEachAsync()
        public async static Task ProcessQueueMessage([QueueTrigger("backupqueue")] CopyItem copyItem, TextWriter log, CancellationToken cancelToken)
        {
            _log = log;
            await log.WriteLineAsync("Job Start: " + copyItem.JobName);

            // This class accumulates transfer data during the process
            ProgressRecorder progressRecorder = new ProgressRecorder();

            try
            {
                // OpContext for pre-copy retries on Azure Storage
                // DML has its own context object and retry
                OperationContext opContext = new OperationContext();
                opContext.Retrying += StorageRequest_Retrying;

                // Define Blob Request Options
                BlobRequestOptions blobRequestOptions = new BlobRequestOptions
                {
                    // Defined Exponential Retry Policy
                    RetryPolicy = _retryPolicy
                };

                // The default number of parallel tasks in DML = # of Processors * 8
                // Set that as our max limit of parallel tasks to that amount since more gives us no additional performance
                //int parallelTasks = Environment.ProcessorCount * 8;
                int parallelTasks = Convert.ToInt32(ConfigurationManager.AppSettings["ParallelTasks"]);

                // Set the number of http connections to # of Processors * 8
                ServicePointManager.DefaultConnectionLimit = Environment.ProcessorCount * 8;

                // Save additional request round trip. We are not chunking and
                // uploading large amounts of data where we'd send 100's so set to false
                ServicePointManager.Expect100Continue = false;

                // CancellationTokenSource used to cancel the transfer
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                // Represents a checkpoint from which a transfer may be resumed and continue
                // The checkpoint gets set on each call to CopyBlobAsync(). This allows the WebJob
                // to fail then pick it right up and continue to copy blobs, completing the copy job
                TransferCheckpoint transferCheckpoint = null;

                // Open connections to both storage accounts
                CloudStorageAccount sourceAccount = GetAccount(copyItem.SourceAccountToken);
                CloudStorageAccount destinationAccount = GetAccount(copyItem.DestinationAccountToken);

                // Context object for the transfer, provides additional runtime information about its execution
                TransferContext transferContext = new TransferContext
                {
                    // Pipe transfer progress data to ProgressRecorder
                    ProgressHandler = progressRecorder,

                    // Callback to overwrite destination if it exists
                    OverwriteCallback = (source, destination) =>
                    {
                        return OverwriteFile(source, destination, sourceAccount, destinationAccount, copyItem, blobRequestOptions, opContext);
                    }
                };

                CloudBlobDirectory sourceContainer = await GetDirectoryAsync(sourceAccount, copyItem.SourceContainer, blobRequestOptions);
                CloudBlobDirectory destinationContainer = await GetDirectoryAsync(destinationAccount, copyItem.DestinationContainer, blobRequestOptions);

                BlobContinuationToken continueToken = null;

                do
                {
                    // Fetch blobs in groups of 5000 max. If more than that loop until continue token is not null
                    var listTask = await sourceContainer.ListBlobsSegmentedAsync(true, BlobListingDetails.None, null, continueToken, blobRequestOptions, opContext, cancelToken);

                    // Save the continuation token
                    continueToken = listTask.ContinuationToken;

                    // Asynchronous parallel iteratation through blobs to copy
                    await listTask.Results.ForEachAsync(parallelTasks, async task =>
                    {
                        CloudBlob sourceBlob = (CloudBlob)task;
                        CloudBlob destinationBlob = GetBlobReference(destinationContainer, sourceBlob);

                        // Copy the blob
                        await CopyBlobAsync(sourceBlob, destinationBlob, transferContext, transferCheckpoint, cancellationTokenSource);

                        // Check for cancellation
                        if (cancelToken.IsCancellationRequested)
                        {
                            await log.WriteLineAsync("Web Job Cancellation Requested");
                            cancellationTokenSource.Cancel();
                        }
                    });
                }
                while (continueToken != null);

                await log.WriteLineAsync(progressRecorder.ToString());
                await log.WriteLineAsync("Job Complete: " + copyItem.JobName);
            }
            catch (Exception ex)
            {
                await log.WriteLineAsync("Backup Job error: " + copyItem.JobName + ", Error: " + ex.Message);
                await log.WriteLineAsync(progressRecorder.ToString());
            }
        }

        // This version uses CopyDirectoryAsync in DML 0.2. I'm not sure it is faster than what I did above copying them manually in DML 0.1
        public async static Task ProcessQueueMessage2([QueueTrigger("backupqueue")] CopyItem copyItem, TextWriter log, CancellationToken cancelToken)
        {
            _log = log;
            log.WriteLine("Job Start: " + copyItem.JobName);

            // This class accumulates transfer data during the process
            ProgressRecorder progressRecorder = new ProgressRecorder();

            try
            {
                // OpContext to track PreCopy Retries on Azure Storage
                // DML has its own context object and retry
                OperationContext opContext = new OperationContext();
                opContext.Retrying += StorageRequest_Retrying;

                // Define Blob Request Options
                BlobRequestOptions blobRequestOptions = new BlobRequestOptions
                {
                    // Defined Exponential Retry Policy above
                    RetryPolicy = _retryPolicy
                };

                // Set the number of parallel tasks in DML. This allows it to copy multiple
                // items at once when copying a container or directory
                //int parallelTasks = Environment.ProcessorCount * 8;
                int parallelTasks = Convert.ToInt32(ConfigurationManager.AppSettings["ParallelTasks"]);

                // Set the number of connections so each DML copy task has its own connection to Azure Storage
                ServicePointManager.DefaultConnectionLimit = Environment.ProcessorCount * 8;

                TransferManager.Configurations.ParallelOperations = parallelTasks; //64;

                log.WriteLine("Parallel Operations = " + parallelTasks.ToString());

                // Short circuit additional request round trips. We are not chunking and
                // uploading large amounts of data where we'd send 100's so set to false
                ServicePointManager.Expect100Continue = false;

                // CancellationTokenSource used to cancel the transfer
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                // Represents a checkpoint from which a transfer may be resumed and continued
                // This is set within the CopyContainerAsync function
                TransferCheckpoint transferCheckpoint = null;

                // Open connections to both storage accounts
                CloudStorageAccount sourceAccount = GetAccount(copyItem.SourceAccountToken);
                CloudStorageAccount destinationAccount = GetAccount(copyItem.DestinationAccountToken);

                // Context object for the transfer, provides additional runtime information about its execution
                TransferContext transferContext = new TransferContext
                {
                    // Pipe transfer progress data to ProgressRecorder
                    ProgressHandler = progressRecorder,

                    // Callback to overwrite destination if it exists
                    OverwriteCallback = (source, destination) =>
                    {
                        return OverwriteFile(source, destination, sourceAccount, destinationAccount, copyItem, blobRequestOptions, opContext);
                    }
                };

                CopyDirectoryOptions copyDirectoryOptions = new CopyDirectoryOptions
                {
                    IncludeSnapshots = true,
                    Recursive = true
                };

                // Get the root source and destination directories for the two containers to be copied
                CloudBlobDirectory sourceDirectory = await GetDirectoryAsync(sourceAccount, copyItem.SourceContainer, blobRequestOptions);
                CloudBlobDirectory destinationDirectory = await GetDirectoryAsync(destinationAccount, copyItem.DestinationContainer, blobRequestOptions);

                // Copy the container
                await CopyDirectoryAsync(sourceDirectory, destinationDirectory, copyDirectoryOptions, transferContext, transferCheckpoint, cancellationTokenSource);


                log.WriteLine(progressRecorder.ToString());
                log.WriteLine("Job Complete: " + copyItem.JobName);
            }
            catch (Exception ex)
            {
                log.WriteLine("Backup Job error: " + copyItem.JobName + ", Error: " + ex.Message);
                log.WriteLine(progressRecorder.ToString());
            }
        }
        private static bool OverwriteFile(string sourceUri, string destinationUri, CloudStorageAccount sourceAccount, CloudStorageAccount destinationAccount, CopyItem copyItem, BlobRequestOptions blobRequestOptions, OperationContext opContext)
        {
            // If Incremental backup only copy if source is newer
            if (copyItem.IsIncremental)
            {
                CloudBlob sourceBlob = new CloudBlob(new Uri(sourceUri), sourceAccount.Credentials);
                CloudBlob destinationBlob = new CloudBlob(new Uri(destinationUri), destinationAccount.Credentials);

                sourceBlob.FetchAttributes(null, blobRequestOptions, opContext);
                destinationBlob.FetchAttributes(null, blobRequestOptions, opContext);

                // Source date is newer (larger) than destination date
                return (sourceBlob.Properties.LastModified > destinationBlob.Properties.LastModified);
            }
            else
            {
                // Full backup, overwrite everything
                return true;
            }

        }
        private async static Task CopyDirectoryAsync(CloudBlobDirectory sourceDirectory, CloudBlobDirectory destinationDirectory, CopyDirectoryOptions copyDirectoryOptions, TransferContext transferContext, TransferCheckpoint transferCheckpoint, CancellationTokenSource cancellationTokenSource)
        {
            // Start the transfer
            try
            {
                await TransferManager.CopyDirectoryAsync(
                    sourceBlobDir: sourceDirectory,
                    destBlobDir: destinationDirectory,
                    isServiceCopy: true,
                    options: copyDirectoryOptions,
                    context: transferContext,
                    cancellationToken: cancellationTokenSource.Token);

                // Store the transfer checkpoint.
                transferCheckpoint = transferContext.LastCheckpoint;
            }
            catch (TransferException te)
            {
                // Swallow Exceptions from skipped files in Overwrite Callback
                // Log any other Transfer Exceptions
                if(te.ErrorCode != TransferErrorCode.SubTransferFails)
                { 
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Transfer Error: " + te.Message);
                    sb.AppendLine("Transfer Error Code: " + te.ErrorCode);
                    await _log.WriteLineAsync(sb.ToString());
                }
            }
        }
        private async static Task CopyBlobAsync(CloudBlob sourceBlob, CloudBlob destinationBlob, TransferContext transferContext, TransferCheckpoint transferCheckpoint, CancellationTokenSource cancellationTokenSource)
        {
            // Start the transfer
            try
            {
                await TransferManager.CopyAsync(
                    sourceBlob: sourceBlob,
                    destBlob: destinationBlob,
                    isServiceCopy: true, //Async Server-Side Copy
                    options: null,
                    context: transferContext,
                    cancellationToken: cancellationTokenSource.Token);

                // Store the transfer checkpoint.
                // [longwen] How does the resume feature work here? Looks the checkpoint is assigned to a local variable and lost afterwards. 
                // FYI, the checkpoint is an serializable object. Thus, DMLib can resume a transfer cross process by presisting the
                // checkpoint into a local file and then load it back.
                transferCheckpoint = transferContext.LastCheckpoint;
            }
            catch (TransferException te)
            {
                // Swallow Exceptions from skipped files in Overwrite Callback
                // Log any other Transfer Exceptions
                if (te.ErrorCode != TransferErrorCode.SubTransferFails)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Transfer Error: " + te.Message);
                    sb.AppendLine("Transfer Error Code: " + te.ErrorCode);
                    await _log.WriteLineAsync(sb.ToString());
                }
            }
        }
        private static CloudBlob GetBlobReference(CloudBlobDirectory directory, CloudBlob blob)
        {
            CloudBlob cloudBlob = null;

            // [longwen] You can use cloudBlob.BlobType to check the blob type here
            switch (blob.GetType().Name)
            {
                case nameof(CloudBlockBlob):
                    cloudBlob = directory.GetBlockBlobReference(blob.Name);
                    break;
                case nameof(CloudPageBlob):
                    cloudBlob = directory.GetPageBlobReference(blob.Name);
                    break;
                case nameof(CloudAppendBlob):
                    cloudBlob = directory.GetAppendBlobReference(blob.Name);
                    break;
                default:
                    throw new Exception("Unknown CloudBlob type");
            }

            return cloudBlob;
        }
         private async static Task<CloudBlobDirectory> GetDirectoryAsync(CloudStorageAccount account, string containerName, BlobRequestOptions blobRequestOptions)
        {
            CloudBlobClient client = account.CreateCloudBlobClient();
            client.DefaultRequestOptions = blobRequestOptions;
            CloudBlobContainer container = client.GetContainerReference(containerName);
            await container.CreateIfNotExistsAsync();

            // Return root directory for container
            return container.GetDirectoryReference("");
        }
        private static CloudStorageAccount GetAccount(string accountToken)
        {
            CloudStorageAccount account;

            if (!CloudStorageAccount.TryParse(GetConnectionString(accountToken), out account))
                throw new StorageException("Error Parsing Storage Account Connection String");
            else
                return account;
        }
        private static string GetConnectionString(string accountToken)
        {
            // Connection strings can be in app/web.config or in portal "connection strings" for host web app.
            return ConfigurationManager.ConnectionStrings[accountToken].ConnectionString;
        }
        static void StorageRequest_Retrying(object sender, RequestEventArgs e)
        {
            string errMessage = e.RequestInformation.Exception.Message;
            string path = e.Request.Address.AbsoluteUri;

            OperationContext oc = (OperationContext)sender;
            int retryCount = oc.RequestResults.Count;

            string message = String.Format(CultureInfo.InvariantCulture, "Retry Count = {0}, Error = {1}, URI = {2}", retryCount, errMessage, path);

            _log.WriteLine("Azure Storage Request Retry", message);
        }
        public static Task ForEachAsync<T>(this IEnumerable<T> source, int dop, Func<T, Task> body)
        {
            // [longwen] This function will separate the listed blobs into #dop groups and then launch #dop async tasks to copy these blobs.
            // Blobs within the same groups will be copied sequentially. The outter listing loop will continue to get more blobs from the source
            // container once all these #dop groups of blobs are copied. Please tell me if I didn't understand this function correctly.
            // 
            // This function can be not very efficient in some cases:
            //   1. If one of the group contains some large blobs, it can be the bottleneck while all other groups finish and the listing loop is 
            //      waiting for it.
            //   2. ListBlobsSegmentedAsync gets at most 5000 blobs. This call can be time consuming regarding the response size while no blob is 
            //      copied at the same time.
            //
            // In azcopy, we use a producer-consumer pattern to list and transfer blobs. Basically, there are two threads: One lists blobs from the source
            // container and add them into a blocking queue. This thread will be blocked if the queue reaches its size limitation. Another thread get blobs
            // out of the queue and start a worker thread to copy the blob. This thread maintains a counter of the outstanding workers and block itself for
            // a while if it reaches the parallel limitation.
            //
            // For the listing thread, it calls ListBlobSegmentedAsync with a smaller segment size. In AzCopy, the magic number is 250.
            return Task.WhenAll(
                from partition in Partitioner.Create(source).GetPartitions(dop)
                select Task.Run(async delegate
                {
                    using (partition)
                        while (partition.MoveNext())
                            await body(partition.Current);
                }));
        }
    }
}
