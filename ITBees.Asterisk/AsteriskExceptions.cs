namespace ITBees.Asterisk;

public class AsteriskException : Exception
{
    public AsteriskException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>The package cannot be rendered (see the validator's errors in the message).</summary>
public sealed class AsteriskConfigurationException : AsteriskException
{
    public AsteriskConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>AMI is not reachable: Asterisk is stopped, not installed or the manager is not configured yet.</summary>
public sealed class AsteriskConnectionException : AsteriskException
{
    public AsteriskConnectionException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>AMI refused the user name / password.</summary>
public sealed class AsteriskAuthenticationException : AsteriskException
{
    public AsteriskAuthenticationException(string message) : base(message)
    {
    }
}

/// <summary>Asterisk answered an action with an error.</summary>
public sealed class AsteriskActionException : AsteriskException
{
    public AsteriskActionException(string action, string message) : base($"{action}: {message}")
    {
        Action = action;
    }

    public string Action { get; }
}

/// <summary>Asterisk is not installed on this host.</summary>
public sealed class AsteriskNotInstalledException : AsteriskException
{
    public AsteriskNotInstalledException(string message) : base(message)
    {
    }
}

/// <summary>A host command (package manager, systemctl, writing a configuration file) failed.</summary>
public sealed class AsteriskHostException : AsteriskException
{
    public AsteriskHostException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
