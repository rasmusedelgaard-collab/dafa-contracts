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
