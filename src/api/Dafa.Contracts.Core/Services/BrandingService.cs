using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Release 4: which files may be a logo. The type is decided from the content (magic numbers), never
/// from the upload's Content-Type, and must agree with the file extension. An SVG is text that a
/// browser can execute, so it is refused when it contains scripts, event handlers, foreignObject,
/// entities or any reference that is not a fragment inside the file itself; on top of that the
/// host serves every logo with a Content-Security-Policy that forbids scripts and external loads.
/// </summary>
public static class LogoRules
{
    public const long MaxBytes = 1024 * 1024;
    public const string AllowedList = "PNG, JPEG, WebP or SVG";

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Webp = "image/webp";
    public const string Svg = "image/svg+xml";

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegSignature = { 0xFF, 0xD8, 0xFF };

    private static bool StartsWith(byte[] content, byte[] signature, int offset = 0)
    {
        if (content.Length < offset + signature.Length)
        {
            return false;
        }
        for (var i = 0; i < signature.Length; i++)
        {
            if (content[offset + i] != signature[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Strict UTF-8 text of the content, or null when it is not valid UTF-8 text.</summary>
    private static string? ReadText(byte[] content)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(content);
            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text.Substring(1);
            }
            return text.IndexOf('\0') >= 0 ? null : text;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The content type the bytes really are, or null when they are none of the four allowed types.</summary>
    public static string? Detect(byte[] content)
    {
        if (StartsWith(content, PngSignature)) return Png;
        if (StartsWith(content, JpegSignature)) return Jpeg;
        if (content.Length >= 12
            && StartsWith(content, Encoding.ASCII.GetBytes("RIFF"))
            && StartsWith(content, Encoding.ASCII.GetBytes("WEBP"), 8)) return Webp;
        var text = ReadText(content);
        if (text is null)
        {
            return null;
        }
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return null;
        }
        // The root element must be <svg>, after an optional XML declaration and comments.
        var withoutProlog = Regex.Replace(trimmed, @"^(\s*(<\?xml[^>]*\?>|<!--.*?-->))*\s*", "", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return Regex.IsMatch(withoutProlog, @"^<svg[\s>/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? Svg : null;
    }

    private static readonly (Regex Pattern, string What)[] SvgForbidden =
    {
        (new Regex(@"<\s*script", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "a script"),
        (new Regex(@"<\s*foreignObject", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "a foreignObject element"),
        (new Regex(@"<\s*(iframe|embed|object|audio|video|link|meta|handler|listener)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "an embedded element"),
        (new Regex(@"<!\s*(DOCTYPE|ENTITY)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "a document type or entity declaration"),
        (new Regex(@"<\?(?!xml\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "a processing instruction"),
        (new Regex(@"[\s""'/]on[a-z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "an event handler"),
        (new Regex(@"(javascript|vbscript|livescript)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "a script link"),
        (new Regex(@"data\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "an embedded data link"),
        // href, xlink:href and src may only point to a fragment inside the file (#id).
        (new Regex(@"(href|src)\s*=\s*(""\s*[^#""\s]|'\s*[^#'\s]|[^""'\s#])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "an external reference"),
        // url(...) in attributes or styles may only point to a fragment inside the file.
        (new Regex(@"url\s*\(\s*[""']?\s*[^#""'\s)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "an external reference"),
        (new Regex(@"@import", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "a style import"),
        (new Regex(@"expression\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "a style expression"),
        // Character references could hide any of the above from this check.
        (new Regex(@"&#", RegexOptions.CultureInvariant), "a character reference")
    };

    /// <summary>Null when the SVG is safe to serve as an image; otherwise what is not allowed in it.</summary>
    public static string? SvgProblem(byte[] content)
    {
        var text = ReadText(content);
        if (text is null)
        {
            return "text that is not valid UTF-8";
        }
        foreach (var rule in SvgForbidden)
        {
            if (rule.Pattern.IsMatch(text))
            {
                return rule.What;
            }
        }
        return null;
    }

    private static string? TypeOfExtension(string fileName)
    {
        switch (Path.GetExtension(fileName ?? "").ToLowerInvariant())
        {
            case ".png": return Png;
            case ".jpg":
            case ".jpeg": return Jpeg;
            case ".webp": return Webp;
            case ".svg": return Svg;
            default: return null;
        }
    }

    /// <summary>Returns the content type to store; throws a field error on "file" when the file may not be a logo.</summary>
    public static string Validate(string fileName, byte[] content)
    {
        if (content is null || content.Length == 0)
        {
            throw new InputValidationException("file", "The file is empty.");
        }
        if (content.LongLength > MaxBytes)
        {
            throw new InputValidationException("file", "The logo is larger than 1 MB.");
        }
        var declared = TypeOfExtension(fileName);
        if (declared is null)
        {
            throw new InputValidationException("file", "This file type is not allowed. Upload a " + AllowedList + " file.");
        }
        var detected = Detect(content);
        if (detected is null || detected != declared)
        {
            throw new InputValidationException("file", "The content of the file does not match its file type. Upload a " + AllowedList + " file.");
        }
        if (detected == Svg)
        {
            var problem = SvgProblem(content);
            if (problem is not null)
            {
                throw new InputValidationException("file", "The SVG contains " + problem + ", which is not allowed in a logo. Export it as a plain SVG or upload a PNG.");
            }
        }
        return detected;
    }
}

public sealed record BrandingFile(byte[] Content, string ContentType, string ETag, string FileName);

/// <summary>
/// Release 4 (CONTRACT_V4 section 6): the organisation's logo (variant full) and compact icon
/// (variant compact). Upload, replace and delete are for administrators (the host restricts the
/// routes); reading is anonymous because the logo is shown on the sign-in screen.
/// </summary>
public class BrandingService
{
    public const string FullKind = "logo";
    public const string CompactKind = "icon";
    /// <summary>The logo for the dark theme; handled exactly like the logo.</summary>
    public const string DarkKind = "logoDark";

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public BrandingService(IAppDbContext db, ICurrentUser currentUser, IClock clock, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>full (default) -> logo, compact -> icon; anything else is 400 on variant.</summary>
    public static string KindOf(string? variant)
    {
        var text = (variant ?? "").Trim();
        if (text.Length == 0 || text == "full") return FullKind;
        if (text == "compact") return CompactKind;
        if (text == "dark" || text == "logoDark") return DarkKind;
        throw new InputValidationException("variant", "Use full, compact or dark.");
    }

    /// <summary>What every page needs: the logo URLs (relative to the API base) and a version that changes with every upload.</summary>
    public static async Task<BrandingDto> LoadAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var rows = await db.BrandingAssets.AsNoTracking().Select(a => new { a.Kind, a.ETag }).ToListAsync(cancellationToken);
        var full = rows.FirstOrDefault(r => r.Kind == FullKind)?.ETag;
        var compact = rows.FirstOrDefault(r => r.Kind == CompactKind)?.ETag;
        var dark = rows.FirstOrDefault(r => r.Kind == DarkKind)?.ETag;
        return new BrandingDto
        {
            LogoUrl = full is null ? null : "branding/logo?variant=full&v=" + full,
            CompactLogoUrl = compact is null ? null : "branding/logo?variant=compact&v=" + compact,
            DarkLogoUrl = dark is null ? null : "branding/logo?variant=dark&v=" + dark,
            Version = full is null && compact is null && dark is null
                ? "0"
                : (Short(full) + "-" + Short(compact) + "-" + Short(dark))
        };
    }

    private static string Short(string? etag) => string.IsNullOrEmpty(etag) ? "0" : etag.Substring(0, Math.Min(8, etag.Length));

    private static string LabelOf(string kind)
    {
        switch (kind)
        {
            case CompactKind: return "compact logo";
            case DarkKind: return "logo for the dark theme";
            default: return "logo";
        }
    }

    public Task<BrandingDto> GetAsync(CancellationToken cancellationToken = default) => LoadAsync(_db, cancellationToken);

    /// <summary>The stored file of a variant, or null when none was uploaded. No access check: logos are public.</summary>
    public async Task<BrandingFile?> GetFileAsync(string? variant, CancellationToken cancellationToken = default)
    {
        var kind = KindOf(variant);
        var asset = await _db.BrandingAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Kind == kind, cancellationToken);
        return asset is null ? null : new BrandingFile(asset.Content, asset.ContentType, asset.ETag, asset.FileName);
    }

    public async Task<BrandingDto> UploadAsync(string? variant, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        var kind = KindOf(variant);
        var name = DocumentWriter.SafeFileName(fileName);
        var contentType = LogoRules.Validate(name, content);
        var asset = await _db.BrandingAssets.FirstOrDefaultAsync(a => a.Kind == kind, cancellationToken);
        var replaced = asset is not null;
        if (asset is null)
        {
            asset = new BrandingAsset { Kind = kind };
            _db.BrandingAssets.Add(asset);
        }
        asset.FileName = name;
        asset.ContentType = contentType;
        asset.Content = content;
        asset.Size = content.LongLength;
        asset.ETag = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant().Substring(0, 32);
        asset.UpdatedById = _currentUser.UserId;
        asset.UpdatedAt = _clock.UtcNow;
        _audit.Record("settings.branding", (replaced ? "Replaced" : "Uploaded") + " the " + LabelOf(kind)
            + " (" + name + ", " + contentType + ", " + content.Length + " bytes)");
        await _db.SaveChangesAsync(cancellationToken);
        return await LoadAsync(_db, cancellationToken);
    }

    public async Task<BrandingDto> DeleteAsync(string? variant, CancellationToken cancellationToken = default)
    {
        var kind = KindOf(variant);
        var asset = await _db.BrandingAssets.FirstOrDefaultAsync(a => a.Kind == kind, cancellationToken);
        if (asset is not null)
        {
            _db.BrandingAssets.Remove(asset);
            _audit.Record("settings.branding", "Removed the " + LabelOf(kind));
            await _db.SaveChangesAsync(cancellationToken);
        }
        return await LoadAsync(_db, cancellationToken);
    }
}
