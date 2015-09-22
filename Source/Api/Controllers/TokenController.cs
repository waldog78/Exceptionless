﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using AutoMapper;
using Exceptionless.Api.Controllers;
using Exceptionless.Api.Extensions;
using Exceptionless.Api.Models;
using Exceptionless.Core.Authorization;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Models;

namespace Exceptionless.App.Controllers.API {
    [RoutePrefix(API_PREFIX + "/tokens")]
    [Authorize(Roles = AuthorizationRoles.User)]
    public class TokenController : RepositoryApiController<ITokenRepository, Token, ViewToken, NewToken, Token> {
        private readonly IApplicationRepository _applicationRepository;
        private readonly IProjectRepository _projectRepository;

        public TokenController(ITokenRepository repository, IApplicationRepository applicationRepository, IProjectRepository projectRepository) : base(repository) {
            _applicationRepository = applicationRepository;
            _projectRepository = projectRepository;
        }

        #region CRUD

        /// <summary>
        /// Get by organization
        /// </summary>
        /// <param name="organizationId">The identifier of the organization.</param>
        /// <param name="page">The page parameter is used for pagination. This value must be greater than 0.</param>
        /// <param name="limit">A limit on the number of objects to be returned. Limit can range between 1 and 100 items.</param>
        /// <response code="404">The organization could not be found.</response>
        [HttpGet]
        [Route("~/" + API_PREFIX + "/organizations/{organizationId:objectid}/tokens")]
        [ResponseType(typeof(List<ViewToken>))]
        public async Task<IHttpActionResult> GetByOrganizationAsync(string organizationId, int page = 1, int limit = 10) {
            if (String.IsNullOrEmpty(organizationId) || !await CanAccessOrganizationAsync(organizationId).AnyContext())
                return NotFound();

            page = GetPage(page);
            limit = GetLimit(limit);
            var options = new PagingOptions { Page = page, Limit = limit };
            var tokens = await _repository.GetByTypeAndOrganizationIdAsync(TokenType.Access, organizationId, options).AnyContext();
            var viewTokens = (await MapCollectionAsync<ViewToken>(tokens.Documents, true).AnyContext()).ToList();
            return OkWithResourceLinks(viewTokens, options.HasMore, page, tokens.Total);
        }

        /// <summary>
        /// Get by project
        /// </summary>
        /// <param name="projectId">The identifier of the project.</param>
        /// <param name="page">The page parameter is used for pagination. This value must be greater than 0.</param>
        /// <param name="limit">A limit on the number of objects to be returned. Limit can range between 1 and 100 items.</param>
        /// <response code="404">The project could not be found.</response>
        [HttpGet]
        [Route("~/" + API_PREFIX + "/projects/{projectId:objectid}/tokens")]
        [ResponseType(typeof(List<ViewToken>))]
        public async Task<IHttpActionResult> GetByProjectAsync(string projectId, int page = 1, int limit = 10) {
            if (String.IsNullOrEmpty(projectId))
                return NotFound();

            var project = await _projectRepository.GetByIdAsync(projectId).AnyContext();
            if (project == null || !await CanAccessOrganizationAsync(project.OrganizationId).AnyContext())
                return NotFound();

            page = GetPage(page);
            limit = GetLimit(limit);
            var options = new PagingOptions { Page = page, Limit = limit };
            var tokens = await _repository.GetByTypeAndProjectIdAsync(TokenType.Access, projectId, options).AnyContext();
            var viewTokens = (await MapCollectionAsync<ViewToken>(tokens.Documents, true).AnyContext()).ToList();
            return OkWithResourceLinks(viewTokens, options.HasMore && !NextPageExceedsSkipLimit(page, limit), page, tokens.Total);
        }

        /// <summary>
        /// Get a projects default token
        /// </summary>
        /// <param name="projectId">The identifier of the project.</param>
        /// <response code="404">The project could not be found.</response>
        [HttpGet]
        [Route("~/" + API_PREFIX + "/projects/{projectId:objectid}/tokens/default")]
        [ResponseType(typeof(ViewToken))]
        public async Task<IHttpActionResult> GetDefaultTokenAsync(string projectId) {
            if (String.IsNullOrEmpty(projectId))
                return NotFound();

            var project = await _projectRepository.GetByIdAsync(projectId).AnyContext();
            if (project == null || !await CanAccessOrganizationAsync(project.OrganizationId).AnyContext())
                return NotFound();

            var token = (await _repository.GetByTypeAndProjectIdAsync(TokenType.Access, projectId, new PagingOptions { Limit = 1 }).AnyContext()).Documents.FirstOrDefault();
            if (token != null)
                return await OkModelAsync(token).AnyContext();

            return await PostAsync(new NewToken { OrganizationId = project.OrganizationId, ProjectId = projectId}).AnyContext();
        }

        /// <summary>
        /// Get by id
        /// </summary>
        /// <param name="id">The identifier of the token.</param>
        /// <response code="404">The token could not be found.</response>
        [HttpGet]
        [Route("{id:token}", Name = "GetTokenById")]
        [ResponseType(typeof(ViewToken))]
        public override Task<IHttpActionResult> GetByIdAsync(string id) {
            return base.GetByIdAsync(id);
        }

        /// <summary>
        /// Create
        /// </summary>
        /// <remarks>
        /// To create a new token, you must specify an organization_id. There are three valid scopes: client, user and admin.
        /// </remarks>
        /// <param name="token">The token.</param>
        /// <response code="400">An error occurred while creating the token.</response>
        /// <response code="409">The token already exists.</response>
        [Route]
        [HttpPost]
        [ResponseType(typeof(ViewToken))]
        public override Task<IHttpActionResult> PostAsync(NewToken token) {
            return base.PostAsync(token);
        }

        /// <summary>
        /// Create for project
        /// </summary>
        /// <remarks>
        /// This is a helper action that makes it easier to create a token for a specific project.
        /// You may also specify a scope when creating a token. There are three valid scopes: client, user and admin.
        /// </remarks>
        /// <param name="projectId">The identifier of the project.</param>
        /// <param name="token">The token.</param>
        /// <response code="400">An error occurred while creating the token.</response>
        /// <response code="409">The token already exists.</response>
        [Route("~/" + API_PREFIX + "/projects/{projectId:objectid}/tokens")]
        [HttpPost]
        [ResponseType(typeof(ViewToken))]
        public async Task<IHttpActionResult> PostByProjectAsync(string projectId, NewToken token) {
            if (token == null)
                token = new NewToken();

            var project = await _projectRepository.GetByIdAsync(projectId, true).AnyContext();
            if (!await IsInProjectAsync(project).AnyContext())
                return BadRequest();

            token.OrganizationId = project.OrganizationId;
            token.ProjectId = projectId;
            return await PostAsync(token).AnyContext();
        }

        /// <summary>
        /// Create for organization
        /// </summary>
        /// <remarks>
        /// This is a helper action that makes it easier to create a token for a specific organization.
        /// You may also specify a scope when creating a token. There are three valid scopes: client, user and admin.
        /// </remarks>
        /// <param name="organizationId">The identifier of the organization.</param>
        /// <param name="token">The token.</param>
        /// <response code="400">An error occurred while creating the token.</response>
        /// <response code="409">The token already exists.</response>
        [Route("~/" + API_PREFIX + "/organizations/{organizationId:objectid}/tokens")]
        [HttpPost]
        [ResponseType(typeof(ViewToken))]
        public async Task<IHttpActionResult> PostByOrganizationAsync(string organizationId, NewToken token) {
            if (token == null)
                token = new NewToken();

            if (!await IsInOrganizationAsync(organizationId).AnyContext())
                return BadRequest();

            token.OrganizationId = organizationId;
            return await PostAsync(token).AnyContext();
        }

        /// <summary>
        /// Remove
        /// </summary>
        /// <param name="ids">A comma delimited list of token identifiers.</param>
        /// <response code="204">No Content.</response>
        /// <response code="400">One or more validation errors occurred.</response>
        /// <response code="404">One or more tokens were not found.</response>
        /// <response code="500">An error occurred while deleting one or more tokens.</response>
        [HttpDelete]
        [Route("{ids:tokens}")]
        public Task<IHttpActionResult> DeleteAsync(string ids) {
            return base.DeleteAsync(ids.FromDelimitedString());
        }

        #endregion

        protected override async Task<Token> GetModelAsync(string id, bool useCache = true) {
            if (String.IsNullOrEmpty(id))
                return null;

            var model = await _repository.GetByIdAsync(id, useCache).AnyContext();
            if (model == null)
                return null;

            if (!String.IsNullOrEmpty(model.OrganizationId) && !await IsInOrganizationAsync(model.OrganizationId).AnyContext())
                return null;

            if (!String.IsNullOrEmpty(model.UserId) && model.UserId != (await Request.GetUserAsync().AnyContext()).Id)
                return null;

            if (model.Type != TokenType.Access)
                return null;

            if (!String.IsNullOrEmpty(model.ProjectId) && !await IsInProjectAsync(model.ProjectId).AnyContext())
                return null;

            return model;
        }

        protected override async Task<PermissionResult> CanAddAsync(Token value) {
            // We only allow users to create organization scoped tokens.
            if (String.IsNullOrEmpty(value.OrganizationId))
                return PermissionResult.Deny;

            if (!String.IsNullOrEmpty(value.ProjectId) && !String.IsNullOrEmpty(value.UserId))
                return PermissionResult.DenyWithMessage("Token can't be associated to both user and project.");

            foreach (string scope in value.Scopes.ToList()) {
                if (scope != scope.ToLower()) {
                    value.Scopes.Remove(scope);
                    value.Scopes.Add(scope.ToLower());
                }

                if (!AuthorizationRoles.AllScopes.Contains(scope.ToLower()))
                    return PermissionResult.DenyWithMessage("Invalid token scope requested.");
            }

            if (value.Scopes.Count == 0)
                value.Scopes.Add(AuthorizationRoles.Client);

            if (value.Scopes.Contains(AuthorizationRoles.Client) && !User.IsInRole(AuthorizationRoles.User))
                return PermissionResult.Deny;

            if (value.Scopes.Contains(AuthorizationRoles.User) && !User.IsInRole(AuthorizationRoles.User) )
                return PermissionResult.Deny;

            if (value.Scopes.Contains(AuthorizationRoles.GlobalAdmin) && !User.IsInRole(AuthorizationRoles.GlobalAdmin))
                return PermissionResult.Deny;

            if (!String.IsNullOrEmpty(value.ProjectId)) {
                Project project = await _projectRepository.GetByIdAsync(value.ProjectId, true).AnyContext();
                if (!await IsInProjectAsync(project).AnyContext())
                    return PermissionResult.Deny;

                value.OrganizationId = project.OrganizationId;
                value.DefaultProjectId = null;
            }

            if (!String.IsNullOrEmpty(value.DefaultProjectId)) {
                Project project = await _projectRepository.GetByIdAsync(value.DefaultProjectId, true).AnyContext();
                if (!await IsInProjectAsync(project).AnyContext())
                    return PermissionResult.Deny;
            }

            if (!String.IsNullOrEmpty(value.ApplicationId)) {
                var application = await _applicationRepository.GetByIdAsync(value.ApplicationId, true).AnyContext();
                if (application == null || !await IsInOrganizationAsync(application.OrganizationId).AnyContext())
                    return PermissionResult.Deny;
            }

            return await base.CanAddAsync(value).AnyContext();
        }

        protected override async Task<Token> AddModelAsync(Token value) {
            value.Id = StringExtensions.GetNewToken();
            value.CreatedUtc = value.ModifiedUtc = DateTime.UtcNow;
            value.Type = TokenType.Access;
            value.CreatedBy = (await Request.GetUserAsync().AnyContext()).Id;

            // add implied scopes
            if (value.Scopes.Contains(AuthorizationRoles.GlobalAdmin))
                value.Scopes.Add(AuthorizationRoles.User);

            if (value.Scopes.Contains(AuthorizationRoles.User))
                value.Scopes.Add(AuthorizationRoles.Client);

            return await base.AddModelAsync(value).AnyContext();
        }

        protected override async Task<PermissionResult> CanDeleteAsync(Token value) {
            if (!String.IsNullOrEmpty(value.ProjectId) && !await IsInProjectAsync(value.ProjectId).AnyContext())
                return PermissionResult.DenyWithNotFound(value.Id);

            return await base.CanDeleteAsync(value).AnyContext();
        }

        private async Task<bool> IsInProjectAsync(string projectId) {
            if (String.IsNullOrEmpty(projectId))
                return false;

            return await IsInProjectAsync(await _projectRepository.GetByIdAsync(projectId, true).AnyContext()).AnyContext();
        }

        private Task<bool> IsInProjectAsync(Project value) {
            if (value == null)
                return Task.FromResult(false);

            return IsInOrganizationAsync(value.OrganizationId);
        }

        protected override Task CreateMapsAsync() {
            if (Mapper.FindTypeMapFor<NewToken, Token>() == null)
                Mapper.CreateMap<NewToken, Token>().ForMember(m => m.Type, m => m.Ignore());

            return base.CreateMapsAsync();
        }
    }
}