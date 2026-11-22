using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dafa.Contracts.Api.Controllers;

/// <summary>All API controllers require a signed-in user unless an action says otherwise.</summary>
[ApiController]
[Authorize]
public abstract class ApiControllerBase : ControllerBase
{
    protected static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
