﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Elasticsearch.Net;
using Exceptionless.Core.Component;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Messaging.Models;
using Exceptionless.Core.Models;
using Exceptionless.Core.Repositories.Configuration;
using FluentValidation;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Messaging;
using Nest;
using DataDictionary = Exceptionless.Core.Models.DataDictionary;

namespace Exceptionless.Core.Repositories {
    public abstract class Repository<T> : ReadOnlyRepository<T>, IRepository<T> where T : class, IIdentity, new() {
        protected readonly IValidator<T> _validator;
        protected readonly IMessagePublisher _messagePublisher;
        protected readonly static bool _isOwnedByOrganization = typeof(IOwnedByOrganization).IsAssignableFrom(typeof(T));
        protected readonly static bool _isOwnedByProject = typeof(IOwnedByProject).IsAssignableFrom(typeof(T));
        protected readonly static bool _isOwnedByStack = typeof(IOwnedByStack).IsAssignableFrom(typeof(T));
        protected readonly static bool _hasDates = typeof(IHaveDates).IsAssignableFrom(typeof(T));
        protected readonly static bool _hasCreatedDate = typeof(IHaveCreatedDate).IsAssignableFrom(typeof(T));

        protected Repository(IElasticClient elasticClient, IElasticSearchIndex index, IValidator<T> validator = null, ICacheClient cacheClient = null, IMessagePublisher messagePublisher = null) : base(elasticClient, index, cacheClient) {
            _validator = validator;
            _messagePublisher = messagePublisher;
        }

        public bool BatchNotifications { get; set; }

        public async Task<T> AddAsync(T document, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotification = true) {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            await AddAsync(new[] { document }, addToCache, expiresIn, sendNotification).AnyContext();
            return document;
        }

        public async Task AddAsync(ICollection<T> documents, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotification = true) {
            if (documents == null || documents.Count == 0)
                return;

            await OnDocumentChangingAsync(ChangeType.Added, documents).AnyContext();

            if (_validator != null)
                foreach (var document in documents)
                    await _validator.ValidateAndThrowAsync(document).AnyContext();

            if (_isEvent) {
                foreach (var group in documents.OfType<PersistentEvent>().GroupBy(e => e.Date.ToUniversalTime().Date)) {
                    var result = await _elasticClient.IndexManyAsync(group.ToList(), String.Concat(_index.VersionedName, "-", group.Key.ToString("yyyyMM"))).AnyContext();
                    if (!result.IsValid)
                        throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
                }
            } else {
                var result = await _elasticClient.IndexManyAsync(documents, _index.VersionedName).AnyContext();
                if (!result.IsValid)
                    throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
            }

            if (addToCache)
                await AddToCacheAsync(documents, expiresIn).AnyContext();

            if (sendNotification)
                await SendNotificationsAsync(ChangeType.Added, documents).AnyContext();

            await OnDocumentChangedAsync(ChangeType.Added, documents).AnyContext();
        }
        public async Task RemoveAsync(string id, bool sendNotification = true) {
            if (String.IsNullOrEmpty(id))
                throw new ArgumentNullException(nameof(id));

            var document = await GetByIdAsync(id, true).AnyContext();
            await RemoveAsync(new[] { document }, sendNotification).AnyContext();
        }

        public Task RemoveAsync(T document, bool sendNotification = true) {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            return RemoveAsync(new[] { document }, sendNotification);
        }

        public async Task RemoveAsync(ICollection<T> documents, bool sendNotification = true) {
            if (documents == null || documents.Count == 0)
                throw new ArgumentException("Must provide one or more documents to remove.", nameof(documents));

            await OnDocumentChangingAsync(ChangeType.Removed, documents).AnyContext();

            string indexName = _isEvent ? _index.VersionedName + "-*" : _index.VersionedName;
            await _elasticClient.DeleteByQueryAsync<T>(q => q.Query(q1 => q1.Ids(documents.Select(d => d.Id))).Index(indexName)).AnyContext();
			
            if (sendNotification)
                await SendNotificationsAsync(ChangeType.Removed, documents).AnyContext();

            await OnDocumentChangedAsync(ChangeType.Removed, documents).AnyContext();
        }

        public async Task RemoveAllAsync() {
            if (EnableCache)
                await Cache.RemoveAllAsync().AnyContext();

            if (_isEvent)
                await _elasticClient.DeleteIndexAsync(d => d.Index(_index.VersionedName + "-*")).AnyContext();
            else
                await RemoveAllAsync(new QueryOptions(), false).AnyContext();
        }

        protected async Task<long> RemoveAllAsync(QueryOptions options, bool sendNotifications = true) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var fields = new List<string>(new[] { "id" });
            if (_isOwnedByOrganization)
                fields.Add("organization_id");
            if (_isOwnedByProject)
                fields.Add("project_id");
            if (_isOwnedByStack)
                fields.Add("stack_id");
            if (_isStack)
                fields.Add("signature_hash");

            long recordsAffected = 0;
            var searchDescriptor = new SearchDescriptor<T>()
                .Index(_index.Name)
                .Query(options.GetElasticSearchQuery<T>(_supportsSoftDeletes))
                .Source(s => s.Include(fields.ToArray()))
                .Size(Settings.Current.BulkBatchSize);

            _elasticClient.EnableTrace();
            var documents = (await _elasticClient.SearchAsync<T>(searchDescriptor).AnyContext()).Documents.ToList();
            _elasticClient.DisableTrace();
            while (documents.Count > 0) {
                recordsAffected += documents.Count;
                await RemoveAsync(documents, sendNotifications).AnyContext();

                documents = (await _elasticClient.SearchAsync<T>(searchDescriptor).AnyContext()).Documents.ToList();
            }
            _elasticClient.DisableTrace();

            return recordsAffected;
        }

        public async Task<T> SaveAsync(T document, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotifications = true) {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            await SaveAsync(new[] { document }, addToCache, expiresIn, sendNotifications).AnyContext();
            return document;
        }

        public async Task SaveAsync(ICollection<T> documents, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotifications = true) {
            if (documents == null || documents.Count == 0)
                return;

            string[] ids = documents.Where(d => !String.IsNullOrEmpty(d.Id)).Select(d => d.Id).ToArray();
            var originalDocuments = ids.Length > 0 ? (await GetByIdsAsync(documents.Select(d => d.Id).ToArray()).AnyContext()).Documents : new List<T>();

            await OnDocumentChangingAsync(ChangeType.Saved, documents, originalDocuments).AnyContext();

            if (_validator != null)
                foreach (var document in documents)
                    await _validator.ValidateAndThrowAsync(document).AnyContext();

            if (_isEvent) {
                foreach (var group in documents.OfType<PersistentEvent>().GroupBy(e => e.Date.ToUniversalTime().Date)) {
                    var result = await _elasticClient.IndexManyAsync(group.ToList(), String.Concat(_index.VersionedName, "-", group.Key.ToString("yyyyMM"))).AnyContext();
                    if (!result.IsValid)
                        throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
                }
            } else {
                var result = await _elasticClient.IndexManyAsync(documents, _index.VersionedName).AnyContext();
                if (!result.IsValid)
                    throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
            }

            if (addToCache)
                await AddToCacheAsync(documents, expiresIn).AnyContext();

            if (sendNotifications)
                await SendNotificationsAsync(ChangeType.Saved, documents, originalDocuments).AnyContext();

            await OnDocumentChangedAsync(ChangeType.Saved, documents, originalDocuments).AnyContext();
        }

        protected Task<long> UpdateAllAsync(string organizationId, QueryOptions options, object update, bool sendNotifications = true) {
            return UpdateAllAsync(new[] { organizationId }, options, update, sendNotifications);
        }

        protected async Task<long> UpdateAllAsync(string[] organizationIds, QueryOptions options, object update, bool sendNotifications = true) {
            long recordsAffected = 0;

            var searchDescriptor = new SearchDescriptor<T>()
                .Index(_index.Name)
                .Query(options.GetElasticSearchQuery<T>(_supportsSoftDeletes))
                .Source(s => s.Include(f => f.Id))
                .SearchType(SearchType.Scan)
                .Scroll("4s")
                .Size(Settings.Current.BulkBatchSize);

            _elasticClient.EnableTrace();
            var scanResults = await _elasticClient.SearchAsync<T>(searchDescriptor).AnyContext();
            _elasticClient.DisableTrace();

            // Check to see if no scroll id was returned. This will occur when the index doesn't exist.
            if (!scanResults.IsValid || scanResults.ScrollId == null)
                return 0;

            var results = await _elasticClient.ScrollAsync<T>("4s", scanResults.ScrollId).AnyContext();
            while (results.Hits.Any()) {
                var bulkResult = await _elasticClient.BulkAsync(b => {
                    string script = update as string;
                    if (script != null)
                        results.Hits.ForEach(h => b.Update<T>(u => u.Id(h.Id).Index(h.Index).Script(script)));
                    else
                        results.Hits.ForEach(h => b.Update<T, object>(u => u.Id(h.Id).Index(h.Index).Doc(update)));

                    return b;
                }).AnyContext();

                if (!bulkResult.IsValid) {
                    Logger.Error().Message("Error occurred while bulk updating").Exception(bulkResult.ConnectionStatus.OriginalException).Write();
                    return 0;
                }

                if (EnableCache)
                    foreach (var hit in results.Hits)
                        await InvalidateCacheAsync(hit.Id).AnyContext();

                recordsAffected += results.Documents.Count();
                results = await _elasticClient.ScrollAsync<T>("4s", results.ScrollId).AnyContext();
            }

            if (recordsAffected <= 0)
                return 0;

            if (!sendNotifications)
                return recordsAffected;

            foreach (var organizationId in organizationIds) {
                await PublishMessageAsync(new EntityChanged {
                    ChangeType = ChangeType.Saved,
                    OrganizationId = organizationId,
                    Type = _entityType
                }, TimeSpan.FromSeconds(1.5)).AnyContext();
            }

            return recordsAffected;
        }

        public Foundatio.Utility.AsyncEvent<DocumentChangeEventArgs<T>> DocumentChanging { get; set; } = new Foundatio.Utility.AsyncEvent<DocumentChangeEventArgs<T>>(true);

        private async Task OnDocumentChangingAsync(ChangeType changeType, ICollection<T> documents, ICollection<T> originalDocuments = null) {
            if (changeType != ChangeType.Added)
                await InvalidateCacheAsync(documents).AnyContext();

            if (changeType != ChangeType.Removed) {
                if (_hasDates)
                    documents.OfType<IHaveDates>().SetDates();
                else if (_hasCreatedDate)
                    documents.OfType<IHaveCreatedDate>().SetCreatedDates();

                documents.EnsureIds();
            }

            await (DocumentChanging?.InvokeAsync(this, new DocumentChangeEventArgs<T>(changeType, documents, this, originalDocuments)) ?? TaskHelper.Completed()).AnyContext();
        }
        
        public Foundatio.Utility.AsyncEvent<DocumentChangeEventArgs<T>> DocumentChanged { get; set; } = new Foundatio.Utility.AsyncEvent<DocumentChangeEventArgs<T>>(true);

        private Task OnDocumentChangedAsync(ChangeType changeType, ICollection<T> documents, ICollection<T> originalDocuments = null) {
            return DocumentChanged?.InvokeAsync(this, new DocumentChangeEventArgs<T>(changeType, documents, this, originalDocuments)) ?? TaskHelper.Completed();
        }

        protected virtual async Task AddToCacheAsync(ICollection<T> documents, TimeSpan? expiresIn = null) {
            if (!EnableCache)
                return;

            foreach (var document in documents)
                await Cache.SetAsync(GetScopedCacheKey(document.Id), document, expiresIn ?? TimeSpan.FromSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS)).AnyContext();
        }

        protected virtual async Task SendNotificationsAsync(ChangeType changeType, ICollection<T> documents, ICollection<T> originalDocuments = null) {
            if (BatchNotifications) {
                await PublishMessageAsync(changeType, documents).AnyContext();
            } else {
                foreach (var document in documents)
                    await PublishMessageAsync(changeType, document).AnyContext();
            }
        }

        protected Task PublishMessageAsync(ChangeType changeType, T document, IDictionary<string, object> data = null) {
            return PublishMessageAsync(changeType, new[] { document }, data);
        }

        protected async Task PublishMessageAsync(ChangeType changeType, IEnumerable<T> documents, IDictionary<string, object> data = null) {
            if (_messagePublisher == null)
                return;

            if (_isOwnedByOrganization && _isOwnedByProject) {
                foreach (var projectDocs in documents.OfType<IOwnedByOrganizationAndProjectWithIdentity>().GroupBy(d => d.ProjectId)) {
                    var firstDoc = projectDocs.FirstOrDefault();
                    if (firstDoc == null)
                        continue;

                    int count = projectDocs.Count();
                    var message = new EntityChanged {
                        ChangeType = changeType,
                        OrganizationId = firstDoc.OrganizationId,
                        ProjectId = projectDocs.Key,
                        Id = count == 1 ? firstDoc.Id : null,
                        Type = _entityType,
						Data = new DataDictionary(data ?? new Dictionary<string, object>())
                    };

                    await PublishMessageAsync(message, TimeSpan.FromSeconds(1.5)).AnyContext();
                }
            } else if (_isOwnedByOrganization) {
                foreach (var orgDocs in documents.OfType<IOwnedByOrganizationWithIdentity>().GroupBy(d => d.OrganizationId)) {
                    var firstDoc = orgDocs.FirstOrDefault();
                    if (firstDoc == null)
                        continue;

                    int count = orgDocs.Count();
                    var message = new EntityChanged {
                        ChangeType = changeType,
                        OrganizationId = orgDocs.Key,
                        Id = count == 1 ? firstDoc.Id : null,
                        Type = _entityType,
						Data = new DataDictionary(data ?? new Dictionary<string, object>())
                    };

                    await PublishMessageAsync(message, TimeSpan.FromSeconds(1.5)).AnyContext();
                }
            } else {
                foreach (var doc in documents) {
                    var message = new EntityChanged {
                        ChangeType = changeType,
                        Id = doc.Id,
                        Type = _entityType,
						Data = new DataDictionary(data ?? new Dictionary<string, object>())
                    };

                    await PublishMessageAsync(message, TimeSpan.FromSeconds(1.5)).AnyContext();
                }
            }
        }

        protected async Task PublishMessageAsync<TMessageType>(TMessageType message, TimeSpan? delay = null) where TMessageType : class {
            if (_messagePublisher == null)
                return;

            await _messagePublisher.PublishAsync(message, delay).AnyContext();
        }
    }
}