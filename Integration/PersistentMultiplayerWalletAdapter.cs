using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BDVM.Domain;
using MPAPI.Interfaces;
using MPAPI.Types;

namespace BDVM.Adapters;

public sealed class PersistentMultiplayerWalletAdapter
{
    private readonly IServer server;
    private readonly IPersistentPlayerWallets wallets;

    public PersistentMultiplayerWalletAdapter(IServer server)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        wallets = server as IPersistentPlayerWallets ?? throw new InvalidOperationException("MultiplayerAPI does not expose persistent individual wallets.");
    }

    public long EnsureAndRead(string playerId, string correlationId, long initialBalance)
    {
        if (initialBalance < 0) throw new ArgumentOutOfRangeException(nameof(initialBalance));
        var player = RequirePlayer(playerId);
        RequireSuccess(wallets.EnsureIndividualBalance(player, RequestId("ensure", playerId), initialBalance), "initialize");
        return RequireAmount(RequireSuccess(wallets.ReadIndividualBalance(player, RequestId("read", correlationId)), "read").Balance);
    }

    public bool TryDebit(string playerId, string operationId, long amount, out long balance)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var result = wallets.DebitIndividualBalance(RequirePlayer(playerId), RequestId("debit", operationId), amount);
        if (result.Status == IndividualWalletStatus.InsufficientFunds)
        {
            balance = RequireAmount(result.Balance);
            return false;
        }
        balance = RequireAmount(RequireSuccess(result, "debit").Balance);
        return true;
    }

    public long Credit(string playerId, string operationId, long amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        return RequireAmount(RequireSuccess(wallets.CreditIndividualBalance(RequirePlayer(playerId), RequestId("credit", operationId), amount), "credit").Balance);
    }

    private IPlayer RequirePlayer(string playerId)
    {
        var matches = server.Players.Where(player => player is IPersistentPlayerIdentity persistent &&
            string.Equals(PlayerIdentity.FromMultiplayerGuid(persistent.PersistentId), playerId, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new UnauthorizedAccessException("The persistent player wallet is unavailable for this authenticated actor.");
        return matches[0];
    }

    private static IndividualWalletResult RequireSuccess(IndividualWalletResult result, string operation)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Persistent individual wallet " + operation + " failed: " + result.Status + ".");
        return result;
    }

    private static long RequireAmount(double amount)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount) || amount < 0 || amount > long.MaxValue || Math.Truncate(amount) != amount)
            throw new InvalidDataException("Persistent individual wallet returned a non-integral or out-of-range balance.");
        return checked((long)amount);
    }

    private static Guid RequestId(string operation, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A stable wallet operation ID is required.", nameof(value));
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes("bdvm-wallet-v1|" + operation + "|" + value));
        var id = new byte[16];
        Buffer.BlockCopy(bytes, 0, id, 0, id.Length);
        return new Guid(id);
    }
}
