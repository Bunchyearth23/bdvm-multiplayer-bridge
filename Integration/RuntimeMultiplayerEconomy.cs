using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BDVM.Domain;
using MPAPI.Interfaces;

namespace BDVM.Adapters;

public sealed class PersistentMultiplayerPeerIdentityResolver : IMultiplayerPeerIdentityResolver
{
    public bool TryResolve(IPlayer peer, out string durablePlayerId)
    {
        try { durablePlayerId = MultiplayerPlayerIdentityAdapter.RequirePersistentPlayerId(peer); return true; }
        catch { durablePlayerId = ""; return false; }
    }
}

public sealed class RuntimeCompanyIntentExecutor : ICompanyIntentExecutor
{
    private readonly AcquisitionRuntimeStateProvider state;
    private readonly INetworkRoleDetector authority;
    private readonly IExistingVehicleOwnershipAdapter world;
    private readonly IAcquisitionCheckpointSink checkpoints;
    private readonly Action<string> stage;
    private readonly Action<string> log;
    private readonly IReadOnlyList<string> starterBundleDefinitionIds;
    private readonly IAuthoritativeModuleIntentExecutor? moduleExecutor;
    private readonly long startingPersonalBalance;

    public RuntimeCompanyIntentExecutor(AcquisitionRuntimeStateProvider state, INetworkRoleDetector authority,
        IExistingVehicleOwnershipAdapter world, IAcquisitionCheckpointSink checkpoints, Action<string> stage, Action<string> log,
        IReadOnlyList<string>? starterBundleDefinitionIds = null, IAuthoritativeModuleIntentExecutor? moduleExecutor = null,
        long startingPersonalBalance = 2000)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        this.stage = stage ?? throw new ArgumentNullException(nameof(stage));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.starterBundleDefinitionIds = starterBundleDefinitionIds ?? Array.Empty<string>();
        this.moduleExecutor = moduleExecutor;
        if (startingPersonalBalance < 0) throw new ArgumentOutOfRangeException(nameof(startingPersonalBalance));
        this.startingPersonalBalance = startingPersonalBalance;
    }

    public ProtocolResult Execute(PeerContext peer, CompanyProtocolEnvelope envelope, CompanyIntent intent)
    {
        if (!NetworkAuthorityPolicy.CanExecuteEconomy(authority.Detect(), out var refusal)) return Rejected(envelope, "host-authority-required", refusal);
        state.EnsurePersistentPlayer(peer.AuthenticatedPlayerId, startingPersonalBalance);
        if (starterBundleDefinitionIds.Count > 0)
            state.GrantStarterBundleFor("starter-bundle:" + peer.AuthenticatedPlayerId, peer.AuthenticatedPlayerId, starterBundleDefinitionIds, authority);
        var economy = state.Current!.Economy;
        string code;
        switch (intent.Type)
        {
            case CompanyIntentType.CreateCompany:
                code = state.CreateCompanyFor(envelope.RequestId, peer.AuthenticatedPlayerId, intent.Name).ResultCode;
                break;
            case CompanyIntentType.ApplyToCompany:
                code = state.ApplyToCompanyFor(envelope.RequestId, peer.AuthenticatedPlayerId, envelope.CompanyId, authority).ResultCode;
                break;
            case CompanyIntentType.InvitePlayer:
                code = state.InvitePlayerFor(envelope.RequestId, peer.AuthenticatedPlayerId, envelope.CompanyId, intent.TargetPlayerId, authority).ResultCode;
                break;
            case CompanyIntentType.ChangePermission:
                if (!Enum.TryParse(intent.Permission, true, out CompanyPermission permission)) return Rejected(envelope, "invalid-permission", intent.Permission);
                code = state.SetPermissionFor(envelope.RequestId, peer.AuthenticatedPlayerId, envelope.CompanyId, intent.TargetPlayerId, permission, intent.Enabled, authority).ResultCode;
                break;
            case CompanyIntentType.DecideApplication:
                code = state.DecideApplicationFor(envelope.RequestId, peer.AuthenticatedPlayerId, intent.MembershipRequestId, intent.Enabled, authority).ResultCode;
                break;
            case CompanyIntentType.RespondToInvitation:
                code = state.RespondToInvitationFor(envelope.RequestId, peer.AuthenticatedPlayerId, intent.MembershipRequestId, intent.Enabled, authority).ResultCode;
                break;
            case CompanyIntentType.ChangeMembershipPolicy:
                if (!Enum.TryParse(intent.MembershipPolicy, true, out MembershipPolicy policy)) return Rejected(envelope, "invalid-membership-policy", intent.MembershipPolicy);
                code = state.SetMembershipPolicyFor(envelope.RequestId, peer.AuthenticatedPlayerId, envelope.CompanyId, policy, authority).ResultCode;
                break;
            case CompanyIntentType.LeaveCompany:
                code = state.LeaveCompanyFor(envelope.RequestId, peer.AuthenticatedPlayerId, authority).ResultCode;
                break;
            case CompanyIntentType.TransferLeadership:
                code = state.TransferLeadershipFor(envelope.RequestId, peer.AuthenticatedPlayerId, envelope.CompanyId, intent.TargetPlayerId, authority).ResultCode;
                break;
            case CompanyIntentType.DissolveCompany:
                code = state.DissolveCompanyFor(envelope.RequestId, peer.AuthenticatedPlayerId, envelope.CompanyId, intent.DebtAmount, intent.PenaltyAmount,
                    authority, new UnityAssetReleaseGuard(), world, new CompositeCompanyContractCancellationPort(
                        new CompanyWorkflowCancellationPort(state.Current!, authority),
                        new OutboundLeaseCompanyContractCancellationPort(state.Current!, authority, new UnityAssetReleaseGuard(), new DeclaredOffSceneLeaseSimulationPort()),
                        new FinancingCompanyContractCancellationPort(state.Current!, authority))).ResultCode;
                break;
            case CompanyIntentType.TransferFunds:
                var debit = ParseAccount(intent.SourceAccount);
                var credit = ParseAccount(intent.DestinationAccount);
                var debitWallet = economy.Wallets.Single(x => x.Account.Key == debit.Key);
                var creditWallet = economy.Wallets.Single(x => x.Account.Key == credit.Key);
                var transfer = new CompanyEconomyEngine(economy).Transfer(new EconomyCommand
                {
                    CommandId = envelope.RequestId,
                    RequesterId = peer.AuthenticatedPlayerId,
                    CompanyId = envelope.CompanyId,
                    ExpectedVersions = new Dictionary<string, long> { [debit.Key] = debitWallet.Version, [credit.Key] = creditWallet.Version }
                }, debit, credit, Convert.ToInt64(intent.Amount), TransferKind(debit, credit));
                code = transfer.ResultCode;
                break;
            case CompanyIntentType.AcquireVehicle:
                var buyer = ParseOwner(intent.Acquirer);
                var payer = ParseAccount(intent.Payer);
                var offer = state.Current.Offers.Single(x => x.OfferId == intent.OfferId && x.AssetId == intent.AssetId);
                var player = economy.Players.Single(x => x.PlayerId == peer.AuthenticatedPlayerId);
                var wallet = economy.Wallets.Single(x => x.Account.Key == payer.Key);
                var ownership = state.Current.Ownership.Single(x => x.AssetId == offer.AssetId);
                var versions = new Dictionary<string, long>
                {
                    ["player:" + player.PlayerId] = player.Version, [payer.Key] = wallet.Version,
                    ["offer:" + offer.OfferId] = offer.Version, ["asset:" + ownership.AssetId] = ownership.Version
                };
                if (buyer.Kind == AssetOwnerKind.Company) versions["company:" + buyer.OwnerId] = economy.Companies.Single(x => x.CompanyId == buyer.OwnerId).Version;
                var acquisition = new VehicleAcquisitionEngine(state.Current, world, authority, checkpoints).Acquire(new AcquireVehicleCommand
                {
                    CommandId = envelope.RequestId, RequesterId = peer.AuthenticatedPlayerId, OfferId = offer.OfferId,
                    AssetId = offer.AssetId, Buyer = buyer, Payer = payer, ExpectedVersions = versions
                });
                code = acquisition.ResultCode;
                break;
            case CompanyIntentType.ModuleOperation:
                if (moduleExecutor == null) return Rejected(envelope, "module-operation-unavailable", intent.ModuleAction);
                var moduleResult = moduleExecutor.Execute(peer.AuthenticatedPlayerId, envelope.RequestId, intent.ModuleAction, intent.ModulePayloadJson)
                    ?? Rejected(envelope, "module-executor-no-result", intent.ModuleAction);
                moduleResult.RequestId = envelope.RequestId;
                if (moduleResult.Status != ProtocolResultStatus.Succeeded && moduleResult.Status != ProtocolResultStatus.Rejected)
                    return Rejected(envelope, "module-result-not-terminal", intent.ModuleAction);
                log("[correlation=" + envelope.RequestId + "] [event=multiplayer-module-intent] player=" + peer.AuthenticatedPlayerId + ", action=" + intent.ModuleAction + ", status=" + moduleResult.Status + ", code=" + moduleResult.Code);
                return moduleResult;
            default:
                return Rejected(envelope, "unsupported-intent", intent.Type.ToString());
        }
        stage(envelope.RequestId);
        log("[correlation=" + envelope.RequestId + "] [event=multiplayer-intent] player=" + peer.AuthenticatedPlayerId + ", type=" + intent.Type + ", result=" + code);
        return new ProtocolResult { RequestId = envelope.RequestId, Status = Success(code) ? ProtocolResultStatus.Succeeded : ProtocolResultStatus.Rejected, Code = code, AuthoritativeVersion = economy.Commands.Count + economy.MembershipRequests.Count + economy.Ledger.Count };
    }

    private static bool Success(string code) => code == "created" || code == "transferred" || code == "acquired" || code == "permission-updated" || code == "membership-policy-updated" || code == "membership-policy-current" || code == "company-left" || code == "already-independent" || code == "leadership-transferred" || code == "dissolved" || code.StartsWith("membership-") || code.StartsWith("invitation-");
    private static ProtocolResult Rejected(CompanyProtocolEnvelope envelope, string code, string detail) => new ProtocolResult { RequestId = envelope.RequestId, Status = ProtocolResultStatus.Rejected, Code = code, Detail = detail };
    private static LedgerEntryKind TransferKind(AccountRef debit, AccountRef credit)
    {
        if (debit.Kind == AccountKind.Player && credit.Kind == AccountKind.Company) return LedgerEntryKind.Contribution;
        if (debit.Kind == AccountKind.Company && credit.Kind == AccountKind.Player) return LedgerEntryKind.Withdrawal;
        throw new InvalidDataException("unsupported-account-route");
    }
    private static AccountRef ParseAccount(string value)
    {
        var parts = (value ?? "").Split(new[] { ':' }, 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]) || !Enum.TryParse(parts[0], true, out AccountKind kind)) throw new InvalidDataException("invalid-account-reference");
        return new AccountRef { Kind = kind, OwnerId = parts[1] };
    }
    private static AssetOwnerRef ParseOwner(string value)
    {
        var parts = (value ?? "").Split(new[] { ':' }, 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]) || !Enum.TryParse(parts[0], true, out AssetOwnerKind kind)) throw new InvalidDataException("invalid-owner-reference");
        return new AssetOwnerRef { Kind = kind, OwnerId = parts[1] };
    }
}
