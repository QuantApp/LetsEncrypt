// Copyright (c) Nate McMaster.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using McMaster.AspNetCore.LetsEncrypt.Accounts;

namespace McMaster.AspNetCore.LetsEncrypt
{
    internal class FileSystemCertificateRepository : ICertificateRepository, IAccountRepository
    {
        private readonly string? _pfxPassword;
        private readonly DirectoryInfo _certDir;
        private readonly DirectoryInfo _accountDir;

        public FileSystemCertificateRepository(DirectoryInfo directory, string? pfxPassword)
        {
            _pfxPassword = pfxPassword;
            _certDir = directory.CreateSubdirectory("certificates");
            _accountDir = directory.CreateSubdirectory("accounts");
        }

        public Task<AccountModel?> GetAccountAsync(CancellationToken cancellationToken)
        {
            throw new System.NotImplementedException();
        }

        public Task SaveAccountAsync(AccountModel account, CancellationToken cancellationToken)
        {
            throw new System.NotImplementedException();
        }

        public Task SaveAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
        {
            _certDir.Create();

            var tmpFile = Path.GetTempFileName();
            File.WriteAllBytes(
                tmpFile,
                certificate.Export(X509ContentType.Pfx, _pfxPassword));

            var fileName = certificate.Thumbprint + ".pfx";
            var output = Path.Combine(_certDir.FullName, fileName);

            // File.Move is an atomic operation on most operating systems. By writing to a temporary file
            // first and then moving it, it avoids potential race conditions with readers.

            File.Move(tmpFile, output);

            return Task.CompletedTask;
        }
    }
}
