using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Functions.Worker;
//using Microsoft.Azure.Workflows.ServiceProvider.Extensions;
using Microsoft.Azure.Functions.Worker.Extensions.Abstractions;
using Microsoft.Azure.Workflows.WebJobs.Extension;

namespace logistics.fa
{
    public static class FileUnzipFA_MSI
    {
        [FunctionName("FileUnzipFA_MSI")]
        public static async Task<TaskStatus> Run(
            [WorkflowActionTrigger] WorkflowAction action,
            ILogger log)
        {
            var currentTaskStatus = new TaskStatus
            {
                CurrentTaskStatus = "Starting the file unzip process."
            };

            try
            {
                // Extracting parameters from the workflow action
                string sourceConnectionString = action.GetParameterValue<string>("SourceConnectionString");
                string destinationConnectionString = action.GetParameterValue<string>("DestinationConnectionString");
                string sourceContainerName = action.GetParameterValue<string>("SourceContainerName");
                string destinationContainerName = action.GetParameterValue<string>("DestinationContainerName");
                string sourceBlobName = action.GetParameterValue<string>("SourceBlobName");
                string destinationFolderName = action.GetParameterValue<string>("DestinationFolderName");
                string serviceBusConnectionString = action.GetParameterValue<string>("ServiceBusConnectionString");
                string topicName = action.GetParameterValue<string>("TopicName");
                string correlationId = action.GetParameterValue<string>("CorrelationId");
                string intId = action.GetParameterValue<string>("IntId");
                string eventType = action.GetParameterValue<string>("EventType");
                string zipFileName = action.GetParameterValue<string>("ZipFileName");

                await FileUnzipAndUploadToBlob(
                    sourceConnectionString, destinationConnectionString,
                    sourceContainerName, destinationContainerName,
                    sourceBlobName, destinationFolderName,
                    serviceBusConnectionString, topicName,
                    correlationId, intId, eventType, zipFileName);

                currentTaskStatus.CurrentTaskStatus = "Files unzipped and uploaded to destination blob successfully.";
            }
            catch (Exception ex)
            {
                currentTaskStatus.CurrentTaskStatus = $"Error: {ex.Message}";
            }

            return currentTaskStatus;
        }

        private static async Task FileUnzipAndUploadToBlob(
            string sourceConnectionString, string destinationConnectionString,
            string sourceContainerName, string destinationContainerName,
            string sourceBlobName, string destinationFolderName,
            string serviceBusConnectionString, string topicName,
            string correlationId, string intId, string eventType, string zipFileName)
        {
            var blobServiceClient = new BlobServiceClient(sourceConnectionString);
            var sourceBlobContainerClient = blobServiceClient.GetBlobContainerClient(sourceContainerName);

            if (!await sourceBlobContainerClient.ExistsAsync())
            {
                throw new DirectoryNotFoundException($"Source blob container '{sourceContainerName}' not found.");
            }

            var sourceBlobClient = sourceBlobContainerClient.GetBlobClient(sourceBlobName);

            if (!await sourceBlobClient.ExistsAsync())
            {
                throw new FileNotFoundException($"Source blob '{sourceBlobName}' not found.");
            }

            var downloadResponse = await sourceBlobClient.DownloadAsync();
            using (var memoryStream = new MemoryStream())
            {
                await downloadResponse.Value.Content.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
                {
                    var topicClient = new TopicClient(serviceBusConnectionString, topicName);

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/") || string.IsNullOrEmpty(entry.Name))
                        {
                            continue;
                        }

                        var destinationBlobName = Path.Combine(destinationFolderName, entry.Name);
                        if (destinationBlobName.Contains("\\"))
                        {
                            destinationBlobName = destinationBlobName.Replace("\\", "/");
                        }

                        var destinationBlobServiceClient = new BlobServiceClient(destinationConnectionString);
                        var destinationContainerClient = destinationBlobServiceClient.GetBlobContainerClient(destinationContainerName);
                        var destinationBlobClient = destinationContainerClient.GetBlobClient(destinationBlobName);

                        using (var entryStream = entry.Open())
                        {
                            await destinationBlobClient.UploadAsync(entryStream, true);
                        }

                        var transactionId = "TRANS" + Guid.NewGuid().ToString().ToUpper();
                        var message = new Message()
                        {
                            MessageId = Guid.NewGuid().ToString(),
                            Body = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                            {
                                IntId = intId,
                                TransactionId = transactionId,
                                CorrelationId = correlationId,
                                FileName = entry.Name,
                                ArchiveBlobFullPath = sourceBlobName,
                                ZipFileName = zipFileName,
                                EventType = eventType,
                                UnzipBlobFullPath = destinationBlobName
                            }))
                        };

                        message.UserProperties.Add("IntId", intId);
                        message.UserProperties.Add("TransactionId", transactionId);
                        message.UserProperties.Add("CorrelationId", correlationId);
                        message.UserProperties.Add("FileName", entry.Name);
                        message.UserProperties.Add("ArchiveBlobFullPath", sourceBlobName);
                        message.UserProperties.Add("ZipFileName", zipFileName);
                        message.UserProperties.Add("EventType", eventType);
                        message.UserProperties.Add("UnzipBlobFullPath", destinationBlobName);

                        await topicClient.SendAsync(message);
                    }

                    await topicClient.CloseAsync();
                }
            }
        }

        public class TaskStatus
        {
            public string CurrentTaskStatus { get; set; }
        }
    }
}