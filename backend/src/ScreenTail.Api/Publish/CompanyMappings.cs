using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Publish;

/// <summary>What a publish asks of the company mappings: the one remembered for a company, remember one, forget a stale one.</summary>
public interface ICompanyMappings
{
    Task<CompanyMapping?> FindAsync(string psaCompany, CancellationToken ct = default);

    Task<CompanyMapping> RememberAsync(string psaCompany, CompanyRef company, string confidence, CancellationToken ct = default);

    Task<bool> ForgetIfUnchangedAsync(CompanyMapping stale, CancellationToken ct = default);
}

/// <summary>One tenant's remembered company mappings (ST-097): read before every article, written by an exact match or a person.</summary>
public sealed class CompanyMappings(ScreenTailContext db, Guid tenantId, TimeProvider time) : ICompanyMappings
{
    /// <summary>
    /// This tenant's rows and no others. Every read and write below starts here, so the tenant is named
    /// once rather than in each predicate, where one dropped clause would file one customer's article
    /// under another's company (2026-10-12 review, T-174).
    /// </summary>
    private IQueryable<CompanyMapping> Mine => db.CompanyMappings.Where(m => m.TenantId == tenantId);

    public Task<CompanyMapping?> FindAsync(string psaCompany, CancellationToken ct = default) =>
        Mine.AsNoTracking().SingleOrDefaultAsync(m => m.PsaCompany == psaCompany.Trim(), ct);

    public Task<List<CompanyMapping>> ListAsync(CancellationToken ct = default) =>
        Mine.AsNoTracking().OrderBy(m => m.PsaCompany).ToListAsync(ct);

    public async Task<CompanyMapping> RememberAsync(string psaCompany, CompanyRef company, string confidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        var name = psaCompany.Trim();
        var row = await Mine.SingleOrDefaultAsync(m => m.PsaCompany == name, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new CompanyMapping { Id = Guid.NewGuid(), TenantId = tenantId, PsaCompany = name, DocCompanyId = company.Id, DocCompanyName = company.Name, Confidence = confidence, CreatedAt = time.GetUtcNow() };
            db.CompanyMappings.Add(row);
        }
        else
        {
            row.DocCompanyId = company.Id;
            row.DocCompanyName = company.Name;
            row.Confidence = confidence;
            row.CreatedAt = time.GetUtcNow();
        }

        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    public async Task<bool> ForgetAsync(string psaCompany, CancellationToken ct = default) =>
        await Mine.Where(m => m.PsaCompany == psaCompany.Trim()).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

    /// <summary>
    /// Forgets this row only if it still says what it said when it was read: a mapping found stale is
    /// dropped, but one a person or an exact match has rewritten in the meantime stays.
    /// </summary>
    public async Task<bool> ForgetIfUnchangedAsync(CompanyMapping stale, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stale);
        return await Mine
            .Where(m => m.Id == stale.Id && m.DocCompanyId == stale.DocCompanyId && m.DocCompanyName == stale.DocCompanyName)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Every mapping of this tenant, for when the documentation platform they point into is gone or is
    /// another one (2026-10-12 review, S-71). A company id means a customer only on the site that issued
    /// it; on another site the same number can be somebody else.
    /// </summary>
    public Task<int> ForgetAllAsync(CancellationToken ct = default) => Mine.ExecuteDeleteAsync(ct);
}
