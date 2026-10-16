using System;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>Business rule error returned to the client as <c>{"error": code, "message": …}</c>.</summary>
public sealed class HolixException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HolixException"/> class.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="code">Machine-readable code.</param>
    /// <param name="message">Description.</param>
    public HolixException(int status, string code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }

    /// <summary>Initializes a new instance of the <see cref="HolixException"/> class.</summary>
    public HolixException()
        : this(500, "internal", "Holix error")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HolixException"/> class.</summary>
    /// <param name="message">Description.</param>
    public HolixException(string message)
        : this(500, "internal", message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HolixException"/> class.</summary>
    /// <param name="message">Description.</param>
    /// <param name="innerException">Inner exception.</param>
    public HolixException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = 500;
        Code = "internal";
    }

    /// <summary>Gets the HTTP status.</summary>
    public int Status { get; }

    /// <summary>Gets the error code.</summary>
    public string Code { get; } = "internal";

    internal static HolixException NotFound(string message) => new(404, "not_found", message);

    internal static HolixException Forbidden(string message) => new(403, "forbidden", message);

    internal static HolixException BadRequest(string code, string message) => new(400, code, message);

    internal static HolixException Conflict(string code, string message) => new(409, code, message);
}
