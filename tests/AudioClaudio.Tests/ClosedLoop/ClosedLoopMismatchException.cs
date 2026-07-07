using System;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Thrown when a synthesized case does not transcribe back within R9.2 tolerance.</summary>
public sealed class ClosedLoopMismatchException : Exception
{
    public ClosedLoopMismatchException(string message)
        : base(message)
    {
    }
}
