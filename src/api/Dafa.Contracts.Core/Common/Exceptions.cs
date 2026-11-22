namespace Dafa.Contracts.Core.Common;

/// <summary>Base for errors the API host translates into RFC 7807 problem responses.</summary>
public class AppException : Exception
{
    public AppException(int statusCode, string title, string message) : base(message)
    {
        StatusCode = statusCode;
        Title = title;
    }

    public int StatusCode { get; }
    public string Title { get; }

    /// <summary>RFC 7807 "type" of the problem response, when the client needs to tell cases apart. Null = not set.</summary>
    public string? ProblemType { get; protected set; }

    /// <summary>Release 4: extra members of the problem response (for example existingId on a duplicate). Null = none.</summary>
    public IDictionary<string, object?>? Extensions { get; protected set; }
}

/// <summary>
/// The "type" values of 409 responses, so the UI can tell "somebody else changed this, reload"
/// from a business rule that will not go away by retrying.
/// </summary>
public static class ProblemTypes
{
    /// <summary>The data was changed by someone else after it was read (optimistic concurrency). Reload and retry.</summary>
    public const string Stale = "urn:dafa:problem:stale";
    /// <summary>No approval rule matches the contract, or the matching rule has no stages.</summary>
    public const string NoApprovalRule = "urn:dafa:problem:no-approval-rule";
    /// <summary>No approver independent of the owner and the submitter could be found for a stage.</summary>
    public const string NoApprover = "urn:dafa:problem:no-approver";
    /// <summary>An approved term or the main document is locked for this user.</summary>
    public const string LockedFields = "urn:dafa:problem:locked-fields";
    /// <summary>The same thing already exists (for example an identical link).</summary>
    public const string Duplicate = "urn:dafa:problem:duplicate";
    /// <summary>The action is not possible in the current state (status, closed approval, term already renewed, ...).</summary>
    public const string InvalidState = "urn:dafa:problem:invalid-state";
}

/// <summary>Problem types added in release 4 (CONTRACT_V4, introduction).</summary>
public static class ProblemTypesV4
{
    /// <summary>403 with a reason the UI can show (no signing right for that entity, may not create counterparties, ...).</summary>
    public const string NotPermitted = "urn:dafa:problem:not-permitted";
    /// <summary>409: the signature request is not in a state that allows the action.</summary>
    public const string SignatureState = "urn:dafa:problem:signature-state";
    /// <summary>502: the external call of a link test itself crashed.</summary>
    public const string LinkFailed = "urn:dafa:problem:link-failed";
}

/// <summary>Problem types of the document store (CONTRACT_V4 section 9a).</summary>
public static class StorageProblemTypes
{
    /// <summary>503: SharePoint is throttling, timing out or failing after the backend's own retries.</summary>
    public const string Unavailable = "urn:dafa:problem:storage-unavailable";
    /// <summary>404: the document row exists but the file is gone from storage.</summary>
    public const string FileMissing = "urn:dafa:problem:file-missing";
    /// <summary>409: the contract's entity or department has no site or library mapping, or credentials are missing.</summary>
    public const string NotConfigured = "urn:dafa:problem:storage-not-configured";
}

/// <summary>
/// The document store cannot be reached right now (throttled, timed out, 5xx, circuit open).
/// The contract data is safe; the file operation can be tried again. Answered as 503 with a
/// Retry-After header when the store said how long to wait.
/// </summary>
public class StorageUnavailableException : AppException
{
    public const string UserMessage = "SharePoint is busy. Try again in a moment.";

    public StorageUnavailableException(string? technicalDetail = null, int? retryAfterSeconds = null, Exception? inner = null)
        : base(503, "Storage unavailable", UserMessage)
    {
        ProblemType = StorageProblemTypes.Unavailable;
        TechnicalDetail = technicalDetail ?? inner?.Message;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>For logs and the storage health page; never sent to users.</summary>
    public string? TechnicalDetail { get; }
    public int? RetryAfterSeconds { get; }
}

/// <summary>The document row exists but its file is no longer in the store. Answered as 404 file-missing.</summary>
public class StorageFileMissingException : AppException
{
    public const string UserMessage = "This file can no longer be found in storage. An administrator has been notified.";

    public StorageFileMissingException(string? storeKey = null) : base(404, "File missing", UserMessage)
    {
        ProblemType = StorageProblemTypes.FileMissing;
        StoreKey = storeKey;
    }

    public string? StoreKey { get; }
}

/// <summary>
/// The store has no place for the file: no site is linked to the legal entity, the department's
/// library does not exist, or the credentials are missing. Never answered by falling back to
/// another site or library. 409 storage-not-configured with a sentence for the user.
/// </summary>
public class StorageNotConfiguredException : AppException
{
    public StorageNotConfiguredException(string message) : base(409, "Storage not configured", message)
    {
        ProblemType = StorageProblemTypes.NotConfigured;
    }
}

/// <summary>Release 4: 403 with problem type not-permitted and a sentence that can be shown as is.</summary>
public class NotPermittedException : AppException
{
    public NotPermittedException(string message) : base(403, "Not permitted", message)
    {
        ProblemType = ProblemTypesV4.NotPermitted;
    }
}

/// <summary>Release 4: 502 with problem type link-failed (an external system could not be reached).</summary>
public class ExternalCallException : AppException
{
    public ExternalCallException(string message) : base(502, "External call failed", message)
    {
        ProblemType = ProblemTypesV4.LinkFailed;
    }
}

public class NotFoundException : AppException
{
    public NotFoundException(string message) : base(404, "Not found", message) { }
}

public class ForbiddenException : AppException
{
    public ForbiddenException(string message) : base(403, "Forbidden", message) { }
}

public class UnauthorizedException : AppException
{
    public UnauthorizedException(string message) : base(401, "Unauthorized", message) { }
}

public class ConflictException : AppException
{
    public ConflictException(string message) : this(message, ProblemTypes.InvalidState) { }

    public ConflictException(string message, string problemType) : base(409, "Conflict", message)
    {
        ProblemType = problemType;
    }

    /// <summary>Release 4: a conflict with extra members in the problem response.</summary>
    public ConflictException(string message, string problemType, IDictionary<string, object?> extensions) : base(409, "Conflict", message)
    {
        ProblemType = problemType;
        Extensions = extensions;
    }
}

public class InputValidationException : AppException
{
    public InputValidationException(IDictionary<string, string[]> errors)
        : base(400, "Validation failed", "One or more fields are invalid.")
    {
        Errors = errors;
    }

    public InputValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = new[] { message } })
    {
    }

    public IDictionary<string, string[]> Errors { get; }
}
