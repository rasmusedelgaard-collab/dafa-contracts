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
    public ConflictException(string message) : base(409, "Conflict", message) { }
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
