﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Raven.Client;
using Raven.Client.Documents.Changes;
using Raven.Server.Documents.Replication;
using Raven.Client.Exceptions.Documents;
using Raven.Client.ServerWide.Revisions;
using Raven.Server.Config;
using Raven.Server.Documents.Revisions;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Voron;
using Voron.Data.Fixed;
using Voron.Data.Tables;
using Voron.Impl;
using Sparrow;
using Sparrow.Binary;
using Sparrow.Logging;
using Voron.Data;
using Voron.Exceptions;

namespace Raven.Server.Documents
{
    public unsafe class DocumentsStorage : IDisposable
    {
        private static readonly Slice DocsSlice;
        private static readonly Slice CollectionEtagsSlice;
        private static readonly Slice AllDocsEtagsSlice;
        private static readonly Slice TombstonesSlice;
        private static readonly Slice CollectionsSlice;
        private static readonly Slice LastReplicatedEtagsSlice;
        private static readonly Slice EtagsSlice;
        private static readonly Slice LastEtagSlice;
        private static readonly Slice GlobalTreeSlice;
        private static readonly Slice GlobalChangeVectorSlice;

        private static readonly Slice AllTombstonesEtagsSlice;
        private static readonly Slice TombstonesPrefix;
        private static readonly Slice DeletedEtagsSlice;

        public static readonly TableSchema DocsSchema = new TableSchema
        {
            TableType = (byte)TableType.Documents
        };

        public static readonly TableSchema TombstonesSchema = new TableSchema();
        private static readonly TableSchema CollectionsSchema = new TableSchema();

        private readonly DocumentDatabase _documentDatabase;

        private Dictionary<string, CollectionName> _collectionsCache;

        internal enum TombstoneTable
        {
            LowerId = 0,
            Etag = 1,
            DeletedEtag = 2,
            TransactionMarker = 3,
            Type = 4,
            Collection = 5,
            Flags = 6,
            ChangeVector = 7,
            LastModified = 8
        }

        public enum DocumentsTable
        {
            LowerId = 0,
            Etag = 1,
            Id = 2, // format of lazy string id is detailed in GetLowerIdSliceAndStorageKey
            Data = 3,
            ChangeVector = 4,
            LastModified = 5,
            Flags = 6,
            TransactionMarker = 7
        }

        static DocumentsStorage()
        {
            Slice.From(StorageEnvironment.LabelsContext, "AllTombstonesEtags", ByteStringType.Immutable, out AllTombstonesEtagsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "Etags", ByteStringType.Immutable, out EtagsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "LastEtag", ByteStringType.Immutable, out LastEtagSlice);
            Slice.From(StorageEnvironment.LabelsContext, "Docs", ByteStringType.Immutable, out DocsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "CollectionEtags", ByteStringType.Immutable, out CollectionEtagsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "AllDocsEtags", ByteStringType.Immutable, out AllDocsEtagsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "Tombstones", ByteStringType.Immutable, out TombstonesSlice);
            Slice.From(StorageEnvironment.LabelsContext, "Collections", ByteStringType.Immutable, out CollectionsSlice);
            Slice.From(StorageEnvironment.LabelsContext, CollectionName.GetTablePrefix(CollectionTableType.Tombstones), ByteStringType.Immutable, out TombstonesPrefix);
            Slice.From(StorageEnvironment.LabelsContext, "DeletedEtags", ByteStringType.Immutable, out DeletedEtagsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "LastReplicatedEtags", ByteStringType.Immutable, out LastReplicatedEtagsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "GlobalTree", ByteStringType.Immutable, out GlobalTreeSlice);
            Slice.From(StorageEnvironment.LabelsContext, "GlobalChangeVector", ByteStringType.Immutable, out GlobalChangeVectorSlice);
            /*
            Collection schema is:
            full name
            collections are never deleted from the collections table
            */
            CollectionsSchema.DefineKey(new TableSchema.SchemaIndexDef
            {
                StartIndex = 0,
                Count = 1,
                IsGlobal = false
            });

            DocsSchema.DefineKey(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)DocumentsTable.LowerId,
                Count = 1,
                IsGlobal = true,
                Name = DocsSlice
            });
            DocsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)DocumentsTable.Etag,
                IsGlobal = false,
                Name = CollectionEtagsSlice
            });
            DocsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)DocumentsTable.Etag,
                IsGlobal = true,
                Name = AllDocsEtagsSlice
            });

            TombstonesSchema.DefineKey(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)TombstoneTable.LowerId,
                Count = 1,
                IsGlobal = true,
                Name = TombstonesSlice
            });
            TombstonesSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)TombstoneTable.Etag,
                IsGlobal = false,
                Name = CollectionEtagsSlice
            });
            TombstonesSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)TombstoneTable.Etag,
                IsGlobal = true,
                Name = AllTombstonesEtagsSlice
            });
            TombstonesSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)TombstoneTable.DeletedEtag,
                IsGlobal = false,
                Name = DeletedEtagsSlice
            });
        }

        private readonly Logger _logger;
        private readonly string _name;

        // this is only modified by write transactions under lock
        // no need to use thread safe ops
        private long _lastEtag;

        public DocumentsContextPool ContextPool;

        public DocumentsStorage(DocumentDatabase documentDatabase)
        {
            _documentDatabase = documentDatabase;
            _name = _documentDatabase.Name;
            _logger = LoggingSource.Instance.GetLogger<DocumentsStorage>(documentDatabase.Name);
        }

        public StorageEnvironment Environment { get; private set; }

        public RevisionsStorage RevisionsStorage;
        public ConflictsStorage ConflictsStorage;
        public AttachmentsStorage AttachmentsStorage;
        public IdentitiesStorage Identities;
        public DocumentPutAction DocumentPut;

        public void Dispose()
        {
            var exceptionAggregator = new ExceptionAggregator(_logger, $"Could not dispose {nameof(DocumentsStorage)}");

            exceptionAggregator.Execute(() =>
            {
                ContextPool?.Dispose();
                ContextPool = null;
            });

            exceptionAggregator.Execute(() =>
            {
                Environment?.Dispose();
                Environment = null;
            });

            exceptionAggregator.ThrowIfNeeded();
        }

        public void Initialize(bool generateNewDatabaseId = false)
        {
            if (_logger.IsInfoEnabled)
            {
                _logger.Info
                ("Starting to open document storage for " + (_documentDatabase.Configuration.Core.RunInMemory
                     ? "<memory>"
                     : _documentDatabase.Configuration.Core.DataDirectory.FullPath));
            }


            var options = GetStorageEnvironmentOptionsFromConfiguration(_documentDatabase.Configuration, _documentDatabase.IoChanges, _documentDatabase.CatastrophicFailureNotification);

            options.OnNonDurableFileSystemError += _documentDatabase.HandleNonDurableFileSystemError;
            options.OnRecoveryError += _documentDatabase.HandleOnRecoveryError;

            options.GenerateNewDatabaseId = generateNewDatabaseId;
            options.CompressTxAboveSizeInBytes = _documentDatabase.Configuration.Storage.CompressTxAboveSize.GetValue(SizeUnit.Bytes);
            options.ForceUsing32BitsPager = _documentDatabase.Configuration.Storage.ForceUsing32BitsPager;
            options.TimeToSyncAfterFlashInSec = (int)_documentDatabase.Configuration.Storage.TimeToSyncAfterFlash.AsTimeSpan.TotalSeconds;
            options.NumOfConcurrentSyncsPerPhysDrive = _documentDatabase.Configuration.Storage.NumberOfConcurrentSyncsPerPhysicalDrive;
            Sodium.CloneKey(out options.MasterKey, _documentDatabase.MasterKey);

            try
            {
                Initialize(options);
            }
            catch (Exception)
            {
                options.Dispose();
                throw;
            }
        }

        public static StorageEnvironmentOptions GetStorageEnvironmentOptionsFromConfiguration(RavenConfiguration config, IoChangesNotifications ioChanges, CatastrophicFailureNotification catastrophicFailureNotification)
        {
            var basePath = config.Core.DataDirectory;

            var tempPath = config.Storage.TempPath != null
                ? config.Storage.TempPath.FullPath
                : basePath.Combine("Scratch").FullPath;

            var journalPath = basePath.Combine("Journal").FullPath;

            if (config.Core.RunInMemory)
                return StorageEnvironmentOptions.CreateMemoryOnly(
                    config.Core.DataDirectory.FullPath,
                    config.Storage.TempPath?.FullPath,
                    ioChanges,
                    catastrophicFailureNotification);

            return StorageEnvironmentOptions.ForPath(
                config.Core.DataDirectory.FullPath,
                tempPath,
                journalPath,
                ioChanges,
                catastrophicFailureNotification
            );
        }

        public void Initialize(StorageEnvironmentOptions options)
        {
            options.SchemaVersion = Constants.Schemas.DocumentsVersion;
            try
            {
                Environment = new StorageEnvironment(options);
                ContextPool = new DocumentsContextPool(_documentDatabase);

                using (var tx = Environment.WriteTransaction())
                {
                    NewPageAllocator.MaybePrefetchSections(
                        tx.LowLevelTransaction.RootObjects,
                        tx.LowLevelTransaction);

                    tx.CreateTree(DocsSlice);
                    tx.CreateTree(LastReplicatedEtagsSlice);
                    tx.CreateTree(GlobalTreeSlice);

                    CollectionsSchema.Create(tx, CollectionsSlice, 32);

                    RevisionsStorage = new RevisionsStorage(_documentDatabase, tx);
                    Identities = new IdentitiesStorage(_documentDatabase, tx);
                    ConflictsStorage = new ConflictsStorage(_documentDatabase, tx);
                    AttachmentsStorage = new AttachmentsStorage(_documentDatabase, tx);
                    DocumentPut = new DocumentPutAction(this, _documentDatabase);

                    _lastEtag = ReadLastEtag(tx);
                    _collectionsCache = ReadCollections(tx);

                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                if (_logger.IsOperationsEnabled)
                    _logger.Operations("Could not open server store for " + _name, e);

                Dispose();
                options.Dispose();
                throw;
            }
        }

        private static void AssertTransaction(DocumentsOperationContext context)
        {
            if (context.Transaction == null) //precaution
                throw new InvalidOperationException("No active transaction found in the context, and at least read transaction is needed");
        }

        public static string GetDatabaseChangeVector(DocumentsOperationContext context)
        {
            AssertTransaction(context);
            var tree = context.Transaction.InnerTransaction.ReadTree(GlobalTreeSlice);
            var val = tree.Read(GlobalChangeVectorSlice);
            if (val == null)
            {
                return string.Empty;
            }
            return Encodings.Utf8.GetString(val.Reader.Base, val.Reader.Length);
        }

        public string GetNewChangeVector(DocumentsOperationContext context, long newEtag)
        {
            var changeVector = GetDatabaseChangeVector(context);
            if (string.IsNullOrEmpty(changeVector))
                return ChangeVectorUtils.NewChangeVector(_documentDatabase.ServerStore.NodeTag, newEtag, _documentDatabase.DbId);

            ChangeVectorUtils.TryUpdateChangeVector(_documentDatabase.ServerStore.NodeTag, Environment.DbId, newEtag, ref changeVector);
            return changeVector;
        }

        public static void SetDatabaseChangeVector(DocumentsOperationContext context, string changeVector)
        {
            var tree = context.Transaction.InnerTransaction.ReadTree(GlobalTreeSlice);
            using (Slice.From(context.Allocator, changeVector, out var slice))
            {
                tree.Add(GlobalChangeVectorSlice, slice);
            }
        }

        public static long ReadLastDocumentEtag(Transaction tx)
        {
            return ReadLastEtagFrom(tx, AllDocsEtagsSlice);
        }

        public static long ReadLastTombstoneEtag(Transaction tx)
        {
            return ReadLastEtagFrom(tx, AllTombstonesEtagsSlice);
        }

        public static long ReadLastConflictsEtag(Transaction tx)
        {
            return ReadLastEtagFrom(tx, ConflictsStorage.AllConflictedDocsEtagsSlice);
        }

        public static long ReadLastRevisionsEtag(Transaction tx)
        {
            return ReadLastEtagFrom(tx, RevisionsStorage.AllRevisionsEtagsSlice);
        }

        public static long ReadLastAttachmentsEtag(Transaction tx)
        {
            return ReadLastEtagFrom(tx, AttachmentsStorage.AttachmentsEtagSlice);
        }

        private static long ReadLastEtagFrom(Transaction tx, Slice name)
        {
            using (var fst = new FixedSizeTree(tx.LowLevelTransaction,
                tx.LowLevelTransaction.RootObjects,
                name, sizeof(long),
                clone: false))
            {
                using (var it = fst.Iterate())
                {
                    if (it.SeekToLast())
                        return it.CurrentKey;
                }
            }

            return 0;
        }

        public static long ReadLastEtag(Transaction tx)
        {
            var tree = tx.CreateTree(EtagsSlice);
            var readResult = tree.Read(LastEtagSlice);
            long lastEtag = 0;
            if (readResult != null)
                lastEtag = readResult.Reader.ReadLittleEndianInt64();

            var lastDocumentEtag = ReadLastDocumentEtag(tx);
            if (lastDocumentEtag > lastEtag)
                lastEtag = lastDocumentEtag;

            var lastTombstoneEtag = ReadLastTombstoneEtag(tx);
            if (lastTombstoneEtag > lastEtag)
                lastEtag = lastTombstoneEtag;

            var lastConflictEtag = ReadLastConflictsEtag(tx);
            if (lastConflictEtag > lastEtag)
                lastEtag = lastConflictEtag;

            var lastRevisionsEtag = ReadLastRevisionsEtag(tx);
            if (lastRevisionsEtag > lastEtag)
                lastEtag = lastRevisionsEtag;

            var lastAttachmentEtag = ReadLastAttachmentsEtag(tx);
            if (lastAttachmentEtag > lastEtag)
                lastEtag = lastAttachmentEtag;

            return lastEtag;
        }

        public IEnumerable<Document> GetDocumentsStartingWith(DocumentsOperationContext context, string idPrefix, string matches, string exclude, string startAfterId,
            int start, int take)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);

            var isStartAfter = string.IsNullOrWhiteSpace(startAfterId) == false;

            var startAfterSlice = Slices.Empty;
            using (DocumentIdWorker.GetSliceFromId(context, idPrefix, out Slice prefixSlice))
            using (isStartAfter ? (IDisposable)DocumentIdWorker.GetSliceFromId(context, startAfterId, out startAfterSlice) : null)
            {
                foreach (var result in table.SeekByPrimaryKeyPrefix(prefixSlice, startAfterSlice, 0))
                {
                    var document = TableValueToDocument(context, ref result.Value.Reader);
                    string documentId = document.Id;
                    if (documentId.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) == false)
                        break;

                    var idTest = documentId.Substring(idPrefix.Length);
                    if (WildcardMatcher.Matches(matches, idTest) == false || WildcardMatcher.MatchesExclusion(exclude, idTest))
                        continue;

                    if (start > 0)
                    {
                        start--;
                        continue;
                    }

                    if (take-- <= 0)
                        yield break;

                    yield return document;
                }
            }
        }

        public IEnumerable<Document> GetDocumentsInReverseEtagOrder(DocumentsOperationContext context, int start, int take)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekBackwardFromLast(DocsSchema.FixedSizeIndexes[AllDocsEtagsSlice]))
            {
                if (start > 0)
                {
                    start--;
                    continue;
                }
                if (take-- <= 0)
                    yield break;
                yield return TableValueToDocument(context, ref result.Reader);
            }
        }

        public IEnumerable<Document> GetDocumentsInReverseEtagOrder(DocumentsOperationContext context, string collection, int start, int take)
        {
            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                yield break;

            var table = context.Transaction.InnerTransaction.OpenTable(DocsSchema,
                collectionName.GetTableName(CollectionTableType.Documents));

            if (table == null)
                yield break;

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekBackwardFromLast(DocsSchema.FixedSizeIndexes[CollectionEtagsSlice]))
            {
                if (start > 0)
                {
                    start--;
                    continue;
                }
                if (take-- <= 0)
                    yield break;
                yield return TableValueToDocument(context, ref result.Reader);
            }
        }

        public IEnumerable<Document> GetDocumentsFrom(DocumentsOperationContext context, long etag, int start, int take)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekForwardFrom(DocsSchema.FixedSizeIndexes[AllDocsEtagsSlice], etag, start))
            {
                if (take-- <= 0)
                {
                    yield break;
                }

                yield return TableValueToDocument(context, ref result.Reader);
            }
        }

        public IEnumerable<ReplicationBatchItem> GetDocumentsFrom(DocumentsOperationContext context, long etag)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekForwardFrom(DocsSchema.FixedSizeIndexes[AllDocsEtagsSlice], etag, 0))
            {
                yield return ReplicationBatchItem.From(TableValueToDocument(context, ref result.Reader));
            }
        }

        public IEnumerable<Document> GetDocuments(DocumentsOperationContext context, List<Slice> ids, int start, int take, Reference<int> totalCount)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);

            foreach (var id in ids)
            {
                // id must be lowercased
                if (table.ReadByKey(id, out TableValueReader reader) == false)
                    continue;

                totalCount.Value++;

                if (start > 0)
                {
                    start--;
                    continue;
                }
                if (take-- <= 0)
                    continue; // we need to calculate totalCount correctly

                yield return TableValueToDocument(context, ref reader);
            }
        }

        public IEnumerable<Document> GetDocumentsFrom(DocumentsOperationContext context, string collection, long etag, int start, int take)
        {
            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                yield break;

            var table = context.Transaction.InnerTransaction.OpenTable(DocsSchema,
                collectionName.GetTableName(CollectionTableType.Documents));

            if (table == null)
                yield break;

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekForwardFrom(DocsSchema.FixedSizeIndexes[CollectionEtagsSlice], etag, start))
            {
                if (take-- <= 0)
                    yield break;
                yield return TableValueToDocument(context, ref result.Reader);
            }
        }

        public IEnumerable<Document> GetDocumentsFrom(DocumentsOperationContext context, List<string> collections, long etag, int take)
        {
            foreach (var collection in collections)
            {
                if (take <= 0)
                    yield break;

                foreach (var document in GetDocumentsFrom(context, collection, etag, 0, int.MaxValue))
                {
                    if (take-- <= 0)
                        yield break;

                    yield return document;
                }
            }
        }

        public DocumentOrTombstone GetDocumentOrTombstone(DocumentsOperationContext context, string id, bool throwOnConflict = true)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Argument is null or whitespace", nameof(id));
            if (context.Transaction == null)
                throw new ArgumentException("Context must be set with a valid transaction before calling Put", nameof(context));

            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            {
                return GetDocumentOrTombstone(context, lowerId, throwOnConflict);
            }
        }

        public struct DocumentOrTombstone
        {
            public Document Document;
            public DocumentTombstone Tombstone;
            public bool Missing => Document == null && Tombstone == null;
        }

        public DocumentOrTombstone GetDocumentOrTombstone(DocumentsOperationContext context, Slice lowerId, bool throwOnConflict = true)
        {
            if (context.Transaction == null)
            {
                DocumentPutAction.ThrowRequiresTransaction();
                return default(DocumentOrTombstone); // never hit
            }

            try
            {
                var doc = Get(context, lowerId);
                if (doc != null)
                    return new DocumentOrTombstone { Document = doc };
            }
            catch (DocumentConflictException)
            {
                if (throwOnConflict)
                    throw;
                return new DocumentOrTombstone();
            }

            var tombstoneTable = new Table(TombstonesSchema, context.Transaction.InnerTransaction);
            tombstoneTable.ReadByKey(lowerId, out TableValueReader tvr);

            return new DocumentOrTombstone
            {
                Tombstone = TableValueToTombstone(context, ref tvr)
            };
        }

        public Document Get(DocumentsOperationContext context, string id, bool throwOnConflict = true)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Argument is null or whitespace", nameof(id));
            if (context.Transaction == null)
                throw new ArgumentException("Context must be set with a valid transaction before calling Get", nameof(context));

            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            {
                return Get(context, lowerId, throwOnConflict);
            }
        }

        public Document Get(DocumentsOperationContext context, Slice lowerId, bool throwOnConflict = true)
        {
            if (GetTableValueReaderForDocument(context, lowerId, throwOnConflict, out TableValueReader tvr) == false)
                return null;

            var doc = TableValueToDocument(context, ref tvr);

            context.DocumentDatabase.HugeDocuments.AddIfDocIsHuge(doc);

            return doc;
        }

        public Document GetByEtag(DocumentsOperationContext context, long etag)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);
            var index = DocsSchema.FixedSizeIndexes[AllDocsEtagsSlice];

            if (table.Read(context.Allocator, index, etag, out var tvr) == false)
                return null;

            return TableValueToDocument(context, ref tvr);
        }

        public DocumentTombstone GetTombstoneByEtag(DocumentsOperationContext context, long etag)
        {
            var table = new Table(TombstonesSchema, context.Transaction.InnerTransaction);
            var index = TombstonesSchema.FixedSizeIndexes[AllTombstonesEtagsSlice];

            if (table.Read(context.Allocator, index, etag, out var tvr) == false)
                return null;

            return TableValueToTombstone(context, ref tvr);
        }

        public IEnumerable<LazyStringValue> GetAllIds(DocumentsOperationContext context)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekForwardFrom(DocsSchema.FixedSizeIndexes[AllDocsEtagsSlice], 0, 0))
            {
                yield return TableValueToId(context, (int)DocumentsTable.Id, ref result.Reader);
            }
        }

        public bool GetTableValueReaderForDocument(DocumentsOperationContext context, Slice lowerId, bool throwOnConflict, out TableValueReader tvr)
        {
            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);

            if (table.ReadByKey(lowerId, out tvr) == false)
            {
                if (throwOnConflict && ConflictsStorage.ConflictsCount > 0)
                    ConflictsStorage.ThrowOnDocumentConflict(context, lowerId);

                return false;
            }
            return true;
        }

        public bool HasMoreOfTombstonesAfter(
            DocumentsOperationContext context,
            long etag,
            int maxAllowed)
        {
            var table = new Table(TombstonesSchema, context.Transaction.InnerTransaction);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var _ in table.SeekForwardFrom(TombstonesSchema.FixedSizeIndexes[AllTombstonesEtagsSlice], etag, 0))
            {
                if (maxAllowed-- < 0)
                    return true;
            }
            return false;
        }

        public IEnumerable<DocumentTombstone> GetTombstonesFrom(DocumentsOperationContext context, long etag, int start, int take)
        {
            var table = new Table(TombstonesSchema, context.Transaction.InnerTransaction);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekForwardFrom(TombstonesSchema.FixedSizeIndexes[AllTombstonesEtagsSlice], etag, start))
            {
                if (take-- <= 0)
                    yield break;

                yield return TableValueToTombstone(context, ref result.Reader);
            }
        }

        public IEnumerable<DocumentTombstone> GetTombstonesFrom(DocumentsOperationContext context, List<string> collections, long etag, int take)
        {
            foreach (var collection in collections)
            {
                if (take <= 0)
                    yield break;

                foreach (var tombstone in GetTombstonesFrom(context, collection, etag, 0, int.MaxValue))
                {
                    if (take-- <= 0)
                        yield break;

                    yield return tombstone;
                }
            }
        }

        public IEnumerable<ReplicationBatchItem> GetTombstonesFrom(DocumentsOperationContext context, long etag)
        {
            var table = new Table(TombstonesSchema, context.Transaction.InnerTransaction);

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekForwardFrom(TombstonesSchema.FixedSizeIndexes[AllTombstonesEtagsSlice], etag, 0))
            {
                yield return ReplicationBatchItem.From(TableValueToTombstone(context, ref result.Reader));
            }
        }

        public IEnumerable<DocumentTombstone> GetTombstonesFrom(
            DocumentsOperationContext context,
            string collection,
            long etag,
            int start,
            int take)
        {
            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                yield break;

            var table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema,
                collectionName.GetTableName(CollectionTableType.Tombstones));

            if (table == null)
                yield break;

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var result in table.SeekForwardFrom(TombstonesSchema.FixedSizeIndexes[CollectionEtagsSlice], etag, start))
            {
                if (take-- <= 0)
                    yield break;

                yield return TableValueToTombstone(context, ref result.Reader);
            }
        }

        public long GetLastDocumentEtag(DocumentsOperationContext context, string collection)
        {
            Table.TableValueHolder result = null;
            if (LastDocument(context, collection, ref result) == false)
                return 0;

            return TableValueToEtag((int)DocumentsTable.Etag, ref result.Reader);
        }

        public string GetLastDocumentChangeVector(DocumentsOperationContext context, string collection)
        {
            Table.TableValueHolder result = null;
            if (LastDocument(context, collection, ref result) == false)
                return null;

            return TableValueToChangeVector(context, (int)DocumentsTable.ChangeVector, ref result.Reader);
        }

        private bool LastDocument(DocumentsOperationContext context, string collection, ref Table.TableValueHolder result)
        {
            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                return false;

            var table = context.Transaction.InnerTransaction.OpenTable(DocsSchema,
                collectionName.GetTableName(CollectionTableType.Documents)
            );

            // ReSharper disable once UseNullPropagation
            if (table == null)
                return false;

            result = table.ReadLast(DocsSchema.FixedSizeIndexes[CollectionEtagsSlice]);
            if (result == null)
                return false;

            return true;
        }

        public long GetLastTombstoneEtag(DocumentsOperationContext context, string collection)
        {
            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                return 0;

            var table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema,
                collectionName.GetTableName(CollectionTableType.Tombstones));

            // ReSharper disable once UseNullPropagation
            if (table == null)
                return 0;

            var result = table.ReadLast(TombstonesSchema.FixedSizeIndexes[CollectionEtagsSlice]);
            if (result == null)
                return 0;

            return TableValueToEtag(1, ref result.Reader);
        }

        public bool HasTombstonesWithDocumentEtagBetween(DocumentsOperationContext context, string collection,
            long start,
            long end)
        {
            if (start >= end)
                return false;

            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                return false;

            var table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema,
                collectionName.GetTableName(CollectionTableType.Tombstones));

            if (table == null)
                return false;

            return table.HasEntriesBetween(TombstonesSchema.FixedSizeIndexes[DeletedEtagsSlice], start, end);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Document TableValueToDocument(DocumentsOperationContext context, ref TableValueReader tvr)
        {
            var document = ParseDocument(context, ref tvr);
#if DEBUG
            DebugDisposeReaderAfterTransaction(context.Transaction, document.Data);
            DocumentPutAction.AssertMetadataWasFiltered(document.Data);
            AttachmentsStorage.AssertAttachments(document.Data, document.Flags);
#endif
            return document;
        }

        [Conditional("DEBUG")]
        public static void DebugDisposeReaderAfterTransaction(DocumentsTransaction tx, BlittableJsonReaderObject reader)
        {
            if (reader == null)
                return;
            Debug.Assert(tx != null);
            // this method is called to ensure that after the transaction is completed, all the readers are disposed
            // so we won't have read-after-tx use scenario, which can in rare case corrupt memory. This is a debug
            // helper that is used across the board, but it is meant to assert stuff during debug only
            tx.InnerTransaction.LowLevelTransaction.OnDispose += state => reader.Dispose();
        }

        private static Document ParseDocument(JsonOperationContext context, ref TableValueReader tvr)
        {
            var result = new Document
            {
                StorageId = tvr.Id,
                LowerId = TableValueToString(context, (int)DocumentsTable.LowerId, ref tvr),
                Id = TableValueToId(context, (int)DocumentsTable.Id, ref tvr),
                Etag = TableValueToEtag((int)DocumentsTable.Etag, ref tvr),
                Data = new BlittableJsonReaderObject(tvr.Read((int)DocumentsTable.Data, out int size), size, context),
                ChangeVector = TableValueToChangeVector(context, (int)DocumentsTable.ChangeVector, ref tvr),
                LastModified = TableValueToDateTime((int)DocumentsTable.LastModified, ref tvr),
                Flags = TableValueToFlags((int)DocumentsTable.Flags, ref tvr),
                TransactionMarker = *(short*)tvr.Read((int)DocumentsTable.TransactionMarker, out size)
            };

            return result;
        }

        public static Document ParseRawDataSectionDocumentWithValidation(JsonOperationContext context, ref TableValueReader tvr, int expectedSize, out long etag)
        {
            var mem = tvr.Read((int)DocumentsTable.Data, out int size);

            if (size > expectedSize || size <= 0)
                throw new ArgumentException("Data size is invalid, possible corruption when parsing BlittableJsonReaderObject", nameof(size));

            var result = new Document
            {
                StorageId = tvr.Id,
                LowerId = TableValueToString(context, (int)DocumentsTable.LowerId, ref tvr),
                Id = TableValueToId(context, (int)DocumentsTable.Id, ref tvr),
                Etag = etag = TableValueToEtag((int)DocumentsTable.Etag, ref tvr),
                Data = new BlittableJsonReaderObject(mem, size, context),
                ChangeVector = TableValueToChangeVector(context, (int)DocumentsTable.ChangeVector, ref tvr),
                LastModified = TableValueToDateTime((int)DocumentsTable.LastModified, ref tvr),
                Flags = TableValueToFlags((int)DocumentsTable.Flags, ref tvr),
                TransactionMarker = *(short*)tvr.Read((int)DocumentsTable.TransactionMarker, out size)
            };

            if (size != sizeof(short))
                throw new ArgumentException("TransactionMarker size is invalid, possible corruption when parsing BlittableJsonReaderObject", nameof(size));

            return result;
        }

        private static DocumentTombstone TableValueToTombstone(JsonOperationContext context, ref TableValueReader tvr)
        {
            if (tvr.Pointer == null)
                return null;

            var result = new DocumentTombstone
            {
                StorageId = tvr.Id,
                LowerId = TableValueToString(context, (int)TombstoneTable.LowerId, ref tvr),
                Etag = TableValueToEtag((int)TombstoneTable.Etag, ref tvr),
                DeletedEtag = TableValueToEtag((int)TombstoneTable.DeletedEtag, ref tvr),
                Type = *(DocumentTombstone.TombstoneType*)tvr.Read((int)TombstoneTable.Type, out int _),
                TransactionMarker = *(short*)tvr.Read((int)TombstoneTable.TransactionMarker, out int _),
                ChangeVector = TableValueToChangeVector(context, (int)TombstoneTable.ChangeVector, ref tvr)
            };

            if (result.Type == DocumentTombstone.TombstoneType.Document)
            {
                result.Collection = TableValueToString(context, (int)TombstoneTable.Collection, ref tvr);
                result.Flags = TableValueToFlags((int)TombstoneTable.Flags, ref tvr);
                result.LastModified = TableValueToDateTime((int)TombstoneTable.LastModified, ref tvr);
            }
            else if (result.Type == DocumentTombstone.TombstoneType.Revision)
            {
                result.Collection = TableValueToString(context, (int)TombstoneTable.Collection, ref tvr);
            }

            return result;
        }

        public DeleteOperationResult? Delete(DocumentsOperationContext context, string id, string excpectedChangeVector)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (var cv = context.GetLazyString(excpectedChangeVector))
            {
                return Delete(context, lowerId, id, cv);
            }
        }

        public DeleteOperationResult? Delete(DocumentsOperationContext context, Slice lowerId, string id,
            LazyStringValue expectedChangeVector, long? lastModifiedTicks = null, string changeVector = null,
            CollectionName collectionName = null, NonPersistentDocumentFlags nonPersistentFlags = NonPersistentDocumentFlags.None)
        {
            if (ConflictsStorage.ConflictsCount != 0)
            {
                var result = ConflictsStorage.DeleteConflicts(context, lowerId, expectedChangeVector, changeVector);
                if (result != null)
                    return result;
            }

            var local = GetDocumentOrTombstone(context, lowerId, throwOnConflict: false);
            var modifiedTicks = lastModifiedTicks ?? _documentDatabase.Time.GetUtcNow().Ticks;

            if (local.Tombstone != null)
            {
                if (string.IsNullOrEmpty(expectedChangeVector) == false)
                    throw new ConcurrencyException($"Document {local.Tombstone.LowerId} does not exist, but delete was called with change vector '{expectedChangeVector}'. " +
                                                   "Optimistic concurrency violation, transaction will be aborted.");

                collectionName = ExtractCollectionName(context, local.Tombstone.Collection);

                var tombstoneTable = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema,
                    collectionName.GetTableName(CollectionTableType.Tombstones));
                tombstoneTable.Delete(local.Tombstone.StorageId);

                // we update the tombstone
                var etag = CreateTombstone(context,
                    lowerId,
                    local.Tombstone.Etag,
                    collectionName,
                    local.Tombstone.ChangeVector,
                    modifiedTicks,
                    changeVector,
                    DocumentFlags.None).Etag;

                // We have to raise the notification here because even though we have deleted
                // a deleted value, we changed the change vector. And maybe we need to replicate 
                // that. Another issue is that the last tombstone etag has changed, and we need 
                // to let the indexes catch up to us here, even if they'll just do a noop.

                // TODO: Do not send here strings. Use lazy strings instead.
                context.Transaction.AddAfterCommitNotification(new DocumentChange
                {
                    Type = DocumentChangeTypes.Delete,
                    Id = id,
                    ChangeVector = changeVector,
                    CollectionName = collectionName.Name,
                });

                return new DeleteOperationResult
                {
                    Collection = collectionName,
                    Etag = etag
                };
            }

            if (local.Document != null)
            {
                // just delete the document
                var doc = local.Document;
                if (expectedChangeVector != null && doc.ChangeVector.CompareTo(expectedChangeVector) != 0)
                {
                    throw new ConcurrencyException(
                        $"Document {lowerId} has etag {doc.Etag}, but Delete was called with change vector '{expectedChangeVector}'. " +
                        "Optimistic concurrency violation, transaction will be aborted.")
                    {
                        ActualChangeVector = doc.ChangeVector,
                        ExpectedChangeVector = expectedChangeVector
                    };
                }

                EnsureLastEtagIsPersisted(context, doc.Etag);

                collectionName = ExtractCollectionName(context, doc.Data);
                var table = context.Transaction.InnerTransaction.OpenTable(DocsSchema, collectionName.GetTableName(CollectionTableType.Documents));

                var ptr = table.DirectRead(doc.StorageId, out int size);
                var tvr = new TableValueReader(ptr, size);
                var flags = TableValueToFlags((int)DocumentsTable.Flags, ref tvr);

                long etag;
                using (TableValueToSlice(context, (int)DocumentsTable.LowerId, ref tvr, out Slice tombstone))
                {
                    var tombstoneEtag = CreateTombstone(context, tombstone, doc.Etag, collectionName, doc.ChangeVector, modifiedTicks, changeVector, doc.Flags);
                    changeVector = tombstoneEtag.ChangeVector;
                    etag = tombstoneEtag.Etag;
                }

                if (collectionName.IsHiLo == false &&
                    (flags & DocumentFlags.Artificial) != DocumentFlags.Artificial)
                {
                    var revisionsStorage = _documentDatabase.DocumentsStorage.RevisionsStorage;
                    if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication) == false && 
                        (revisionsStorage.Configuration != null || flags.Contain(DocumentFlags.Resolved)))
                    {
                        revisionsStorage.Delete(context, id, lowerId, collectionName, changeVector, modifiedTicks, doc.NonPersistentFlags, flags);
                    }
                }

                table.Delete(doc.StorageId);

                if ((flags & DocumentFlags.HasAttachments) == DocumentFlags.HasAttachments)
                    AttachmentsStorage.DeleteAttachmentsOfDocument(context, lowerId, changeVector);

                // TODO: Do not send here strings. Use lazy strings instead.
                context.Transaction.AddAfterCommitNotification(new DocumentChange
                {
                    Type = DocumentChangeTypes.Delete,
                    Id = id,
                    ChangeVector = changeVector,
                    CollectionName = collectionName.Name,
                });

                return new DeleteOperationResult
                {
                    Collection = collectionName,
                    Etag = etag
                };
            }
            else
            {
                // we adding a tombstone without having any previous document, it could happened if this was called
                // from the incoming replication or if we delete document that wasn't exist at the first place.
                if (string.IsNullOrEmpty(expectedChangeVector) == false)
                    throw new ConcurrencyException($"Document {lowerId} does not exist, but delete was called with change vector '{expectedChangeVector}'. " +
                                                   "Optimistic concurrency violation, transaction will be aborted.");

                if (collectionName == null)
                {
                    // this basically mean that we tried to delete document that doesn't exist.
                    return null;
                }

                // ensures that the collection trees will be created
                collectionName = ExtractCollectionName(context, collectionName.Name);

                var etag = CreateTombstone(context,
                    lowerId,
                    -1, // delete etag is not relevant
                    collectionName,
                    changeVector,
                    DateTime.UtcNow.Ticks,
                    null,
                    DocumentFlags.None).Etag;

                return new DeleteOperationResult
                {
                    Collection = collectionName,
                    Etag = etag
                };
            }
        }

        // Note: Make sure to call this with a separator, to you won't delete "users/11" for "users/1"
        public List<DeleteOperationResult> DeleteDocumentsStartingWith(DocumentsOperationContext context, string prefix)
        {
            var deleteResults = new List<DeleteOperationResult>();

            var table = new Table(DocsSchema, context.Transaction.InnerTransaction);

            using (DocumentIdWorker.GetSliceFromId(context, prefix, out Slice prefixSlice))
            {
                var hasMore = true;
                while (hasMore)
                {
                    hasMore = false;

                    foreach (var holder in table.SeekByPrimaryKeyPrefix(prefixSlice, Slices.Empty, 0))
                    {
                        hasMore = true;
                        var id = TableValueToId(context, (int)DocumentsTable.Id, ref holder.Value.Reader);

                        var deleteOperationResult = Delete(context, id, null);
                        if (deleteOperationResult != null)
                            deleteResults.Add(deleteOperationResult.Value);
                    }

                }

            }

            return deleteResults;
        }

        public struct DeleteOperationResult
        {
            public long Etag;
            public string ChangeVector;
            public CollectionName Collection;
        }

        public long GenerateNextEtag()
        {
            return Interlocked.Increment(ref _lastEtag); // use interlocked so the GetDatabaseChangeVector can read the latest version
        }

        public void EnsureLastEtagIsPersisted(DocumentsOperationContext context, long docEtag)
        {
            // this is called only from write tx, don't need to worry about threading to read _lastEtag
            if (docEtag != _lastEtag)
                return;
            var etagTree = context.Transaction.InnerTransaction.ReadTree(EtagsSlice);
            var etag = _lastEtag;
            using (Slice.External(context.Allocator, (byte*)&etag, sizeof(long), out Slice etagSlice))
                etagTree.Add(LastEtagSlice, etagSlice);
        }

        public (long Etag, string ChangeVector) CreateTombstone(
            DocumentsOperationContext context,
            Slice lowerId,
            long documentEtag,
            CollectionName collectionName,
            string docChangeVector,
            long lastModifiedTicks,
            string changeVector,
            DocumentFlags flags)
        {
            var newEtag = GenerateNextEtag();

            if (string.IsNullOrEmpty(changeVector))
            {
                changeVector = ConflictsStorage.GetMergedConflictChangeVectorsAndDeleteConflicts(
                    context,
                    lowerId,
                    newEtag,
                    docChangeVector);
                context.LastDatabaseChangeVector = changeVector;
            }
            else
            {
                ConflictsStorage.DeleteConflictsFor(context, lowerId, null);
            }

            var table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema,
                collectionName.GetTableName(CollectionTableType.Tombstones));
            using (Slice.From(context.Allocator, collectionName.Name, out Slice collectionSlice))
            using (Slice.From(context.Allocator, changeVector, out var cv))
            using (table.Allocate(out TableValueBuilder tvb))
            {
                tvb.Add(lowerId);
                tvb.Add(Bits.SwapBytes(newEtag));
                tvb.Add(Bits.SwapBytes(documentEtag));
                tvb.Add(context.GetTransactionMarker());
                tvb.Add((byte)DocumentTombstone.TombstoneType.Document);
                tvb.Add(collectionSlice);
                tvb.Add((int)flags);
                tvb.Add(cv.Content.Ptr, cv.Size);
                tvb.Add(lastModifiedTicks);
                table.Insert(tvb);
            }
            return (newEtag, changeVector);
        }

        public struct PutOperationResults
        {
            public string Id;
            public long Etag;
            public CollectionName Collection;
            public DateTime LastModified;
            public string ChangeVector;
            public DocumentFlags Flags;
        }

        public void DeleteWithoutCreatingTombstone(DocumentsOperationContext context, string collection, long storageId, bool isTombstone)
        {
            // we delete the data directly, without generating a tombstone, because we have a 
            // conflict instead
            var tx = context.Transaction.InnerTransaction;

            var collectionObject = new CollectionName(collection);
            var collectionName = isTombstone ?
                collectionObject.GetTableName(CollectionTableType.Tombstones) :
                collectionObject.GetTableName(CollectionTableType.Documents);

            //make sure that the relevant collection tree exists
            Table table = isTombstone ?
                tx.OpenTable(TombstonesSchema, collectionName) :
                tx.OpenTable(DocsSchema, collectionName);

            table.Delete(storageId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PutOperationResults Put(DocumentsOperationContext context, string id,
            string excpectedChangeVector, BlittableJsonReaderObject document, long? lastModifiedTicks = null, string changeVector = null,
            DocumentFlags flags = DocumentFlags.None, NonPersistentDocumentFlags nonPersistentFlags = NonPersistentDocumentFlags.None)
        {
            return DocumentPut.PutDocument(context, id, excpectedChangeVector, document, lastModifiedTicks, changeVector, flags, nonPersistentFlags);
        }

        public long GetNumberOfDocumentsToProcess(DocumentsOperationContext context, string collection, long afterEtag, out long totalCount)
        {
            return GetNumberOfItemsToProcess(context, collection, afterEtag, tombstones: false, totalCount: out totalCount);
        }

        public long GetNumberOfTombstonesToProcess(DocumentsOperationContext context, string collection, long afterEtag, out long totalCount)
        {
            return GetNumberOfItemsToProcess(context, collection, afterEtag, tombstones: true, totalCount: out totalCount);
        }

        private long GetNumberOfItemsToProcess(DocumentsOperationContext context, string collection, long afterEtag, bool tombstones, out long totalCount)
        {
            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
            {
                totalCount = 0;
                return 0;
            }

            Table table;
            TableSchema.FixedSizeSchemaIndexDef indexDef;
            if (tombstones)
            {
                table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema,
                    collectionName.GetTableName(CollectionTableType.Tombstones));

                indexDef = TombstonesSchema.FixedSizeIndexes[CollectionEtagsSlice];
            }
            else
            {
                table = context.Transaction.InnerTransaction.OpenTable(DocsSchema,
                    collectionName.GetTableName(CollectionTableType.Documents));
                indexDef = DocsSchema.FixedSizeIndexes[CollectionEtagsSlice];
            }
            if (table == null)
            {
                totalCount = 0;
                return 0;
            }

            return table.GetNumberOfEntriesAfter(indexDef, afterEtag, out totalCount);
        }

        public long GetNumberOfDocuments()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
                return GetNumberOfDocuments(context);
        }

        public long GetNumberOfDocuments(DocumentsOperationContext context)
        {
            var fstIndex = DocsSchema.FixedSizeIndexes[AllDocsEtagsSlice];
            var fst = context.Transaction.InnerTransaction.FixedTreeFor(fstIndex.Name, sizeof(long));
            return fst.NumberOfEntries;
        }


        public class CollectionStats
        {
            public string Name;
            public long Count;
        }

        public IEnumerable<CollectionStats> GetCollections(DocumentsOperationContext context)
        {
            foreach (var kvp in _collectionsCache)
            {
                var collectionTable = context.Transaction.InnerTransaction.OpenTable(DocsSchema, kvp.Value.GetTableName(CollectionTableType.Documents));

                yield return new CollectionStats
                {
                    Name = kvp.Key,
                    Count = collectionTable.NumberOfEntries
                };
            }
        }

        public CollectionStats GetCollection(string collection, DocumentsOperationContext context)
        {
            var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
            {
                return new CollectionStats
                {
                    Name = collection,
                    Count = 0
                };
            }

            var collectionTable = context.Transaction.InnerTransaction.OpenTable(DocsSchema,
                collectionName.GetTableName(CollectionTableType.Documents));

            if (collectionTable == null)
            {
                return new CollectionStats
                {
                    Name = collection,
                    Count = 0
                };
            }

            return new CollectionStats
            {
                Name = collectionName.Name,
                Count = collectionTable.NumberOfEntries
            };
        }

        public void DeleteTombstonesBefore(string collection, long etag, DocumentsOperationContext context)
        {
            string tableName;

            if (collection == AttachmentsStorage.AttachmentsTombstones ||
                collection == RevisionsStorage.RevisionsTombstones)
            {
                tableName = collection;
            }
            else
            {
                var collectionName = GetCollection(collection, throwIfDoesNotExist: false);
                if (collectionName == null)
                    return;

                tableName = collectionName.GetTableName(CollectionTableType.Tombstones);
            }

            var table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema, tableName);
            if (table == null)
                return;

            var deleteCount = table.DeleteBackwardFrom(TombstonesSchema.FixedSizeIndexes[CollectionEtagsSlice], etag, long.MaxValue);
            if (_logger.IsInfoEnabled && deleteCount > 0)
                _logger.Info($"Deleted {deleteCount:#,#;;0} tombstones earlier than {etag} in {collection}");

        }

        public IEnumerable<string> GetTombstoneCollections(Transaction transaction)
        {
            yield return AttachmentsStorage.AttachmentsTombstones;
            yield return RevisionsStorage.RevisionsTombstones;

            using (var it = transaction.LowLevelTransaction.RootObjects.Iterate(false))
            {
                it.SetRequiredPrefix(TombstonesPrefix);

                if (it.Seek(TombstonesPrefix) == false)
                    yield break;

                do
                {
                    var tombstoneCollection = it.CurrentKey.ToString();
                    yield return tombstoneCollection.Substring(TombstonesPrefix.Size);
                }
                while (it.MoveNext());
            }
        }

        public long GetLastReplicateEtagFrom(DocumentsOperationContext context, string dbId)
        {
            var readTree = context.Transaction.InnerTransaction.ReadTree(LastReplicatedEtagsSlice);
            var readResult = readTree.Read(dbId);
            if (readResult == null)
                return 0;
            return readResult.Reader.ReadLittleEndianInt64();
        }

        public void SetLastReplicateEtagFrom(DocumentsOperationContext context, string dbId, long etag)
        {
            var etagsTree = context.Transaction.InnerTransaction.CreateTree(LastReplicatedEtagsSlice);
            using (Slice.From(context.Allocator, dbId, out Slice dbIdSlice))
            using (Slice.External(context.Allocator, (byte*)&etag, sizeof(long), out Slice etagSlice))
            {
                etagsTree.Add(dbIdSlice, etagSlice);
            }
        }

        public CollectionName GetCollection(string collection, bool throwIfDoesNotExist)
        {
            if (_collectionsCache.TryGetValue(collection, out CollectionName collectionName) == false && throwIfDoesNotExist)
                throw new InvalidOperationException($"There is no collection for '{collection}'.");

            return collectionName;
        }

        public CollectionName ExtractCollectionName(DocumentsOperationContext context, BlittableJsonReaderObject document)
        {
            var originalCollectionName = CollectionName.GetCollectionName(document);
            return ExtractCollectionName(context, originalCollectionName);
        }

        public CollectionName ExtractCollectionName(DocumentsOperationContext context, string collectionName)
        {
            if (_collectionsCache.TryGetValue(collectionName, out CollectionName name))
                return name;

            if (context.Transaction == null)
            {
                ThrowNoActiveTransactionException(); //this throws, return null in the next row is there so intellisense will be happy
                return null;
            }

            var collections = context.Transaction.InnerTransaction.OpenTable(CollectionsSchema, CollectionsSlice);
            if (collections == null)
                throw new InvalidOperationException("Should never happen!");

            name = new CollectionName(collectionName);
            using (Slice.From(context.Allocator, collectionName, out Slice collectionSlice))
            {
                using (collections.Allocate(out TableValueBuilder tvr))
                {
                    tvr.Add(collectionSlice);
                    collections.Set(tvr);
                }

                DocsSchema.Create(context.Transaction.InnerTransaction, name.GetTableName(CollectionTableType.Documents), 16);
                TombstonesSchema.Create(context.Transaction.InnerTransaction,
                    name.GetTableName(CollectionTableType.Tombstones), 16);

                // Add to cache ONLY if the transaction was committed. 
                // this would prevent NREs next time a PUT is run,since if a transaction
                // is not commited, DocsSchema and TombstonesSchema will not be actually created..
                // has to happen after the commit, but while we are holding the write tx lock
                context.Transaction.InnerTransaction.LowLevelTransaction.BeforeCommitFinalization += _ =>
                {
                    var collectionNames = new Dictionary<string, CollectionName>(_collectionsCache, OrdinalIgnoreCaseStringStructComparer.Instance)
                    {
                        [name.Name] = name
                    };
                    _collectionsCache = collectionNames;
                };
            }
            return name;
        }

        private static void ThrowNoActiveTransactionException()
        {
            throw new InvalidOperationException("This method requires active transaction, and no active transactions in the current context...");
        }

        private Dictionary<string, CollectionName> ReadCollections(Transaction tx)
        {
            var result = new Dictionary<string, CollectionName>(OrdinalIgnoreCaseStringStructComparer.Instance);

            var collections = tx.OpenTable(CollectionsSchema, CollectionsSlice);

            using (ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                foreach (var tvr in collections.SeekByPrimaryKey(Slices.BeforeAllKeys, 0))
                {
                    var collection = TableValueToString(context, 0, ref tvr.Reader);
                    var collectionName = new CollectionName(collection);
                    result.Add(collection, collectionName);

                    var documentsTree = tx.ReadTree(collectionName.GetTableName(CollectionTableType.Documents), RootObjectType.Table);
                    NewPageAllocator.MaybePrefetchSections(documentsTree, tx.LowLevelTransaction);

                    var tombstonesTree = tx.ReadTree(collectionName.GetTableName(CollectionTableType.Tombstones), RootObjectType.Table);
                    NewPageAllocator.MaybePrefetchSections(tombstonesTree, tx.LowLevelTransaction);
                }
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long TableValueToEtag(int index, ref TableValueReader tvr)
        {
            var ptr = tvr.Read(index, out _);
            var etag = Bits.SwapBytes(*(long*)ptr);
            return etag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DocumentFlags TableValueToFlags(int index, ref TableValueReader tvr)
        {
            return *(DocumentFlags*)tvr.Read(index, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DateTime TableValueToDateTime(int index, ref TableValueReader tvr)
        {
            return new DateTime(*(long*)tvr.Read(index, out _));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LazyStringValue TableValueToString(JsonOperationContext context, int index, ref TableValueReader tvr)
        {
            var ptr = tvr.Read(index, out int size);
            return context.AllocateStringValue(null, ptr, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string TableValueToChangeVector(JsonOperationContext context, int index, ref TableValueReader tvr)
        {
            var ptr = tvr.Read(index, out int size);
            return Encodings.Utf8.GetString(ptr, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LazyStringValue TableValueToId(JsonOperationContext context, int index, ref TableValueReader tvr)
        {
            // See format of the lazy string ID in the GetLowerIdSliceAndStorageKey method
            var ptr = tvr.Read(index, out int size);
            size = BlittableJsonReaderBase.ReadVariableSizeInt(ptr, 0, out byte offset);
            return context.AllocateStringValue(null, ptr + offset, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ByteStringContext.InternalScope TableValueToSlice(
            DocumentsOperationContext context, int index, ref TableValueReader tvr, out Slice slice)
        {
            var ptr = tvr.Read(index, out int size);
            return Slice.From(context.Allocator, ptr, size, ByteStringType.Immutable, out slice);
        }
    }

    public enum TableType : byte
    {
        None = 0,
        Documents = 1,
        Revisions = 2,
        Conflicts = 3
    }
}
