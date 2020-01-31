using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace McMaster.AspNetCore.LetsEncrypt.Accounts
{
    public interface IAccountRepository
    {
        public Task SaveAccountAsync(AccountModel account, CancellationToken cancellationToken);
        public Task<AccountModel?> GetAccountAsync(CancellationToken cancellationToken);
    }
}
