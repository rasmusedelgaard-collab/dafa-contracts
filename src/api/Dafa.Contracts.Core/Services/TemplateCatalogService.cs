using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Release 4: mapping and validation of the template fields added in CONTRACT_V4 section 5.</summary>
public static class TemplateCatalog
{
    public static readonly string[] Languages = { "da", "en", "sv", "de", "pl", "it", "zh" };

    /// <summary>The contract types a template is meant for: the stored list, else the single TypeId, else none (= all).</summary>
    public static List<Guid> TypeIdsOf(ContractTemplate template)
    {
        var ids = JsonDefaults.ReadList<Guid>(template.ContractTypeIdsJson);
        if (ids.Count == 0 && template.TypeId is not null)
        {
            ids.Add(template.TypeId.Value);
        }
        return ids;
    }

    public static TemplateDto ToDto(ContractTemplate template, IReadOnlyDictionary<Guid, string>? categoryNames = null)
    {
        var typeIds = TypeIdsOf(template);
        string? categoryName = null;
        if (template.CategoryId is not null && categoryNames is not null)
        {
            categoryNames.TryGetValue(template.CategoryId.Value, out categoryName);
        }
        return new TemplateDto
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description ?? "",
            TypeId = typeIds.Count == 0 ? null : typeIds[0],
            TypeIds = typeIds,
            FileName = template.FileName,
            Placeholders = (template.Placeholders ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            IsActive = template.IsActive,
            CategoryId = template.CategoryId,
            CategoryName = categoryName,
            Language = template.Language,
            UpdatedAt = template.UpdatedAt
        };
    }

    public static async Task<Dictionary<Guid, string>> CategoryNamesAsync(IAppDbContext db, CancellationToken cancellationToken = default) =>
        (await db.TemplateCategories.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(c => c.Id, c => c.Name);

    /// <summary>
    /// Validates category, contract types and language of a template input and applies them.
    /// typeIds given = the list (typeId becomes its first); typeIds omitted = the single typeId.
    /// </summary>
    public static async Task ApplyAsync(IAppDbContext db, ContractTemplate template, TemplateInput input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.CategoryId is not null)
        {
            var categoryId = input.CategoryId.Value;
            if (!await db.TemplateCategories.AnyAsync(c => c.Id == categoryId, cancellationToken))
                ContractValidator.Add(errors, "categoryId", "Unknown template category.");
        }
        var typeIds = input.TypeIds is not null
            ? input.TypeIds.Distinct().ToList()
            : (input.TypeId is null ? new List<Guid>() : new List<Guid> { input.TypeId.Value });
        if (typeIds.Count > 0)
        {
            var known = await db.ContractTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
            if (known.Count != typeIds.Count)
                ContractValidator.Add(errors, input.TypeIds is not null ? "typeIds" : "typeId", "Unknown contract type.");
        }
        var language = string.IsNullOrWhiteSpace(input.Language) ? null : input.Language.Trim().ToLowerInvariant();
        if (language is not null && !Languages.Contains(language))
            ContractValidator.Add(errors, "language", "Use da, en, sv, de, pl, it or zh.");
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description is not null && description.Length > 2000)
            ContractValidator.Add(errors, "description", "The description can be at most 2000 characters.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        template.CategoryId = input.CategoryId;
        template.TypeId = typeIds.Count == 0 ? null : typeIds[0];
        template.ContractTypeIdsJson = typeIds.Count == 0 ? null : JsonDefaults.Write(typeIds);
        template.Language = language;
    }

    private static readonly Dictionary<string, string> Samples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "Supply agreement, EPDM rubber compounds",
        ["contractNo"] = "C-2026-0042",
        ["counterparty"] = "Nordic Polymer Compounds AB",
        ["counterpartyName"] = "Nordic Polymer Compounds AB",
        ["entity"] = "DAFA A/S",
        ["entityName"] = "DAFA A/S",
        ["department"] = "Purchasing",
        ["departmentName"] = "Purchasing",
        ["owner"] = "Mette Holm",
        ["ownerName"] = "Mette Holm",
        ["startDate"] = "2027-01-01",
        ["endDate"] = "2029-12-31",
        ["noticePeriodDays"] = "90",
        ["renewalMonths"] = "12",
        ["annualValue"] = "1250000",
        ["totalValue"] = "3750000",
        ["currency"] = "DKK",
        ["paymentTerms"] = "Net 45 days",
        ["governingLaw"] = "Danish law",
        ["liabilityCap"] = "100% of the annual value",
        ["today"] = "2026-11-22",
        ["date"] = "2026-11-22"
    };

    /// <summary>"counterpartyName" -> "Counterparty name"; "start_date" -> "Start date".</summary>
    public static string LabelFor(string placeholder)
    {
        var builder = new StringBuilder();
        var previousLower = false;
        foreach (var ch in placeholder ?? "")
        {
            if (ch == '_' || ch == '.' || ch == '-')
            {
                builder.Append(' ');
                previousLower = false;
                continue;
            }
            if (char.IsUpper(ch) && previousLower)
            {
                builder.Append(' ');
            }
            builder.Append(char.ToLowerInvariant(ch));
            previousLower = char.IsLower(ch) || char.IsDigit(ch);
        }
        var text = builder.ToString().Trim();
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    public static TemplatePlaceholderDto ToPlaceholder(string name) => new()
    {
        Name = name,
        Label = LabelFor(name),
        Sample = Samples.TryGetValue(name, out var sample) ? sample : LabelFor(name)
    };
}

/// <summary>
/// Release 4 (CONTRACT_V4 section 5): template categories (admin CRUD, order, active), the template
/// list for users (active templates in active categories, filterable) and the placeholder preview.
/// </summary>
public class TemplateCatalogService
{
    public const int ExcerptLength = 1000;

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly AuditService _audit;
    private readonly IDocumentTextExtractor? _text;

    public TemplateCatalogService(IAppDbContext db, ICurrentUser currentUser, AuditService audit, IDocumentTextExtractor? text = null)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
        _text = text;
    }

    private static TemplateCategoryDto ToDto(TemplateCategory category, int templateCount) => new()
    {
        Id = category.Id,
        Name = category.Name,
        Description = category.Description ?? "",
        Order = category.SortOrder,
        IsActive = category.IsActive,
        TemplateCount = templateCount
    };

    private static List<TemplateCategory> Ordered(IEnumerable<TemplateCategory> categories) =>
        categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // ---------- admin ----------

    public async Task<List<TemplateCategoryDto>> GetAllCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _db.TemplateCategories.AsNoTracking().ToListAsync(cancellationToken);
        var counts = (await _db.Templates.AsNoTracking().Where(t => t.CategoryId != null).Select(t => t.CategoryId!.Value).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return Ordered(categories).Select(c => ToDto(c, counts.TryGetValue(c.Id, out var n) ? n : 0)).ToList();
    }

    private async Task ApplyAsync(TemplateCategory category, TemplateCategoryInput input, CancellationToken cancellationToken)
    {
        var name = (input?.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 80)
        {
            throw new InputValidationException("name", "Name is required (at most 80 characters).");
        }
        var description = string.IsNullOrWhiteSpace(input?.Description) ? null : input!.Description!.Trim();
        if (description is not null && description.Length > 400)
        {
            throw new InputValidationException("description", "The description can be at most 400 characters.");
        }
        var id = category.Id;
        var others = await _db.TemplateCategories.AsNoTracking().Where(c => c.Id != id).Select(c => c.Name).ToListAsync(cancellationToken);
        if (others.Any(other => string.Equals(other, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictException("A template category with this name already exists.", ProblemTypes.Duplicate);
        }
        category.Name = name;
        category.Description = description;
        category.IsActive = input!.IsActive ?? category.IsActive;
    }

    public async Task<TemplateCategoryDto> CreateCategoryAsync(TemplateCategoryInput input, CancellationToken cancellationToken = default)
    {
        var orders = await _db.TemplateCategories.AsNoTracking().Select(c => c.SortOrder).ToListAsync(cancellationToken);
        var category = new TemplateCategory { Id = Guid.NewGuid(), IsActive = true, SortOrder = orders.Count == 0 ? 1 : orders.Max() + 1 };
        await ApplyAsync(category, input, cancellationToken);
        _db.TemplateCategories.Add(category);
        _audit.Record("settings.template-categories", "Created template category " + category.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(category, 0);
    }

    public async Task<TemplateCategoryDto> UpdateCategoryAsync(Guid id, TemplateCategoryInput input, CancellationToken cancellationToken = default)
    {
        var category = await _db.TemplateCategories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException("Template category not found.");
        await ApplyAsync(category, input, cancellationToken);
        _audit.Record("settings.template-categories", "Updated template category " + category.Name + (category.IsActive ? "" : " (inactive)"));
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(category, await _db.Templates.CountAsync(t => t.CategoryId == id, cancellationToken));
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await _db.TemplateCategories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException("Template category not found.");
        if (await _db.Templates.AnyAsync(t => t.CategoryId == id, cancellationToken))
        {
            throw new ConflictException("The category has templates. Move them to another category or deactivate the category.", ProblemTypesV3.InUse);
        }
        _db.TemplateCategories.Remove(category);
        _audit.Record("settings.template-categories", "Deleted template category " + category.Name);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>PUT admin/template-categories/order: every category id exactly once, in the new order.</summary>
    public async Task<List<TemplateCategoryDto>> ReorderCategoriesAsync(IdsInput input, CancellationToken cancellationToken = default)
    {
        var ids = input?.Ids ?? new List<Guid>();
        var categories = await _db.TemplateCategories.ToListAsync(cancellationToken);
        if (ids.Count != categories.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => categories.All(c => c.Id != id)))
        {
            throw new InputValidationException("ids", "Send every template category id exactly once.");
        }
        for (var i = 0; i < ids.Count; i++)
        {
            categories.First(c => c.Id == ids[i]).SortOrder = i + 1;
        }
        _audit.Record("settings.template-categories", "Changed the order of the template categories");
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAllCategoriesAsync(cancellationToken);
    }

    // ---------- every user ----------

    /// <summary>GET template-categories: active categories with the number of their active templates.</summary>
    public async Task<List<TemplateCategoryDto>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var categories = await _db.TemplateCategories.AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken);
        var counts = (await _db.Templates.AsNoTracking().Where(t => t.IsActive && t.CategoryId != null).Select(t => t.CategoryId!.Value).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return Ordered(categories).Select(c => ToDto(c, counts.TryGetValue(c.Id, out var n) ? n : 0)).ToList();
    }

    /// <summary>
    /// GET templates: active templates in active categories (and templates without a category),
    /// sorted by category order, then name. Optional filters: categoryId, typeId (templates for
    /// that type or for every type), language and q (name, description, category name).
    /// </summary>
    public async Task<List<TemplateDto>> ListAsync(Guid? categoryId, Guid? typeId, string? language, string? q, CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var categories = (await _db.TemplateCategories.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(c => c.Id);
        var names = categories.ToDictionary(c => c.Key, c => c.Value.Name);
        var templates = await _db.Templates.AsNoTracking().Where(t => t.IsActive).ToListAsync(cancellationToken);
        var term = (q ?? "").Trim();
        var wantedLanguage = (language ?? "").Trim().ToLowerInvariant();
        IEnumerable<ContractTemplate> rows = templates.Where(t =>
            t.CategoryId is null || !categories.TryGetValue(t.CategoryId.Value, out var category) || category.IsActive);
        if (categoryId is not null)
        {
            rows = rows.Where(t => t.CategoryId == categoryId);
        }
        if (typeId is not null)
        {
            rows = rows.Where(t =>
            {
                var typeIds = TemplateCatalog.TypeIdsOf(t);
                return typeIds.Count == 0 || typeIds.Contains(typeId.Value);
            });
        }
        if (wantedLanguage.Length > 0)
        {
            rows = rows.Where(t => string.Equals(t.Language, wantedLanguage, StringComparison.OrdinalIgnoreCase));
        }
        if (term.Length > 0)
        {
            rows = rows.Where(t =>
                t.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (t.Description ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || (t.CategoryId is not null && names.TryGetValue(t.CategoryId.Value, out var categoryName) && categoryName.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        return rows
            .OrderBy(t => t.CategoryId is not null && categories.TryGetValue(t.CategoryId.Value, out var category) ? category.SortOrder : int.MaxValue)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => TemplateCatalog.ToDto(t, names))
            .ToList();
    }

    /// <summary>GET templates/{id}/preview: the placeholders (with label and sample) and the first part of the text.</summary>
    public async Task<TemplatePreviewInfoDto> PreviewAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var template = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null || (!template.IsActive && !profile.IsAdmin))
        {
            throw new NotFoundException("Template not found.");
        }
        var excerpt = "";
        if (_text is not null)
        {
            try
            {
                var extracted = await _text.ExtractAsync(template.FileName, template.Content, cancellationToken);
                excerpt = (extracted.Text ?? "").Trim();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                excerpt = "";
            }
        }
        if (excerpt.Length > ExcerptLength)
        {
            // Cut at a word boundary where there is one.
            var cut = excerpt.LastIndexOf(' ', ExcerptLength);
            excerpt = excerpt.Substring(0, cut > ExcerptLength - 200 ? cut : ExcerptLength) + "…";
        }
        var placeholders = (template.Placeholders ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new TemplatePreviewInfoDto
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description ?? "",
            Placeholders = placeholders.Select(TemplateCatalog.ToPlaceholder).ToList(),
            Excerpt = excerpt
        };
    }
}
