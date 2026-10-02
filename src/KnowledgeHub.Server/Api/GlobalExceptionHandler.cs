using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Embeddings;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// SPEC-20260914-error-handling: maps unhandled exceptions to RFC 7807
/// ProblemDetails. Stack traces never reach the response.
/// </summary>
public sealed class GlobalExceptionHandler(
    IHostEnvironment env,
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, "Bad request"),
            EmbeddingProviderException => (StatusCodes.Status502BadGateway, "Embedding provider failure"),
            ChatProviderException => (StatusCodes.Status502BadGateway, "Chat provider failure"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            OperationCanceledException => (499, "Request cancelled"),
            InvalidOperationException => (StatusCodes.Status500InternalServerError, "Operation failed"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception on {Method} {Path}",
                Safe(httpContext.Request.Method), Safe(httpContext.Request.Path.Value));

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = env.IsDevelopment() ? exception.Message : "An error occurred while processing the request.",
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = status;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = status >= 500 ? exception : null
        });
        return true;
    }

    private static string? Safe(string? value) => value?.ReplaceLineEndings("_");
}
