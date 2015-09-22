﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Results;
using Exceptionless.Api.Extensions;
using Exceptionless.Api.Security;
using Exceptionless.Api.Utility;
using Exceptionless.Core.Extensions;
using Exceptionless.Api.Utility.Results;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Models;
using Exceptionless.DateTimeExtensions;

namespace Exceptionless.Api.Controllers {
    [RequireHttpsExceptLocal]
    public abstract class ExceptionlessApiController : ApiController {
        public const string API_PREFIX = "api/v2";
        protected const int DEFAULT_LIMIT = 10;
        protected const int MAXIMUM_LIMIT = 100;
        protected const int MAXIMUM_SKIP = 1000;

        public ExceptionlessApiController() {
            AllowedFields = new List<string>();
        }

        protected TimeSpan GetOffset(string offset) {
            double offsetInMinutes;
            if (!String.IsNullOrEmpty(offset) && Double.TryParse(offset, out offsetInMinutes))
                return TimeSpan.FromMinutes(offsetInMinutes);

            return TimeSpan.Zero;
        }

        protected ICollection<string> AllowedFields { get; private set; }

        protected virtual TimeInfo GetTimeInfo(string time, string offset) {
            string field = null;
            if (!String.IsNullOrEmpty(time) && time.Contains("|")) {
                var parts = time.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                field = parts.Length > 0 && AllowedFields.Contains(parts[0]) ? parts[0] : null;
                time = parts.Length > 1 ? parts[1] : null;
            }

            var utcOffset = GetOffset(offset);

            // range parsing needs to be based on the user's local time.
            var localRange = DateTimeRange.Parse(time, DateTime.UtcNow.Add(utcOffset));
            var utcRange = localRange != DateTimeRange.Empty ? localRange.Subtract(utcOffset) : localRange;

            return new TimeInfo {
                Field = field,
                Offset = utcOffset,
                UtcRange = utcRange
            };
        }

        protected virtual Tuple<string, SortOrder> GetSort(string sort) {
            var order = SortOrder.Ascending;
            if (!String.IsNullOrEmpty(sort) && sort.StartsWith("-")) {
                sort = sort.Substring(1);
                order = SortOrder.Descending;
            }

            return Tuple.Create(AllowedFields.Contains(sort) ? sort : null, order);
        }

        protected int GetLimit(int limit) {
            if (limit < 1)
                limit = DEFAULT_LIMIT;
            else if (limit > MAXIMUM_LIMIT)
                limit = MAXIMUM_LIMIT;

            return limit;
        }

        protected int GetPage(int page) {
            if (page < 1)
                page = 1;

            return page;
        }

        protected int GetSkip(int currentPage, int limit) {
            if (currentPage < 1)
                currentPage = 1;

            int skip = (currentPage - 1) * limit;
            if (skip < 0)
                skip = 0;

            return skip;
        }

        public Task<User> GetExceptionlessUserAsync() => Request.GetUserAsync();

        public Task<Project> GetDefaultProjectAsync() => Request.GetDefaultProjectAsync();

        public AuthType AuthType => User.GetAuthType();

        public Task<bool> CanAccessOrganizationAsync(string organizationId) {
            return Request.CanAccessOrganizationAsync(organizationId);
        }

        public Task<bool> IsInOrganizationAsync(string organizationId) {
            if (String.IsNullOrEmpty(organizationId))
                return Task.FromResult(false);

            return Request.IsInOrganizationAsync(organizationId);
        }

        public Task<ICollection<string>> GetAssociatedOrganizationIdsAsync() {
            return Request.GetAssociatedOrganizationIdsAsync();
        }

        public async Task<string> GetAssociatedOrganizationsFilterAsync(IOrganizationRepository repository, bool filterUsesPremiumFeatures, bool hasOrganizationOrProjectFilter, string retentionDateFieldName = "date") {
            if (hasOrganizationOrProjectFilter && Request.IsGlobalAdmin())
                return null;

            var associatedOrganizations = await repository.GetByIdsAsync(await GetAssociatedOrganizationIdsAsync().AnyContext(), useCache: true).AnyContext();
            var organizations = associatedOrganizations.Documents.Where(o => !o.IsSuspended || o.HasPremiumFeatures || (!o.HasPremiumFeatures && !filterUsesPremiumFeatures)).ToList();
            if (organizations.Count == 0)
                return "organization:none";

            var builder = new StringBuilder();
            for (int index = 0; index < organizations.Count; index++) {
                if (index > 0)
                    builder.Append(" OR ");
                
                var organization = organizations[index];
                if (organization.RetentionDays > 0)
                    builder.AppendFormat("(organization:{0} AND {1}:[now/d-{2}d TO now/d+1d}})", organization.Id, retentionDateFieldName, organization.RetentionDays);
                else
                    builder.AppendFormat("organization:{0}", organization.Id);
            }

            return builder.ToString();
        }

        protected bool HasOrganizationOrProjectFilter(string filter) {
            if (String.IsNullOrWhiteSpace(filter))
                return false;

            return filter.Contains("organization:") || filter.Contains("project:");
        }

        public Task<string> GetDefaultOrganizationIdAsync() {
            return Request.GetDefaultOrganizationIdAsync();
        }

        protected StatusCodeActionResult StatusCodeWithMessage(HttpStatusCode statusCode, string message, string reason = null) {
            return new StatusCodeActionResult(statusCode, Request, message, reason);
        }

        protected IHttpActionResult BadRequest(ModelActionResults results) {
            return new NegotiatedContentResult<ModelActionResults>(HttpStatusCode.BadRequest, results, this);
        }

        public PermissionActionResult Permission(PermissionResult permission) {
            return new PermissionActionResult(permission, Request);
        }

        public PlanLimitReachedActionResult PlanLimitReached(string message) {
            return new PlanLimitReachedActionResult(message, Request);
        }

        public NotImplementedActionResult NotImplemented(string message) {
            return new NotImplementedActionResult(message, Request);
        }

        public OkWithHeadersContentResult<T> OkWithLinks<T>(T content, params string[] links) {
            return new OkWithHeadersContentResult<T>(content, this, links.Where(l => l != null).Select(l => new KeyValuePair<string, IEnumerable<string>>("Link", new[] { l })));
        }

        public OkWithHeadersContentResult<T> OkWithHeaders<T>(T content, params Tuple<string, string>[] headers) {
            return new OkWithHeadersContentResult<T>(content, this, headers.Where(h => h != null).Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Item1, new[] { h.Item2 })));
        }

        public OkWithHeadersContentResult<T> OkWithHeaders<T>(T content, params Tuple<string, string[]>[] headers) {
            return new OkWithHeadersContentResult<T>(content, this, headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Item1, h.Item2)));
        }

        public OkWithHeadersContentResult<T> OkWithHeaders<T>(T content, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) {
            return new OkWithHeadersContentResult<T>(content, this, headers);
        }

        public OkWithResourceLinks<TEntity> OkWithResourceLinks<TEntity>(ICollection<TEntity> content, bool hasMore, Func<TEntity, string> pagePropertyAccessor = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = null, bool isDescending = false) where TEntity : class {
            return new OkWithResourceLinks<TEntity>(content, this, hasMore, null, pagePropertyAccessor, headers, isDescending);
        }

        public OkWithResourceLinks<TEntity> OkWithResourceLinks<TEntity>(ICollection<TEntity> content, bool hasMore, int page, long? total = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = null) where TEntity : class {
            return new OkWithResourceLinks<TEntity>(content, this, hasMore, page, total);
        }

        protected Dictionary<string, IEnumerable<string>> GetLimitedByPlanHeader(long totalLimitedByPlan) {
            var headers = new Dictionary<string, IEnumerable<string>>();
            if (totalLimitedByPlan > 0)
                headers.Add(ExceptionlessHeaders.LimitedByPlan, new[] { totalLimitedByPlan.ToString() });
            return headers;
        }

        protected string GetResourceLink(string url, string type) {
            return url != null ? $"<{url}>; rel=\"{type}\"" : null;
        }

        protected bool NextPageExceedsSkipLimit(int page, int limit) {
            return (page + 1) * limit >= MAXIMUM_SKIP;
        }

        public string GetSystemFilter(bool filterUsesPremiumFeatures, bool hasOrganizationFilter) {
            if (hasOrganizationFilter && Request.IsGlobalAdmin())
                return null;

            return null;
        }

        protected bool HasOrganizationFilter(string filter) {
            if (String.IsNullOrWhiteSpace(filter))
                return false;

            return filter.Contains("organization:");
        }
    }
}