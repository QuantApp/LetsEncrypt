using System;

namespace McMaster.AspNetCore.LetsEncrypt.Accounts
{
    public class AccountModel
    {
        public string[] EmailAddresses { get; set; }
        public byte[] KeyMaterial { get; set; }
        public Uri DirectoryUri { get; set; }
    }
}
