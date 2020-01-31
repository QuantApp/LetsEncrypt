﻿// Copyright (c) Nate McMaster.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using McMaster.AspNetCore.LetsEncrypt.Accounts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#if NETSTANDARD2_0
using IHostEnvironment = Microsoft.Extensions.Hosting.IHostingEnvironment;
#endif

namespace McMaster.AspNetCore.LetsEncrypt.Internal
{
    internal class CertificateFactory
    {
        private readonly IOptions<LetsEncryptOptions> _options;
        private readonly IHttpChallengeResponseStore _challengeStore;
        private readonly IAccountRepository _accountRepository;
        private readonly ILogger _logger;
        private AcmeContext? _context;
        private IAccountContext? _accountContext;

        public CertificateFactory(
            IOptions<LetsEncryptOptions> options,
            IHttpChallengeResponseStore challengeStore,
            IAccountRepository accountRepository,
            ILogger logger,
            IHostEnvironment env)
        {
            _options = options;
            _challengeStore = challengeStore;
            _accountRepository = accountRepository;
            _logger = logger;
            AcmeServer = GetAcmeServer(_options.Value, env);
        }

        public Uri AcmeServer { get; }

        public async Task GetOrRegisterAccountAsync(CancellationToken cancellationToken)
        {
            var account = await _accountRepository.GetAccountAsync(cancellationToken);

            var acmeAccountKey = account != null
                ? KeyFactory.FromDer(account.KeyMaterial)
                : null;

            _context = new AcmeContext(AcmeServer, acmeAccountKey);

            if (account != null && await ExistingAccountIsValidAsync(_context))
            {
                return;
            }

            await CreateAccount(cancellationToken);

            _logger.LogDebug("Using Let's Encrypt Account {accountId}", _accountContext?.Location);
        }

        private async Task CreateAccount(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Debug.Assert(_context != null);

            var tosUri = await _context.TermsOfService();

            EnsureAgreementToTermsOfServices(tosUri);

            var options = _options.Value;
            _logger.LogInformation("Creating certificate registration for {email}", options.EmailAddress);
            _accountContext = await _context.NewAccount(options.EmailAddress, termsOfServiceAgreed: true);
            _logger.LogAcmeAction("NewRegistration", _accountContext);

            var account = new AccountModel
            {
                EmailAddresses = new[] { options.EmailAddress },
                KeyMaterial = _context.AccountKey.ToDer(),
                DirectoryUri = _context.DirectoryUri,
            };

            await _accountRepository.SaveAccountAsync(account, cancellationToken);
        }

        private async Task<bool> ExistingAccountIsValidAsync(AcmeContext context)
        {
            // double checks the account is still valid
            Account existingAccount;
            try
            {
                _accountContext = await context.Account();
                existingAccount = await _accountContext.Resource();
            }
            catch (AcmeRequestException exception)
            {
                _logger.LogWarning(
                    "An account key for a Let's Encrypt account was found, but could not be matched to a valid account. Validation error: {acmeError}",
                    exception.Error);
                return false;
            }

            if (existingAccount.Status != AccountStatus.Valid)
            {
                _logger.LogWarning(
                    "An account key for a Let's Encrypt account was found, but the account is no longer valid. Account status: {status}." +
                    "A new account will be registered.",
                    existingAccount.Status);
                return false;
            }

            if (existingAccount.TermsOfServiceAgreed != true)
            {
                var tosUri = await _context.TermsOfService();
                EnsureAgreementToTermsOfServices(tosUri);
                await _accountContext.Update(agreeTermsOfService: true);
            }

            return true;
        }

        public async Task<X509Certificate2> CreateCertificateAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_context != null);

            cancellationToken.ThrowIfCancellationRequested();
            var order = await _context.NewOrder(_options.Value.DomainNames);

            cancellationToken.ThrowIfCancellationRequested();
            var authorizations = await order.Authorizations();

            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(BeginValidateAllAuthorizations(authorizations, cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            return await CompleteCertificateRequestAsync(order, cancellationToken);
        }

        /// <summary>
        /// The uri to the server that implements the ACME protocol for certificate generation.
        /// </summary>
        internal static Uri GetAcmeServer(LetsEncryptOptions options, IHostEnvironment env)
        {
            var useStaging = options.UseStagingServerExplicitlySet
                ? options.UseStagingServer
                : env.IsDevelopment();

            return useStaging
                ? WellKnownServers.LetsEncryptStagingV2
                : WellKnownServers.LetsEncryptV2;
        }

        private IEnumerable<Task> BeginValidateAllAuthorizations(IEnumerable<IAuthorizationContext> authorizations, CancellationToken cancellationToken)
        {
            foreach (var authorization in authorizations)
            {
                yield return ValidateDomainOwnershipAsync(authorization, cancellationToken);
            }
        }

        private void EnsureAgreementToTermsOfServices(Uri tosUri)
        {
            if (_options.Value.AcceptTermsOfService)
            {
                _logger.LogDebug("Terms of service has been accepted");
                return;
            }

            if (!Console.IsInputRedirected)
            {
                Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("By proceeding, you must agree with Let's Encrypt terms of services.");
                Console.WriteLine(tosUri);
                Console.Write("Do you accept? [Y/n] ");
                Console.ResetColor();
                try
                {
                    Console.CursorVisible = true;
                }
                catch { }

                var result = Console.ReadLine().Trim();

                try
                {
                    Console.CursorVisible = false;
                }
                catch { }

                if (string.IsNullOrEmpty(result)
                    || string.Equals("y", result, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _logger.LogError($"You must accept the terms of service to continue.");
            throw new InvalidOperationException("Could not automatically accept the terms of service");
        }

        private async Task ValidateDomainOwnershipAsync(IAuthorizationContext authorizationContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var authorization = await authorizationContext.Resource();
            var domainName = authorization.Identifier.Value;

            _logger.LogDebug("Requesting authorization to create certificate for {domainName}", domainName);

            cancellationToken.ThrowIfCancellationRequested();

            var httpChallenge = await authorizationContext.Http();

            cancellationToken.ThrowIfCancellationRequested();

            if (httpChallenge == null)
            {
                throw new InvalidOperationException($"Did not receive challenge information for challenge type {ChallengeTypes.Http01}");
            }

            var keyAuth = httpChallenge.KeyAuthz;
            _challengeStore.AddChallengeResponse(httpChallenge.Token, keyAuth);

            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogDebug("Requesting completion of challenge to prove ownership of domain {domainName}", domainName);

            var challenge = await httpChallenge.Validate();

            var retries = 60;
            var delay = TimeSpan.FromSeconds(2);

            while (retries > 0)
            {
                retries--;

                cancellationToken.ThrowIfCancellationRequested();

                authorization = await authorizationContext.Resource();

                _logger.LogAcmeAction("GetAuthorization", authorization);

                switch (authorization.Status)
                {
                    case AuthorizationStatus.Valid:
                        return;
                    case AuthorizationStatus.Pending:
                        await Task.Delay(delay);
                        continue;
                    case AuthorizationStatus.Invalid:
                        throw InvalidAuthorizationError(authorization);
                    case AuthorizationStatus.Revoked:
                        throw new InvalidOperationException($"The authorization to verify domainName '{domainName}' has been revoked.");
                    case AuthorizationStatus.Expired:
                        throw new InvalidOperationException($"The authorization to verify domainName '{domainName}' has expired.");
                    default:
                        throw new ArgumentOutOfRangeException("Unexpected response from server while validating domain ownership.");
                }
            }

            throw new TimeoutException("Timed out waiting for domain ownership validation.");
        }

        private Exception InvalidAuthorizationError(Authorization authorization)
        {
            var reason = "unknown";
            var domainName = authorization.Identifier.Value;
            try
            {
                var errors = authorization.Challenges.Where(a => a.Error != null).Select(a => a.Error)
                    .Select(error => $"{error.Type}: {error.Detail}, Code = {error.Status}");
                reason = string.Join("; ", errors);
            }
            catch
            {
                _logger.LogTrace("Could not determine reason why validation failed. Response: {resp}", authorization);
            }

            _logger.LogError("Failed to validate ownership of domainName '{domainName}'. Reason: {reason}", domainName, reason);

            return new InvalidOperationException($"Failed to validate ownership of domainName '{domainName}'");
        }

        private async Task<X509Certificate2> CompleteCertificateRequestAsync(IOrderContext order, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commonName = _options.Value.DomainNames[0];
            _logger.LogDebug("Creating cert for {commonName}", commonName);

            var csrInfo = new CsrInfo
            {
                CommonName = commonName,
            };
            var privateKey = KeyFactory.NewKey((Certes.KeyAlgorithm)_options.Value.KeyAlgorithm);
            var acmeCert = await order.Generate(csrInfo, privateKey);


            _logger.LogAcmeAction("NewCertificate", acmeCert);

            var pfxBuilder = acmeCert.ToPfx(privateKey);
            var pfx = pfxBuilder.Build("Let's Encrypt - " + _options.Value.DomainNames, string.Empty);
            return new X509Certificate2(pfx, string.Empty, X509KeyStorageFlags.Exportable);
        }
    }
}
