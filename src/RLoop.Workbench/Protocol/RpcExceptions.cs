namespace ResoniteWorkbench.Protocol;

/// <summary>A frame that is empty, oversized, truncated or not a valid message. The connection is unusable after it.</summary>
public sealed class RpcFrameException : IOException
{
    public RpcFrameException()
    {
    }

    public RpcFrameException(string message)
        : base(message)
    {
    }

    public RpcFrameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The server refused the handshake, or answered it with something other than a valid welcome.</summary>
public sealed class RpcHandshakeException : Exception
{
    public RpcHandshakeException()
    {
    }

    public RpcHandshakeException(string message)
        : base(message)
    {
    }

    public RpcHandshakeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RpcHandshakeException(RpcReject rejection)
        : base(rejection?.Message)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        Rejection = rejection;
    }

    /// <summary>The server's rejection, including the protocol versions it supports; null for an invalid reply.</summary>
    public RpcReject? Rejection { get; }
}

/// <summary>A request the server answered with an error.</summary>
public sealed class RpcCallException : Exception
{
    public RpcCallException()
        : this(new RpcErrorDetail(RpcErrorCodes.InternalError, "The request failed."))
    {
    }

    public RpcCallException(string message)
        : this(new RpcErrorDetail(RpcErrorCodes.InternalError, message))
    {
    }

    public RpcCallException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = new RpcErrorDetail(RpcErrorCodes.InternalError, message);
    }

    public RpcCallException(RpcErrorDetail error)
        : base(error?.Message)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public RpcCallException(string code, string message)
        : this(new RpcErrorDetail(code, message))
    {
    }

    public RpcErrorDetail Error { get; }

    public string Code => Error.Code;
}
