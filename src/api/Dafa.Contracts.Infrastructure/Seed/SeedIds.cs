using System.Security.Cryptography;
using System.Text;

namespace Dafa.Contracts.Infrastructure.Seed;

/// <summary>Stable ids for seeded rows, so seeding is repeatable and tests can refer to demo data.</summary>
public static class SeedIds
{
    public static Guid For(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("dafa-contracts:" + key));
        return new Guid(hash);
    }

    public static Guid Entity(string name) => For("entity:" + name);
    public static Guid Department(string name) => For("department:" + name);
    public static Guid ContractType(string name) => For("type:" + name);
    public static Guid User(string key) => For("user:" + key);
    public static Guid Supplier(string externalId) => For("supplier:" + externalId);
}

/// <summary>The fixed reference data from the build brief, section 4.</summary>
public static class SeedNames
{
    public static readonly string[] Entities =
    {
        "DAFA Group A/S",
        "DAFA A/S",
        "DAFA Building Solutions A/S",
        "DAFA Sverige AB",
        "DAFA Deutschland GmbH",
        "DAFA Sealing Technology (Tianjin) Co., Ltd.",
        "DAFA US Inc.",
        "DAFA Polska sp. z o.o.",
        "DAFA Italia S.r.l."
    };

    public static readonly string[] Departments =
    {
        "Management",
        "Finance",
        "Purchasing",
        "Sales",
        "Production",
        "Supply chain and logistics",
        "Quality",
        "R&D and engineering",
        "Maintenance and facilities",
        "HSE",
        "IT",
        "HR",
        "Marketing"
    };

    public static readonly string[] ContractTypes =
    {
        "Supplier – direct materials",
        "Supplier – indirect and services",
        "Logistics",
        "IT and SaaS",
        "Lease and property",
        "Finance and insurance",
        "NDA",
        "R&D and partnership",
        "Corporate (shareholder, M&A)",
        "Other"
    };

    public const string TypeDirect = "Supplier – direct materials";
    public const string TypeIndirect = "Supplier – indirect and services";
    public const string TypeLogistics = "Logistics";
    public const string TypeIt = "IT and SaaS";
    public const string TypeLease = "Lease and property";
    public const string TypeFinance = "Finance and insurance";
    public const string TypeNda = "NDA";
    public const string TypeRd = "R&D and partnership";
    public const string TypeCorporate = "Corporate (shareholder, M&A)";
    public const string TypeOther = "Other";
}
