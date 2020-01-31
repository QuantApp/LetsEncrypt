// Copyright (c) Nate McMaster.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;

namespace McMaster.AspNetCore.LetsEncrypt
{
    /// <summary>
    /// Extensions for configuring certificate persistence
    /// </summary>
    public static class FileSystemStorageExtensions
    {
        /// <summary>
        /// Save Let's Encrypt data to a directory.
        /// Certificates are stored in the .pfx format, and account information is stored in JSON files.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="directory">The directory where .pfx files will be saved.</param>
        /// <param name="pfxPassword">Set to null or empty for passwordless .pfx files.</param>
        /// <returns></returns>
        public static ILetsEncryptServiceBuilder PersistDataToDirectory(
            this ILetsEncryptServiceBuilder builder,
            DirectoryInfo directory,
            string? pfxPassword)
        {
            if (builder is null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (directory is null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            var fileSystemRepo = new FileSystemCertificateRepository(directory, pfxPassword);
            builder.Services
                .AddSingleton<ICertificateRepository>(fileSystemRepo)
                .AddSingleton<IAccountRepository>(fileSystemRepo);
            return builder;
        }
    }
}
