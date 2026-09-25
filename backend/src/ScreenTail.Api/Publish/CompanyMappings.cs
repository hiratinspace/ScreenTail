using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Publish;

/// <summary>One tenant's remembered company mappings (ST-097): read before every article, written by an exact match or a person.</summary>
public sealed class CompanyMappings(ScreenTailContext db, Guid tenantId, TimeProvider time)
{
    public Task<CompanyMapping?> FindAsync(string psaCompany, CancellationToken ct = default) =>
        db.CompanyMappings.AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == tenantId && m.PsaCompany == psaCompany.Trim(), ct);

    public Task<List<CompanyMapping>> ListAsync(CancellationToken ct = default) =>
        db.CompanyMappings.AsNoTracking().Where(m => m.TenantId == tenantId).OrderBy(m => m.PsaCompany).ToListAsync(ct);

    public async Task<CompanyMapping> RememberAsync(string psaCompany, CompanyRef company, string confidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        var name = psaCompany.Trim();
        var row = await db.CompanyMappings.SingleOrDefaultAsync(m => m.TenantId == tenantId && m.PsaCompany == name, ct).ConfigureAwait(false);
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
        await db.CompanyMappings.Where(m => m.TenantId == tenantId && m.PsaCompany == psaCompany.Trim()).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
}
