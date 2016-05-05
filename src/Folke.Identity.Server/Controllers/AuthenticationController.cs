﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Folke.Identity.Server.Enumeration;
using Folke.Identity.Server.Services;
using Folke.Identity.Server.Views;
using Folke.Mvc.Extensions;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Mvc;
using Microsoft.Extensions.Logging;

namespace Folke.Identity.Server.Controllers
{
    [Route("api/authentication")]
    public class AuthenticationController<TUser, TKey, TUserView> : TypedControllerBase
         where TKey : IEquatable<TKey>
         where TUser : class
         where TUserView : class
    {
        private readonly ILogger<AuthenticationController<TUser, TKey, TUserView>> logger;
        protected IUserService<TUser, TUserView> UserService { get; }
        protected UserManager<TUser> UserManager { get; }
        protected SignInManager<TUser> SignInManager { get; }
        protected IUserEmailService<TUser> EmailService { get; }

        public AuthenticationController(IUserService<TUser, TUserView> userService,
            UserManager<TUser> userManager,
            SignInManager<TUser> signInManager, 
            IUserEmailService<TUser> emailService, 
            ILogger<AuthenticationController<TUser, TKey, TUserView>> logger)
        {
            this.logger = logger;
            UserService = userService;
            SignInManager = signInManager;
            UserManager = userManager;
            EmailService = emailService;
        }

        [HttpPut("login")]
        public async Task<IHttpActionResult<LoginResultView>> Login([FromBody] LoginView loginView)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest<LoginResultView>(ModelState);
            }

            var result =
                await
                    SignInManager.PasswordSignInAsync(loginView.Email, loginView.Password, loginView.RememberMe,
                        lockoutOnFailure: false);
            if (result.Succeeded)
            {
                return Ok(new LoginResultView { Status = LoginStatusEnum.Success });
            }

            if (result.IsLockedOut)
            {
                return Ok(new LoginResultView { Status = LoginStatusEnum.LockedOut });
            }

            if (result.RequiresTwoFactor)
            {
                return Ok(new LoginResultView { Status = LoginStatusEnum.RequiresVerification });
            }
            
            // TODO localization
            return BadRequest<LoginResultView>("Mot-de-passe ou e-mail non valide");
        }

        [HttpPut("verifycode")]
        public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeView verifyCodeView)
        {
            if (!ModelState.IsValid)
            {
                return HttpBadRequest(ModelState);
            }

            var result =
                await
                    SignInManager.TwoFactorSignInAsync(verifyCodeView.Provider, verifyCodeView.Code,
                        isPersistent: verifyCodeView.RememberMe, rememberClient: verifyCodeView.RememberBrowser);
            if (result.Succeeded)
            {
                return Ok();
            }
            return HttpUnauthorized();
        }

        [HttpPost("register")]
        public async Task<IHttpActionResult<TUserView>> Register([FromBody] RegisterView registerView)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest<TUserView>(ModelState);
            }

            var user = UserService.CreateNewUser(registerView.Email, registerView.Email, false);
            
            var result = await UserManager.CreateAsync(user, registerView.Password);
            if (!result.Succeeded)
            {
                AddErrors(result);
                return BadRequest<TUserView>(ModelState);
            }
            await SignInManager.SignInAsync(user, isPersistent: false);

            var code = await UserManager.GenerateEmailConfirmationTokenAsync(user);
            await EmailService.SendEmailConfirmationEmail(user, code);
            return Created("GetAccount", Convert.ChangeType(await UserManager.GetUserIdAsync(user), typeof(TKey)), UserService.MapToUserView(user));
        }


        [HttpPut("send-account-confirm")]
        public async Task<IActionResult> SendAccountConfirm([FromQuery]int userId)
        {
            var user = await GetCurrentUserAsync();
            var code = await UserManager.GenerateEmailConfirmationTokenAsync(user);
            await EmailService.SendEmailConfirmationEmail(user, code);
            return Ok();
        }

        private Task<TUser> GetCurrentUserAsync()
        {
            return UserManager.FindByIdAsync(HttpContext.User.GetUserId());
        }

        [HttpPut("confirm-email")]
        public async Task<IActionResult> ConfirmEmail([FromQuery]int userId, [FromQuery]string code)
        {
            if (code == null)
            {
                return HttpBadRequest();
            }

            var user = await GetCurrentUserAsync();
            var result = await UserManager.ConfirmEmailAsync(user, code);
            if (!result.Succeeded)
            {
                AddErrors(result);
                return HttpBadRequest(ModelState);
            }
            return Ok();
        }

        [HttpPut("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordView forgotPasswordView)
        {
            if (!ModelState.IsValid)
            {
                return HttpBadRequest(ModelState);
            }

            var user = await UserManager.FindByEmailAsync(forgotPasswordView.Email);
            if (user == null)
            {
                return HttpBadRequest();
            }

            string code = await UserManager.GeneratePasswordResetTokenAsync(user);
            await EmailService.SendPasswordResetEmail(user, code);
            return Ok();
        }

        [HttpPut("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordView resetPasswordView)
        {
            if (!ModelState.IsValid)
            {
                return HttpBadRequest(ModelState);
            }

            TUser user;
            if (!string.IsNullOrEmpty(resetPasswordView.Email))
            {
                user = await UserManager.FindByEmailAsync(resetPasswordView.Email);

            }
            else
            {
                user = await UserManager.FindByIdAsync(resetPasswordView.UserId);
            }

            var result =
                await UserManager.ResetPasswordAsync(user, resetPasswordView.Code, resetPasswordView.Password);
            if (!result.Succeeded)
            {
                AddErrors(result);
                return HttpBadRequest(ModelState);
            }
            return Ok();
        }

        [HttpGet("link-external-login")]
        public IActionResult LinkLogin([FromQuery]string provider)
        {
            // Request a redirect to the external login provider to link a login for the current user
            var redirectUrl = Request.Scheme + "://" + Request.Host + "/api/authentication/link-callback";
            var properties = SignInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl, User.GetUserId());
            return new ChallengeResult(provider, properties);
        }
        
        [HttpGet("link-callback")]
        public async Task<ActionResult> LinkLoginCallback()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return View("ExternalLoginCallback", "failure");
            }
            var info = await SignInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                return View("ExternalLoginCallback", "failure");
            }
            var result = await UserManager.AddLoginAsync(user, info);
            return View("ExternalLoginCallback", result.Succeeded ? "success" : "failure"); ;
        }

        [HttpGet("external-login")]
        public IActionResult ExternalLogin([FromQuery] string provider, [FromQuery] string returnUrl)
        {
            var redirectUrl = Request.Scheme + "://" + Request.Host + "/api/authentication/callback";
            var properties = SignInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
            return new ChallengeResult(provider, properties);
        }

        [HttpGet("callback")]
        public async Task<ActionResult> ExternalLoginCallback([FromQuery]string returnUrl)
        {
            var loginInfo = await SignInManager.GetExternalLoginInfoAsync();
            if (loginInfo == null)
            {
                return View((object)"failure");
            }

            var result = await SignInManager.ExternalLoginSignInAsync(loginInfo.LoginProvider, loginInfo.ProviderKey, isPersistent: false);
            if (result.Succeeded)
            {
                return View((object)"success");
            }
            if (result.IsLockedOut)
            {
                return View((object)"lockedout");
            }
            if (result.RequiresTwoFactor)
            {
                return View((object)"requires-verification");
            }

            string userName;
            if (loginInfo.ExternalPrincipal.GetUserName() != null)
            {
                logger.LogInformation(
                    $"Proposed external principal user name: {loginInfo.ExternalPrincipal.GetUserName()}");
                userName = loginInfo.ExternalPrincipal.GetUserName();
            }
            else
            {
                userName = Guid.NewGuid().ToString("N");
            }
            userName = Regex.Replace(userName, @"[^a-zA-Z0-9]", "", RegexOptions.CultureInvariant);
            while (await UserManager.FindByNameAsync(userName) != null)
                userName += Guid.NewGuid().ToString("N")[0];
            logger.LogInformation($"Creating new user {userName}");
            var email = loginInfo.ExternalPrincipal.FindFirstValue(ClaimTypes.Email);
            if (await UserManager.FindByEmailAsync(email) != null)
            {
                return View((object) "password");
            }

            var user = UserService.CreateNewUser(userName, email, true);
            var creationResult = await UserManager.CreateAsync(user);
            if (creationResult.Succeeded)
            {
                creationResult = await UserManager.AddLoginAsync(user, loginInfo);
                if (creationResult.Succeeded)
                {
                    await SignInManager.SignInAsync(user, isPersistent: false);
                    return View((object)"success");
                }
            }
            return View((object)"failure");
        }

        [HttpGet("send-code")]
        public async Task<IHttpActionResult<SendCodeView>> GetSendCode([FromQuery] bool rememberMe)
        {
            var user = await SignInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
            {
                return BadRequest<SendCodeView>("No user");
            }
            var userFactors = await UserManager.GetValidTwoFactorProvidersAsync(user);
            return Ok(new SendCodeView { RememberMe = rememberMe, Providers = userFactors });
        }

        [HttpDelete("")]
        public async Task LogOff()
        {
            await SignInManager.SignOutAsync();
        }

        [HttpGet("external-login-providers")]
        public IEnumerable<AuthenticationDescription> GetExternalAuthenticationProviders()
        {
            return SignInManager.GetExternalAuthenticationSchemes();
        }

        [HttpGet("external-logins")]
        public async Task<IEnumerable<UserLoginInfo>> GetExternalLogins()
        {
            return await UserManager.GetLoginsAsync(await GetCurrentUserAsync());
        }

        private void AddErrors(IdentityResult result)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error.Description);
            }
        }
    }
}
