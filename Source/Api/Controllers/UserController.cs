﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using Exceptionless.Api.Extensions;
using Exceptionless.Api.Models;
using Exceptionless.Api.Utility;
using Exceptionless.Core.Authorization;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Mail;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Models;
using FluentValidation;
using NLog.Fluent;

namespace Exceptionless.Api.Controllers {
    [RoutePrefix(API_PREFIX + "/users")]
    [Authorize(Roles = AuthorizationRoles.User)]
    public class UserController : RepositoryApiController<IUserRepository, User, ViewUser, User, UpdateUser> {
        private readonly IOrganizationRepository _organizationRepository;
        private readonly IMailer _mailer;

        public UserController(IUserRepository userRepository, IOrganizationRepository organizationRepository, IMailer mailer) : base(userRepository) {
            _organizationRepository = organizationRepository;
            _mailer = mailer;
        }

        /// <summary>
        /// Get current user
        /// </summary>
        /// <response code="404">The current user could not be found.</response>
        [HttpGet]
        [Route("me")]
        [ResponseType(typeof(ViewCurrentUser))]
        public async Task<IHttpActionResult> GetCurrentUserAsync() {
            var currentUser = await GetModelAsync((await GetExceptionlessUserAsync().AnyContext()).Id).AnyContext();
            if (currentUser == null)
                return NotFound();

            return Ok(new ViewCurrentUser(currentUser));
        }

        /// <summary>
        /// Get by id
        /// </summary>
        /// <param name="id">The identifier of the user.</param>
        /// <response code="404">The user could not be found.</response>
        [HttpGet]
        [Route("{id:objectid}", Name = "GetUserById")]
        [ResponseType(typeof(ViewUser))]
        public override Task<IHttpActionResult> GetByIdAsync(string id) {
            return base.GetByIdAsync(id);
        }

        /// <summary>
        /// Get by organization
        /// </summary>
        /// <param name="organizationId">The identifier of the organization.</param>
        /// <param name="page">The page parameter is used for pagination. This value must be greater than 0.</param>
        /// <param name="limit">A limit on the number of objects to be returned. Limit can range between 1 and 100 items.</param>
        /// <response code="404">The organization could not be found.</response>
        [HttpGet]
        [Route("~/" + API_PREFIX + "/organizations/{organizationId:objectid}/users")]
        [ResponseType(typeof(List<ViewUser>))]
        public async Task<IHttpActionResult> GetByOrganizationAsync(string organizationId, int page = 1, int limit = 10) {
            if (!await CanAccessOrganizationAsync(organizationId).AnyContext())
                return NotFound();

            var users = (await MapCollectionAsync<ViewUser>((await _repository.GetByOrganizationIdAsync(organizationId).AnyContext()).Documents, true).AnyContext()).ToList();
            var organization = await _organizationRepository.GetByIdAsync(organizationId, true).AnyContext();
            if (organization.Invites.Any())
                users.AddRange(organization.Invites.Select(i => new ViewUser { EmailAddress = i.EmailAddress, IsInvite = true }));

            page = GetPage(page);
            limit = GetLimit(limit);
            return OkWithResourceLinks(users.Skip(GetSkip(page, limit)).Take(limit).ToList(), users.Count > limit, page);
        }

        /// <summary>
        /// Update
        /// </summary>
        /// <param name="id">The identifier of the user.</param>
        /// <param name="changes">The changes</param>
        /// <response code="400">An error occurred while updating the user.</response>
        /// <response code="404">The user could not be found.</response>
        [HttpPatch]
        [HttpPut]
        [Route("{id:objectid}")]
        public override Task<IHttpActionResult> PatchAsync(string id, Delta<UpdateUser> changes) {
            return base.PatchAsync(id, changes);
        }

        /// <summary>
        /// Update email address
        /// </summary>
        /// <param name="id">The identifier of the user.</param>
        /// <param name="email">The new email address.</param>
        /// <response code="400">An error occurred while updating the users email address.</response>
        /// <response code="404">The user could not be found.</response>
        [HttpPost]
        [Route("{id:objectid}/email-address/{email:minlength(1)}")]
        [ResponseType(typeof(UpdateEmailAddressResult))]
        public async Task<IHttpActionResult> UpdateEmailAddressAsync(string id, string email) {
            var user = await GetModelAsync(id, false).AnyContext();
            if (user == null)
                return NotFound();

            email = email.ToLower();
            if (!await IsEmailAddressAvailableInternalAsync(email).AnyContext())
                return BadRequest("A user with this email address already exists.");

            if (String.Equals((await GetExceptionlessUserAsync().AnyContext()).EmailAddress, email, StringComparison.OrdinalIgnoreCase))
                return Ok(new UpdateEmailAddressResult { IsVerified = user.IsEmailAddressVerified });

            user.EmailAddress = email;
            user.IsEmailAddressVerified = user.OAuthAccounts.Count(oa => String.Equals(oa.EmailAddress(), email, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!user.IsEmailAddressVerified)
                user.CreateVerifyEmailAddressToken();

            try {
                await _repository.SaveAsync(user);
            } catch (ValidationException ex) {
                return BadRequest(String.Join(", ", ex.Errors));
            } catch (Exception ex) {
                Log.Error().Exception(ex).Property("User", user).ContextProperty("HttpActionContext", ActionContext).Write();
                return BadRequest("An error occurred.");
            }

            if (!user.IsEmailAddressVerified)
                await ResendVerificationEmailAsync(id).AnyContext();

            return Ok(new UpdateEmailAddressResult { IsVerified = user.IsEmailAddressVerified });
        }

        /// <summary>
        /// Verify email address
        /// </summary>
        /// <param name="token">The token identifier.</param>
        /// <response code="400">Verify Email Address Token has expired.</response>
        /// <response code="404">The user could not be found.</response>
        [HttpGet]
        [Route("verify-email-address/{token:token}")]
        public async Task<IHttpActionResult> VerifyAsync(string token) {
            var user = await _repository.GetByVerifyEmailAddressTokenAsync(token).AnyContext();
            if (user == null)
                return NotFound();

            if (!user.HasValidVerifyEmailAddressTokenExpiration())
                return BadRequest("Verify Email Address Token has expired.");

            user.MarkEmailAddressVerified();
            await _repository.SaveAsync(user).AnyContext();

            //ExceptionlessClient.Default.CreateFeatureUsage("Verify Email Address").AddObject(user).Submit();
            return Ok();
        }

        /// <summary>
        /// Resend verification email
        /// </summary>
        /// <param name="id">The identifier of the user.</param>
        /// <response code="404">The user could not be found.</response>
        [HttpGet]
        [Route("{id:objectid}/resend-verification-email")]
        public async Task<IHttpActionResult> ResendVerificationEmailAsync(string id) {
            var user = await GetModelAsync(id, false).AnyContext();
            if (user == null)
                return NotFound();
            
            if (!user.IsEmailAddressVerified) {
                user.CreateVerifyEmailAddressToken();
                await _repository.SaveAsync(user).AnyContext();
                await _mailer.SendVerifyEmailAsync(user).AnyContext();
            }

            return Ok();
        }

        [HttpPost]
        [Route("{id:objectid}/admin-role")]
        [OverrideAuthorization]
        [Authorize(Roles = AuthorizationRoles.GlobalAdmin)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IHttpActionResult> AddAdminRoleAsync(string id) {
            var user = await GetModelAsync(id, false).AnyContext();
            if (user == null)
                return NotFound();

            if (!user.Roles.Contains(AuthorizationRoles.GlobalAdmin)) {
                user.Roles.Add(AuthorizationRoles.GlobalAdmin);
                await _repository.SaveAsync(user, true).AnyContext();
            }

            return Ok();
        }

        [HttpDelete]
        [Route("{id:objectid}/admin-role")]
        [OverrideAuthorization]
        [Authorize(Roles = AuthorizationRoles.GlobalAdmin)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IHttpActionResult> DeleteAdminRoleAsync(string id) {
            var user = await GetModelAsync(id, false).AnyContext();
            if (user == null)
                return NotFound();

            if (user.Roles.Contains(AuthorizationRoles.GlobalAdmin)) {
                user.Roles.Remove(AuthorizationRoles.GlobalAdmin);
                await _repository.SaveAsync(user, true).AnyContext();
            }

            return StatusCode(HttpStatusCode.NoContent);
        }

        private async Task<bool> IsEmailAddressAvailableInternalAsync(string email) {
            if (String.IsNullOrWhiteSpace(email))
                return false;

            var loggedInUser = await GetExceptionlessUserAsync().AnyContext();
            if (loggedInUser != null && String.Equals(loggedInUser.EmailAddress, email, StringComparison.OrdinalIgnoreCase))
                return true;

            return await _repository.GetByEmailAddressAsync(email).AnyContext() == null;
        }

        protected override async Task<User> GetModelAsync(string id, bool useCache = true) {
            if (Request.IsGlobalAdmin() || String.Equals((await GetExceptionlessUserAsync().AnyContext()).Id, id))
                return await base.GetModelAsync(id, useCache).AnyContext();

            return null;
        }

        protected override async Task<ICollection<User>> GetModelsAsync(string[] ids, bool useCache = true) {
            if (Request.IsGlobalAdmin())
                return await base.GetModelsAsync(ids, useCache);

            var loggedInUser = await GetExceptionlessUserAsync().AnyContext();
            return await base.GetModelsAsync(ids.Where(id => String.Equals(loggedInUser.Id, id)).ToArray(), useCache);
        }
    }
}