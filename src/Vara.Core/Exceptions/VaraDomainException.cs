namespace Vara.Core.Exceptions;

/// <summary>
/// Closed root for all user-facing domain errors produced by Vara.
/// </summary>
public closed class VaraDomainException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Base for domain errors caused by a conflicting operation or inaccessible run lock.
/// </summary>
public closed class ConcurrencyException(string message, Exception? innerException = null)
    : VaraDomainException(message, innerException);

/// <summary>
/// Base for domain errors raised while starting or confirming a backup-related run.
/// </summary>
public closed class BackupRunException(string message, Exception? innerException = null)
    : VaraDomainException(message, innerException);
