namespace InterviewAssistant.Backend.Infrastructure;

/// <summary>Structured, user-safe API error (mapped to HTTP status + ApiError JSON by middleware).</summary>
public sealed class ApiException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public ApiException(int status, string code, string message) : base(message) { Status = status; Code = code; }

    public static ApiException NotFound() => new(404, "not_found", "Resource not found.");
    public static ApiException Forbidden(string code, string msg) => new(403, code, msg);
    public static ApiException Payment(string code, string msg) => new(402, code, msg);
    public static ApiException Conflict(string code, string msg) => new(409, code, msg);
    public static ApiException TooMany(string code, string msg) => new(429, code, msg);
    public static ApiException BadRequest(string code, string msg) => new(400, code, msg);
}
