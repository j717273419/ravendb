﻿using System;
using Jurassic.Library;
using Raven.Client;
using Raven.Client.Documents.Operations;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Voron.Exceptions;

namespace Raven.Server.Documents.Patch
{
    public class PatchDocumentCommand : TransactionOperationsMerger.MergedTransactionCommand, IDisposable
    {
        private readonly string _id;
        private readonly LazyStringValue _expectedChangeVector;
        private readonly bool _skipPatchIfChangeVectorMismatch;

        private readonly JsonOperationContext _externalContext;

        private readonly DocumentDatabase _database;
        private readonly bool _isTest;

        private ScriptRunner.SingleRun _run;
        private readonly ScriptRunner.SingleRun _runIfMissing;
        private ScriptRunner.ReturnRun _returnRun;
        private ScriptRunner.ReturnRun _returnRunIfMissing;

        public PatchDocumentCommand(
            JsonOperationContext context, 
            string id, 
            LazyStringValue expectedChangeVector, 
            bool skipPatchIfChangeVectorMismatch, 
            PatchRequest run,
            PatchRequest runIfMissing,
            DocumentDatabase database, 
            bool isTest)
        {
            _externalContext = context;
            _returnRun = database.Scripts.GetScriptRunner(run,out _run);
            _returnRunIfMissing = database.Scripts.GetScriptRunner(runIfMissing, out _runIfMissing);
            _id = id;
            _expectedChangeVector = expectedChangeVector;
            _skipPatchIfChangeVectorMismatch = skipPatchIfChangeVectorMismatch;
            _database = database;
            _isTest = isTest;
        }

        public PatchResult PatchResult { get; private set; }

        public override int Execute(DocumentsOperationContext context)
        {
            var originalDocument = _database.DocumentsStorage.Get(context, _id);

            if (_expectedChangeVector != null)
            {
                if (originalDocument == null)
                {
                    if (_skipPatchIfChangeVectorMismatch)
                    {
                        PatchResult = new PatchResult
                        {
                            Status = PatchStatus.Skipped
                        };
                        return 1;
                    }

                    throw new ConcurrencyException($"Could not patch document '{_id}' because non current change vector was used")
                    {
                        ActualChangeVector = null,
                        ExpectedChangeVector = _expectedChangeVector
                    };
                }

                if (originalDocument.ChangeVector.CompareTo(_expectedChangeVector) != 0)
                {
                    if (_skipPatchIfChangeVectorMismatch)
                    {
                        PatchResult = new PatchResult
                        {
                            Status = PatchStatus.Skipped
                        };
                        return 1;
                    }

                    throw new ConcurrencyException($"Could not patch document '{_id}' because non current change vector was used")
                    {
                        ActualChangeVector = originalDocument.ChangeVector,
                        ExpectedChangeVector = _expectedChangeVector
                    };
                }
            }

            if (originalDocument == null && _runIfMissing == null)
            {
                PatchResult = new PatchResult
                {
                    Status = PatchStatus.DocumentDoesNotExist
                };
                return 1;
            }

            object documentInstance = originalDocument;

            if (originalDocument == null)
            {
                _run = _runIfMissing;
                documentInstance = _runIfMissing.CreateEmptyObject();
            }
            var scriptResult = _run.Run(context, "execute", new[] {documentInstance,});

            var modifiedDocument = scriptResult.TranslateFromJurrasic<BlittableJsonReaderObject>(_externalContext, 
                BlittableJsonDocumentBuilder.UsageMode.ToDisk);

            var result = new PatchResult
            {
                Status = PatchStatus.NotModified,
                OriginalDocument = _isTest == false ? originalDocument?.Data : originalDocument?.Data?.Clone(_externalContext),
                ModifiedDocument = modifiedDocument
            };

            if (modifiedDocument == null)
            {
                result.Status = PatchStatus.Skipped;
                PatchResult = result;

                return 1;
            }

            DocumentsStorage.PutOperationResults? putResult = null;

            if (originalDocument == null)
            {
                if (_isTest == false)
                    putResult = _database.DocumentsStorage.Put(context, _id, null, modifiedDocument);

                result.Status = PatchStatus.Created;
            }
            else if (DocumentCompare.IsEqualTo(originalDocument.Data, modifiedDocument, tryMergeAttachmentsConflict: true) == DocumentCompareResult.NotEqual)
            {
                if (_isTest == false)
                    putResult = _database.DocumentsStorage.Put(context, originalDocument.Id,
                        originalDocument.ChangeVector, modifiedDocument, null, null, originalDocument.Flags);

                result.Status = PatchStatus.Patched;
            }

            if (putResult != null)
            {
                result.ChangeVector = putResult.Value.ChangeVector;
                result.Collection = putResult.Value.Collection.Name;
            }

            PatchResult = result;
            return 1;
        }

        public void Dispose()
        {
            _returnRun.Dispose();
            _returnRunIfMissing.Dispose();
        }
    }
}
