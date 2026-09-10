using System;

namespace BDVM.Domain;

public sealed class AuthenticatedActorVisibility
{
    public AuthenticatedActorVisibility(string playerId, string? companyId)
    {
        PlayerId = string.IsNullOrWhiteSpace(playerId) ? throw new ArgumentException("A persistent player ID is required.", nameof(playerId)) : playerId;
        CompanyId = string.IsNullOrWhiteSpace(companyId) ? null : companyId;
    }

    public string PlayerId { get; }
    public string? CompanyId { get; }

    public bool CanView(AccountRef? account)
        => account != null && ((account.Kind == AccountKind.Player && string.Equals(account.OwnerId, PlayerId, StringComparison.Ordinal)) ||
            (account.Kind == AccountKind.Company && CompanyId != null && string.Equals(account.OwnerId, CompanyId, StringComparison.Ordinal)));

    public bool CanView(AssetOwnerRef? owner)
        => owner != null && ((owner.Kind == AssetOwnerKind.Player && string.Equals(owner.OwnerId, PlayerId, StringComparison.Ordinal)) ||
            (owner.Kind == AssetOwnerKind.Company && CompanyId != null && string.Equals(owner.OwnerId, CompanyId, StringComparison.Ordinal)));

    public bool IsPlayer(string? playerId) => string.Equals(playerId, PlayerId, StringComparison.Ordinal);
    public bool IsCompany(string? companyId) => CompanyId != null && string.Equals(companyId, CompanyId, StringComparison.Ordinal);
}
