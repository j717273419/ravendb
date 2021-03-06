using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;

using Jint;
using Jint.Parser;

using Raven.Abstractions.Replication;
using Raven.Database.Storage;
using Raven.Imports.Newtonsoft.Json;
using Raven.Abstractions;
using Raven.Abstractions.Commands;
using Raven.Abstractions.Data;
using Raven.Abstractions.Database.Smuggler;
using Raven.Abstractions.Database.Smuggler.Common;
using Raven.Abstractions.Database.Smuggler.Database;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Json;
using Raven.Abstractions.Util;
using Raven.Bundles.Versioning.Triggers;
using Raven.Client.Util;
using Raven.Database.Actions;
using Raven.Database.Bundles.SqlReplication;
using Raven.Database.Extensions;
using Raven.Database.Server.WebApi.Attributes;
using Raven.Database.Smuggler.Embedded;
using Raven.Json.Linq;
using Raven.Smuggler.Database;
using Raven.Smuggler.Database.Streams;

namespace Raven.Database.Server.Controllers
{
    public class StudioTasksController : BaseDatabaseApiController
    {
        const int CsvImportBatchSize = 512;


        [HttpGet]
        [RavenRoute("studio-tasks/config")]
        [RavenRoute("databases/{databaseName}/studio-tasks/config")]
        public HttpResponseMessage StudioConfig()
        {
            var documentsController = new DocumentsController();
            documentsController.InitializeFrom(this);
            var httpResponseMessage = documentsController.DocGet("Raven/StudioConfig");
            if (httpResponseMessage.StatusCode != HttpStatusCode.NotFound)
                return httpResponseMessage.WithNoCache();

            documentsController.SetResource(DatabasesLandlord.SystemDatabase);
            return documentsController.DocGet("Raven/StudioConfig").WithNoCache();
        }
        [HttpGet]
        [RavenRoute("studio-tasks/server-configs")]
        public HttpResponseMessage GerServerConfigs()
        {
            var userInfo = GetUserInfo();
            var serverConfigs = new ServerConfigs
            {
                IsGlobalAdmin = userInfo.IsAdminGlobal,
                CanReadWriteSettings = userInfo.IsAdminGlobal ||
                                       (userInfo.ReadWriteDatabases != null &&
                                        userInfo.ReadWriteDatabases.Any(x => x.Equals(Constants.SystemDatabase, StringComparison.InvariantCultureIgnoreCase))),
                CanReadSettings = userInfo.IsAdminGlobal ||
                                  (userInfo.ReadOnlyDatabases != null &&
                                   userInfo.ReadOnlyDatabases.Any(x => x.Equals(Constants.SystemDatabase, StringComparison.InvariantCultureIgnoreCase))),
                CanExposeConfigOverTheWire = CanExposeConfigOverTheWire()
            };

            return GetMessageWithObject(serverConfigs);
        }

        private class ServerConfigs
        {
            public bool IsGlobalAdmin { get; set; }
            public bool CanReadWriteSettings { get; set; }
            public bool CanReadSettings { get; set; }
            public bool CanExposeConfigOverTheWire { get; set; }
        }

        [HttpPost]
        [RavenRoute("studio-tasks/validateCustomFunctions")]
        [RavenRoute("databases/{databaseName}/studio-tasks/validateCustomFunctions")]
        public async Task<HttpResponseMessage> ValidateCustomFunctions()
        {
            try
            {
                var document = await ReadJsonAsync().ConfigureAwait(false);
                ValidateCustomFunctions(document);
                return GetEmptyMessage();
            }
            catch (ParserException e)
            {
                return GetMessageWithString(e.Message, HttpStatusCode.BadRequest);
            }
        }

        private void ValidateCustomFunctions(RavenJObject document)
        {
            var engine = new Engine(cfg =>
            {
                cfg.AllowDebuggerStatement();
                cfg.MaxStatements(1000);
                cfg.NullPropagation();
            });

            engine.Execute(string.Format(@"
var customFunctions = function() {{ 
    var exports = {{ }};
    {0};
    return exports;
}}();
for(var customFunction in customFunctions) {{
    this[customFunction] = customFunctions[customFunction];
}};", document.Value<string>("Functions")));

        }

        [HttpGet]
        [RavenRoute("studio-tasks/check-sufficient-diskspace")]
        [RavenRoute("databases/{databaseName}/studio-tasks/check-sufficient-diskspace")]
        public async Task<HttpResponseMessage> CheckSufficientDiskspaceBeforeImport(long fileSize)
        {
            string tempRoot = Path.GetPathRoot(Database.Configuration.Core.TempPath);
            var rootPathToDriveInfo = new Dictionary<string, DriveInfo>();
            DriveInfo.GetDrives().ForEach(drive => rootPathToDriveInfo[drive.RootDirectory.FullName] = drive);
            DriveInfo tempFolderDrive;
            if (!rootPathToDriveInfo.TryGetValue(tempRoot, out tempFolderDrive) ||
                tempFolderDrive.AvailableFreeSpace - (long)(tempFolderDrive.TotalSize * 0.1) < fileSize)
                throw new HttpResponseException(HttpStatusCode.BadRequest);

            return GetEmptyMessage();
        }

        [HttpPost]
        [RavenRoute("studio-tasks/import")]
        [RavenRoute("databases/{databaseName}/studio-tasks/import")]
        public async Task<HttpResponseMessage> ImportDatabase(int batchSize, bool includeExpiredDocuments, bool stripReplicationInformation,bool shouldDisableVersioningBundle, DatabaseItemType operateOnTypes, string filtersPipeDelimited, string transformScript)
        {
            if (!Request.Content.IsMimeMultipartContent())
            {
                throw new HttpResponseException(HttpStatusCode.UnsupportedMediaType);
            }

            string tempPath = Database.Configuration.Core.TempPath;
            var fullTempPath = tempPath + Constants.TempUploadsDirectoryName;
            if (File.Exists(fullTempPath))
                File.Delete(fullTempPath);
            if (Directory.Exists(fullTempPath) == false)
                Directory.CreateDirectory(fullTempPath);

            var streamProvider = new MultipartFileStreamProvider(fullTempPath);
            await Request.Content.ReadAsMultipartAsync(streamProvider).ConfigureAwait(false);
            var uploadedFilePath = streamProvider.FileData[0].LocalFileName;
            
            string fileName = null;
            var fileContent = streamProvider.Contents.SingleOrDefault();
            if (fileContent != null)
            {
                fileName = fileContent.Headers.ContentDisposition.FileName.Replace("\"", string.Empty);
            }

            var status = new ImportOperationStatus();
            var cts = new CancellationTokenSource();
            
            var task = Task.Run(async () =>
            {
                try
                {
                    using (var fileStream = File.Open(uploadedFilePath, FileMode.Open, FileAccess.Read))
                    {
                        var smugglerOptions = new DatabaseSmugglerOptions();
                        smugglerOptions.BatchSize = batchSize;
                        smugglerOptions.ShouldExcludeExpired = !includeExpiredDocuments;
                        smugglerOptions.StripReplicationInformation = stripReplicationInformation;
                        smugglerOptions.ShouldDisableVersioningBundle = shouldDisableVersioningBundle;
                        smugglerOptions.OperateOnTypes = operateOnTypes;
                        smugglerOptions.TransformScript = transformScript;

                        // Filters are passed in without the aid of the model binder. Instead, we pass in a list of FilterSettings using a string like this: pathHere;;;valueHere;;;true|||againPathHere;;;anotherValue;;;false
                        // Why? Because I don't see a way to pass a list of a values to a WebAPI method that accepts a file upload, outside of passing in a simple string value and parsing it ourselves.
                        if (filtersPipeDelimited != null)
                        {
                            smugglerOptions.Filters.AddRange(filtersPipeDelimited
                                .Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(f => f.Split(new[] { ";;;" }, StringSplitOptions.RemoveEmptyEntries))
                                .Select(o => new FilterSetting { Path = o[0], Values = new List<string> { o[1] }, ShouldMatch = bool.Parse(o[2]) }));
                        }

                        var smuggler = new DatabaseSmuggler(smugglerOptions, new DatabaseSmugglerStreamSource(fileStream), new DatabaseSmugglerEmbeddedDestination(Database));
                        smuggler.Notifications.OnProgress += (sender, message) => status.LastProgress = message;

                        await smuggler.ExecuteAsync(cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    status.Faulted = true;
                    status.State = RavenJObject.FromObject(new
                                                           {
                                                               Error = e.ToString()
                                                           });
                    if (cts.Token.IsCancellationRequested)
                    {
                        status.State = RavenJObject.FromObject(new { Error = "Task was cancelled"  });
                        cts.Token.ThrowIfCancellationRequested(); //needed for displaying the task status as canceled and not faulted
                    }

                    if (e is InvalidDataException)
                    {
                        status.ExceptionDetails = e.Message;
                    }
                    else if (e is JsonReaderException)
                    {
                        status.ExceptionDetails = "Failed to load JSON Data. Please make sure you are importing .ravendump file, exported by smuggler (aka database export). If you are importing a .ravnedump file then the file may be corrupted";
                    }
                    else if (e is OperationVetoedException && e.Message.Contains(VersioningPutTrigger.CreationOfHistoricalRevisionIsNotAllowed))
                    {
                        status.ExceptionDetails = "You are trying to import historical documents while the versioning bundle is enabled. " +
                                                  "The versioning bundle is enabled. You should disable versioning during import. " +
                                                  "Please mark the checkbox 'Disable versioning bundle during import' at Import Database: Advanced settings before importing";
                    }
                    else
                    {
                        status.ExceptionDetails = e.ToString();
                    }
                    throw;
                }
                finally
                {
                    status.Completed = true;
                    File.Delete(uploadedFilePath);
                }
            }, cts.Token);

            long id;
            Database.Tasks.AddTask(task, status, new TaskActions.PendingTaskDescription
            {
                StartTime = SystemTime.UtcNow,
                TaskType = TaskActions.PendingTaskType.ImportDatabase,
                Payload = fileName,
                
            }, out id, cts);

            return GetMessageWithObject(new
            {
                OperationId = id
            }, HttpStatusCode.Accepted);
        }

        public class ExportData
        {
            public string SmugglerOptions { get; set; }

            public string FileName { get; set; }
        }

        [HttpPost]
        [RavenRoute("studio-tasks/exportDatabase")]
        [RavenRoute("databases/{databaseName}/studio-tasks/exportDatabase")]
        public Task<HttpResponseMessage> ExportDatabase([FromBody]ExportData exportData)
        {
            var requestString = exportData.SmugglerOptions;
            DatabaseSmugglerOptions smugglerOptions;

            using (var jsonReader = new RavenJsonTextReader(new StringReader(requestString)))
            {
                var serializer = JsonExtensions.CreateDefaultJsonSerializer();
                smugglerOptions = (DatabaseSmugglerOptions)serializer.Deserialize(jsonReader, typeof(DatabaseSmugglerOptions));
            }

            var result = GetEmptyMessage();

            // create PushStreamContent object that will be called when the output stream will be ready.
            result.Content = new PushStreamContent(async (outputStream, content, arg3) =>
            {
                try
                {
                    var smuggler = new DatabaseSmuggler(smugglerOptions, new DatabaseSmugglerEmbeddedSource(Database), new DatabaseSmugglerStreamDestination(outputStream, leaveOpen: true));
                    await smuggler.ExecuteAsync().ConfigureAwait(false);
                }
                finally
                {
                    outputStream.Close();
                }
            });

            var fileName = string.IsNullOrEmpty(exportData.FileName) || (exportData.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ?
                string.Format("Dump of {0}, {1}", DatabaseName, DateTime.Now.ToString("yyyy-MM-dd HH-mm")) :
              exportData.FileName;

            result.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = fileName + ".ravendump"
            };

            return new CompletedTask<HttpResponseMessage>(result);
        }

        [HttpPost]
        [RavenRoute("studio-tasks/createSampleData")]
        [RavenRoute("databases/{databaseName}/studio-tasks/createSampleData")]
        public async Task<HttpResponseMessage> CreateSampleData()
        {
            var results = Database.Queries.Query(Constants.DocumentsByEntityNameIndex, new IndexQuery(), CancellationToken.None);
            if (results.Results.Count > 0)
            {
                return GetMessageWithString("You cannot create sample data in a database that already contains documents", HttpStatusCode.BadRequest);
            }

            using (var stream = typeof(StudioTasksController).Assembly.GetManifestResourceStream("Raven.Database.Server.Assets.EmbeddedData.Northwind.dump"))
            {
                var smuggler = new DatabaseSmuggler(new DatabaseSmugglerOptions
                {
                    OperateOnTypes = DatabaseItemType.Documents | DatabaseItemType.Indexes | DatabaseItemType.Transformers,
                    ShouldExcludeExpired = false
                },
                new DatabaseSmugglerStreamSource(stream),
                new DatabaseSmugglerEmbeddedDestination(Database));

                await smuggler.ExecuteAsync().ConfigureAwait(false);
            }

            return GetEmptyMessage();
        }

        [HttpPost]
        [RavenRoute("studio-tasks/simulate-sql-replication")]
        [RavenRoute("databases/{databaseName}/studio-tasks/simulate-sql-replication")]
        public async Task<HttpResponseMessage> SimulateSqlReplication()
        {
            var sqlSimulate = await ReadJsonObjectAsync<SimulateSqlReplicationResult>().ConfigureAwait(false);

            var task = Database.StartupTasks.OfType<SqlReplicationTask>().FirstOrDefault();
            if (task == null)
                return GetMessageWithObject(new
                {
                    Error = "SQL Replication bundle is not installed"
                }, HttpStatusCode.NotFound);
            
            try
            {
                Alert alert = null;
                var sqlReplication =
                    JsonConvert.DeserializeObject<SqlReplicationConfig>(sqlSimulate.SqlReplication);

                // string strDocumentId, SqlReplicationConfig sqlReplication, bool performRolledbackTransaction, out Alert alert, out Dictionary<string,object> parameters
                var results = task.SimulateSqlReplicationSqlQueries(sqlSimulate.DocumentId, sqlReplication, sqlSimulate.PerformRolledBackTransaction, out alert);
                return GetMessageWithObject(new {
                    Results = results,
                    LastAlert = alert
                });
            }
            catch (Exception ex)
            {
                    return GetMessageWithObject(new
                    {
                        Error = "Executeion failed",
                        Exception = ex
                    }, HttpStatusCode.BadRequest);
            }
        }

        [HttpGet]
        [RavenRoute("studio-tasks/test-sql-replication-connection")]
        [RavenRoute("databases/{databaseName}/studio-tasks/test-sql-replication-connection")]
        public Task<HttpResponseMessage> TestSqlReplicationConnection(string factoryName, string connectionString)
        {
            try
            {
                RelationalDatabaseWriter.TestConnection(factoryName, connectionString);
                return GetEmptyMessageAsTask(HttpStatusCode.NoContent);
            }
            catch (Exception ex)
            {
                return GetMessageWithObjectAsTask(new
                {
                    Error = "Connection failed",
                    Exception = ex
                }, HttpStatusCode.BadRequest);
            }
        }

        [HttpGet]
        [RavenRoute("studio-tasks/createSampleDataClass")]
        [RavenRoute("databases/{databaseName}/studio-tasks/createSampleDataClass")]
        public Task<HttpResponseMessage> CreateSampleDataClass()
        {
            using (var sampleData = typeof(StudioTasksController).Assembly.GetManifestResourceStream("Raven.Database.Server.Assets.EmbeddedData.NorthwindHelpData.cs"))
            {
                if (sampleData == null)
                    return GetEmptyMessageAsTask();
                   
                sampleData.Position = 0;
                using (var reader = new StreamReader(sampleData, Encoding.UTF8))
                {
                   var data = reader.ReadToEnd();
                   return GetMessageWithObjectAsTask(data);
                }
            }
        }

        [HttpGet]
        [RavenRoute("studio-tasks/get-sql-replication-stats")]
        [RavenRoute("databases/{databaseName}/studio-tasks/get-sql-replication-stats")]
        public HttpResponseMessage GetSQLReplicationStats(string sqlReplicationName)
        {
            var task = Database.StartupTasks.OfType<SqlReplicationTask>().FirstOrDefault();
            if (task == null)
                return GetMessageWithObject(new
                {
                    Error = "SQL Replication bundle is not installed"
                }, HttpStatusCode.NotFound);

            var matchingStats = task.Statistics.FirstOrDefault(x => x.Key == sqlReplicationName);

            if (matchingStats.Key != null)
            {
                return GetMessageWithObject(task.Statistics.FirstOrDefault(x => x.Key == sqlReplicationName));
            }
            return GetEmptyMessage(HttpStatusCode.NotFound);
        }

        [HttpPost]
        [RavenRoute("studio-tasks/reset-sql-replication")]
        [RavenRoute("databases/{databaseName}/studio-tasks/reset-sql-replication")]
        public Task<HttpResponseMessage> ResetSqlReplication(string sqlReplicationName)
        {
            var task = Database.StartupTasks.OfType<SqlReplicationTask>().FirstOrDefault();
            if (task == null)
                return GetMessageWithObjectAsTask(new
                {
                    Error = "SQL Replication bundle is not installed"
                }, HttpStatusCode.NotFound);
            SqlReplicationStatistics stats;
            task.Statistics.TryRemove(sqlReplicationName, out stats);
            var jsonDocument = Database.Documents.Get(SqlReplicationTask.RavenSqlReplicationStatus);
            if (jsonDocument != null)
            {
                var replicationStatus = jsonDocument.DataAsJson.JsonDeserialization<SqlReplicationStatus>();
                replicationStatus.LastReplicatedEtags.RemoveAll(x => x.Name == sqlReplicationName);
                
                Database.Documents.Put(SqlReplicationTask.RavenSqlReplicationStatus, null, RavenJObject.FromObject(replicationStatus), new RavenJObject(), null);
            }

            return GetEmptyMessageAsTask(HttpStatusCode.NoContent);
        }

        [HttpGet]
        [RavenRoute("studio-tasks/latest-server-build-version")]
        public HttpResponseMessage GetLatestServerBuildVersion(bool stableOnly = true, int min = 40000, int max = 44999)
        {
            var args = string.Format("stableOnly={0}&min={1}&max={2}", stableOnly, min, max);
            var request = (HttpWebRequest)WebRequest.Create("http://hibernatingrhinos.com/downloads/ravendb/latestVersion?" + args);
            try
            {
                request.Timeout = 5000;
                using (var response = request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    var result = new StreamReader(stream).ReadToEnd();
                    var parts = result.Split('-');
                    var build = int.Parse(parts[0]);
                    return GetMessageWithObject(new { LatestBuild = build });
                }
            }
            catch (Exception e)
            {
                return GetMessageWithObject(new { Exception = e.Message });
            }
        }

        [HttpGet]
        [RavenRoute("studio-tasks/new-encryption-key")]
        public HttpResponseMessage GetNewEncryption(string path = null)
        {
            RandomNumberGenerator randomNumberGenerator = new RNGCryptoServiceProvider();
            var byteStruct = new byte[Constants.DefaultGeneratedEncryptionKeyLength];
            randomNumberGenerator.GetBytes(byteStruct);
            var result = Convert.ToBase64String(byteStruct);

            HttpResponseMessage response = Request.CreateResponse(HttpStatusCode.OK, result);
            return response;
        }

        [HttpPost]
        [RavenRoute("studio-tasks/is-base-64-key")]
        public async Task<HttpResponseMessage> IsBase64Key(string path = null)
        {
            string message = null;
            try
            {
                //Request is of type HttpRequestMessage
                string keyObjectString = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
                NameValueCollection nvc = HttpUtility.ParseQueryString(keyObjectString);
                var key = nvc["key"];

                //Convert base64-encoded hash value into a byte array.
                //ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                Convert.FromBase64String(key);
            }
            catch (Exception)
            {
                message = "The key must be in Base64 encoding format!";
            }

            HttpResponseMessage response = Request.CreateResponse((message == null) ? HttpStatusCode.OK : HttpStatusCode.BadRequest, message);
            return response;
        }

        private Task FlushBatch(IEnumerable<RavenJObject> batch)
        {
            var commands = (from doc in batch
                            let metadata = doc.Value<RavenJObject>("@metadata")
                            let removal = doc.Remove("@metadata")
                            select new PutCommandData
                            {
                                Metadata = metadata,
                                Document = doc,
                                Key = metadata.Value<string>("@id"),
                            }).ToArray();

            Database.Batch(commands, CancellationToken.None);
            return new CompletedTask();
        }

        [HttpGet]
        [RavenRoute("studio-tasks/resolveMerge")]
        [RavenRoute("databases/{databaseName}/studio-tasks/resolveMerge")]
        public Task<HttpResponseMessage> ResolveMerge(string documentId)
        {
            int nextPage = 0;
            var docs = Database.Documents.GetDocumentsWithIdStartingWith(documentId + "/conflicts", null, null, 0, 1024, CancellationToken.None, ref nextPage);
            var conflictsResolver = new ConflictsResolver(docs.Values<RavenJObject>());
            return GetMessageWithObjectAsTask(conflictsResolver.Resolve());
        }

        [HttpPost]
        [RavenRoute("studio-tasks/loadCsvFile")]
        [RavenRoute("databases/{databaseName}/studio-tasks/loadCsvFile")]
        public async Task<HttpResponseMessage> LoadCsvFile()
        {

            if (!Request.Content.IsMimeMultipartContent())
                throw new Exception(); // divided by zero

            var provider = new MultipartMemoryStreamProvider();
            await Request.Content.ReadAsMultipartAsync(provider).ConfigureAwait(false);

            foreach (var file in provider.Contents)
            {
                var filename = file.Headers.ContentDisposition.FileName.Trim('\"');

                var stream = await file.ReadAsStreamAsync().ConfigureAwait(false);

                using (var csvReader = new TextFieldParser(stream))
                {
                    csvReader.SetDelimiters(",");
                    var headers = csvReader.ReadFields();
                    var entity =
                        Inflector.Pluralize(CSharpClassName.ConvertToValidClassName(Path.GetFileNameWithoutExtension(filename)));
                    if (entity.Length > 0 && char.IsLower(entity[0]))
                        entity = char.ToUpper(entity[0]) + entity.Substring(1);

                    var totalCount = 0;
                    var batch = new List<RavenJObject>();
                    var columns = headers.Where(x => x.StartsWith("@") == false).ToArray();

                    batch.Clear();
                    while (csvReader.EndOfData == false)
                    {
                        var record = csvReader.ReadFields();
                        var document = new RavenJObject();
                        string id = null;
                        RavenJObject metadata = null;
                        for (int index = 0; index < columns.Length; index++)
                        {
                            var column = columns[index];
                            if (string.IsNullOrEmpty(column))
                                continue;

                            if (string.Equals("@id", column, StringComparison.OrdinalIgnoreCase))
                            {
                                id = record[index];
                            }
                            else if (string.Equals(Constants.RavenEntityName, column, StringComparison.OrdinalIgnoreCase))
                            {
                                metadata = metadata ?? new RavenJObject();
                                metadata[Constants.RavenEntityName] = record[index];
                                id = id ?? record[index] + "/";
                            }
                            else if (string.Equals(Constants.RavenClrType, column, StringComparison.OrdinalIgnoreCase))
                            {
                                metadata = metadata ?? new RavenJObject();
                                metadata[Constants.RavenClrType] = record[index];
                                id = id ?? record[index] + "/";
                            }
                            else
                            {
                                document[column] = SetValueInDocument(record[index]);
                            }
                        }

                        metadata = metadata ?? new RavenJObject { { "Raven-Entity-Name", entity } };
                        document.Add("@metadata", metadata);
                        metadata.Add("@id", id ?? Guid.NewGuid().ToString());

                        batch.Add(document);
                        totalCount++;

                        if (batch.Count >= CsvImportBatchSize)
                        {
                            await FlushBatch(batch).ConfigureAwait(false);
                            batch.Clear();
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await FlushBatch(batch).ConfigureAwait(false);
                    }
                }

            }

            return GetEmptyMessage();
        }

        [HttpGet]
        [RavenRoute("studio-tasks/collection/counts")]
        [RavenRoute("databases/{databaseName}/studio-tasks/collection/counts")]
        public Task<HttpResponseMessage> CollectionCount()
        {
            var fromDate = GetQueryStringValue("fromDate");

            DateTime date;
            if (string.IsNullOrEmpty(fromDate) || DateTime.TryParse(fromDate, out date) == false)
                date = DateTime.MinValue;

            var collections = Database
                .LastCollectionEtags
                .GetLastChangedCollections(date.ToUniversalTime());

            var results = new ConcurrentBag<CollectionNameAndCount>();

            Parallel.ForEach(collections, collectionName =>
            {
                var result = Database
                    .Queries
                    .Query(Constants.DocumentsByEntityNameIndex, new IndexQuery { Query = "Tag:" + collectionName, PageSize = 0 }, CancellationToken.None);

                results.Add(new CollectionNameAndCount { CollectionName = collectionName, Count = result.TotalResults });
            });

            return GetMessageWithObjectAsTask(results);
        }

        [HttpPost]
        [RavenRoute("studio-tasks/replication/conflicts/resolve")]
        [RavenRoute("databases/{databaseName}/studio-tasks/replication/conflicts/resolve")]
        public Task<HttpResponseMessage> ResolveAllConflicts()
        {
            var resolutionAsString = GetQueryStringValue("resolution");
            StraightforwardConflictResolution resolution;
            if (Enum.TryParse(resolutionAsString, true, out resolution) == false || resolution == StraightforwardConflictResolution.None)
                return GetMessageWithStringAsTask("Invalid conflict resolution.", HttpStatusCode.BadRequest);

            if (Database.IndexDefinitionStorage.Contains("Raven/ConflictDocuments") == false)
                return GetMessageWithStringAsTask("Raven/ConflictDocuments index does not exist.", HttpStatusCode.BadRequest);

            var cts = new CancellationTokenSource();

            var task = Task.Factory.StartNew(() => Database.TransactionalStorage.Batch(accessor =>
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, Database.WorkContext.CancellationToken))
                {
                    var transactionalStorageId = Database.TransactionalStorage.Id.ToString();
                    bool stale;
                    foreach (var documentId in Database.Queries.QueryDocumentIds("Raven/ConflictDocuments", new IndexQuery { PageSize = int.MaxValue }, linked, out stale))
                    {
                        var conflicts = accessor
                            .Documents
                            .GetDocumentsWithIdStartingWith(documentId, 0, Int32.MaxValue, null)
                            .Where(x => x.Key.Contains("/conflicts/"))
                            .ToList();

                        KeyValuePair<JsonDocument, DateTime> local;
                        KeyValuePair<JsonDocument, DateTime> remote;
                        GetConflictDocuments(conflicts, accessor, documentId, transactionalStorageId, out local, out remote);

                        var documentToSave = GetDocumentToSave(resolution, local, remote);
                        if (documentToSave == null)
                            continue;

                        documentToSave.Metadata.Remove(Constants.RavenReplicationConflictDocument);

                        if (documentToSave.Metadata.Value<bool>(Constants.RavenDeleteMarker))
                            Database.Documents.Delete(documentId, null, null);
                        else
                            Database.Documents.Put(documentId, null, documentToSave.DataAsJson, documentToSave.Metadata, null);
                    }
                }
            }));

            long id;
            Database.Tasks.AddTask(task, new TaskBasedOperationState(task), new TaskActions.PendingTaskDescription
                                                                            {
                                                                                StartTime = SystemTime.UtcNow,
                                                                                TaskType = TaskActions.PendingTaskType.BulkInsert,
                                                                            }, out id, cts);

            return GetMessageWithObjectAsTask(new
            {
                OperationId = id
            }, HttpStatusCode.Accepted);
        }

        [HttpPost]
        [RavenRoute("studio-tasks/validateExportOptions")]
        [RavenRoute("databases/{databaseName}/studio-tasks/validateExportOptions")]
        public HttpResponseMessage ValidateExportOptions([FromBody] DatabaseSmugglerOptions smugglerOptions)
        {
            try
            {
                new SmugglerJintHelper().Initialize(smugglerOptions);
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Incorrect transform script", e);
            }

            return GetEmptyMessage(HttpStatusCode.NoContent);
        }

        private static void GetConflictDocuments(IEnumerable<JsonDocument> conflicts, IStorageActionsAccessor actions, string documentId, string transactionalStorageId, out KeyValuePair<JsonDocument, DateTime> local, out KeyValuePair<JsonDocument, DateTime> remote)
        {
            DateTime localModified = DateTime.MinValue, remoteModified = DateTime.MinValue;
            JsonDocument localDocument = null, newestRemote = null;
            foreach (var conflict in conflicts)
            {
                var lastModified = conflict.LastModified.HasValue ? conflict.LastModified.Value : DateTime.MinValue;
                var replicationSource = conflict.Metadata.Value<string>(Constants.RavenReplicationSource);

                if (string.Equals(replicationSource, transactionalStorageId, StringComparison.OrdinalIgnoreCase))
                {
                    localModified = lastModified;
                    localDocument = conflict;
                    continue;
                }

                if (lastModified <= remoteModified)
                    continue;

                newestRemote = conflict;
                remoteModified = lastModified;
            }
            
            local = new KeyValuePair<JsonDocument, DateTime>(localDocument, localModified);
            remote = new KeyValuePair<JsonDocument, DateTime>(newestRemote, remoteModified);
        }

        private static JsonDocument GetDocumentToSave(StraightforwardConflictResolution resolution, KeyValuePair<JsonDocument, DateTime> local, KeyValuePair<JsonDocument, DateTime> remote)
        {
            if (local.Key == null && remote.Key == null) 
                return null;

            if (local.Key == null) 
                return remote.Key;

            if (remote.Key == null) 
                return local.Key;

            JsonDocument documentToSave;
            switch (resolution)
            {
                case StraightforwardConflictResolution.ResolveToLatest:
                    documentToSave = local.Value >= remote.Value ? local.Key : remote.Key;
                    break;
                case StraightforwardConflictResolution.ResolveToLocal:
                    documentToSave = local.Key;
                    break;
                case StraightforwardConflictResolution.ResolveToRemote:
                    documentToSave = remote.Key;
                    break;
                default:
                    throw new NotSupportedException(resolution.ToString());
            }

            return documentToSave;
        }

        private static RavenJToken SetValueInDocument(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var ch = value[0];
            if (ch == '[' || ch == '{')
            {
                try
                {
                    return RavenJToken.Parse(value);
                }
                catch (Exception)
                {
                    // ignoring failure to parse, will proceed to insert as a string value
                }
            }
            else if (char.IsDigit(ch) || ch == '-' || ch == '.')
            {
                // maybe it is a number?
                long longResult;
                if (long.TryParse(value, out longResult))
                {
                    return longResult;
                }

                decimal decimalResult;
                if (decimal.TryParse(value, out decimalResult))
                {
                    return decimalResult;
                }
            }
            else if (ch == '"' && value.Length > 1 && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private class ImportOperationStatus : IOperationState
        {
            public bool Completed { get; set; }
            public string LastProgress { get; set; }
            public string ExceptionDetails { get; set; }
            public bool Faulted { get; set; }
            public RavenJToken State { get; set; }
        }

        private class CollectionNameAndCount
        {
            public string CollectionName { get; set; }

            public int Count { get; set; }
        }
    }
}

